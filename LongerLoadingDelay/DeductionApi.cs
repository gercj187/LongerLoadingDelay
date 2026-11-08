//FILE:		DeductionApi.cs

using System;

namespace LongerLoadingDelay
{
	// =========================
	// ÖFFENTLICHE SCHNITTSTELLE FÜR ANDERE MODS
	// =========================
	// Andere Mods können prozentuale Lohnabzüge für einen Job eintragen.
	// LongerLoadingDelay
	//   - kürzt damit Lohn UND Zeitbonus (über Job.GetBasePaymentForTheJob),
	//   - zeigt den Abzug im Auftragsbericht als eigene Zeile,
	//   - speichert ihn im Spielstand und räumt ihn nach Abschluss des Jobs auf.
	//
	// Signaturen bleiben stabil; Änderungen nur mit neuer ApiVersion.
	public static class DeductionApi
	{
		public const int ApiVersion = 1;

		/// <summary>LongerLoadingDelay ist geladen UND aktiv (nur dann werden Abzüge angewendet).</summary>
		public static bool IsActive => Main.Enabled;

		/// <summary>
		/// Abzug setzen oder ersetzen.
		/// jobId   = Job.ID (z. B. "HB-SL-98")
		/// source  = eindeutiger Schlüssel deiner Mod (z. B. "MyMod.Damage"); gleicher Schlüssel überschreibt
		/// percent = 0..100, Prozent von Lohn + Bonus (mehrere Abzüge werden addiert, gesamt max. 100 %)
		/// label   = Text im Bericht (bereits übersetzt), z. B. "Abzug Ladungsschaden"
		/// Rückgabe: false, wenn LongerLoadingDelay deaktiviert ist (dann wird nichts abgezogen).
		/// </summary>
		public static bool SetPercent(string jobId, string source, float percent, string label)
		{
			if (string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(source))
				return false;

			if (percent <= 0f)
			{
				Penalties.RemoveExternal(jobId, source);
				return Main.Enabled;
			}

			Penalties.SetExternal(jobId, source, percent, label);
			Main.Log($" API [{jobId}] : {source} -{percent:0.##} % \"{label}\"");
			return Main.Enabled;
		}

		/// <summary>Abzug dieser Quelle entfernen.</summary>
		public static void Remove(string jobId, string source)
		{
			if (!string.IsNullOrEmpty(jobId) && !string.IsNullOrEmpty(source))
				Penalties.RemoveExternal(jobId, source);
		}

		/// <summary>Summe aller Abzüge dieses Jobs in Prozent (alle Quellen, inkl. Ersatzgleis/Standgeld).</summary>
		public static float GetTotalPercent(string jobId)
		{
			var p = string.IsNullOrEmpty(jobId) ? null : Penalties.Find(jobId);
			return p?.TotalPct ?? 0f;
		}
	}
}
