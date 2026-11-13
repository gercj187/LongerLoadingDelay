//FILE:		LoadingService.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// UPDATER (ein GameObject, DontDestroyOnLoad)
	// =========================
	public class LongerLoadingDelay_Updater : MonoBehaviour
	{
		void Update()
		{
			if (!Main.Enabled)
				return;

			LoadingService.FrameUpdate(Time.unscaledDeltaTime, Time.deltaTime);
		}
	}

	// =========================
	// KERN
	// =========================
	// Die Mod ist die einzige Ladeautorität: Die Vanilla-Coroutine wird ersetzt (siehe Patches),
	// beladen wird ausschließlich hier – über die Logik-Ebene (WarehouseMachine / Car),
	// die unabhängig vom Streaming existiert. Deshalb gibt es kein "Nachholen" mehr.
	public static class LoadingService
	{
		public static readonly List<LoadSequence> Sequences = new();

		private const float TickInterval = 0.5f;		// Logik 2×/s (Echtzeit) → Anzeige sekundengenau
		private const float ScanInterval = 2f;			// Suche nach hinzugestellten Zügen
		private const float ResumeAfterStable = 3f;		// Sekunden stillstehend, bevor es weitergeht
		private const float ErrorFlashSeconds = 10f;
		private const int MaxCarsPerTick = 3;			// nach Zeitsprung nicht alles in einem Frame
		private const double MaxJumpMinutes = 12 * 60;	// Schlaf max. 10 h → alles darüber ist ein Fehler

		private static LongerLoadingDelay_Updater? updater;

		private static float tickAcc, scanAcc;
		private static double? lastAbsMinutes;
		private static double? lastRawMinutes;
		private static float tickRealAcc;

		// Uhr-Messung (Spielsekunden pro Echtsekunde)
		private static double rateGameAcc;
		private static float rateRealAcc;
		private static bool rateSavedThisSession;
		private static int outlierCount;
		private static float lastOutlier;

		public const float MinPlausibleClockRate = 0.2f;
		public const float MaxPlausibleClockRate = 30f;

		private static readonly Dictionary<string, LampState> lampStates = new();
		private static readonly Dictionary<string, float> errorFlashUntil = new();
		private static readonly HashSet<string> tracksWithDisplay = new();
		private static readonly Dictionary<string, WarehouseMachineController> controllersByTrack = new();

		// Wiederverwendete Puffer (keine Allokationen pro Tick)
		private static readonly List<WarehouseTask> taskBuffer = new();
		private static readonly List<LoadSequence> trackBuffer = new();
		private static readonly HashSet<string> trackNames = new();
		private static readonly StringBuilder sb = new();

		// =========================
		// REFLECTION (einmalig)
		// =========================
		internal static readonly AccessTools.FieldRef<WarehouseMachine, Dictionary<Job, List<WarehouseTask>>> jobToTasksRef =
			AccessTools.FieldRefAccess<WarehouseMachine, Dictionary<Job, List<WarehouseTask>>>("currentJobToTasks");

		private static readonly AccessTools.FieldRef<WarehouseMachineController, Coroutine> loadUnloadCoroRef =
			AccessTools.FieldRefAccess<WarehouseMachineController, Coroutine>("loadUnloadCoro");

		private static readonly MethodInfo? setScreenMethod =
			AccessTools.Method(typeof(WarehouseMachineController), "SetScreen");

		private static readonly MethodInfo? displayIdleMethod =
			AccessTools.Method(typeof(WarehouseMachineController), "DisplayIdleText");

		// =========================
		// SETUP
		// =========================
		public static void EnsureUpdater()
		{
			if (updater != null)
				return;

			var go = new GameObject("LongerLoadingDelay_Updater");
			UnityEngine.Object.DontDestroyOnLoad(go);
			updater = go.AddComponent<LongerLoadingDelay_Updater>();
		}

		public static void ClearAll()
		{
			Main.Log(" <<< RESET MOD >>>");

			var tracks = Sequences.Select(s => s.TrackName).Distinct().ToList();
			Sequences.Clear();
			lampStates.Clear();
			errorFlashUntil.Clear();

			foreach (string t in tracks)
				RestoreIdleDisplay(t);

			tracksWithDisplay.Clear();
		}

		// Spielstand geladen / neue Karriere
		public static void ResetRuntime()
		{
			lastAbsMinutes = null;
			lastRawMinutes = null;
			tickAcc = 0;
			scanAcc = 0;
			lampStates.Clear();
			errorFlashUntil.Clear();
			tracksWithDisplay.Clear();
			controllersByTrack.Clear();
			GameClock.ResetCache();
		}

		private static bool InGame()
		{
			return WorldStreamingInit.IsStreamingDone
				&& AStartGameData.carsAndJobsLoadingFinished
				&& SingletonBehaviour<JobsManager>.Instance != null;
		}

		// =========================
		// FRAME / TICK
		// =========================
		public static void FrameUpdate(float unscaledDt, float scaledDt)
		{
			tickRealAcc += scaledDt;
			tickAcc += unscaledDt;

			if (tickAcc < TickInterval)
				return;

			float dt = tickAcc;
			float realDt = tickRealAcc;
			tickAcc = 0;
			tickRealAcc = 0;

			if (!InGame() || !GameClock.TryGetNowAbsMinutes(out double nowAbs))
			{
				lastAbsMinutes = null;
				lastRawMinutes = null;
				return;
			}

			double fromAbs = lastAbsMinutes ?? nowAbs;		// erster Tick nach Laden: nichts verrechnen
			lastAbsMinutes = nowAbs;

			double raw = GameClock.LastRawAbsMinutes;
			double rawDelta = raw - (lastRawMinutes ?? raw);
			lastRawMinutes = raw;

			double deltaMin = nowAbs - fromAbs;

			if (deltaMin < 0 || deltaMin > MaxJumpMinutes)
			{
				Main.Log($" Clock discontinuity ({deltaMin:F1} min) ignored");
				fromAbs = nowAbs;
				deltaMin = 0;
			}

			if (rawDelta >= 0 && rawDelta <= MaxJumpMinutes)
				MeasureClockRate(rawDelta * 60.0, realDt);

			Tick(fromAbs, nowAbs, dt);
		}

		private static void MeasureClockRate(double gameSeconds, float realSeconds)
		{
			if (realSeconds <= 0.0001f)
				return;

			// Sprünge (Schlaf, Fast Travel, Laden, Zeit-Slider) verwerfen das ganze Messfenster
			if (gameSeconds > realSeconds * MaxPlausibleClockRate * 3)
			{
				rateGameAcc = 0;
				rateRealAcc = 0;
				return;
			}

			rateGameAcc += gameSeconds;
			rateRealAcc += realSeconds;

			// Uhr angehalten / extrem langsam: Fenster nach 120 s ohne genug Spielzeit verwerfen
			if (rateRealAcc > 180f && rateGameAcc < 120.0)
			{
				rateGameAcc = 0;
				rateRealAcc = 0;
				return;
			}

			// mind. 60 s Echtzeit und 2 Spielminuten (falls die Uhr nur minutengenau ist)
			if (rateRealAcc < 60f || rateGameAcc < 120.0)
				return;

			float measured = (float)(rateGameAcc / rateRealAcc);
			rateGameAcc = 0;
			rateRealAcc = 0;

			if (measured < MinPlausibleClockRate || measured > MaxPlausibleClockRate)
				return;

			var s = Main.Settings;
			float old = s.ClockRate;

			// Große Abweichung erst nach 3 übereinstimmenden Messungen übernehmen (z. B. Schwierigkeit geändert)
			if (old > 0.0001f && Mathf.Abs(measured - old) > old * 0.5f)
			{
				if (outlierCount > 0 && Mathf.Abs(measured - lastOutlier) > lastOutlier * 0.2f)
					outlierCount = 0;

				lastOutlier = measured;
				if (++outlierCount < 2)
					return;

				old = 0f;		// neu beginnen
			}

			outlierCount = 0;
			s.ClockRate = old <= 0.0001f ? measured : Mathf.Lerp(old, measured, 0.3f);

			// selten speichern: einmal pro Sitzung oder bei deutlicher Änderung
			if (!rateSavedThisSession || Mathf.Abs(s.ClockRate - old) > old * 0.1f)
			{
				rateSavedThisSession = true;
				s.Save(Main.ModEntry);
				Main.Log($" Clock rate measured: {s.ClockRate:F3} game s / real s");
			}
		}

		private static void Tick(double fromAbs, double nowAbs, float dt)
		{
			// Ladungs-Optik von Wagen nachziehen, die bearbeitet wurden, während ihr Interior ausgeladen war
			// (läuft auch beim Multiplayer-Client: dort kommen die Ladungsänderungen vom Host)
			CargoVisuals.Tick(dt);
			LoadStatusRadio.EnforceAvailability();

			// Multiplayer-Client: nichts selbst berechnen, nur den Zustand des Hosts anzeigen
			if (Mp.IsClient)
			{
				ClientTick(nowAbs);
				return;
			}

			double work = Schedule.Integrate(fromAbs, nowAbs);
			bool breakNow = Schedule.RateAt(nowAbs) <= 0;

			AltTracks.Tick(dt);
			Penalties.Tick(dt);

			// --- Jobs prüfen (beendet / abgebrochen / verschwunden) ---
			for (int i = Sequences.Count - 1; i >= 0; i--)
			{
				var seq = Sequences[i];

				if (!ResolveRuntime(seq, dt))
				{
					Sequences.RemoveAt(i);
					continue;
				}
			}

			// --- Pro Gleis abarbeiten ---
			trackNames.Clear();
			foreach (var seq in Sequences)
				trackNames.Add(seq.TrackName);

			foreach (string track in trackNames)
				ProcessTrack(track, work, dt, fromAbs, nowAbs);

			// --- Hinzugestellte Züge erkennen ---
			scanAcc += dt;
			if (scanAcc >= ScanInterval)
			{
				scanAcc = 0;
				if (Main.Settings.AutoDetectNewJobs)
					AutoDetect();
			}

			// --- Zustände & Displays ---
			UpdateLampStates(breakNow);
			RefreshDisplays();
			MachineClock.Tick(nowAbs);

			// Multiplayer-Host: Zustand an alle Clients
			Mp.HostTick(dt);
		}

		private static void NotifyCarUpdated(string track, LoadSequence seq, Car car)
		{
			try
			{
				var ctrl = FindController(track);
				if (ctrl == null)
					return;

				var cargo = seq.IsLoading ? car.CurrentCargoTypeInCar : car.LastUnloadedCargoType;
				ShowVanilla(ctrl, WarehouseMachineController.TextPreset.CarUpdated, seq.IsLoading, seq.JobId, null, car,
					cargo.ToV2());
			}
			catch (Exception e)
			{
				Main.Log(" MP CarUpdated failed: " + e.Message);
			}
		}

		// =========================
		// MULTIPLAYER-CLIENT
		// =========================
		private static void ClientTick(double nowAbs)
		{
			foreach (var seq in Sequences)
			{
				seq.Job ??= FindJobById(seq.JobId);

				if (seq.Machine == null)
				{
					var ctrl = FindController(seq.TrackName);
					seq.Machine = ctrl != null ? ctrl.warehouseMachine : null;
				}
			}

			// Lampenfarben NICHT selbst berechnen: dafür braucht es Wagen-/Gleisdaten, die nur der Host auflöst
			// (sonst z. B. "Unterbrochen" beim Client weiß statt rot) → Zustand des Hosts übernehmen
			lampStates.Clear();
			foreach (var kv in SyncedLampStates)
				lampStates[kv.Key] = kv.Value;

			RefreshDisplays();
			MachineClock.Tick(nowAbs);
		}

		// Multiplayer: Lampenzustände des Hosts (Gleis → Zustand)
		public static readonly Dictionary<string, LampState> SyncedLampStates = new();
		public static IReadOnlyDictionary<string, LampState> CurrentLampStates => lampStates;

		// Vom Host empfangene Sequenzen übernehmen (Gleise ohne Sequenz bekommen wieder ihre Normalanzeige)
		public static void ApplySyncedSequences(List<LoadSequence> list)
		{
			var oldTracks = new HashSet<string>(Sequences.Select(s => s.TrackName));

			// Laufzeit-Verweise behalten, wo derselbe Job auf demselben Gleis schon bekannt ist
			foreach (var n in list)
			{
				var old = Sequences.Find(s => s.JobId == n.JobId && s.TrackName == n.TrackName && s.IsLoading == n.IsLoading);
				if (old != null)
				{
					n.Job = old.Job;
					n.Machine = old.Machine;
				}
			}

			Sequences.Clear();
			Sequences.AddRange(list);

			foreach (string t in oldTracks)
			{
				if (!Sequences.Exists(s => s.TrackName == t))
				{
					lampStates.Remove(t);
					RestoreIdleDisplay(t);
				}
			}
		}

		// Job / Maschine / Wagen auflösen. false = Sequenz entfernen
		private static bool ResolveRuntime(LoadSequence seq, float dt)
		{
			if (seq.Job == null)
			{
				seq.Job = FindJobById(seq.JobId);

				if (seq.Job == null)
				{
					seq.MissingJobFor += dt;

					if (seq.MissingJobFor > 15f)
					{
						Main.Log($" {seq.TrackName} [{seq.JobId}] : JOB NOT FOUND → REMOVED");
						return false;
					}

					return true;
				}
			}

			seq.MissingJobFor = 0;

			switch (seq.Job.State)
			{
				case JobState.InProgress:
					break;

				case JobState.Abandoned:
					Main.Log($" {seq.TrackName} [{seq.JobId}] : ABANDONED");
					FlashError(seq.TrackName);
					return false;

				default:
					Main.Log($" {seq.TrackName} [{seq.JobId}] : JOB {seq.Job.State} → REMOVED");
					return false;
			}

			if (seq.Machine == null)
			{
				var ctrl = FindController(seq.TrackName);
				seq.Machine = ctrl != null ? ctrl.warehouseMachine : null;
			}

			if (seq.Machine != null && (seq.Cars == null || seq.Cars.Count == 0))
			{
				GetReadyTasks(seq.Machine, seq.Job, seq.TaskType, taskBuffer);
				seq.Cars = CollectCars(seq.Job, seq.Machine, seq.TaskType, taskBuffer);
			}

			return true;
		}

		// =========================
		// GLEIS-LOGIK
		// =========================
		private static void ProcessTrack(string track, double work, float dt, double fromAbs, double nowAbs)
		{
			trackBuffer.Clear();
			foreach (var s in Sequences)
				if (s.TrackName == track)
					trackBuffer.Add(s);

			bool anyActive = false;
			bool parallel = Main.Settings.ParallelProcessing;

			// Gleichzeitige Abfertigung: solange mehrere Jobs laufen, dauert jeder länger
			int activeCount = 0;
			foreach (var s in trackBuffer)
				if (s.State == SeqState.Active)
					activeCount++;

			double seqWork = parallel && activeCount > 1 ? work / Mathf.Max(1f, Main.Settings.ParallelFactor) : work;

			foreach (var seq in trackBuffer)
			{
				var machine = seq.Machine;
				var job = seq.Job;

				if (machine == null || job == null)
					continue;

				var trackLogic = machine.WarehouseTrack;

				// --- DONE: bleibt, bis die Wagen das Gleis verlassen haben ---
				if (seq.State == SeqState.Done)
				{
					if (!AnyCarOnTrack(seq.Cars, trackLogic))
					{
						Main.Log($" {track} [{seq.JobId}] : CARS LEFT → REMOVED");
						Sequences.Remove(seq);
						continue;
					}

					// Standgeld: fertiger Zug blockiert das Ladegleis
					var st = Main.Settings;
					if (st.IdlePenalty && seq.FinishedAt >= 0)
					{
						double standing = nowAbs - seq.FinishedAt;
						double chargeable = standing / 60.0 - st.IdleGraceHours;

						if (chargeable > 0)
							Penalties.SetIdle(seq.JobId, (float)(chargeable * st.IdlePenaltyPerHour), standing);
					}

					continue;
				}

				// --- Zug wieder abgezogen, bevor fertig → abmelden (kann woanders neu angemeldet werden) ---
				if (seq.Cars != null && seq.Cars.Count > 0 && !AnyCarOnTrack(seq.Cars, trackLogic))
				{
					seq.GoneFor += dt;

					if (seq.GoneFor >= 5f)
					{
						// Noch nicht begonnen und auf ein Ersatzgleis umgeleitet → Umleitung + Abzug zurücknehmen
						if (seq.CarsDone == 0 && AltTracks.Redirects.TryGetValue(seq.JobId, out var r) && r.to == track)
							AltTracks.Revert(seq.JobId);

						Main.Log($" {track} [{seq.JobId}] : TRAIN LEFT → UNREGISTERED ({seq.CarsDone}/{seq.JobCars})");
						Sequences.Remove(seq);
					}

					continue;
				}

				seq.GoneFor = 0f;

				GetReadyTasks(machine, job, seq.TaskType, taskBuffer);

				if (taskBuffer.Count == 0 || CountPendingCars(taskBuffer, seq.IsLoading) == 0)
				{
					// Aufgaben der Maschine erledigt (oder noch nie bereit)
					if (AllCarsProcessed(seq.Cars, seq.IsLoading))
						MarkDone(seq);

					continue;
				}

				bool allPresent = AllCarsOnTrack(taskBuffer, trackLogic);
				bool moving = AnyCarMoving(taskBuffer);

				switch (seq.State)
				{
					case SeqState.Active:
					{
						if (!allPresent || moving)
						{
							// Nicht sofort abbrechen: beim Laden/Entladen der Umgebung (Spieler läuft weg/zurück)
							// melden Wagen kurz eine Geschwindigkeit oder kein Gleis. Erst wenn es anhält → Abbruch.
							// Solange pausiert die Arbeit (kein Fortschritt in diesem Tick).
							seq.BadFor += dt;

							if (seq.BadFor >= InterruptAfter)
							{
								if (Main.DebugOn)
									Main.Log($" {track} [{seq.JobId}] : INTERRUPT after {seq.BadFor:0.0}s – {DescribeProblem(taskBuffer, trackLogic)}");

								Interrupt(seq, moving ? InterruptReason.Moving : InterruptReason.CarsMissing);
							}
							break;
						}

						if (seq.BadFor > 0f && Main.DebugOn)
							Main.Log($" {track} [{seq.JobId}] : short glitch ignored ({seq.BadFor:0.0}s)");
						seq.BadFor = 0f;

						anyActive = true;

						// Exakter Fertig-Zeitpunkt, falls die Arbeit in diesem Tick fertig wird
						// (wichtig bei Zeitsprüngen: Schlaf / Schnellreise → Standgeld ab echtem Ende)
						double remaining = seq.TotalWork - seq.WorkDone;
						// Nur beim ERSTEN Erreichen setzen – nach einem Zeitsprung werden die Wagen über mehrere
						// Ticks abgearbeitet (max. 3 pro Tick); danach ist remaining = 0 und würde "jetzt" ergeben.
						if (seq.FinishHint < 0 && remaining > 1e-6 && seqWork > 0 && seqWork >= remaining)
						{
							double needed = remaining * (work / seqWork);		// zurück in ungeteilte Arbeit (Parallelbetrieb)
							double? at = Schedule.FinishTime(fromAbs, needed);
							seq.FinishHint = at.HasValue ? Math.Min(at.Value, nowAbs) : nowAbs;
							Main.Log($" {track} [{seq.JobId}] : WORK COMPLETE at {GameClock.FormatTimeOfDay(seq.FinishHint)} (tick {GameClock.FormatTimeOfDay(fromAbs)} → {GameClock.FormatTimeOfDay(nowAbs)})");
						}

						seq.WorkDone = Math.Min(seq.TotalWork, seq.WorkDone + seqWork);

						int target = Math.Min(seq.JobCars, (int)Math.Floor(seq.WorkDone / seq.DelaySeconds + 1e-6));
						int processedThisTick = 0;

						while (seq.CarsDone < target && processedThisTick < MaxCarsPerTick)
						{
							var car = ProcessOneCar(machine, taskBuffer, seq.IsLoading);
							CargoVisuals.MarkProcessed(car);
							if (car == null)
								break;

							seq.CarsDone++;
							processedThisTick++;

							// Multiplayer-Host: wie Vanilla "Wagen bearbeitet" melden → die Multiplayer-Mod
							// gleicht damit die Lageraufgaben bei den Clients ab
							if (Mp.IsHost)
								NotifyCarUpdated(track, seq, car);

							Main.Log($" {track} [{seq.JobId}] : CAR {car.ID} {(seq.IsLoading ? "LOADED" : "UNLOADED")} {seq.CarsDone}/{seq.JobCars}");
						}

						if (seq.CarsDone >= seq.JobCars || CountPendingCars(taskBuffer, seq.IsLoading) == 0)
						{
							MarkDone(seq);
							anyActive = false;
						}

						break;
					}

					case SeqState.Interrupted:
					{
						if (allPresent && !moving)
						{
							seq.StableFor += dt;

							if (seq.StableFor >= ResumeAfterStable)
							{
								seq.State = SeqState.Queued;
								seq.Reason = InterruptReason.None;
								seq.StableFor = 0;
								Main.Log($" {track} [{seq.JobId}] : RESUME → QUEUED");
							}
						}
						else
						{
							seq.StableFor = 0;
						}

						break;
					}
				}
			}

			if (!anyActive || parallel)
				TryStartNext(track);
		}

		private static void TryStartNext(string track)
		{
			bool parallel = Main.Settings.ParallelProcessing;

			// nacheinander: nur starten, wenn gerade nichts läuft
			if (!parallel)
			{
				foreach (var seq in Sequences)
				{
					if (seq.TrackName == track && seq.State == SeqState.Active)
						return;
				}
			}

			foreach (var seq in Sequences)
			{
				if (seq.TrackName != track || seq.State != SeqState.Queued)
					continue;

				var machine = seq.Machine;
				var job = seq.Job;

				if (machine == null || job == null)
					continue;

				GetReadyTasks(machine, job, seq.TaskType, taskBuffer);

				if (taskBuffer.Count == 0 || CountPendingCars(taskBuffer, seq.IsLoading) == 0)
					continue;

				if (!AllCarsOnTrack(taskBuffer, machine.WarehouseTrack) || AnyCarMoving(taskBuffer))
					continue;

				// angefangener Wagen zählt nicht – Fortschritt immer auf ganze Wagen
				seq.WorkDone = seq.CarsDone * seq.DelaySeconds;
				seq.FinishHint = -1;
				seq.State = SeqState.Active;
				seq.Reason = InterruptReason.None;

				Main.Log($" {track} [{seq.JobId}] : STARTED ({seq.CarsDone}/{seq.JobCars})");

				if (!parallel)
					return;		// gleichzeitig: alle bereitstehenden starten
			}
		}

		private static void Interrupt(LoadSequence seq, InterruptReason reason)
		{
			seq.State = SeqState.Interrupted;
			seq.Reason = reason;
			seq.StableFor = 0;
			seq.BadFor = 0;

			// ToDo-Vorgabe: Timer sofort auf (JobCars - CarsDone) × Delay zurück
			seq.WorkDone = seq.CarsDone * seq.DelaySeconds;
			seq.FinishHint = -1;

			Main.Log($" {seq.TrackName} [{seq.JobId}] : INTERRUPTED ({reason})");

			var ctrl = FindController(seq.TrackName);
			if (ctrl != null && ctrl.isActiveAndEnabled && ctrl.errorSound != null)
				AudioSource.PlayClipAtPoint(ctrl.errorSound, ctrl.transform.position);
		}

		private static void MarkDone(LoadSequence seq)
		{
			if (seq.State == SeqState.Done)
				return;

			seq.State = SeqState.Done;
			seq.FinishedAt = seq.FinishHint >= 0 ? seq.FinishHint : (lastAbsMinutes ?? -1);
			seq.CarsDone = Math.Max(seq.CarsDone, seq.JobCars);
			seq.WorkDone = seq.TotalWork;

			Main.Log($" {seq.TrackName} [{seq.JobId}] : DONE");

			var ctrl = FindController(seq.TrackName);
			if (ctrl != null && ctrl.isActiveAndEnabled && ctrl.machineSound != null)
				AudioSource.PlayClipAtPoint(ctrl.machineSound, ctrl.transform.position);
		}

		// =========================
		// HEBEL (ersetzt Vanilla DelayedLoadUnload)
		// =========================
		public static IEnumerator LeverCoroutine(WarehouseMachineController ctrl, bool isLoading)
		{
			// mindestens ein Frame, damit StartCoroutine() loadUnloadCoro setzen kann
			yield return null;

			try
			{
				OnLever(ctrl, isLoading);
			}
			catch (Exception e)
			{
				Debug.LogError("[LongerLoadingDelay] Lever handling failed: " + e);
			}

			// Hebel wieder freigeben (Vanilla macht das am Ende der Coroutine).
			// Bei ActivateExternally ist das Feld ohnehin null – beide Wege schließen sich gegenseitig aus.
			if (ctrl != null)
				loadUnloadCoroRef(ctrl) = null!;
		}

		private static void OnLever(WarehouseMachineController ctrl, bool isLoading)
		{
			EnsureUpdater();

			var machine = ctrl.warehouseMachine;
			if (machine == null)
				return;

			string track = ctrl.warehouseTrackName;
			controllersByTrack[track] = ctrl;

			// Ersatzgleis: Züge auf diesem Gleis, deren Job auf ein Nachbargleis zeigt, hierher umleiten
			AltTracks.TryRedirectTo(ctrl, isLoading);

			var type = isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading;
			var data = machine.GetCurrentLoadUnloadData(type);

			bool registered = false;
			bool error = false;

			foreach (var d in data)
			{
				// Sonderlieferungen (z. B. Lok-Restaurierung): wie Vanilla sofort
				if (d.specialDeliveryToProcess != null)
				{
					if (AnyCarMoving(d.specialDeliveryToProcess.reservedCarsOnTrack))
					{
						error = true;
						ShowVanilla(ctrl, WarehouseMachineController.TextPreset.Moving, isLoading, d.id);
						continue;
					}

					var cars = isLoading
						? machine.LoadSpecialDelivery(d.specialDeliveryToProcess)
						: machine.UnloadSpecialDelivery(d.specialDeliveryToProcess);

					if (cars != null && cars.Count > 0)
					{
						ShowVanilla(ctrl, isLoading ? WarehouseMachineController.TextPreset.InstantLoad : WarehouseMachineController.TextPreset.InstantUnload,
							false, null, string.Join(", ", cars.Select(c => c.ID)));
						registered = true;
					}
					continue;
				}

				if (d.state == WarehouseMachine.WarehouseLoadUnloadDataPerJob.State.SomeCarsPresentLoadUnloadForbiden)
				{
					error = true;
					ShowVanilla(ctrl, WarehouseMachineController.TextPreset.Error, isLoading, d.id);
					continue;
				}

				var tasks = d.tasksAvailableToProcess;
				if (tasks == null || tasks.Count == 0)
					continue;

				if (AnyCarMoving(tasks))
				{
					error = true;
					ShowVanilla(ctrl, WarehouseMachineController.TextPreset.Moving, isLoading, d.id);
					continue;
				}

				var job = tasks[0].Job;
				if (job != null && Register(machine, track, job, tasks, isLoading))
					registered = true;
			}

			// Gegenrichtung ebenfalls annehmen (z. B. ShuntingUnload neben ShuntingLoad) –
			// sie wird hinter die Aufträge der gezogenen Richtung eingereiht.
			if (RegisterDirection(machine, track, !isLoading) > 0)
				registered = true;

			PrioritizeQueued(track, isLoading);

			bool trackHasSequences = Sequences.Exists(s => s.TrackName == track);

			if (!registered && !trackHasSequences)
			{
				FlashError(track);

				if (!error)
					ShowVanilla(ctrl, WarehouseMachineController.TextPreset.NoTrains, isLoading);

				return;
			}

			if (registered && ctrl.machineSound != null)
				AudioSource.PlayClipAtPoint(ctrl.machineSound, ctrl.transform.position);

			TryStartNext(track);
			UpdateLampStates(Schedule.RateAt(lastAbsMinutes ?? 0) <= 0);
			RefreshDisplay(ctrl);
		}

		// Neue Sequenz anlegen oder bestehende erweitern. true = es gibt Arbeit
		private static bool Register(WarehouseMachine machine, string track, Job job, List<WarehouseTask> tasks, bool isLoading)
		{
			int pending = CountPendingCars(tasks, isLoading);
			if (pending == 0)
				return false;

			var existing = Sequences.Find(s => s.JobId == job.ID && s.TrackName == track && s.IsLoading == isLoading);

			if (existing != null)
			{
				int open = existing.JobCars - existing.CarsDone;

				if (existing.State == SeqState.Done || pending > open)
				{
					existing.JobCars = existing.CarsDone + pending;
					existing.Cars = CollectCars(job, machine, existing.TaskType, tasks);

					if (existing.State == SeqState.Done)
					{
						existing.State = SeqState.Queued;
						existing.WorkDone = existing.CarsDone * existing.DelaySeconds;
					}

					Main.Log($" {track} [{job.ID}] : EXTENDED → {existing.JobCars} cars");
				}

				// Hebel bei unterbrochener Sequenz = sofort fortsetzen (statt 3 s warten)
				if (existing.State == SeqState.Interrupted)
				{
					existing.State = SeqState.Queued;
					existing.Reason = InterruptReason.None;
					existing.StableFor = 0;
				}

				existing.Job = job;
				existing.Machine = machine;
				return true;
			}

			var seq = new LoadSequence
			{
				JobId = job.ID,
				TrackName = track,
				IsLoading = isLoading,
				JobCars = pending,
				CarsDone = 0,
				WorkDone = 0,
				DelaySeconds = Main.Settings.CarDelayGameSeconds,
				State = SeqState.Queued,
				Job = job,
				Machine = machine,
			};

			seq.Cars = CollectCars(job, machine, seq.TaskType, tasks);
			Sequences.Add(seq);

			Main.Log($" {track} [{job.ID}] : REGISTERED {(isLoading ? "LOAD" : "UNLOAD")} CARS={pending}");
			return true;
		}

		// Während eine Maschine arbeitet: neu hinzugestellte, stehende Züge automatisch einreihen
		private static void AutoDetect()
		{
			trackNames.Clear();
			foreach (var seq in Sequences)
				if (seq.IsOperating)
					trackNames.Add(seq.TrackName);

			foreach (string track in trackNames)
			{
				LoadSequence? first = Sequences.Find(s => s.TrackName == track && s.IsOperating);
				WarehouseMachine? machine = first?.Machine;
				if (first == null || machine == null)
					continue;

				// beide Richtungen: Beladen und Entladen
				int added = RegisterDirection(machine, track, true) + RegisterDirection(machine, track, false);
				if (added > 0)
					Main.Log($" {track} : AUTO-DETECTED {added} job(s)");
			}
		}

		// Alle bereitstehenden, stehenden Jobs einer Richtung an dieser Maschine registrieren. Rückgabe: Anzahl neu/erweitert
		private static int RegisterDirection(WarehouseMachine machine, string track, bool isLoading)
		{
			var dict = jobToTasksRef(machine);
			if (dict == null)
				return 0;

			var type = isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading;
			int count = 0;

			foreach (var kvp in dict.ToList())
			{
				var job = kvp.Key;
				if (job == null)
					continue;

				taskBuffer.Clear();
				foreach (var t in kvp.Value)
					if (t.warehouseTaskType == type && t.readyForMachine && machine.IsCargoSupported(t.cargoType))
						taskBuffer.Add(t);

				if (taskBuffer.Count == 0)
					continue;

				if (!AllCarsOnTrack(taskBuffer, machine.WarehouseTrack) || AnyCarMoving(taskBuffer))
					continue;

				bool known = Sequences.Exists(s => s.JobId == job.ID && s.TrackName == track && s.IsLoading == isLoading);
				var copy = new List<WarehouseTask>(taskBuffer);

				if (Register(machine, track, job, copy, isLoading) && !known)
					count++;
			}

			return count;
		}

		// Wartende Aufträge der gezogenen Richtung nach vorne (stabil, laufender Auftrag bleibt unberührt)
		private static void PrioritizeQueued(string track, bool isLoading)
		{
			var queued = new List<int>();
			for (int i = 0; i < Sequences.Count; i++)
				if (Sequences[i].TrackName == track && Sequences[i].State == SeqState.Queued)
					queued.Add(i);

			if (queued.Count < 2)
				return;

			var ordered = queued.Select(i => Sequences[i])
				.OrderBy(s => s.IsLoading == isLoading ? 0 : 1)
				.ToList();

			for (int k = 0; k < queued.Count; k++)
				Sequences[queued[k]] = ordered[k];
		}

		// =========================
		// LAMPE
		// =========================
		public static LampState GetLampState(string track)
		{
			if (errorFlashUntil.TryGetValue(track, out float until) && Time.unscaledTime < until)
				return LampState.Error;

			if (lampStates.TryGetValue(track, out var st))
				return st;

			// Freies Gleis während einer Pause laut Schichtplan → blau statt weiß
			if (lastAbsMinutes.HasValue && Schedule.RateAt(lastAbsMinutes.Value) <= 0)
				return LampState.Paused;

			return LampState.Idle;
		}

		private static void FlashError(string track)
		{
			errorFlashUntil[track] = Time.unscaledTime + ErrorFlashSeconds;
		}

		private static void UpdateLampStates(bool breakNow)
		{
			lampStates.Clear();

			foreach (var seq in Sequences)
			{
				var st = seq.State switch
				{
					SeqState.Interrupted => AnyCarOnTrack(seq.Cars, seq.Machine?.WarehouseTrack) ? LampState.Error : LampState.Idle,
					SeqState.Active => breakNow ? LampState.Paused : LampState.Loading,
					SeqState.Queued => !AnyCarOnTrack(seq.Cars, seq.Machine?.WarehouseTrack) ? LampState.Idle
						: breakNow ? LampState.Paused : LampState.Loading,
					SeqState.Done => LampState.Finished,
					_ => LampState.Idle
				};

				if (st == LampState.Idle && breakNow)
					st = LampState.Paused;

				lampStates.TryGetValue(seq.TrackName, out var current);
				if (Priority(st) > Priority(current))
					lampStates[seq.TrackName] = st;
			}
		}

		private static int Priority(LampState s) => s switch
		{
			LampState.Error => 5,
			LampState.Loading => 4,
			LampState.Paused => 3,
			LampState.Finished => 2,
			LampState.Idle => 1,
			_ => 0
		};

		// =========================
		// DISPLAY
		// =========================
		private static void RefreshDisplays()
		{
			trackNames.Clear();
			foreach (var seq in Sequences)
				trackNames.Add(seq.TrackName);

			// Gleise, deren letzte Sequenz verschwunden ist → Vanilla-Leerlauf
			foreach (string old in tracksWithDisplay.ToList())
			{
				if (!trackNames.Contains(old))
				{
					RestoreIdleDisplay(old);
					tracksWithDisplay.Remove(old);
				}
			}

			foreach (string track in trackNames)
			{
				var ctrl = FindController(track);
				if (ctrl != null && ctrl.isActiveAndEnabled)
					RefreshDisplay(ctrl);

				tracksWithDisplay.Add(track);
			}
		}

		private static void RefreshDisplay(WarehouseMachineController ctrl)
		{
			// Fehler-Presets (NoTrains/Failed) spielen bei jedem UpdateScreen einen Ton → vorher ersetzen
			foreach (var p in ctrl.CurrentTextPresets)
			{
				if (p.preset == WarehouseMachineController.TextPreset.NoTrains || p.preset == WarehouseMachineController.TextPreset.Failed)
				{
					ShowVanilla(ctrl, WarehouseMachineController.TextPreset.Busy, true);
					break;
				}
			}

			ctrl.UpdateScreen();		// → Postfix ApplyDisplay
		}

		private static void RestoreIdleDisplay(string track)
		{
			var ctrl = FindController(track);
			if (ctrl == null || !ctrl.isActiveAndEnabled || displayIdleMethod == null)
				return;

			try
			{
				displayIdleMethod.Invoke(ctrl, null);
			}
			catch (Exception e)
			{
				Main.Log(" DisplayIdleText failed: " + e.Message);
			}
		}

		// Aufgerufen aus dem UpdateScreen-Postfix
		//
		//   WIRD ENTLADEN...                               14:25
		//   JOB            WAGEN      STATUS              FERTIG
		//   ==================================================
		//   [HB-SL-37]     12/12      FERTIG               14:20
		//   [HB-SU-77]      2/5       0:16:21            ~ 14:32
		//   [HB-SU-27]      0/5       WARTET             ~ 14:51
		//   ...
		//   [HB-SU-77] - Ersatz für D7L (belegt) -5 %      ← unten, nach oben aufbauend
		public static void ApplyDisplay(WarehouseMachineController ctrl)
		{
			string track = ctrl.warehouseTrackName;

			if (!Sequences.Exists(s => s.TrackName == track))
			{
				RestoreBottomText(ctrl);
				return;
			}

			double nowAbs = lastAbsMinutes ?? 0;
			bool hasClock = lastAbsMinutes.HasValue;
			var lamp = GetLampState(track);

			// --- Titel ---
			var shown = Sequences.Find(s => s.TrackName == track && s.State == SeqState.Active)
				?? Sequences.Find(s => s.TrackName == track && s.State != SeqState.Done)
				?? Sequences.Find(s => s.TrackName == track);

			bool isLoading = shown?.IsLoading ?? true;
			string title;

			switch (lamp)
			{
				case LampState.Error:
					title = Loc.T("title.interrupted");
					break;

				case LampState.Paused:
					double? end = hasClock ? Schedule.BreakEnd(nowAbs) : null;
					title = Loc.T("title.paused", end.HasValue ? Schedule.FormatClock(end.Value) : "--:--");
					break;

				case LampState.Finished:
					// eigener Text: die deutsche Spielübersetzung von "whm/unloading_completed" lautet "ENTLADEN ABGEBROCHEN"
					title = Loc.T(isLoading ? "title.done.load" : "title.done.unload");
					break;

				default:
					title = Loc.Game(isLoading ? "whm/loading_in_progress" : "whm/unloading_in_progress");
					break;
			}

			// --- Tabelle ---
			bool parallel = Main.Settings.ParallelProcessing;
			double parallelFactor = Mathf.Max(1f, Main.Settings.ParallelFactor);
			int runningCount = 0;
			foreach (var s in Sequences)
				if (s.TrackName == track && s.State == SeqState.Active)
					runningCount++;

			double queueWork = 0;		// nacheinander: Arbeit der Jobs davor

			sb.Clear();
			// etwas mehr Zeilenabstand, Trennlinie kleiner und mit eigenem Abstand
			tableText = ctrl.displayText;

			sb.Append("<size=85%><line-height=125%>");

			TableRow(sb, Loc.T("hdr.job"), Loc.T("hdr.cars"), Loc.T("hdr.status"), Loc.T("hdr.eta"));
			sb.Append('=', SeparatorCount()).Append('\n');

			foreach (var seq in Sequences)
			{
				if (seq.TrackName != track)
					continue;

				string job = "[" + seq.JobId + "]";
				string cars = seq.CarsDone + "/" + seq.JobCars;

				switch (seq.State)
				{
					case SeqState.Active:
					case SeqState.Queued:
					{
						bool active = seq.State == SeqState.Active;

						double needed;
						if (parallel)
							needed = seq.RemainingWork * ((active ? runningCount > 1 : runningCount >= 1) ? parallelFactor : 1.0);
						else
							needed = queueWork += seq.RemainingWork;

						double? finish = hasClock ? Schedule.FinishTime(nowAbs, needed) : null;

						string status = !active ? Loc.T("line.queued")
							: lamp == LampState.Paused ? Loc.T("line.break")
							: finish.HasValue ? RealCountdown(seq, finish.Value - nowAbs)
							: "";

						string eta = finish.HasValue ? "~ " + EtaText(nowAbs, finish.Value) : "";
						TableRow(sb, job, cars, status, eta);
						break;
					}

					case SeqState.Interrupted:
						TableRow(sb, job, cars, Loc.T(seq.Reason == InterruptReason.Moving ? "line.moving.short" : "line.missing.short"), "");
						break;

					case SeqState.Done:
						TableRow(sb, job, seq.JobCars + "/" + seq.JobCars, Loc.T("line.done"),
							seq.FinishedAt >= 0 ? Schedule.FormatClock(seq.FinishedAt) : "");
						break;
				}
			}

			sb.Append("</line-height></size>");

			if (ctrl.displayTitleText != null)
				ctrl.displayTitleText.text = title;

			if (ctrl.displayText != null)
				ctrl.displayText.text = sb.ToString();

			// --- Abzüge unten, nach oben aufbauend ---
			ApplyBottomText(ctrl, track);
		}

		// Textfeld, an dem gerade gemessen wird (für rechtsbündige Spalte und Trennlinie)
		private static TMPro.TextMeshPro? tableText;
		private const string TableSize = "<size=85%>";

		// Nutzbare Breite des Textfelds (ohne Ränder)
		private static float TableWidth()
		{
			if (tableText == null)
				return 0f;

			var m = tableText.margin;
			return Mathf.Max(0f, tableText.rectTransform.rect.width - m.x - m.z);
		}

		private static float TextWidth(string s)
		{
			if (tableText == null || s.Length == 0)
				return 0f;

			return tableText.GetPreferredValues(TableSize + s + "</size>").x;
		}

		// Trennlinie in derselben Schriftgröße, exakt bis zum rechten Rand
		// Trennlinie: Anzahl pro Display, wird am tatsächlich Gerenderten nachkalibriert
		private static readonly Dictionary<TMPro.TMP_Text, int> separatorCounts = new();

		private static int SeparatorCount()
		{
			if (tableText == null)
				return 58;

			if (!separatorCounts.TryGetValue(tableText, out int n))
			{
				// Startwert aus der Vorab-Messung
				float width = TableWidth();
				float measured = tableText.GetPreferredValues(TableSize + "==========" + "</size>").x / 10f;
				n = width > 0f && measured > 0f ? Math.Max(1, (int)Math.Floor(width / measured) - 1) : 58;
			}

			int calibrated = CalibrateSeparator(n);
			if (calibrated != n)
				Main.Log($" Separator {n} → {calibrated} '='");

			n = Mathf.Clamp(calibrated, 1, 200);
			separatorCounts[tableText] = n;
			return n;
		}

		// Vergleicht im zuletzt gerenderten Text das Ende der '='-Linie mit dem rechten Ende der Kopfzeile ("FERTIG")
		private static int CalibrateSeparator(int current)
		{
			var info = tableText!.textInfo;
			if (info == null || info.characterCount == 0)
				return current;

			int first = -1, last = -1, lines = 0, prevLine = -1;
			float headerRight = float.MinValue;
			int headerLine = info.characterInfo[0].lineNumber;

			for (int i = 0; i < info.characterCount; i++)
			{
				var c = info.characterInfo[i];

				if (c.lineNumber == headerLine && c.isVisible)
					headerRight = Mathf.Max(headerRight, c.topRight.x);

				if (c.character == '=')
				{
					if (first < 0)
						first = i;
					else if (last != i - 1)
						break;		// zweite '='-Folge → Ende

					if (c.lineNumber != prevLine)
					{
						lines++;
						prevLine = c.lineNumber;
					}
					last = i;
				}
				else if (first >= 0)
					break;
			}

			if (first < 0 || headerRight == float.MinValue)
				return current;

			int count = last - first + 1;
			if (count != current)
				return current;		// Text noch nicht neu gerendert

			if (lines > 1)
				return current - 1;		// umgebrochen → kürzer

			float charW = (info.characterInfo[last].topRight.x - info.characterInfo[first].bottomLeft.x) / count;
			if (charW <= 0f)
				return current;

			float gap = headerRight - info.characterInfo[last].topRight.x;
			int add = (int)Math.Floor(gap / charW);		// negativ = zu lang
			return current + add;
		}

		// Spalten WAGEN / STATUS direkt hinter der längstmöglichen Job-ID (3-Buchstaben-Bahnhof, 3-stellige Nummer)
		private static string Pct(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

		private static void ColumnPositions(out float carsPct, out float statusPct)
		{
			carsPct = 28f;
			statusPct = 42f;

			float width = TableWidth();
			if (width <= 0f)
				return;

			float gap = TextWidth("   ");
			float idW = TextWidth("[MMM-SU-888]");
			float carsW = Mathf.Max(TextWidth(Loc.T("hdr.cars")), TextWidth("88/88"));

			if (idW <= 0f || carsW <= 0f)
				return;

			carsPct = Mathf.Clamp((idW + gap) / width * 100f, 15f, 45f);
			statusPct = Mathf.Clamp(carsPct + (carsW + gap) / width * 100f, carsPct + 8f, 65f);
		}

		// Eine Tabellenzeile: Spalten per <pos>; die rechte Spalte wird vermessen und so gesetzt,
		// dass sie exakt am rechten Rand endet – alles auf derselben Grundlinie.
		private static void TableRow(StringBuilder b, string job, string cars, string status, string right)
		{
			ColumnPositions(out float carsPct, out float statusPct);
			b.Append(job).Append("<pos=").Append(Pct(carsPct)).Append("%>").Append(cars)
				.Append("<pos=").Append(Pct(statusPct)).Append("%>").Append(status);

			if (right.Length > 0)
			{
				float width = TableWidth();
				float textWidth = TextWidth(right);

				if (width > 0f && textWidth > 0f)
				{
					float pct = Mathf.Clamp((width - textWidth) / width * 100f - 0.5f, 0f, 100f);		// 0,5 % Luft gegen Umbruch
					b.Append("<pos=").Append(pct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("%>").Append(right);
				}
				else
				{
					b.Append("<pos=80%>").Append(right);
				}
			}

			b.Append('\n');
		}

		// =========================
		// ZEILEN EINES GLEISES (für Funkgerät-Anzeige; gleiche Rechnung wie die Tabelle am Display)
		// =========================
		public struct TrackRow
		{
			public string Job, Cars, Status, Eta;
			public SeqState State;
		}

		public static List<TrackRow> GetTrackRows(string track)
		{
			var rows = new List<TrackRow>();
			bool hasClock = GameClock.TryGetNowAbsMinutes(out double nowAbs);
			bool onBreak = hasClock && Schedule.RateAt(nowAbs) <= 0;

			bool parallel = Main.Settings.ParallelProcessing;
			double parallelFactor = Mathf.Max(1f, Main.Settings.ParallelFactor);
			int runningCount = 0;
			foreach (var s in Sequences)
				if (s.TrackName == track && s.State == SeqState.Active)
					runningCount++;

			double queueWork = 0;

			foreach (var seq in Sequences)
			{
				if (seq.TrackName != track)
					continue;

				var row = new TrackRow { Job = seq.JobId, Cars = seq.CarsDone + "/" + seq.JobCars, Status = "", Eta = "", State = seq.State };

				switch (seq.State)
				{
					case SeqState.Active:
					case SeqState.Queued:
					{
						bool active = seq.State == SeqState.Active;
						double needed = parallel
							? seq.RemainingWork * ((active ? runningCount > 1 : runningCount >= 1) ? parallelFactor : 1.0)
							: (queueWork += seq.RemainingWork);

						double? finish = hasClock ? Schedule.FinishTime(nowAbs, needed) : null;

						row.Status = !active ? Loc.T("line.queued")
							: onBreak ? Loc.T("line.break")
							: finish.HasValue ? RealCountdown(seq, finish.Value - nowAbs) : "";
						row.Eta = finish.HasValue ? "~ " + EtaText(nowAbs, finish.Value) : "";
						break;
					}

					case SeqState.Interrupted:
						row.Status = Loc.T(seq.Reason == InterruptReason.Moving ? "line.moving.short" : "line.missing.short");
						break;

					case SeqState.Done:
						row.Cars = seq.JobCars + "/" + seq.JobCars;
						row.Status = Loc.T("line.done");
						row.Eta = seq.FinishedAt >= 0 ? Schedule.FormatClock(seq.FinishedAt) : "";
						break;
				}

				rows.Add(row);
			}

			return rows;
		}

		private static string EtaText(double nowAbs, double finishAbs)
		{
			string t = Schedule.FormatClock(finishAbs);
			int days = (int)(Math.Floor(finishAbs / Schedule.Day) - Math.Floor(nowAbs / Schedule.Day));
			return days > 0 ? t + " +" + days : t;
		}

		// Ursprüngliche Ausrichtung des unteren Textfelds (Vanilla: "Zug in Reichweite")
		private static readonly Dictionary<WarehouseMachineController, TMPro.TextAlignmentOptions> bottomAlignments = new();

		private static void ApplyBottomText(WarehouseMachineController ctrl, string track)
		{
			var bottom = ctrl.displayTrainInRangeText;
			if (bottom == null)
				return;

			if (!bottomAlignments.ContainsKey(ctrl))
				bottomAlignments[ctrl] = bottom.alignment;

			var lines = new List<string>();

			foreach (var seq in Sequences)
			{
				if (seq.TrackName != track)
					continue;

				var p = Penalties.Find(seq.JobId);
				string? original = AltTracks.OriginalTrackDisplay(seq.JobId);

				var parts = new List<string>();

				if (original != null || (p != null && p.AltPct > 0))
				{
					string from = original ?? p?.AltFrom ?? "?";
					string state = Loc.T(p == null || p.AltOccupied ? "pen.alt.occupied" : "pen.alt.free");
					string text = Loc.T("pen.alt", from, state);
					if (p != null && p.AltPct > 0)
						text += " -" + p.AltPct + " %";
					parts.Add(text);
				}

				if (p != null && p.IdlePct > 0.001f)
					parts.Add(Loc.T("pen.idle", Schedule.FormatDuration(p.IdleMinutes)) + " -" + p.IdleEffective.ToString("0.#") + " %");

				if (parts.Count > 0)
					lines.Add("[" + seq.JobId + "] - " + string.Join(" · ", parts));
			}

			bottom.alignment = TMPro.TextAlignmentOptions.BottomLeft;
			bottom.text = lines.Count > 0 ? "<size=80%>" + string.Join("\n", lines) + "</size>" : string.Empty;

			AlignBottomToTrackLabel(ctrl, bottom);
		}

		// Unterste Zeile auf dieselbe Grundlinie wie das Gleisschild (z. B. "D8L") schieben
		private static readonly Dictionary<WarehouseMachineController, Vector3> bottomPositions = new();

		private static void AlignBottomToTrackLabel(WarehouseMachineController ctrl, TMPro.TextMeshPro bottom)
		{
			var label = ctrl.trackIdText;
			if (label == null || string.IsNullOrEmpty(bottom.text))
				return;

			try
			{
				if (!bottomPositions.TryGetValue(ctrl, out var original))
				{
					original = bottom.transform.localPosition;
					bottomPositions[ctrl] = original;
				}

				bottom.transform.localPosition = original;
				bottom.ForceMeshUpdate();
				label.ForceMeshUpdate();

				var ourInfo = bottom.textInfo;
				var labelInfo = label.textInfo;
				if (ourInfo == null || labelInfo == null || ourInfo.lineCount == 0 || labelInfo.lineCount == 0)
					return;

				float ourBase = ourInfo.lineInfo[ourInfo.lineCount - 1].baseline;
				float labelBase = labelInfo.lineInfo[0].baseline;

				Vector3 ourWorld = bottom.transform.TransformPoint(new Vector3(0f, ourBase, 0f));
				Vector3 labelWorld = label.transform.TransformPoint(new Vector3(0f, labelBase, 0f));

				Vector3 up = bottom.transform.up;
				float delta = Vector3.Dot(labelWorld - ourWorld, up);

				bottom.transform.position += up * delta;
			}
			catch (Exception e)
			{
				Main.Log(" Bottom text alignment failed: " + e.Message);
			}
		}

		private static void RestoreBottomText(WarehouseMachineController ctrl)
		{
			if (ctrl.displayTrainInRangeText != null && bottomAlignments.TryGetValue(ctrl, out var align))
			{
				ctrl.displayTrainInRangeText.alignment = align;
				bottomAlignments.Remove(ctrl);
			}

			if (ctrl.displayTrainInRangeText != null && bottomPositions.TryGetValue(ctrl, out var pos))
			{
				ctrl.displayTrainInRangeText.transform.localPosition = pos;
				bottomPositions.Remove(ctrl);
			}
		}

		// Echtzeit-Countdown = Rest-Spielzeit (inkl. Schichtfaktoren und Pausen) ÷ Uhrgeschwindigkeit.
		// Direkt aus der geglätteten Spieluhr gerechnet → läuft 1 s pro Echtsekunde, kein lokales Nachführen mehr.
		private static string RealCountdown(LoadSequence seq, double remainingGameMinutes)
		{
			var s = Main.Settings;
			if (!s.HasClockRate)
				return Schedule.FormatDuration(remainingGameMinutes);		// Rate unbekannt → Spielzeit

			float rate = s.EffectiveClockRate;
			if (rate <= 0.0001f)
				return "--:--:--";		// Uhr angehalten → Laden pausiert

			double realMinutes = remainingGameMinutes / rate;
			return Schedule.FormatDurationSeconds(Math.Max(0.0, realMinutes));
		}

		// "~02:22" bzw. "~02:22 +1" wenn das Ende am nächsten Tag liegt
		private static void AppendEta(StringBuilder b, double nowAbs, double finishAbs)
		{
			b.Append('~').Append(Schedule.FormatClock(finishAbs));

			int days = (int)(Math.Floor(finishAbs / Schedule.Day) - Math.Floor(nowAbs / Schedule.Day));
			if (days > 0)
				b.Append(" +").Append(days);
		}

		private static void ShowVanilla(WarehouseMachineController ctrl, WarehouseMachineController.TextPreset preset,
			bool isLoading = false, string? jobId = null, string? extra = null, Car? car = null, object? cargoV2 = null)
		{
			if (setScreenMethod == null || ctrl == null)
				return;

			try
			{
				setScreenMethod.Invoke(ctrl, new object?[] { preset, isLoading, jobId, car, cargoV2, extra });
			}
			catch (Exception e)
			{
				Main.Log(" SetScreen failed: " + e.Message);
			}
		}

		// =========================
		// JOB-ZEITLIMIT
		// =========================
		// JobsManager.Time läuft in Echtzeit → Ladedauer (Spielzeit) über gemessene Uhrgeschwindigkeit umrechnen.
		public static float ExtraTimeLimitSeconds(int cars)
		{
			var s = Main.Settings;

			// Bewusst großzügig (Worst Case): langsamster Faktor + längste Pause komplett,
			// damit keine Pause den Bonus ruiniert.
			double realSeconds = (cars * s.CarDelayGameSeconds * Schedule.WorstFactor
				+ Schedule.LongestBreakMinutes * 60.0) / s.EffectiveClockRate;

			return (float)(realSeconds * s.TimeLimitFactor);
		}

		public static int CountWarehouseCars(Job job)
		{
			int count = 0;
			foreach (var wt in EnumerateWarehouseTasks(job))
				count += wt.cars?.Count ?? 0;
			return count;
		}

		// =========================
		// HELFER: TASKS & WAGEN
		// =========================
		private static void GetReadyTasks(WarehouseMachine machine, Job job, WarehouseTaskType type, List<WarehouseTask> result)
		{
			result.Clear();

			var dict = jobToTasksRef(machine);
			if (dict == null || !dict.TryGetValue(job, out var list) || list == null)
				return;

			foreach (var t in list)
				if (t.warehouseTaskType == type && t.readyForMachine && machine.IsCargoSupported(t.cargoType))
					result.Add(t);
		}

		private static bool IsPending(Car car, WarehouseTask task, bool isLoading)
		{
			return isLoading
				? car.CurrentCargoTypeInCar == CargoType.None && car.LoadedCargoAmount == 0f
				: car.CurrentCargoTypeInCar == task.cargoType && car.LoadedCargoAmount > 0f;
		}

		private static int CountPendingCars(List<WarehouseTask> tasks, bool isLoading)
		{
			int n = 0;
			foreach (var t in tasks)
				foreach (var c in t.cars)
					if (IsPending(c, t, isLoading))
						n++;
			return n;
		}

		private static Car? ProcessOneCar(WarehouseMachine machine, List<WarehouseTask> tasks, bool isLoading)
		{
			// Kopie: Load/UnloadOneCarOfTask entfernt fertige Tasks aus der Maschine
			foreach (var task in tasks.ToArray())
			{
				bool hasPending = false;
				foreach (var c in task.cars)
				{
					if (IsPending(c, task, isLoading))
					{
						hasPending = true;
						break;
					}
				}

				if (!hasPending)
					continue;

				return isLoading ? machine.LoadOneCarOfTask(task) : machine.UnloadOneCarOfTask(task);
			}

			return null;
		}

		private static bool AllCarsProcessed(List<Car>? cars, bool isLoading)
		{
			if (cars == null || cars.Count == 0)
				return false;

			foreach (var c in cars)
			{
				bool processed = isLoading ? c.LoadedCargoAmount > 0f : c.LoadedCargoAmount <= 0f;
				if (!processed)
					return false;
			}

			return true;
		}

		private static bool AllCarsOnTrack(List<WarehouseTask> tasks, Track track)
		{
			foreach (var t in tasks)
				foreach (var c in t.cars)
					if (c.CurrentTrack != track)
						return false;
			return true;
		}

		private static bool AnyCarOnTrack(List<Car>? cars, Track? track)
		{
			if (cars == null || track == null)
				return false;

			foreach (var c in cars)
				if (c.CurrentTrack == track || c.FrontBogieTrack == track || c.RearBogieTrack == track)
					return true;

			return false;
		}

		// Abbruch erst, wenn das Problem so lange ununterbrochen besteht
		private const float InterruptAfter = 3f;

		// Für das Debug-Log: welcher Wagen ist das Problem?
		private static string DescribeProblem(List<WarehouseTask> tasks, Track track)
		{
			var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
			var sb = new StringBuilder();

			foreach (var t in tasks)
			{
				foreach (var c in t.cars)
				{
					float speed = -1f;
					if (registry != null && registry.logicCarToTrainCar.TryGetValue(c, out var tc) && tc != null)
						speed = tc.GetAbsSpeed();

					bool onTrack = c.CurrentTrack == track;
					if (!onTrack || speed > 0.3f)
						sb.Append(c.ID).Append(" track=").Append(c.CurrentTrack?.ID?.FullID ?? "null")
						  .Append(" speed=").Append(speed.ToString("0.00", CultureInfo.InvariantCulture))
						  .Append(speed < 0f ? " (no TrainCar)" : "").Append("; ");
				}
			}

			return sb.Length > 0 ? sb.ToString() : "?";
		}

		private static bool AnyCarMoving(List<WarehouseTask> tasks)
		{
			foreach (var t in tasks)
				if (AnyCarMoving(t.cars))
					return true;
			return false;
		}

		private static bool AnyCarMoving(List<Car>? cars)
		{
			if (cars == null)
				return false;

			var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
			if (registry == null)
				return false;

			foreach (var car in cars)
				if (registry.logicCarToTrainCar.TryGetValue(car, out var trainCar) && trainCar != null && trainCar.GetAbsSpeed() > 0.3f)
					return true;

			return false;
		}

		// Alle Wagen des Jobs an dieser Maschine (auch bereits fertige Tasks) + Fallback aus den aktuellen Tasks
		private static List<Car> CollectCars(Job job, WarehouseMachine machine, WarehouseTaskType type, List<WarehouseTask>? fallback)
		{
			var result = new List<Car>();

			if (fallback != null)
				foreach (var t in fallback)
					foreach (var c in t.cars)
						if (!result.Contains(c))
							result.Add(c);

			foreach (var wt in EnumerateWarehouseTasks(job))
			{
				if (wt.warehouseMachine != machine || wt.warehouseTaskType != type || wt.cars == null)
					continue;

				foreach (var c in wt.cars)
					if (!result.Contains(c))
						result.Add(c);
			}

			return result;
		}

		// Läuft rekursiv durch die Task-Struktur des Jobs (Sequential/Parallel sind Container mit Task-Listen)
		private static readonly Dictionary<Type, FieldInfo[]> enumerableFields = new();

		public static IEnumerable<WarehouseTask> EnumerateWarehouseTasks(Job job)
		{
			return EnumerateAllTasks(job).OfType<WarehouseTask>().ToList();
		}

		// Alle Tasks eines Jobs (Container + Blätter)
		public static List<Task> EnumerateAllTasks(Job job)
		{
			var found = new List<Task>();
			var visited = new HashSet<object>();

			if (job?.tasks != null)
				foreach (var t in job.tasks)
					Walk(t, found, visited, 0);

			return found;
		}

		private static void Walk(object? node, List<Task> found, HashSet<object> visited, int depth)
		{
			if (node == null || depth > 12 || !visited.Add(node))
				return;

			if (node is not Task task)
				return;

			found.Add(task);

			if (node is WarehouseTask || node is TransportTask)
				return;		// Blätter

			var type = node.GetType();
			if (!enumerableFields.TryGetValue(type, out var fields))
			{
				fields = AccessTools.GetDeclaredFields(type)
					.Concat(type.BaseType != null ? AccessTools.GetDeclaredFields(type.BaseType) : Enumerable.Empty<FieldInfo>())
					.Where(f => !f.IsStatic && typeof(IEnumerable).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(string))
					.ToArray();
				enumerableFields[type] = fields;
			}

			foreach (var f in fields)
			{
				if (f.GetValue(node) is not IEnumerable items)
					continue;

				foreach (var item in items)
					if (item is Task)
						Walk(item, found, visited, depth + 1);
			}
		}

		// =========================
		// HELFER: SUCHE
		// =========================
		public static WarehouseMachineController? FindController(string track)
		{
			if (controllersByTrack.TryGetValue(track, out var cached) && cached != null)
				return cached;

			foreach (var c in WarehouseMachineController.allControllers)
			{
				if (c != null && c.warehouseTrackName == track)
				{
					controllersByTrack[track] = c;
					return c;
				}
			}

			return null;
		}

		public static Job? FindJobById(string jobId)
		{
			var jm = SingletonBehaviour<JobsManager>.Instance;
			if (jm == null || jm.currentJobs == null || string.IsNullOrEmpty(jobId))
				return null;

			foreach (var j in jm.currentJobs)
				if (j != null && j.ID == jobId)
					return j;

			return null;
		}
	}
}
