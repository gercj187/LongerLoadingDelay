//FILE:		Schedule.cs

using System;
using System.Collections.Generic;
using System.Reflection;
using DV.TimeKeeping;
using HarmonyLib;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// SPIELUHR
	// =========================
	// Quelle: WorldClockController → WeatherPresetManager.DateTime (mit Datum, also fortlaufend).
	// Schlaf (max. 10 h) und Fast Travel sind einfach große Deltas.
	public static class GameClock
	{
		private static WorldClockController? clock;
		private static float nextSearch;

		// Geglättete Zeit: Falls die Spieluhr nur minutengenau ist, wird zwischen zwei
		// Minutenwechseln mit der gemessenen Uhrgeschwindigkeit weitergezählt (max. 1 min Vorlauf).
		private static double? smoothed;
		private static double lastRaw = double.NaN;
		private static float lastScaledTime;

		// Ungeglätteter Wert (für die Messung der Uhrgeschwindigkeit)
		public static double LastRawAbsMinutes { get; private set; }

		// =========================
		// EXAKTE UHRGESCHWINDIGKEIT
		// =========================
		// Der Himmel nutzt das Asset "Time of Day" (Shader "Time of Day/…" im Inspektor-Log).
		// Dessen Komponente TOD_Time hat DayLengthInMinutes (Echtzeit-Minuten pro Spieltag) und ProgressTime.
		// → Rate = 1440 / DayLengthInMinutes, angehaltene Uhr = 0. Per Reflection, damit keine DLL-Abhängigkeit nötig ist.
		private static Type? todTimeType;
		private static bool todTypeSearched;
		private static UnityEngine.Object? todTime;
		private static MemberInfo? dayLengthMember, progressMember;
		private static float nextTodSearch;
		private static bool todLogged;

		// =========================
		// MULTIPLAYER: Uhr des Hosts (Client rechnet mit exakt derselben Zeit)
		// =========================
		private static bool hostTime;
		private static double hostAbs;
		private static float hostRate = 1f;
		private static float hostAt;

		public static void SetHostTime(double absMinutes, float rate)
		{
			hostTime = true;
			hostAbs = absMinutes;
			hostRate = rate > 0f ? rate : 0f;
			hostAt = Time.unscaledTime;
		}

		public static void ClearHostTime()
		{
			hostTime = false;
			smoothed = null;
		}

		public static float? ExactRate
		{
			get
			{
				if (hostTime)
					return hostRate;

				if (!todTypeSearched)
				{
					todTypeSearched = true;
					todTimeType = AccessTools.TypeByName("TOD_Time");

					if (todTimeType != null)
					{
						dayLengthMember = (MemberInfo?)AccessTools.Field(todTimeType, "DayLengthInMinutes") ?? AccessTools.Property(todTimeType, "DayLengthInMinutes");
						progressMember = (MemberInfo?)AccessTools.Field(todTimeType, "ProgressTime") ?? AccessTools.Property(todTimeType, "ProgressTime");
					}

					Main.Log($" Clock: TOD_Time {(todTimeType != null ? "found" : "NOT found")}, DayLengthInMinutes {(dayLengthMember != null ? "found" : "NOT found")}");
				}

				if (todTimeType == null || dayLengthMember == null)
					return null;

				if (todTime == null && Time.unscaledTime >= nextTodSearch)
				{
					nextTodSearch = Time.unscaledTime + 5f;
					todTime = UnityEngine.Object.FindObjectOfType(todTimeType);
				}

				if (todTime == null)
					return null;

				try
				{
					if (progressMember != null && GetValue(progressMember, todTime) is bool progress && !progress)
						return 0f;		// Uhr angehalten

					// TOD_Time.FixedUpdate: Zeit läuft nicht, wenn sky.Cycle.Hour überschrieben ist (Fotomodus, Zeit-Lock)
					if (IsHourOverridden(todTime))
						return 0f;

					// DayLengthInMinutes ist ein OverridableValue<float> (kein float) → Wert herauslösen
					float? dayLength = ToFloat(GetValue(dayLengthMember, todTime));

					if (dayLength.HasValue && dayLength.Value > 0.0001f)
					{
						float rate = 1440f / dayLength.Value;

						if (!todLogged || Mathf.Abs(rate - lastLoggedRate) > 0.01f)
						{
							todLogged = true;
							lastLoggedRate = rate;
							Main.Log($" Clock: exact rate {rate:F3} (day length {dayLength.Value:F1} real min)");
						}

						return rate;
					}
				}
				catch (Exception e)
				{
					Main.Log(" Clock: reading TOD_Time failed: " + e.Message);
					dayLengthMember = null;
				}

				return null;
			}
		}

		private static float lastLoggedRate;
		private static FieldInfo? skyField;
		private static bool skyFieldSearched;

		// float, oder ein Wrapper wie OverridableValue<float> (impliziter Operator / CurrentValue / Value)
		private static float? ToFloat(object? v)
		{
			switch (v)
			{
				case null: return null;
				case float f: return f;
				case double d: return (float)d;
			}

			var t = v.GetType();

			foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
			{
				if (m.Name == "op_Implicit" && m.ReturnType == typeof(float) && m.GetParameters().Length == 1)
					return (float)m.Invoke(null, new[] { v });
			}

			foreach (string name in new[] { "CurrentValue", "Value", "RealValue" })
			{
				var member = (MemberInfo?)AccessTools.Property(t, name) ?? AccessTools.Field(t, name);
				if (member != null && GetValue(member, v) is float f2)
					return f2;
			}

			return null;
		}

		// sky (TOD_Sky) → Cycle (TOD_CycleParameters) → Hour (OverridableValue<float>) → IsOverridden
		private static bool IsHourOverridden(object todTimeObj)
		{
			try
			{
				if (!skyFieldSearched)
				{
					skyFieldSearched = true;
					skyField = todTimeType != null ? AccessTools.Field(todTimeType, "sky") : null;
				}

				var sky = skyField?.GetValue(todTimeObj);
				if (sky == null)
					return false;

				var cycleMember = (MemberInfo?)AccessTools.Field(sky.GetType(), "Cycle") ?? AccessTools.Property(sky.GetType(), "Cycle");
				var cycle = cycleMember != null ? GetValue(cycleMember, sky) : null;
				if (cycle == null)
					return false;

				var hourMember = (MemberInfo?)AccessTools.Field(cycle.GetType(), "Hour") ?? AccessTools.Property(cycle.GetType(), "Hour");
				var hour = hourMember != null ? GetValue(hourMember, cycle) : null;
				if (hour == null)
					return false;

				var ovMember = (MemberInfo?)AccessTools.Property(hour.GetType(), "IsOverridden") ?? AccessTools.Field(hour.GetType(), "IsOverridden");
				return ovMember != null && GetValue(ovMember, hour) is bool b && b;
			}
			catch
			{
				return false;
			}
		}

		private static object? GetValue(MemberInfo m, object target) => m switch
		{
			FieldInfo f => f.GetValue(target),
			PropertyInfo p => p.GetValue(target, null),
			_ => null
		};

		public static bool TryGetNowAbsMinutes(out double absMinutes)
		{
			absMinutes = 0;

			if (hostTime)
			{
				// zwischen zwei Paketen (1×/s) mit der Rate des Hosts weiterzählen, höchstens 5 s
				float since = Mathf.Min(5f, Mathf.Max(0f, Time.unscaledTime - hostAt));
				absMinutes = hostAbs + since * hostRate / 60.0;
				LastRawAbsMinutes = absMinutes;
				return true;
			}

			// FindObjectOfType ist teuer → höchstens alle 2 s suchen
			if (clock == null && Time.unscaledTime >= nextSearch)
			{
				nextSearch = Time.unscaledTime + 2f;
				clock = UnityEngine.Object.FindObjectOfType<WorldClockController>();
			}

			if (clock == null)
				return false;

			var t = clock.GetCurrentAnglesAndTimeOfDay();

			if (!t.validTime)
				return false;

			double raw = ToAbsMinutes(t.timeOfDay);
			LastRawAbsMinutes = raw;

			float now = Time.time;			// skaliert → steht in der Pause
			float dt = Mathf.Max(0f, now - lastScaledTime);
			lastScaledTime = now;

			if (smoothed == null || raw < smoothed.Value - 1.0 || raw > smoothed.Value + 1.5)
			{
				smoothed = raw;					// Start, Laden, Schlaf, Fast Travel
			}
			else if (raw != lastRaw)
			{
				smoothed = Math.Max(raw, smoothed.Value);
			}
			else
			{
				double step = dt * Main.Settings.EffectiveClockRate / 60.0;
				smoothed = Math.Min(smoothed.Value + step, raw + 1.0);
			}

			lastRaw = raw;
			absMinutes = smoothed.Value;
			return true;
		}

		// "14:17" bzw. "2:17 PM" (Einstellung 24-h-Format gilt für Funkgerät und Lademaschine)
		public static string FormatTimeOfDay(double absMinutes)
		{
			int m = (int)Schedule.Mod(absMinutes) % 1440;
			int h = m / 60, min = m % 60;

			if (Main.Settings.RadioClock24h)
				return $"{h:00}:{min:00}";

			int h12 = h % 12 == 0 ? 12 : h % 12;
			return $"{h12}:{min:00} {(h < 12 ? "AM" : "PM")}";
		}

		// Minuten seit 01.01.0001 00:00 → "abs mod 1440" ist immer die Uhrzeit
		public static double ToAbsMinutes(DateTime dt) => dt.Ticks / (double)TimeSpan.TicksPerMinute;

		public static void ResetCache()
		{
			clock = null;
			todTime = null;
			nextTodSearch = 0f;
			nextSearch = 0f;
			smoothed = null;
			lastRaw = double.NaN;
		}
	}

	// =========================
	// SCHICHTPLAN
	// =========================
	// Rate = Arbeitssekunden pro Spielsekunde.
	//   Pause → 0, sonst 1 / Faktor (Faktor 0.8 = schneller, 1.5 = langsamer).
	// Integrate() rechnet stückweise über alle Schicht-/Pausengrenzen → exakt auch bei Zeitsprüngen.
	public static class Schedule
	{
		public const double Day = 1440.0;
		private const double Eps = 1e-6;

		public enum SegmentKind { Day, Night, Break, ShiftChange }

		private static readonly List<(double start, double len, bool change)> breaks = new();
		private static double[] boundaries = Array.Empty<double>();
		private static double dayStart, dayLen;
		private static bool enabled, breaksEnabled;
		private static float dayFactor = 1f, nightFactor = 1f;

		public static double Mod(double x)
		{
			x %= Day;
			if (x < 0) x += Day;
			return x;
		}

		private static bool InInterval(double minuteOfDay, double start, double len)
		{
			if (len <= 0) return false;
			if (len >= Day) return true;
			return Mod(minuteOfDay - start) < len;
		}

		// Nach jeder Einstellungsänderung aufrufen
		public static void Rebuild()
		{
			var s = Main.Settings;

			avgMultiplier = null;
			enabled = s.EnableSchedule;
			breaksEnabled = s.EnableSchedule && s.EnableBreaks;
			dayFactor = Mathf.Max(0.05f, s.DayFactor);
			nightFactor = Mathf.Max(0.05f, s.NightFactor);

			dayStart = Mod(s.DayStartMinute);
			dayLen = Mod(s.DayEndMinute - s.DayStartMinute);
			if (dayLen < Eps) dayLen = Day;			// Start == Ende → durchgehend Tagschicht

			double nightLen = Day - dayLen;
			double dayEnd = Mod(dayStart + dayLen);

			breaks.Clear();

			if (breaksEnabled)
			{
				double change = Math.Max(0, s.ShiftChangeBreakMinutes);
				double mid = Math.Max(0, s.MidShiftBreakMinutes);

				// Schichtwechsel: halb vor, halb nach dem Wechsel
				if (change > 0)
				{
					breaks.Add((Mod(dayStart - change / 2), change, true));
					if (nightLen > Eps)
						breaks.Add((Mod(dayEnd - change / 2), change, true));
				}

				// Pause genau in der Mitte jeder Schicht
				if (mid > 0)
				{
					breaks.Add((Mod(dayStart + dayLen / 2 - mid / 2), mid, false));
					if (nightLen > Eps)
						breaks.Add((Mod(dayEnd + nightLen / 2 - mid / 2), mid, false));
				}
			}

			var b = new List<double>();
			if (enabled)
			{
				b.Add(dayStart);
				b.Add(dayEnd);
				foreach (var br in breaks)
				{
					b.Add(Mod(br.start));
					b.Add(Mod(br.start + br.len));
				}
			}

			b.Sort();

			var unique = new List<double>();
			foreach (double x in b)
				if (unique.Count == 0 || x - unique[unique.Count - 1] > Eps)
					unique.Add(x);

			boundaries = unique.ToArray();
		}

		public static bool IsBreak(double minuteOfDay)
		{
			if (!breaksEnabled) return false;

			foreach (var br in breaks)
				if (InInterval(minuteOfDay, br.start, br.len))
					return true;

			return false;
		}

		public static bool IsDayShift(double minuteOfDay) => InInterval(minuteOfDay, dayStart, dayLen);

		public static SegmentKind KindAt(double minuteOfDay)
		{
			double m = Mod(minuteOfDay);

			if (breaksEnabled)
				foreach (var br in breaks)
					if (InInterval(m, br.start, br.len))
						return br.change ? SegmentKind.ShiftChange : SegmentKind.Break;

			return !enabled || IsDayShift(m) ? SegmentKind.Day : SegmentKind.Night;
		}

		// Zusammenhängender Abschnitt gleicher Art um eine Uhrzeit (für die Tooltips der Vorschau)
		public static (SegmentKind kind, double start, double end) SegmentAt(double minuteOfDay)
		{
			const double step = 0.5;
			// Mitte der halben Minute → Abschnittsgrenzen (volle/halbe Minuten) liegen genau zwischen zwei Proben
			double m = Math.Floor(Mod(minuteOfDay) * 2) / 2 + 0.25;
			var kind = KindAt(m);

			double start = m, end = m;
			int guard = 0;

			while (guard++ < Day / step && KindAt(start - step) == kind)
				start -= step;

			guard = 0;
			while (guard++ < Day / step && KindAt(end + step) == kind)
				end += step;

			if (end - start >= Day - step)
				return (kind, 0, Day);			// ganzer Tag gleich

			return (kind, Mod(start - 0.25), Mod(end + 0.25));
		}

		public static double RateAt(double absOrMinuteOfDay)
		{
			if (!enabled) return 1.0;

			double m = Mod(absOrMinuteOfDay);

			if (IsBreak(m)) return 0.0;

			return 1.0 / (IsDayShift(m) ? dayFactor : nightFactor);
		}

		// Nächste Grenze strikt nach t (absolute Minuten)
		private static double NextBoundary(double t)
		{
			double m = Mod(t);
			double baseDay = t - m;

			foreach (double b in boundaries)
				if (b > m + Eps)
					return baseDay + b;

			return baseDay + Day + (boundaries.Length > 0 ? boundaries[0] : 0.0);
		}

		// Arbeitssekunden zwischen zwei absoluten Spielzeitpunkten (Minuten)
		public static double Integrate(double fromAbs, double toAbs)
		{
			if (toAbs <= fromAbs) return 0.0;
			if (!enabled) return (toAbs - fromAbs) * 60.0;

			double work = 0.0, t = fromAbs;
			int guard = 0;

			while (t < toAbs && guard++ < 10000)
			{
				double next = Math.Min(toAbs, NextBoundary(t));
				work += (next - t) * 60.0 * RateAt((t + next) * 0.5);
				t = next;
			}

			return work;
		}

		// Wann ist "work" Arbeitssekunden ab fromAbs erledigt? (absolute Minuten, null = > 7 Tage)
		public static double? FinishTime(double fromAbs, double work)
		{
			if (work <= 0) return fromAbs;
			if (!enabled) return fromAbs + work / 60.0;

			double t = fromAbs, remaining = work;
			double limit = fromAbs + 7 * Day;

			while (t < limit)
			{
				double next = NextBoundary(t);
				double rate = RateAt((t + next) * 0.5);

				if (rate > 0)
				{
					double segWork = (next - t) * 60.0 * rate;
					if (segWork >= remaining)
						return t + remaining / (60.0 * rate);
					remaining -= segWork;
				}

				t = next;
			}

			return null;
		}

		// Ende der aktuellen Pause (absolute Minuten), null wenn gerade keine Pause
		public static double? BreakEnd(double nowAbs)
		{
			if (RateAt(nowAbs) > 0) return null;

			double t = nowAbs;
			for (int i = 0; i < 64; i++)
			{
				double next = NextBoundary(t);
				if (RateAt(next + 0.001) > 0)
					return next;
				t = next;
			}

			return null;
		}

		// Für das Job-Zeitlimit: langsamster Faktor und längste Einzelpause
		public static float WorstFactor => enabled ? Mathf.Max(dayFactor, nightFactor) : 1f;

		// Wie lange dauert 1 Arbeitsminute im 24-h-Mittel? (Faktoren + Pausen anteilig, z. B. 1.05)
		// Durchschnitt über 24 h inkl. Pausen (gecacht, wird von Rebuild zurückgesetzt)
		private static double? avgMultiplier;

		public static double AverageDurationMultiplier
		{
			get
			{
				if (!enabled)
					return 1.0;

				if (avgMultiplier.HasValue)
					return avgMultiplier.Value;

				double workPerDay = Integrate(0, Day);		// Arbeitssekunden in 24 h
				avgMultiplier = workPerDay > 1.0 ? Day * 60.0 / workPerDay : 1.0;
				return avgMultiplier.Value;
			}
		}

		public static double LongestBreakMinutes
		{
			get
			{
				double max = 0;
				foreach (var br in breaks)
					max = Math.Max(max, br.len);
				return max;
			}
		}

		public static string FormatClock(double absOrMinuteOfDay)
		{
			int m = (int)Math.Floor(Mod(absOrMinuteOfDay) + 1e-6) % (int)Day;
			return $"{m / 60:00}:{m % 60:00}";
		}

		// h:mm:ss (Spielzeit)
		public static string FormatDurationSeconds(double minutes)
		{
			if (minutes < 0) minutes = 0;
			int total = (int)Math.Ceiling(minutes * 60.0 - 1e-6);
			return $"{total / 3600}:{(total % 3600) / 60:00}:{total % 60:00}";
		}

		public static string FormatDuration(double minutes)
		{
			if (minutes < 0) minutes = 0;
			int total = (int)Math.Ceiling(minutes - 1e-6);
			return $"{total / 60}:{total % 60:00} h";
		}
	}
}
