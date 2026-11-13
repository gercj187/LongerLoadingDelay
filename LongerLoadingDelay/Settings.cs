//FILE:		Settings.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;

namespace LongerLoadingDelay
{
	public class Settings : UnityModManager.ModSettings
	{
		// --- Ladezeit ---
		public float CarDelayGameMinutes = 2.5f;		// Spielminuten pro Wagen (bei Faktor 1.0)
		public float ClockRate = 0f;				// gemessen: Spielsekunden pro Echtsekunde (0 = unbekannt)

		// Vorrang: exakt aus dem Spiel (TOD_Time), sonst gemessen, sonst 1
		// Uhrgeschwindigkeit: manuell (fest) oder automatisch (exakt aus TOD_Time, sonst gemessen)

		public bool HasClockRate => GameClock.ExactRate.HasValue || ClockRate > 0.0001f;
		public float EffectiveClockRate => GameClock.ExactRate ?? (ClockRate > 0.0001f ? ClockRate : 1f);

		// Intern rechnet die Mod in Spielzeit (Schlaf, Fast Travel, Uhrzeit-Faktoren)
		public double CarDelayGameSeconds => Math.Max(30.0, CarDelayGameMinutes * 60.0);

		// --- Schichtplan ---
		public bool EnableSchedule = true;
		public int DayStartMinute = 6 * 60;
		public int DayEndMinute = 18 * 60;
		public float DayFactor = 0.8f;
		public float NightFactor = 1.5f;

		public bool EnableBreaks = true;
		public int ShiftChangeBreakMinutes = 15;
		public int MidShiftBreakMinutes = 60;

		// --- Job-Zeitlimit ---
		public float TimeLimitFactor = 0.5f;		// 1.0 = genau der Worst Case

		// --- Lohnausgleich für die Ladezeit (Rangierjobs) ---
		public bool PayScaling = true;
		public float PayPerLoadHourPct = 50f;		// + % Grundlohn pro Spielstunde erwarteter Ladezeit

		// --- Anzeige / Verhalten ---
		// (Statuslampe, Lichtschein und Lensflare sind immer aktiv – keine Einstellung mehr)
		public bool AutoDetectNewJobs = true;
		// --- Ersatz-Ladegleise ---
		public bool AltTracksAllowed = true;
		public bool AltTrackPenalty = true;
		public int AltTrackPenaltyOccupied = 5;		// Originalgleis beschäftigt / fertig aber belegt
		public int AltTrackPenaltyFree = 10;		// Originalgleis frei

		// --- Standgeld: fertiger Zug bleibt am Ladegleis stehen ---
		public bool IdlePenalty = true;
		public float IdlePenaltyPerHour = 2.5f;		// % pro Spielstunde
		public float IdleMaxPct = 50f;				// Obergrenze Standgeld in %
		public float IdleGraceHours = 1f;			// freie Standzeit

		// --- Gleichzeitige Abfertigung ---
		public bool ParallelProcessing = false;		// mehrere Jobs auf einem Gleis gleichzeitig statt nacheinander
		public float ParallelFactor = 1.5f;			// Ladezeit-Multiplikator, solange mehrere gleichzeitig laufen
		// Funkgerät-Ladestatus: fest an, im Multiplayer alle Jobs (keine Einstellung mehr, gespeicherte Werte werden ignoriert)
		public bool RadioLoadStatus { get => true; set { } }
		public bool RadioShowAllJobs { get => true; set { } }
		public bool RadioClock = true;				// Uhrzeit im Funkgerät
		public bool RadioClock24h = true;			// gilt für Funkgerät und Lademaschine
		public bool MachineClock = true;			// Uhrzeit in der Kopfzeile der Lademaschine
		public string Language = "auto";			// fest: Sprache des Spiels (keine Auswahl mehr)
		public bool EnableDebug = false;

		public void Sanitize()
		{
			CarDelayGameMinutes = SnapDelay(CarDelayGameMinutes);
			if (ClockRate > LoadingService.MaxPlausibleClockRate) ClockRate = 0f;		// verfälschte Messung verwerfen
			DayStartMinute = Mathf.Clamp(DayStartMinute, 0, 1439);
			DayEndMinute = Mathf.Clamp(DayEndMinute, 0, 1439);
			DayFactor = Mathf.Clamp(DayFactor, 0.1f, 5f);
			NightFactor = Mathf.Clamp(NightFactor, 0.1f, 5f);
			ShiftChangeBreakMinutes = Mathf.Clamp(ShiftChangeBreakMinutes, 0, 180);
			MidShiftBreakMinutes = Mathf.Clamp(MidShiftBreakMinutes, 0, 180);
			TimeLimitFactor = Mathf.Clamp(TimeLimitFactor, 0f, 5f);
			if (float.IsNaN(ClockRate) || ClockRate < 0f) ClockRate = 0f;
			ParallelFactor = Mathf.Clamp(ParallelFactor, 1f, 5f);
			AltTrackPenaltyOccupied = Mathf.Clamp(AltTrackPenaltyOccupied, 0, 100);
			AltTrackPenaltyFree = Mathf.Clamp(AltTrackPenaltyFree, 0, 100);
			IdlePenaltyPerHour = Mathf.Clamp(IdlePenaltyPerHour, 0f, 50f);
			IdleMaxPct = Mathf.Clamp(Mathf.Round(IdleMaxPct / 5f) * 5f, 10f, 100f);
			IdleGraceHours = Mathf.Clamp(IdleGraceHours, 0f, 24f);
			PayPerLoadHourPct = Mathf.Clamp(PayPerLoadHourPct, 0f, 500f);
			Language = "auto";
		}

		public override void Save(UnityModManager.ModEntry modEntry)
		{
			Save(this, modEntry);
		}

		// =========================
		// GUI
		// =========================
		public void Draw(UnityModManager.ModEntry modEntry)
		{
			bool changedBefore = GUI.changed;
			GUI.changed = false;

			var inv = CultureInfo.InvariantCulture;

			DrawClockWarning();

			// Multiplayer-Client: spielrelevante Einstellungen kommen vom Host → ausblenden
			bool hostControlled = Mp.HasHostState;

			if (hostControlled)
			{
				GUILayout.BeginVertical("box");
				GUILayout.Label("<b>" + Loc.T("gui.mp.host") + "</b>");
				GUILayout.Label("<i>" + Loc.T("gui.mp.host.text") + "</i>");
				GUILayout.EndVertical();
				GUILayout.Space(6);
			}
			else
			{
			// --- Ladezeit ---
			GUILayout.BeginVertical("box");

			CarDelayGameMinutes = DelaySlider(Loc.T("gui.delay"), CarDelayGameMinutes,
				Loc.T("gui.minutes", CarDelayGameMinutes.ToString("0.#", inv)), TipDelay());

			if (HasClockRate)
			{
				float rate = EffectiveClockRate;
				GUILayout.Label("<i>" + Loc.T("gui.delay.real",
					FormatRealSeconds((float)(CarDelayGameMinutes * 60.0 / Math.Max(0.0001f, rate))),
					rate.ToString("0.##", inv)) + "</i>");
			}
			else
			{
				GUILayout.Label("<i>" + Loc.T("gui.delay.real.unknown") + "</i>");
			}

			GUILayout.EndVertical();
			GUILayout.Space(6);

			// --- Schichtplan ---
			GUILayout.BeginVertical("box");

			EnableSchedule = Toggle(EnableSchedule, " <b>" + Loc.T("gui.schedule") + "</b>", Loc.T("tip.schedule"));

			if (EnableSchedule)
			{
				GUILayout.Space(4);

				DayStartMinute = IntSlider("   " + Loc.T("gui.dayshift") + " " + Loc.T("gui.from"), DayStartMinute, 0, 1425, 15, Schedule.FormatClock(DayStartMinute), Loc.T("tip.dayshift"));
				DayEndMinute = IntSlider("   " + Loc.T("gui.dayshift") + " " + Loc.T("gui.to"), DayEndMinute, 0, 1425, 15, Schedule.FormatClock(DayEndMinute), Loc.T("tip.dayshift"));

				DayFactor = FloatSlider("   " + Loc.T("gui.factor.day"), DayFactor, 0.1f, 3f, 0.05f, TipFactor(DayFactor));
				NightFactor = FloatSlider("   " + Loc.T("gui.factor.night"), NightFactor, 0.1f, 3f, 0.05f, TipFactor(NightFactor));

				GUILayout.Space(4);
				EnableBreaks = Toggle(EnableBreaks, " " + Loc.T("gui.breaks"), Loc.T("tip.breaks"));

				if (EnableBreaks)
				{
					ShiftChangeBreakMinutes = IntSlider("   " + Loc.T("gui.break.change"), ShiftChangeBreakMinutes, 0, 120, 5, Loc.T("gui.minutes", ShiftChangeBreakMinutes), Loc.T("tip.break.change"));
					MidShiftBreakMinutes = IntSlider("   " + Loc.T("gui.break.mid"), MidShiftBreakMinutes, 0, 120, 5, Loc.T("gui.minutes", MidShiftBreakMinutes), Loc.T("tip.break.mid"));
				}
			}

			if (GUI.changed)
				Schedule.Rebuild();

			if (EnableSchedule)
			{
				GUILayout.Space(4);
				GUILayout.BeginHorizontal();
				GUILayout.Label(Loc.T("gui.preview"), GUILayout.ExpandWidth(false));
				Tip(Loc.T("tip.preview") + "\n\n" + ExampleText());
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
				DrawPreviewBar();
			}

			GUILayout.EndVertical();
			GUILayout.Space(6);

			// --- Dynamische Faktoren ---
			GUILayout.BeginVertical("box");

			TimeLimitFactor = FloatSlider(Loc.T("gui.timelimit"), TimeLimitFactor, 0f, 3f, 0.05f,
				Loc.T("tip.timelimit",
					FormatRealSeconds(LoadingService.ExtraTimeLimitSeconds(5)),
					FormatRealSeconds(LoadingService.ExtraTimeLimitSeconds(20))));

			GUILayout.Space(4);
			PayScaling = Toggle(PayScaling, " " + Loc.T("gui.pay"), TipPay());
			if (PayScaling)
			{
				PayPerLoadHourPct = StepSlider("   " + Loc.T("gui.pay.rate"), PayPerLoadHourPct, 0f, 300f, 5f,
					"+" + PayPerLoadHourPct.ToString("0") + " % / h", TipPay());
			}

			GUILayout.EndVertical();
			GUILayout.Space(6);

			// --- Verhalten ---
			GUILayout.BeginVertical("box");

			AutoDetectNewJobs = Toggle(AutoDetectNewJobs, " " + Loc.T("gui.autodetect"), Loc.T("tip.autodetect"));

			GUILayout.Space(4);
			IdlePenalty = Toggle(IdlePenalty, " " + Loc.T("gui.idle"), Loc.T("tip.idle"));
			if (IdlePenalty)
			{
				IdlePenaltyPerHour = StepSlider("   " + Loc.T("gui.idle.rate"), IdlePenaltyPerHour, 0.5f, 10f, 0.5f,
					"-" + IdlePenaltyPerHour.ToString("0.0") + " % / h", Loc.T("tip.idle.rate"));
				IdleMaxPct = StepSlider("   " + Loc.T("gui.idle.max"), IdleMaxPct, 10f, 100f, 5f,
					"max. -" + IdleMaxPct.ToString("0") + " %", Loc.T("tip.idle.max", IdleMaxPct.ToString("0"),
						(IdleMaxPct / Mathf.Max(0.1f, IdlePenaltyPerHour) + IdleGraceHours).ToString("0.#")));
				IdleGraceHours = StepSlider("   " + Loc.T("gui.idle.grace"), IdleGraceHours, 0f, 6f, 0.25f,
					IdleGraceHours.ToString("0.##") + " h", Loc.T("tip.idle.grace"));
			}

			GUILayout.Space(4);
			ParallelProcessing = Toggle(ParallelProcessing, " " + Loc.T("gui.parallel"), Loc.T("tip.parallel"));
			if (ParallelProcessing)
				ParallelFactor = FloatSlider("   " + Loc.T("gui.parallel.factor"), ParallelFactor, 1f, 3f, 0.05f, Loc.T("tip.parallel.factor"));

			GUILayout.Space(4);
			AltTracksAllowed = Toggle(AltTracksAllowed, " " + Loc.T("gui.alt"), Loc.T("tip.alt"));
			if (AltTracksAllowed)
			{
				AltTrackPenalty = Toggle(AltTrackPenalty, "    " + Loc.T("gui.alt.penalty"), Loc.T("tip.alt.penalty"));

				if (AltTrackPenalty)
				{
					AltTrackPenaltyOccupied = IntSlider("       " + Loc.T("gui.alt.penalty.occupied"), AltTrackPenaltyOccupied, 0, 50, 1, "-" + AltTrackPenaltyOccupied + " %", Loc.T("tip.alt.occupied"));
					AltTrackPenaltyFree = IntSlider("       " + Loc.T("gui.alt.penalty.free"), AltTrackPenaltyFree, 0, 50, 1, "-" + AltTrackPenaltyFree + " %", Loc.T("tip.alt.free"));
				}
			}

			GUILayout.EndVertical();
			GUILayout.Space(6);

			}	// Ende: spielrelevante Einstellungen

			// --- Anzeige (immer lokal, auch im Multiplayer) ---
			GUILayout.BeginVertical("box");

			RadioClock = Toggle(RadioClock, " " + Loc.T("gui.radioclock"), Loc.T("tip.radioclock"));

			bool machineClockBefore = MachineClock;
			MachineClock = Toggle(MachineClock, " " + Loc.T("gui.machineclock"), Loc.T("tip.machineclock"));

			bool h24Before = RadioClock24h;
			if (RadioClock || MachineClock)
				RadioClock24h = Toggle(RadioClock24h, "    " + Loc.T("gui.radioclock.24h"), Loc.T("tip.24h"));

			if (machineClockBefore != MachineClock || h24Before != RadioClock24h)
				global::LongerLoadingDelay.MachineClock.Refresh();		// Feld heißt auch MachineClock

			GUILayout.EndVertical();
			GUILayout.Space(6);

			// --- Debug ---
			GUILayout.BeginHorizontal();
			GUILayout.FlexibleSpace();
			EnableDebug = GUILayout.Toggle(EnableDebug, " " + Loc.T("gui.debug"), GUILayout.ExpandWidth(false));
			GUILayout.EndHorizontal();

			if (EnableDebug)
			{
				GUILayout.BeginVertical("box");
				if (GUILayout.Button(Loc.T("gui.reset"), GUILayout.Width(300)))
					LoadingService.ClearAll();

				// Sucht alle Objekte rund um die nächste Lademaschine (für die echte Lampe am Mast)
				if (GUILayout.Button("DUMP OBJECTS NEAR NEAREST MACHINE", GUILayout.Width(300)))
					SceneDump.DumpNearestMachine();
				GUILayout.EndVertical();
			}

			DrawTooltip();

			GUI.changed |= changedBefore;
		}

		// =========================
		// TOOLTIP-TEXTE (dynamisch)
		// =========================
		private string TipDelay()
		{
			string mins = CarDelayGameMinutes.ToString("0.#", CultureInfo.InvariantCulture);
			return Loc.T("tip.delay", mins) + "\n\n" + ExampleText();
		}

		private string TipFactor(float factor)
		{
			double perCar = CarDelayGameMinutes * factor;
			return Loc.T("tip.factor",
				factor.ToString("0.00", CultureInfo.InvariantCulture),
				CarDelayGameMinutes.ToString("0.#", CultureInfo.InvariantCulture),
				perCar.ToString("0.#", CultureInfo.InvariantCulture));
		}

		private string TipPay()
		{
			var inv = CultureInfo.InvariantCulture;
			string Ex(int cars) => Loc.T("tip.pay.example", cars,
				Schedule.FormatDuration(LoadPay.ExpectedGameHours(cars) * 60.0),
				LoadPay.FactorForCars(cars).ToString("0.00", inv));

			return Loc.T("tip.pay", PayPerLoadHourPct.ToString("0", inv)) + "\n\n" + Ex(5) + "\n" + Ex(20);
		}

		private static string ExampleText()
		{
			double start;

			if (!GameClock.TryGetNowAbsMinutes(out start))
				start = 19 * 60 + 30;		// Beispiel außerhalb des Spiels: 19:30

			double work = 20 * Main.Settings.CarDelayGameSeconds;
			double? finish = Schedule.FinishTime(start, work);

			return Loc.T("gui.example",
				Schedule.FormatClock(start),
				finish.HasValue ? Schedule.FormatClock(finish.Value) + "  (" + Schedule.FormatDuration(finish.Value - start) + ")" : "—");
		}

		// =========================
		// GUI-HELFER
		// =========================
		private static GUIStyle? tipIconStyle, tipBoxStyle;

		// "?" mit Tooltip
		private static void Tip(string tip)
		{
			if (string.IsNullOrEmpty(tip))
				return;

			tipIconStyle ??= new GUIStyle(GUI.skin.label)
			{
				fontStyle = FontStyle.Bold,
				normal = { textColor = new Color(1f, 0.8f, 0.3f) }
			};

			GUILayout.Label(new GUIContent(" ?", tip), tipIconStyle, GUILayout.ExpandWidth(false));
		}

		private static bool Toggle(bool value, string label, string tip)
		{
			GUILayout.BeginHorizontal();
			value = GUILayout.Toggle(value, label, GUILayout.ExpandWidth(false));
			Tip(tip);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			return value;
		}

		// Tooltip zuletzt zeichnen (liegt über allem); danach leeren, damit der Mod Manager ihn nicht doppelt zeigt
		private static void DrawTooltip()
		{
			string tip = GUI.tooltip;
			if (string.IsNullOrEmpty(tip) || Event.current.type != EventType.Repaint)
				return;

			tipBoxStyle ??= new GUIStyle(GUI.skin.label)
			{
				wordWrap = true,
				richText = true,
				alignment = TextAnchor.UpperLeft,
				padding = new RectOffset(10, 10, 8, 8),
				normal = { textColor = new Color(0.95f, 0.95f, 0.95f) }
			};

			const float width = 460f;
			var content = new GUIContent(tip);
			float height = tipBoxStyle.CalcHeight(content, width);

			Vector2 m = Event.current.mousePosition;
			float x = m.x > width + 40f ? m.x - width - 12f : m.x + 18f;		// "?" stehen rechts → Box links davon
			var rect = new Rect(x, m.y + 12f, width, height);

			Color old = GUI.color;
			GUI.color = new Color(0f, 0f, 0f, 0.92f);
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			GUI.color = new Color(1f, 0.8f, 0.3f, 0.9f);
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2f), Texture2D.whiteTexture);
			GUI.color = old;

			GUI.Label(rect, content, tipBoxStyle);
			GUI.tooltip = string.Empty;
		}

		private static int IntSlider(string label, int value, int min, int max, int step, string valueText, string tip = "")
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(380));
			float raw = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220));
			GUILayout.Label(" " + valueText, GUILayout.ExpandWidth(false));
			Tip(tip);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();

			int stepped = Mathf.RoundToInt(raw / step) * step;
			return Mathf.Clamp(stepped, min, max);
		}

		private static float StepSlider(string label, float value, float min, float max, float step, string valueText, string tip = "")
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(380));
			float raw = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220));
			GUILayout.Label(" " + valueText, GUILayout.ExpandWidth(false));
			Tip(tip);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();

			return Mathf.Clamp(Mathf.Round(raw / step) * step, min, max);
		}

		private static float FloatSlider(string label, float value, float min, float max, float step, string tip = "")
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(380));
			float raw = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220));
			GUILayout.Label($" x{value:0.00}", GUILayout.ExpandWidth(false));
			Tip(tip);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();

			float stepped = Mathf.Round(raw / step) * step;
			return Mathf.Clamp((float)Math.Round(stepped, 2), min, max);
		}

		// Rote Warnung, wenn die Spieluhr so schnell läuft, dass Zeitlimits kaum einzuhalten sind
		private static void DrawClockWarning()
		{
			var s = Main.Settings;
			if (!s.HasClockRate)
				return;

			float rate = s.EffectiveClockRate;
			if (rate <= 3f)
				return;

			Color oldBg = GUI.backgroundColor, oldContent = GUI.contentColor;
			GUI.backgroundColor = new Color(0.9f, 0.1f, 0.1f);
			GUI.contentColor = new Color(1f, 0.85f, 0.85f);

			GUILayout.BeginVertical("box");
			GUILayout.Label("<b>" + Loc.T("gui.warn.clock.title", rate.ToString("0.#")) + "</b>");
			GUILayout.Label(Loc.T("gui.warn.clock.text"));
			GUILayout.EndVertical();

			GUI.backgroundColor = oldBg;
			GUI.contentColor = oldContent;
			GUILayout.Space(6);
		}

		private static void DrawPreviewBar()
		{
			Rect rect = GUILayoutUtility.GetRect(600, 18, GUILayout.ExpandWidth(true));
			Rect labels = GUILayoutUtility.GetRect(600, 16, GUILayout.ExpandWidth(true));
			Rect hover = GUILayoutUtility.GetRect(600, 20, GUILayout.ExpandWidth(true));

			if (Event.current.type != EventType.Repaint)
				return;

			const int segments = 288;		// 5-Minuten-Blöcke
			float w = rect.width / segments;
			Color old = GUI.color;

			for (int i = 0; i < segments; i++)
			{
				double minute = i * 5 + 2.5;
				GUI.color = KindColor(Schedule.KindAt(minute));
				GUI.DrawTexture(new Rect(rect.x + i * w, rect.y, w + 0.5f, rect.height), Texture2D.whiteTexture);
			}

			// Tooltip: Abschnitt unter der Maus
			Vector2 mouse = Event.current.mousePosition;
			string hoverText = "<i>" + Loc.T("gui.hover") + "</i>";

			if (rect.Contains(mouse))
			{
				double minute = (mouse.x - rect.x) / rect.width * Schedule.Day;
				var seg = Schedule.SegmentAt(minute);

				// Abschnitt hervorheben
				float x0 = rect.x + (float)(seg.start / Schedule.Day) * rect.width;
				float x1 = rect.x + (float)(seg.end / Schedule.Day) * rect.width;
				GUI.color = new Color(1f, 1f, 1f, 0.35f);

				if (seg.end > seg.start)
				{
					GUI.DrawTexture(new Rect(x0, rect.y, x1 - x0, 3), Texture2D.whiteTexture);
				}
				else
				{
					// über Mitternacht: zwei Teile
					GUI.DrawTexture(new Rect(x0, rect.y, rect.xMax - x0, 3), Texture2D.whiteTexture);
					GUI.DrawTexture(new Rect(rect.x, rect.y, x1 - rect.x, 3), Texture2D.whiteTexture);
				}

				string name = seg.kind switch
				{
					Schedule.SegmentKind.Night => Loc.T("seg.night"),
					Schedule.SegmentKind.Break => Loc.T("seg.break"),
					Schedule.SegmentKind.ShiftChange => Loc.T("seg.change"),
					_ => Loc.T("seg.day")
				};

				hoverText = $"<b>{name}</b>  {Schedule.FormatClock(seg.start)} – {Schedule.FormatClock(seg.end)}";
			}

			GUI.color = old;

			for (int h = 0; h <= 24; h += 6)
			{
				float x = labels.x + labels.width * h / 24f - (h == 24 ? 34 : h == 0 ? 0 : 17);
				GUI.Label(new Rect(x, labels.y, 40, labels.height), $"{h:00}:00");
			}

			GUI.Label(hover, hoverText);
		}

		private static Color KindColor(Schedule.SegmentKind kind) => kind switch
		{
			Schedule.SegmentKind.Break => new Color(0.85f, 0.2f, 0.2f),
			Schedule.SegmentKind.ShiftChange => new Color(0.85f, 0.2f, 0.2f),
			Schedule.SegmentKind.Day => new Color(0.95f, 0.85f, 0.45f),
			_ => new Color(0.2f, 0.25f, 0.45f)
		};

		// Zeit pro Wagen: 0,5 … 5 in 0,5er-Schritten, darüber 1er-Schritte bis 20
		public const float DelayMin = 0.5f, DelayMax = 20f;

		public static float SnapDelay(float v)
		{
			v = Mathf.Clamp(v, DelayMin, DelayMax);
			return v < 5f ? Mathf.Clamp(Mathf.Round(v * 2f) / 2f, DelayMin, 5f) : Mathf.Round(v);
		}

		private static float DelaySlider(string label, float value, string valueText, string tip)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(380));
			float raw = GUILayout.HorizontalSlider(value, DelayMin, DelayMax, GUILayout.Width(220));
			GUILayout.Label(" " + valueText, GUILayout.ExpandWidth(false));
			Tip(tip);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			return SnapDelay(raw);
		}

		// =========================
		// MULTIPLAYER: spielrelevante Werte (Reihenfolge = Protokoll, nur am Ende erweitern!)
		// =========================
		public void WriteGameplay(System.IO.BinaryWriter w)
		{
			w.Write(CarDelayGameMinutes);
			w.Write(EnableSchedule);
			w.Write(DayStartMinute);
			w.Write(DayEndMinute);
			w.Write(DayFactor);
			w.Write(NightFactor);
			w.Write(EnableBreaks);
			w.Write(ShiftChangeBreakMinutes);
			w.Write(MidShiftBreakMinutes);
			w.Write(TimeLimitFactor);
			w.Write(PayScaling);
			w.Write(PayPerLoadHourPct);
			w.Write(AutoDetectNewJobs);
			w.Write(AltTracksAllowed);
			w.Write(AltTrackPenalty);
			w.Write(AltTrackPenaltyOccupied);
			w.Write(AltTrackPenaltyFree);
			w.Write(IdlePenalty);
			w.Write(IdlePenaltyPerHour);
			w.Write(IdleGraceHours);
			w.Write(ParallelProcessing);
			w.Write(ParallelFactor);
			w.Write(IdleMaxPct);
		}

		public void ReadGameplay(System.IO.BinaryReader r)
		{
			CarDelayGameMinutes = r.ReadSingle();
			EnableSchedule = r.ReadBoolean();
			DayStartMinute = r.ReadInt32();
			DayEndMinute = r.ReadInt32();
			DayFactor = r.ReadSingle();
			NightFactor = r.ReadSingle();
			EnableBreaks = r.ReadBoolean();
			ShiftChangeBreakMinutes = r.ReadInt32();
			MidShiftBreakMinutes = r.ReadInt32();
			TimeLimitFactor = r.ReadSingle();
			PayScaling = r.ReadBoolean();
			PayPerLoadHourPct = r.ReadSingle();
			AutoDetectNewJobs = r.ReadBoolean();
			AltTracksAllowed = r.ReadBoolean();
			AltTrackPenalty = r.ReadBoolean();
			AltTrackPenaltyOccupied = r.ReadInt32();
			AltTrackPenaltyFree = r.ReadInt32();
			IdlePenalty = r.ReadBoolean();
			IdlePenaltyPerHour = r.ReadSingle();
			IdleGraceHours = r.ReadSingle();
			ParallelProcessing = r.ReadBoolean();
			ParallelFactor = r.ReadSingle();
			IdleMaxPct = r.ReadSingle();
		}

		// Lokale Anzeige-Einstellungen (gehören dem Spieler, nicht dem Host)
		public void CopyDisplayPrefsFrom(Settings other)
		{
			RadioClock = other.RadioClock;
			RadioClock24h = other.RadioClock24h;
			MachineClock = other.MachineClock;
			EnableDebug = other.EnableDebug;
		}

		public Settings CloneSettings() => (Settings)MemberwiseClone();

		public static string FormatRealSeconds(float seconds)
		{
			if (seconds < 0 || float.IsNaN(seconds) || float.IsInfinity(seconds))
				return "—";

			int s = Mathf.RoundToInt(seconds);
			if (s >= 3600)
				return $"{s / 3600}:{(s % 3600) / 60:00}:{s % 60:00} h";

			return $"{s / 60}:{s % 60:00} min";
		}
	}
}
