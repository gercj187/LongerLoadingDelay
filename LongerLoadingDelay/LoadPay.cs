//FILE:		LoadPay.cs

using DV.Logic.Job;
using DV.ThingTypes;

namespace LongerLoadingDelay
{
	// =========================
	// LOHNAUSGLEICH FÜR DIE LADEZEIT
	// =========================
	// Rangierjobs dauern mit der Mod Stunden statt Minuten → Grundlohn (und damit auch der Bonus,
	// der im Spiel = Grundlohn × 0.5 ist) wächst mit der ERWARTETEN Ladezeit:
	//   Faktor = 1 + Spielstunden × PayPerLoadHourPct / 100
	// Erwartet = Wagen × Zeit pro Wagen × Durchschnittsfaktor des Schichtplans (inkl. Pausen).
	// Bewusst unabhängig von der Uhrzeit, zu der der Job genommen wird → derselbe Job kostet immer gleich viel.
	// Wird nicht gespeichert, sondern bei jedem Aufruf berechnet → kein Aufsummieren beim Laden.
	public static class LoadPay
	{
		public static double ExpectedGameHours(int cars)
		{
			if (cars <= 0)
				return 0.0;

			var s = Main.Settings;
			return cars * s.CarDelayGameMinutes * Schedule.AverageDurationMultiplier / 60.0;
		}

		public static float FactorForCars(int cars)
		{
			var s = Main.Settings;
			if (!s.PayScaling || cars <= 0)
				return 1f;

			return 1f + (float)(ExpectedGameHours(cars) * s.PayPerLoadHourPct / 100.0);
		}

		public static float Factor(Job job)
		{
			if (!Main.Enabled || job == null)
				return 1f;

			if (job.jobType != JobType.ShuntingLoad && job.jobType != JobType.ShuntingUnload)
				return 1f;

			return FactorForCars(LoadingService.CountWarehouseCars(job));
		}
	}
}
