//FILE:		Loc.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DV.Localization;
using Newtonsoft.Json.Linq;

namespace LongerLoadingDelay
{
	// =========================
	// MEHRSPRACHIGKEIT
	// =========================
	// Sprache: Einstellung "auto" = Spielsprache (I2 über LocalizationAPI.CC), sonst fester Code.
	// Eigene Sprachen: <Mod-Ordner>/lang/<code>.json  →  { "key": "Text", ... }  (UTF-8 ohne BOM)
	// Fehlende Keys fallen auf Englisch zurück.
	public static class Loc
	{
		private static readonly Dictionary<string, Dictionary<string, string>> langs = new(StringComparer.OrdinalIgnoreCase)
		{
			["en"] = new()
			{
				// Display
				["title.paused"] = "BREAK UNTIL {0}",
				["title.interrupted"] = "PROCESS INTERRUPTED",
				["title.done.load"] = "LOADING COMPLETE",
				["title.done.unload"] = "UNLOADING COMPLETE",
				["gui.pay"] = "Dynamic payout factor",
				["gui.pay.rate"] = "Extra base pay per game hour of loading",
				["gui.pay.example"] = "Example {0} cars: ~{1} loading → pay x{2}",
				["tip.delay"] = "<b>Time per car</b>\nWorking time per car in game minutes at factor 1.00 (currently {0} min).\n\n• Actual duration per car = time per car × factor of the current shift (day/night).\n• Breaks stop the work completely.\n• Sleep and fast travel count – the work continues during the time jump.\n• Also drives the dynamic job time factor and the dynamic payout factor.",
				["tip.schedule"] = "<b>Time-of-day factors</b>\nOff: every car takes exactly the time per car.\nOn: the duration is multiplied by the factor of the current shift. The factor applies continuously – if the shift changes during loading, the speed changes too.",
				["tip.dayshift"] = "Start and end of the day shift. The rest of the day is the night shift. Shift-change breaks are placed around these times.",
				["tip.factor"] = "<b>Multiplier for the loading time</b>\nbelow 1.00 = faster, above 1.00 = slower\n\nx{0}: {1} min per car → {2} min per car",
				["tip.breaks"] = "<b>Breaks</b>\nDuring a break all loading machines stop (lamp blue, display \"BREAK UNTIL …\"). Idle machines show the break as well.\n\n• Job time factor: the longest break is added completely.\n• Payout factor: breaks are included via the daily average.",
				["tip.break.change"] = "Total length of the break at each shift change – half before, half after the change.",
				["tip.break.mid"] = "Break exactly in the middle of each shift.",
				["tip.preview"] = "Hover over the bar to see the exact times of each shift and break.",
				["tip.timelimit"] = "<b>Dynamic job time factor</b>\nExtends the bonus time of shunting jobs by the worst-case loading time:\nslowest factor + longest break, × this factor.\n\n1.00 = exactly the worst case · 1.20 = 20 % buffer · 0 = no extension\n\n5 cars: +{0} real time\n20 cars: +{1} real time",
				["tip.pay"] = "<b>Dynamic payout factor</b>\nRaises the base pay of shunting jobs according to the expected loading time:\npay × (1 + game hours × {0} %)\n\n• The bonus (= half of the base pay) rises too.\n• Expected time = cars × time per car × daily average of the shift factors incl. breaks – independent of when you take the job.\n• Deductions (replacement track, demurrage) are taken from the raised amount.",
				["tip.pay.example"] = "{0} cars: ~{1} loading → pay x{2}",
				["tip.autodetect"] = "If another train for a different job is put on the track during loading, it is queued automatically – no need to pull the lever again.",
				["tip.idle"] = "<b>Demurrage</b>\nIf a finished train still stands at the loading track after the free time, the payout drops every game hour. Counts during sleep and fast travel as well (from the real finish time). Shown as a separate line in the job report.",
				["tip.idle.rate"] = "Deduction from wage and bonus per started game hour after the free standing time.",
				["tip.idle.grace"] = "How long a finished train may stand at the loading track without a deduction.",
				["tip.parallel"] = "Off: jobs on one track are processed one after another.\nOn: all at the same time – while several run, every car takes longer (multiplier below).",
				["tip.parallel.factor"] = "Loading time multiplier while more than one job is processed at the same time on this track.",
				["tip.alt"] = "<b>Replacement loading tracks</b>\nA train may be loaded/unloaded on another L track of the same group at the same station (e.g. D8L/D9L instead of D7L): put the train there and pull the lever.\nIf the train is pulled out again before the first car was processed, the redirect and the deduction are reverted.",
				["tip.alt.penalty"] = "Using a replacement track costs part of wage and bonus. The percentage is fixed at the moment of redirecting.",
				["tip.alt.occupied"] = "Deduction if the original track was busy or blocked by a finished train.",
				["tip.alt.free"] = "Deduction if the original track would have been free.",
				["tip.radioclock"] = "Shows the game time in the top right corner of the comms radio display.",
				["tip.machineclock"] = "Shows the game time right-aligned in the header of the loading machine.",
				["tip.24h"] = "Off: 12-hour format with AM/PM (00:45 = 12:45 AM, 12:45 = 12:45 PM).",
				["gui.mp.host"] = "Multiplayer: settings are controlled by the host",
				["gui.mp.host.text"] = "Loading time, shifts, breaks, time and payout factors, deductions and replacement tracks are taken from the host so everything runs exactly the same for all players. Only the display options below are your own.",
				["line.progress"] = "{0}/{1} cars",
				["line.remaining"] = "{0} left (~{1})",
				["line.queued"] = "WAITING",
				["line.moving"] = "STOPPED - IN MOTION",
				["line.missing"] = "STOPPED - CARS MISSING",
				["line.done"] = "DONE",
				["line.break"] = "BREAK",
				["seg.day"] = "Day shift",
				["seg.night"] = "Night shift",
				["seg.break"] = "Break",
				["seg.change"] = "Shift change",
				["gui.hover"] = "Hover over the bar for details",

				// GUI
				["gui.delay"] = "Time per car (game time)",
				["gui.delay.real"] = "(equals {0} real time – time acceleration in the game options x{1})",
				["gui.delay.real.unknown"] = "(real time is calculated in game from the time acceleration)",
				["gui.schedule"] = "Time-of-day factors",
				["gui.dayshift"] = "Day shift",
				["gui.from"] = "from",
				["gui.to"] = "to",
				["gui.factor.day"] = "Factor day shift",
				["gui.factor.night"] = "Factor night shift",
				["gui.factor.hint"] = "below 1.0 = faster, above 1.0 = slower",
				["gui.breaks"] = "Breaks",
				["gui.break.change"] = "Shift change break (half before, half after)",
				["gui.break.mid"] = "Break in the middle of each shift",
				["gui.preview"] = "24 h preview:  light = day  ·  dark = night  ·  red = break",
				["gui.example"] = "Example 20 cars: start {0} → done {1}",
				["gui.timelimit"] = "Dynamic job time factor",
				["gui.timelimit.example"] = "Extra bonus time for 20 cars: {0} real time",
				["gui.lamp"] = "Status lamp (beacon on the mast)",
				["gui.lamp.light"] = "Additional light glow",
				["gui.lamp.corona"] = "Corona at night (halo like street lamps)",
				["gui.lamp.brightness"] = "Lamp brightness (1.0 = like station beacon)",
				["gui.lamp.corona.size"] = "Corona size",
				["gui.autodetect"] = "Detect trains added while loading",
				["gui.alt"] = "Allow alternative loading tracks",
				["gui.alt.off"] = "Off",
				["gui.alt.occupied"] = "If original is occupied",
				["gui.alt.always"] = "Always",
				["gui.alt.hint"] = "Same station, same group (e.g. D8L/D9L instead of D7L) – put the train there and pull the lever",
				["line.redirect"] = "{0}: replacement for {1}",
				["gui.alt.penalty"] = "Reduce payout (wage and bonus)",
				["gui.alt.penalty.occupied"] = "Original track busy / done but occupied",
				["gui.alt.penalty.free"] = "Original track free",
				["gui.parallel"] = "Process several jobs on one track at the same time",
				["gui.idle"] = "Demurrage: penalty if a finished train stays at the loading track",
				["gui.clockrate"] = "Clock speed (game time : real time)",
				["gui.clockrate.auto"] = "Automatic",
				["gui.clockrate.manual"] = "Manual",
				["gui.clockrate.value"] = "Game time per real time",
				["gui.clockrate.detected"] = "Detected: from game {0} · measured {1}",
				["gui.idle.rate"] = "Penalty per game hour",
				["gui.idle.grace"] = "Free standing time",
				["gui.warn.clock.title"] = "WARNING: game time runs {0}x faster than real time!",
				["gui.warn.clock.text"] = "At this speed time limits and penalties (demurrage, bonus time) can hardly be avoided.",
				["hdr.job"] = "JOB",
				["report.alt"] = "Deduction replacement for {0} (-{1} %)",
				["report.idle"] = "Deduction demurrage {0} (-{1} %)",
				["report.both"] = "Deductions: track -{0} %, demurrage -{1} %",
				["report.multi"] = "Deductions: {0}",
				["report.short.alt"] = "track -{0} %",
				["report.short.idle"] = "demurrage -{0} %",
				["hdr.cars"] = "CARS",
				["hdr.status"] = "STATUS",
				["hdr.eta"] = "ETA",
				["pen.alt"] = "replacement for {0} ({1})",
				["pen.alt.occupied"] = "occupied",
				["pen.alt.free"] = "free",
				["pen.idle"] = "standing {0}",
				["line.moving.short"] = "STOP: MOVING",
				["line.missing.short"] = "STOP: CARS MISSING",
				["gui.parallel.factor"] = "Loading time multiplier while running in parallel",
				["line.penalty"] = "-{0} %",
				["gui.language"] = "Language",
				["gui.radioclock"] = "Game clock in the comms radio",
				["gui.radioclock.24h"] = "24-hour format",
				["gui.machineclock"] = "Game clock in the loading machine header",
				["gui.language.auto"] = "Game",
				["gui.debug"] = "Debug log",
				["gui.reset"] = "RESET ALL SEQUENCES",
				["gui.minutes"] = "{0} min",
			},
			["de"] = new()
			{
				["title.paused"] = "PAUSE BIS {0}",
				["title.interrupted"] = "VORGANG UNTERBROCHEN",
				["title.done.load"] = "BELADEN ABGESCHLOSSEN",
				["title.done.unload"] = "ENTLADEN ABGESCHLOSSEN",
				["gui.pay"] = "Dynamischer Auszahlungsfaktor",
				["gui.pay.rate"] = "Mehr Grundlohn pro Spielstunde Ladezeit",
				["gui.pay.example"] = "Beispiel {0} Wagen: ~{1} Ladezeit → Lohn x{2}",
				["tip.delay"] = "<b>Zeit pro Wagen</b>\nArbeitszeit pro Wagen in Spielminuten bei Faktor 1.00 (aktuell {0} min).\n\n• Tatsächliche Dauer pro Wagen = Zeit pro Wagen × Faktor der aktuellen Schicht (Tag/Nacht).\n• Pausen halten die Arbeit komplett an.\n• Schlaf und Schnellreise zählen mit – die Arbeit läuft während des Zeitsprungs weiter.\n• Bestimmt außerdem den dynamischen Job-Zeitfaktor und den dynamischen Auszahlungsfaktor.",
				["tip.schedule"] = "<b>Uhrzeitabhängige Faktoren</b>\nAus: jeder Wagen dauert genau die Zeit pro Wagen.\nAn: die Dauer wird mit dem Faktor der aktuellen Schicht multipliziert. Der Faktor gilt laufend – wechselt die Schicht während der Beladung, ändert sich auch das Tempo.",
				["tip.dayshift"] = "Beginn und Ende der Tagschicht. Der Rest des Tages ist Nachtschicht. Die Schichtwechsel-Pausen liegen um diese Uhrzeiten.",
				["tip.factor"] = "<b>Multiplikator für die Ladezeit</b>\nunter 1.00 = schneller, über 1.00 = langsamer\n\nx{0}: {1} min pro Wagen → {2} min pro Wagen",
				["tip.breaks"] = "<b>Pausen</b>\nWährend einer Pause stehen alle Lademaschinen still (Lampe blau, Display \"PAUSE BIS …\"). Auch Maschinen ohne Auftrag zeigen die Pause.\n\n• Job-Zeitfaktor: die längste Pause wird komplett eingerechnet.\n• Auszahlungsfaktor: Pausen fließen über den Tagesdurchschnitt ein.",
				["tip.break.change"] = "Gesamtlänge der Pause bei jedem Schichtwechsel – zur Hälfte vor, zur Hälfte nach dem Wechsel.",
				["tip.break.mid"] = "Pause genau in der Mitte jeder Schicht.",
				["tip.preview"] = "Mit der Maus über den Balken fahren, um die genauen Zeiten jeder Schicht und Pause zu sehen.",
				["tip.timelimit"] = "<b>Dynamischer Job-Zeitfaktor</b>\nVerlängert die Bonuszeit von Rangierjobs um die schlechtestmögliche Ladezeit:\nlangsamster Faktor + längste Pause, × diesen Faktor.\n\n1.00 = genau der Worst Case · 1.20 = 20 % Puffer · 0 = keine Verlängerung\n\n5 Wagen: +{0} Echtzeit\n20 Wagen: +{1} Echtzeit",
				["tip.pay"] = "<b>Dynamischer Auszahlungsfaktor</b>\nErhöht den Grundlohn von Rangierjobs nach der erwarteten Ladezeit:\nLohn × (1 + Spielstunden × {0} %)\n\n• Der Bonus (= halber Grundlohn) steigt mit.\n• Erwartete Zeit = Wagen × Zeit pro Wagen × Tagesdurchschnitt der Schichtfaktoren inkl. Pausen – unabhängig davon, wann man den Job annimmt.\n• Abzüge (Ersatzgleis, Standgeld) gehen vom erhöhten Betrag ab.",
				["tip.pay.example"] = "{0} Wagen: ~{1} Ladezeit → Lohn x{2}",
				["tip.autodetect"] = "Wird während einer Beladung ein weiterer Zug für einen anderen Job aufs Gleis gestellt, kommt er automatisch in die Warteschlange – ohne erneut den Hebel zu ziehen.",
				["tip.idle"] = "<b>Standgeld</b>\nSteht ein fertiger Zug nach der freien Standzeit noch am Ladegleis, sinkt die Auszahlung mit jeder Spielstunde. Zählt auch bei Schlaf und Schnellreise (ab dem echten Fertig-Zeitpunkt). Erscheint im Auftragsbericht als eigene Zeile.",
				["tip.idle.rate"] = "Abzug von Lohn und Bonus pro Spielstunde nach Ablauf der freien Standzeit.",
				["tip.idle.grace"] = "So lange darf ein fertiger Zug ohne Abzug am Ladegleis stehen.",
				["tip.parallel"] = "Aus: Jobs auf einem Gleis werden nacheinander abgefertigt.\nAn: alle gleichzeitig – solange mehrere laufen, dauert jeder Wagen länger (Multiplikator darunter).",
				["tip.parallel.factor"] = "Ladezeit-Multiplikator, solange auf diesem Gleis mehr als ein Job gleichzeitig läuft.",
				["tip.alt"] = "<b>Ersatz-Ladegleise</b>\nEin Zug darf auf einem anderen L-Gleis derselben Gruppe im selben Bahnhof be-/entladen werden (z. B. D8L/D9L statt D7L): Zug dort abstellen und Hebel ziehen.\nWird der Zug vor dem ersten fertigen Wagen wieder abgezogen, werden Umleitung und Abzug zurückgenommen.",
				["tip.alt.penalty"] = "Das Ausweichen kostet einen Teil von Lohn und Bonus. Der Prozentsatz wird beim Umleiten festgelegt.",
				["tip.alt.occupied"] = "Abzug, wenn das Originalgleis beschäftigt war oder ein fertiger Zug darauf stand.",
				["tip.alt.free"] = "Abzug, wenn das Originalgleis frei gewesen wäre.",
				["tip.radioclock"] = "Zeigt die Spieluhrzeit oben rechts im Display des Funkgeräts.",
				["tip.machineclock"] = "Zeigt die Spieluhrzeit rechtsbündig in der Kopfzeile der Lademaschine.",
				["tip.24h"] = "Aus: 12-Stunden-Format mit AM/PM (00:45 = 12:45 AM, 12:45 = 12:45 PM).",
				["gui.mp.host"] = "Multiplayer: Einstellungen werden vom Host vorgegeben",
				["gui.mp.host.text"] = "Ladezeit, Schichten, Pausen, Zeit- und Auszahlungsfaktor, Abzüge und Ersatzgleise kommen vom Host, damit für alle Spieler alles exakt gleich läuft. Nur die Anzeige-Optionen darunter sind deine eigenen.",
				["line.progress"] = "{0}/{1} Wagen",
				["line.remaining"] = "noch {0} (~{1})",
				["line.queued"] = "WARTET",
				["line.moving"] = "GESTOPPT - IN BEWEGUNG",
				["line.missing"] = "GESTOPPT - WAGEN FEHLEN",
				["line.done"] = "FERTIG",
				["line.break"] = "PAUSE",
				["seg.day"] = "Tagschicht",
				["seg.night"] = "Nachtschicht",
				["seg.break"] = "Pause",
				["seg.change"] = "Schichtwechsel",
				["gui.hover"] = "Maus über den Balken für Details",

				["gui.delay"] = "Zeit pro Wagen (Spielzeit)",
				["gui.delay.real"] = "(entspricht {0} Echtzeit – Zeitbeschleunigung in den Spieloptionen x{1})",
				["gui.delay.real.unknown"] = "(die Echtzeit wird im Spiel aus der Zeitbeschleunigung berechnet)",
				["gui.schedule"] = "Uhrzeitabhängige Faktoren",
				["gui.dayshift"] = "Tagschicht",
				["gui.from"] = "von",
				["gui.to"] = "bis",
				["gui.factor.day"] = "Faktor Tagschicht",
				["gui.factor.night"] = "Faktor Nachtschicht",
				["gui.factor.hint"] = "unter 1.0 = schneller, über 1.0 = langsamer",
				["gui.breaks"] = "Pausen",
				["gui.break.change"] = "Pause beim Schichtwechsel (halb davor, halb danach)",
				["gui.break.mid"] = "Pause in der Mitte jeder Schicht",
				["gui.preview"] = "24-h-Vorschau:  hell = Tag  ·  dunkel = Nacht  ·  rot = Pause",
				["gui.example"] = "Beispiel 20 Wagen: Start {0} → fertig {1}",
				["gui.timelimit"] = "Dynamischer Job-Zeitfaktor",
				["gui.timelimit.example"] = "Extra-Bonuszeit für 20 Wagen: {0} Echtzeit",
				["gui.lamp"] = "Statuslampe (Leuchte am Mast)",
				["gui.lamp.light"] = "Zusätzlicher Lichtschein",
				["gui.lamp.corona"] = "Corona bei Nacht (Lichthof wie bei Straßenlaternen)",
				["gui.lamp.brightness"] = "Leuchtstärke (1.0 = wie Bahnhofs-Beacon)",
				["gui.lamp.corona.size"] = "Größe der Corona",
				["gui.autodetect"] = "Während der Beladung hinzugestellte Züge erkennen",
				["gui.alt"] = "Ausweichen auf Ersatz-Ladegleise erlauben",
				["gui.alt.off"] = "Aus",
				["gui.alt.occupied"] = "Wenn Original belegt",
				["gui.alt.always"] = "Immer",
				["gui.alt.hint"] = "Gleicher Bahnhof, gleiche Gruppe (z. B. D8L/D9L statt D7L) – Zug dort abstellen und Hebel ziehen",
				["line.redirect"] = "{0}: Ersatz für {1}",
				["gui.alt.penalty"] = "Ausweichen bestrafen (Lohn und Bonus)",
				["gui.alt.penalty.occupied"] = "Originalgleis beschäftigt / fertig aber belegt",
				["gui.alt.penalty.free"] = "Originalgleis frei",
				["gui.parallel"] = "Mehrere Jobs auf einem Gleis gleichzeitig abfertigen",
				["gui.idle"] = "Standgeld: Abzug, wenn ein fertiger Zug am Ladegleis stehen bleibt",
				["gui.clockrate"] = "Uhrgeschwindigkeit (Spielzeit : Echtzeit)",
				["gui.clockrate.auto"] = "Automatisch",
				["gui.clockrate.manual"] = "Manuell",
				["gui.clockrate.value"] = "Spielzeit pro Echtzeit",
				["gui.clockrate.detected"] = "Erkannt: aus dem Spiel {0} · gemessen {1}",
				["gui.idle.rate"] = "Abzug pro Spielstunde",
				["gui.idle.grace"] = "Freie Standzeit",
				["gui.warn.clock.title"] = "ACHTUNG: Die Spielzeit läuft {0}-mal schneller als Echtzeit!",
				["gui.warn.clock.text"] = "Bei dieser Geschwindigkeit lassen sich Zeitlimits und Abzüge (Standgeld, Bonuszeit) kaum noch vermeiden.",
				["hdr.job"] = "JOB",
				["report.alt"] = "Abzug Ersatz für {0} (-{1} %)",
				["report.idle"] = "Abzug Standgeld {0} (-{1} %)",
				["report.both"] = "Abzüge: Gleis -{0} %, Standgeld -{1} %",
				["report.multi"] = "Abzüge: {0}",
				["report.short.alt"] = "Gleis -{0} %",
				["report.short.idle"] = "Standgeld -{0} %",
				["hdr.cars"] = "WAGEN",
				["hdr.status"] = "STATUS",
				["hdr.eta"] = "FERTIG",
				["pen.alt"] = "Ersatz für {0} ({1})",
				["pen.alt.occupied"] = "belegt",
				["pen.alt.free"] = "frei",
				["pen.idle"] = "Standzeit {0}",
				["line.moving.short"] = "STOPP: BEWEGUNG",
				["line.missing.short"] = "STOPP: WAGEN FEHLEN",
				["gui.parallel.factor"] = "Ladezeit-Multiplikator bei gleichzeitiger Abfertigung",
				["line.penalty"] = "-{0} %",
				["gui.language"] = "Sprache",
				["gui.radioclock"] = "Spieluhrzeit im Funkgerät",
				["gui.radioclock.24h"] = "24-Stunden-Format",
				["gui.machineclock"] = "Spieluhrzeit in der Kopfzeile der Lademaschine",
				["gui.language.auto"] = "Spiel",
				["gui.debug"] = "Debug-Log",
				["gui.reset"] = "ALLE SEQUENZEN ZURÜCKSETZEN",
				["gui.minutes"] = "{0} min",
			},
		};

		public static IEnumerable<string> AvailableLanguages => langs.Keys;

		// alle Spielsprachen (außer EN/DE, die stehen oben) einhängen
		static Loc()
		{
			LocLanguages.AddTo(langs);
		}

		// Spielsprache → Schlüssel unserer Tabellen
		// Varianten: zh-CN / zh-TW, pt (Portugal) / pt-BR, Norwegisch nb/nn/no → "no"
		private static string ResolveCulture(System.Globalization.CultureInfo cc)
		{
			string name = cc.Name ?? "";
			string two = cc.TwoLetterISOLanguageName ?? "en";

			switch (two)
			{
				case "zh":
					return name.Contains("TW") || name.Contains("HK") || name.Contains("MO") || name.Contains("Hant") ? "zh-TW" : "zh-CN";
				case "pt":
					return name.Contains("BR") ? "pt-BR" : "pt";
				case "nb":
				case "nn":
				case "no":
					return "no";
			}

			if (langs.ContainsKey(name))
				return name;

			return two;
		}

		public static void LoadExternal(string modPath)
		{
			try
			{
				string dir = Path.Combine(modPath, "lang");
				if (!Directory.Exists(dir))
					return;

				foreach (string file in Directory.GetFiles(dir, "*.json"))
				{
					string code = Path.GetFileNameWithoutExtension(file);
					string json = File.ReadAllText(file, new UTF8Encoding(false)).TrimStart('﻿');

					if (!langs.TryGetValue(code, out var dict))
					{
						dict = new Dictionary<string, string>();
						langs[code] = dict;
					}

					foreach (var prop in JObject.Parse(json).Properties())
						dict[prop.Name] = prop.Value.ToString();

					Main.Log($" Language file loaded: {code}");
				}
			}
			catch (Exception e)
			{
				UnityEngine.Debug.LogWarning("[LongerLoadingDelay] Could not load language files: " + e.Message);
			}
		}

		public static string CurrentCode
		{
			get
			{
				string setting = Main.Settings?.Language ?? "auto";

				if (!string.IsNullOrEmpty(setting) && setting != "auto")
					return setting;

				try
				{
					var cc = LocalizationAPI.CC;
					if (cc != null)
						return ResolveCulture(cc);
				}
				catch
				{
					// I2 noch nicht bereit (Hauptmenü)
				}

				return "en";
			}
		}

		public static string T(string key)
		{
			if (langs.TryGetValue(CurrentCode, out var dict) && dict.TryGetValue(key, out var text))
				return text;

			if (langs["en"].TryGetValue(key, out var en))
				return en;

			return key;
		}

		public static string T(string key, params object[] args)
		{
			try
			{
				return string.Format(T(key), args);
			}
			catch (FormatException)
			{
				return T(key);
			}
		}

		// Vanilla-Texte des Spiels (bereits in allen Spielsprachen vorhanden)
		public static string Game(string key)
		{
			try
			{
				return LocalizationAPI.L(key);
			}
			catch
			{
				return key;
			}
		}
	}
}
