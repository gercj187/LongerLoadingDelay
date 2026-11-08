//FILE:		MachineClock.cs

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// UHR IN DER KOPFZEILE DER LADEMASCHINE
	// =========================
	// Titelzeile links (z. B. "LOADING..." / "BREAK UNTIL 06:07"), Spieluhrzeit rechtsbündig in derselben Zeile.
	// TMP-Trick: <line-height=0> vor dem Umbruch → die zweite Zeile liegt genau auf der ersten.
	public static class MachineClock
	{
		// Titel ohne Uhr, pro Maschine (wird bei jedem UpdateScreen neu gemerkt)
		private static readonly Dictionary<WarehouseMachineController, string> baseTitles = new();
		private static int lastMinute = -1;

		// Aus dem UpdateScreen-Postfix (nach ApplyDisplay)
		public static void Apply(WarehouseMachineController ctrl)
		{
			var title = ctrl.displayTitleText;
			if (title == null)
				return;

			string text = title.text ?? "";
			baseTitles[ctrl] = text;

			if (Main.Settings.MachineClock)
				title.text = text + Suffix();
		}

		// Aus dem Tick: nur beim Minutenwechsel alle sichtbaren Maschinen aktualisieren
		public static void Tick(double nowAbs)
		{
			int minute = (int)Schedule.Mod(nowAbs);
			if (minute == lastMinute)
				return;

			lastMinute = minute;
			Refresh();
		}

		public static void Refresh()
		{
			string suffix = Main.Settings.MachineClock ? Suffix() : "";

			foreach (var kvp in baseTitles)
			{
				var ctrl = kvp.Key;
				if (ctrl == null || !ctrl.isActiveAndEnabled || ctrl.displayTitleText == null)
					continue;

				ctrl.displayTitleText.text = kvp.Value + suffix;
			}

			// zerstörte Controller aufräumen (Unity-"null": Objekt existiert noch, ist aber zerstört)
			foreach (var c in baseTitles.Keys.Where(k => k == null).ToList())
				baseTitles.Remove(c);
		}

		private static string Suffix()
		{
			string time = GameClock.TryGetNowAbsMinutes(out double now) ? GameClock.FormatTimeOfDay(now) : "--:--";
			return "<line-height=0>\n<align=right>" + time + "</align>";
		}

		public static void Reset()
		{
			baseTitles.Clear();
			lastMinute = -1;
		}
	}
}
