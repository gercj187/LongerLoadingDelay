//FILE:		LoadStatusRadio.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DV;
using DV.CabControls;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using HarmonyLib;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// FUNKGERÄT: EIGENER MODUS "LADESTATUS"
	// =========================
	// Liste ALLER laufenden Ladevorgänge:   Gleis -> Job        Fertig-Uhrzeit
	//   z. B.  CP-D7L -> CP-SU-21      FERTIG
	//          HB-D7L -> HB-SU-66     ~ 06:30
	// Werte wie an der Maschine (gleiche Berechnung über LoadingService.GetTrackRows).
	//   Abzug (Trigger) → nächste Seite (wenn mehr Einträge als aufs Display passen)
	//   A / B           → wie immer: vorheriger / nächster Funkgerät-Modus
	// Funktioniert auch beim Multiplayer-Client (Sequenzen kommen vom Host).
	public class LoadStatusRadio : MonoBehaviour, ICommsRadioMode
	{
		private const int LinesPerPage = 6;
		private const float RefreshInterval = 0.5f;
		private static readonly Color LaserColor = new Color(1f, 0.75f, 0.2f);

		internal static LoadStatusRadio? Instance;
		private static CommsRadioController? controller;
		private static readonly FieldInfo? allModesField = AccessTools.Field(typeof(CommsRadioController), "allModes");
		private static readonly FieldInfo? disabledField = AccessTools.Field(typeof(CommsRadioController), "disabledModeIndices");
		private static readonly MethodInfo? setNextMode = AccessTools.Method(typeof(CommsRadioController), "SetNextMode");

		private CommsRadioDisplay? display;
		private ArrowLCD? arrow;
		private Transform? signalOrigin;

		private readonly List<string> lines = new();
		private int page;
		private float nextRefresh;
		private string lastTitle = "", lastContent = "", lastAction = "";
		private readonly StringBuilder sb = new();

		// =========================
		// EINHÄNGEN (CommsRadioController.Awake legt allModes an)
		// =========================
		[HarmonyPatch(typeof(CommsRadioController), "Awake")]
		public static class Awake_Patch
		{
			[HarmonyPostfix]
			static void Postfix(CommsRadioController __instance)
			{
				try
				{
					if (allModesField?.GetValue(__instance) is not List<ICommsRadioMode> modes)
						return;

					var mode = __instance.gameObject.GetComponent<LoadStatusRadio>() ?? __instance.gameObject.AddComponent<LoadStatusRadio>();
					mode.display = __instance.crewVehicleControl != null ? __instance.crewVehicleControl.display : null;
					mode.arrow = __instance.crewVehicleControl != null ? __instance.crewVehicleControl.lcdArrow : null;
					mode.signalOrigin = __instance.transform;

					// vor dem Licht-Modus (Taschenlampe bleibt letzter Eintrag)
					if (!modes.Contains(mode))
					{
						int idx = modes.IndexOf(__instance.commsRadioLight);
						if (idx >= 0) modes.Insert(idx, mode);
						else modes.Add(mode);
					}

					controller = __instance;
					Instance = mode;
				}
				catch (Exception e)
				{
					Debug.LogWarning("[LongerLoadingDelay] Comms radio mode failed: " + e.Message);
				}
			}
		}

		// Modus je nach Einstellung ein-/ausblenden (das Spiel setzt die Liste ab und zu zurück → regelmäßig prüfen)
		internal static void EnforceAvailability()
		{
			if (controller == null || Instance == null)
				return;

			try
			{
				if (allModesField?.GetValue(controller) is not List<ICommsRadioMode> modes)
					return;
				if (disabledField?.GetValue(controller) is not HashSet<int> disabled)
					return;

				int idx = modes.IndexOf(Instance);
				if (idx < 0)
					return;

				bool want = Main.Enabled && Main.Settings.RadioLoadStatus;

				if (want)
				{
					disabled.Remove(idx);
				}
				else if (disabled.Add(idx) && ReferenceEquals(controller.CurrentActiveMode, Instance))
				{
					setNextMode?.Invoke(controller, null);
				}
			}
			catch
			{
				// nicht kritisch
			}
		}

		// =========================
		// ICommsRadioMode
		// =========================
		public ButtonBehaviourType ButtonBehaviour => ButtonBehaviourType.Regular;

		public void Enable()
		{
			page = 0;
			Refresh(force: true);
		}

		public void Disable() { }

		public void OverrideSignalOrigin(Transform transform) => signalOrigin = transform;

		public void OnUse()
		{
			int pages = PageCount();
			if (pages > 1)
			{
				page = (page + 1) % pages;
				if (controller != null && controller.selectionAction != null)
					CommsRadioController.PlayAudioFromRadio(controller.selectionAction, transform);
			}
			Refresh(force: true);
		}

		public void OnUpdate()
		{
			if (Time.unscaledTime >= nextRefresh)
				Refresh(force: false);
		}

		public bool ButtonACustomAction() => false;
		public bool ButtonBCustomAction() => false;

		public void SetStartingDisplay()
		{
			page = 0;
			Refresh(force: true);
		}

		public Color GetLaserBeamColor() => LaserColor;

		// =========================
		// INHALT
		// =========================
		// =========================
		// "MEINE" JOBS (Multiplayer)
		// =========================
		// Im Einzelspieler sind alle Jobs die eigenen. Im Multiplayer: Jobs, deren Auftragsheft der Spieler
		// in der Hand, im Inventar oder im eigenen Lager hat.
		private static readonly HashSet<string> myJobs = new();
		private static readonly HashSet<string> seenJobs = new();
		private static float nextOwnScan;

		private static bool FilterMine => !Main.Settings.RadioShowAllJobs && (Mp.IsHost || Mp.IsClient);

		private static void ScanMyJobs()
		{
			if (Time.unscaledTime < nextOwnScan)
				return;
			nextOwnScan = Time.unscaledTime + 2f;

			myJobs.Clear();
			seenJobs.Clear();

			try
			{
				// FindObjectsOfType findet nur AKTIVE Objekte – Hefte im Inventar sind deaktiviert!
				// Daher alle geladenen Hefte (auch inaktive), nur Szenen-Objekte (keine Prefabs)
				foreach (var booklet in Resources.FindObjectsOfTypeAll<JobBooklet>())
				{
					if (booklet == null || booklet.job == null || !booklet.gameObject.scene.IsValid())
						continue;

					seenJobs.Add(booklet.job.ID);

					var item = booklet.GetComponent<ItemBase>();
					if (item == null)
						continue;

					bool mine = item.IsGrabbed()
						|| (Inventory.Instance != null && Inventory.Instance.Contains(booklet.gameObject, false))
						|| (StorageController.Instance != null && StorageController.Instance.StorageInventory != null
							&& StorageController.Instance.StorageInventory.ContainsItem(item));

					if (mine)
						myJobs.Add(booklet.job.ID);
				}
			}
			catch (Exception e)
			{
				Main.Log(" Radio: job owner scan failed: " + e.Message);
			}

			if (Main.DebugOn)
				Main.Log($" Radio: booklets found {seenJobs.Count}, mine {myJobs.Count} [{string.Join(", ", myJobs)}]");
		}

		private int PageCount() => Math.Max(1, (lines.Count + LinesPerPage - 1) / LinesPerPage);

		// Eine Zeile pro Ladevorgang, sortiert nach Gleis
		private void CollectLines()
		{
			lines.Clear();

			bool filterMine = FilterMine;
			if (filterMine)
				ScanMyJobs();

			var tracks = LoadingService.Sequences.Select(q => q.TrackName).Distinct().OrderBy(t => t, StringComparer.Ordinal);

			foreach (string track in tracks)
			{
				string label = ShortTrack(track);

				foreach (var row in LoadingService.GetTrackRows(track))
				{
					// ausblenden nur, wenn das Heft gefunden wurde und NICHT beim Spieler ist
					// (Jobs ohne auffindbares Heft bleiben sichtbar → Liste nie fälschlich leer)
					if (filterMine && seenJobs.Contains(row.Job) && !myJobs.Contains(row.Job))
						continue;

					string right = row.State switch
					{
						SeqState.Done => row.Status,							// FERTIG
						SeqState.Interrupted => "STOP",
						_ => row.Eta.Length > 0 ? row.Eta : row.Status			// ~ 06:30
					};

					// links "GF-B1L -> GF-SL-43", rechts bündig die Zeit (gleiche Zeile über line-height=0)
					lines.Add(label + " -> " + row.Job + "<line-height=0>\n<align=right>" + right + "</align><line-height=125%>");
				}
			}
		}

		private void Refresh(bool force)
		{
			nextRefresh = Time.unscaledTime + RefreshInterval;

			CollectLines();

			int pages = PageCount();
			if (page >= pages)
				page = 0;

			string title = Loc.T("radio.title");
			// klein, damit eine 6. Zeile Platz hat; Seite steht dezent dahinter
			string action = pages > 1 ? "<size=65%>" + Loc.T("radio.next") + "  " + (page + 1) + "/" + pages + "</size>" : "";
			string content;

			if (lines.Count == 0)
			{
				content = Loc.T("radio.none");
			}
			else
			{
				sb.Clear();
				sb.Append("<size=90%><line-height=120%>");
				int start = page * LinesPerPage;
				for (int i = start; i < lines.Count && i < start + LinesPerPage; i++)
					sb.Append(lines[i]).Append('\n');
				sb.Append("</line-height></size>");
				content = sb.ToString();
			}

			if (!force && title == lastTitle && content == lastContent && action == lastAction)
				return;

			lastTitle = title;
			lastContent = content;
			lastAction = action;

			display?.SetDisplay(title, content, action);
			arrow?.TurnOff();
		}

		// Gleisname kurz: Bahnhofskürzel-Gleis ohne Bindestriche/führende Null
		//   "[Y]_[GF]_[B-01-L]" → "GF-B1L"     "HB-D-07-L" → "HB-D7L"     "...-10-L" → "…10L"
		internal static string ShortTrack(string full)
		{
			if (string.IsNullOrEmpty(full))
				return "";

			string station, part;

			if (full.Contains("["))
			{
				// Teile in eckigen Klammern: [Typ]_[Bahnhof]_[Gleis]
				var groups = new List<string>();
				foreach (var g in full.Split('_'))
				{
					string t = g.Trim().TrimStart('[').TrimEnd(']');
					if (t.Length > 0)
						groups.Add(t);
				}

				if (groups.Count < 2)
					return full.Replace("[", "").Replace("]", "").Replace("_", "-");

				station = groups[groups.Count - 2];
				part = groups[groups.Count - 1];
			}
			else
			{
				// "HB-D-07-L"
				var p = full.Split('-');
				if (p.Length < 4)
					return full;

				station = p[0];
				part = string.Join("-", p, 1, p.Length - 1);
			}

			return station + "-" + CompactPart(part);
		}

		// "B-01-L" → "B1L", "D-10-L" → "D10L"
		private static string CompactPart(string part)
		{
			var p = part.Split('-');
			var sb = new StringBuilder();
			foreach (var x in p)
			{
				if (int.TryParse(x, out int n))
					sb.Append(n);
				else
					sb.Append(x);
			}
			return sb.ToString();
		}
	}
}
