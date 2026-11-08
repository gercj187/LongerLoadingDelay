//FILE:		AltTracks.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// ERSATZ-LADEGLEISE
	// =========================
	// Beispiel: Job sagt "Entladen an HB-D7L", D7L ist belegt → Zug auf D8L stellen, Hebel ziehen.
	// Erlaubt: gleicher Bahnhof (yardId), gleicher Bereich (subYardId), Gleistyp "L", Maschine kann die Fracht.
	//   HB-D8L / HB-D9L  ✔      HB-C1L ✘      HBM-D1L ✘
	//
	// Umleitung:
	//   - WarehouseTask.warehouseMachine (readonly) → neue Maschine, Registrierung an alter/neuer Maschine umziehen
	//   - TransportTask.destinationTrack / startingTrack (private) == altes Gleis → neues Gleis
	// Der Spielstand speichert weiter die Original-Maschine (loadMachineId/unloadMachineId) →
	// die Mod merkt sich die Umleitung selbst und wendet sie nach dem Laden erneut an.
	public static class AltTracks
	{
		private static readonly FieldInfo? warehouseMachineField = AccessTools.Field(typeof(WarehouseTask), "warehouseMachine");
		private static readonly FieldInfo? destinationTrackField = AccessTools.Field(typeof(TransportTask), "destinationTrack");
		private static readonly FieldInfo? startingTrackField = AccessTools.Field(typeof(TransportTask), "startingTrack");

		// jobId → (Originalgleis, Ersatzgleis) als warehouseTrackName
		public static readonly Dictionary<string, (string from, string to)> Redirects = new();


		// nach dem Laden noch anzuwenden
		private static readonly Dictionary<string, (string from, string to)> pending = new();
		private static float pendingTime;

		// =========================
		// HEBEL AM ERSATZGLEIS
		// =========================
		public static void TryRedirectTo(WarehouseMachineController ctrl, bool isLoading)
		{
			if (!Main.Settings.AltTracksAllowed)
				return;

			var target = ctrl.warehouseMachine;
			if (target == null || warehouseMachineField == null)
				return;

			var targetTrack = target.WarehouseTrack;
			var carsHere = targetTrack.GetCarsFullyOnTrack();
			if (carsHere == null || carsHere.Count == 0)
				return;

			var jm = SingletonBehaviour<JobsManager>.Instance;
			if (jm == null || jm.currentJobs == null)
				return;

			var type = isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading;

			foreach (var job in jm.currentJobs.ToList())
			{
				if (job == null || job.State != JobState.InProgress)
					continue;

				foreach (var wt in LoadingService.EnumerateWarehouseTasks(job))
				{
					var original = wt.warehouseMachine;

					if (wt.warehouseTaskType != type || original == null || original == target)
						continue;

					// Aufgabe noch offen? (offene Tasks sind an der Maschine registriert)
					if (!IsRegistered(original, job, wt))
						continue;

					// Alle Wagen dieser Aufgabe stehen hier?
					if (wt.cars == null || wt.cars.Count == 0 || !wt.cars.All(c => c.CurrentTrack == targetTrack))
						continue;

					if (!IsSibling(original.WarehouseTrack, targetTrack))
					{
						Main.Log($" ALT [{job.ID}] : {Display(original)} → {Display(target)} not allowed (other yard/group)");
						continue;
					}

					if (!target.IsCargoSupported(wt.cargoType))
					{
						Main.Log($" ALT [{job.ID}] : {Display(target)} does not support {wt.cargoType}");
						continue;
					}

					var originalCtrl = FindControllerFor(original);
					if (originalCtrl == null)
						continue;

					// Abzug abhängig vom Originalgleis: belegt/beschäftigt → kleiner, frei → größer
					bool occupied = IsOccupied(original);

					Redirect(job, original, target);
					Redirects[job.ID] = (originalCtrl.warehouseTrackName, ctrl.warehouseTrackName);

					var s = Main.Settings;
					int pct = !s.AltTrackPenalty ? 0 : occupied ? s.AltTrackPenaltyOccupied : s.AltTrackPenaltyFree;

					if (pct > 0)
						Penalties.SetAlt(job.ID, pct, occupied, original.WarehouseTrack.ID.TrackPartOnly);

					Main.Log($" ALT [{job.ID}] : original {(occupied ? "occupied" : "free")} → penalty {pct} %");
					break;		// pro Job reicht eine Umleitung (alle Tasks der Maschine werden mitgenommen)
				}
			}
		}

		// =========================
		// UMLEITEN
		// =========================
		private static void Redirect(Job job, WarehouseMachine from, WarehouseMachine to)
		{
			if (warehouseMachineField == null)
				return;

			var fromTrack = from.WarehouseTrack;
			var toTrack = to.WarehouseTrack;

			foreach (var task in LoadingService.EnumerateAllTasks(job))
			{
				if (task is WarehouseTask wt && wt.warehouseMachine == from)
				{
					bool registered = IsRegistered(from, job, wt);

					if (registered)
						from.RemoveWarehouseTask(wt);

					warehouseMachineField.SetValue(wt, to);

					if (registered)
						to.AddWarehouseTask(wt);
				}
				else if (task is TransportTask tt)
				{
					if (destinationTrackField != null && destinationTrackField.GetValue(tt) == fromTrack)
						destinationTrackField.SetValue(tt, toTrack);

					if (startingTrackField != null && startingTrackField.GetValue(tt) == fromTrack)
						startingTrackField.SetValue(tt, toTrack);
				}
			}

			// Task-Zustände neu bewerten → Fahr-Aufgabe "nach D8L" ist sofort erledigt, Lade-Aufgabe wird bereit
			foreach (var t in job.tasks)
			{
				try { t.UpdateTaskState(); }
				catch (Exception e) { Main.Log($" ALT [{job.ID}] : UpdateTaskState failed: {e.Message}"); }
			}

			Main.Log($" ALT [{job.ID}] : REDIRECTED {Display(from)} → {Display(to)}");
		}

		// =========================
		// NACH DEM LADEN / AUFRÄUMEN
		// =========================
		public static void Tick(float dt)
		{
			if (pending.Count > 0)
			{
				pendingTime += dt;

				foreach (var kvp in pending.ToList())
				{
					var job = LoadingService.FindJobById(kvp.Key);
					var fromCtrl = LoadingService.FindController(kvp.Value.from);
					var toCtrl = LoadingService.FindController(kvp.Value.to);

					if (job != null && fromCtrl != null && toCtrl != null
						&& fromCtrl.warehouseMachine != null && toCtrl.warehouseMachine != null)
					{
						Redirect(job, fromCtrl.warehouseMachine, toCtrl.warehouseMachine);
						Redirects[kvp.Key] = kvp.Value;
						pending.Remove(kvp.Key);
					}
					else if (pendingTime > 15f)
					{
						pending.Remove(kvp.Key);		// Job gibt es nicht mehr
					}
				}
			}

			// beendete Jobs vergessen
			foreach (var id in Redirects.Keys.ToList())
			{
				var job = LoadingService.FindJobById(id);
				if (job == null || job.State != JobState.InProgress)
					Redirects.Remove(id);
			}
		}

		// Umleitung zurücknehmen (Zug wurde vor Beginn wieder abgezogen) → auch kein Abzug mehr
		public static void Revert(string jobId)
		{
			if (!Redirects.TryGetValue(jobId, out var r))
				return;

			var job = LoadingService.FindJobById(jobId);
			var fromCtrl = LoadingService.FindController(r.from);
			var toCtrl = LoadingService.FindController(r.to);

			if (job != null && fromCtrl != null && toCtrl != null
				&& fromCtrl.warehouseMachine != null && toCtrl.warehouseMachine != null)
			{
				Redirect(job, toCtrl.warehouseMachine, fromCtrl.warehouseMachine);
			}

			Redirects.Remove(jobId);
			Penalties.ClearAlt(jobId);

			Main.Log($" ALT [{jobId}] : REVERTED (train left replacement track before start)");
		}

		public static string? OriginalTrackDisplay(string jobId)
		{
			if (!Redirects.TryGetValue(jobId, out var r))
				return null;

			var ctrl = LoadingService.FindController(r.from);
			return ctrl != null && ctrl.warehouseMachine != null ? ctrl.warehouseMachine.WarehouseTrack.ID.TrackPartOnly : null;
		}

		// =========================
		// SPEICHERN / LADEN
		// =========================
		public static JArray Write()
		{
			var arr = new JArray();
			foreach (var kvp in Redirects)
				arr.Add(new JObject { ["job"] = kvp.Key, ["from"] = kvp.Value.from, ["to"] = kvp.Value.to });
			return arr;
		}

		public static void Read(JArray? arr)
		{
			Reset();
			if (arr == null)
				return;

			foreach (var t in arr)
			{
				if (t is not JObject o)
					continue;

				string job = o["job"]?.ToString() ?? "";
				string from = o["from"]?.ToString() ?? "";
				string to = o["to"]?.ToString() ?? "";

				if (job.Length > 0 && from.Length > 0 && to.Length > 0)
					pending[job] = (from, to);
			}

			Main.Log($" ALT : {pending.Count} redirects to restore");
		}

		public static void Reset()
		{
			Redirects.Clear();
			pending.Clear();
			pendingTime = 0f;
		}

		// =========================
		// HELFER
		// =========================
		private static bool IsRegistered(WarehouseMachine machine, Job job, WarehouseTask wt)
		{
			var dict = LoadingService.jobToTasksRef(machine);
			return dict != null && dict.TryGetValue(job, out var list) && list != null && list.Contains(wt);
		}

		// FullID = "yard-subYard-order-type", z. B. "HB-D-07-L"
		private static bool IsSibling(Track a, Track b)
		{
			var pa = a.ID.FullID.Split('-');
			var pb = b.ID.FullID.Split('-');

			if (pa.Length < 4 || pb.Length < 4)
				return false;

			return pa[0] == pb[0]			// gleicher Bahnhof
				&& pa[1] == pb[1]			// gleicher Bereich (D)
				&& pa[3] == "L" && pb[3] == "L"
				&& pa[2] != pb[2];
		}

		private static bool IsOccupied(WarehouseMachine machine)
		{
			if (!machine.WarehouseTrack.IsFree())
				return true;

			foreach (var s in LoadingService.Sequences)
				if (s.Machine == machine && s.IsOperating)
					return true;

			return false;
		}

		private static WarehouseMachineController? FindControllerFor(WarehouseMachine machine)
		{
			foreach (var c in WarehouseMachineController.allControllers)
				if (c != null && c.warehouseMachine == machine)
					return c;
			return null;
		}

		private static string Display(WarehouseMachine m) => m.WarehouseTrack.ID.FullDisplayID;
	}
}
