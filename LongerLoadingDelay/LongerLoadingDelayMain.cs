//FILE:		LongerLoadingDelayMain.cs

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace LongerLoadingDelay
{
	public static class Main
	{
		public static UnityModManager.ModEntry ModEntry = null!;
		public static Settings Settings = null!;			// wirksame Einstellungen (im MP-Client: vom Host)
		public static Settings LocalSettings = null!;		// eigene, gespeicherte Einstellungen
		public static bool Enabled;

		private static Harmony? harmony;

		// String wird nur gebaut, wenn Debug an ist → Aufrufer nutzen Main.Debug-Check bei teuren Texten
		public static bool DebugOn => Settings != null && Settings.EnableDebug;

		public static void Log(string msg)
		{
			if (DebugOn)
				Debug.Log("[LongerLoadingDelay] " + msg);
		}

		// =========================
		// LOAD MOD
		// =========================
		static bool Load(UnityModManager.ModEntry modEntry)
		{
			ModEntry = modEntry;
			Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
			Settings.Sanitize();
			LocalSettings = Settings;

			Schedule.Rebuild();
			Loc.LoadExternal(modEntry.Path);

			ModEntry.OnGUI = OnGUI;
			ModEntry.OnSaveGUI = OnSaveGUI;
			ModEntry.OnToggle = OnToggle;
			ModEntry.OnLateUpdate = InitMultiplayer;		// erst wenn alle Mods geladen sind

			// genau EIN PatchAll pro Spielsitzung
			if (harmony == null)
			{
				harmony = new Harmony(modEntry.Info.Id);
				harmony.PatchAll(Assembly.GetExecutingAssembly());
			}

			return true;
		}

		// =========================
		// ENABLE / DISABLE
		// =========================
		static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
		{
			Enabled = value;

			if (value)
				LoadingService.EnsureUpdater();

			return true;
		}

		// =========================
		// GUI
		// =========================
		static void OnGUI(UnityModManager.ModEntry modEntry)
		{
			bool before = GUI.changed;
			GUI.changed = false;

			LocalSettings.Draw(modEntry);

			if (GUI.changed)
			{
				if (!ReferenceEquals(Settings, LocalSettings))
					Settings.CopyDisplayPrefsFrom(LocalSettings);		// MP-Client: nur Anzeige ist lokal
				else
					Mp.SendSoon();										// Host: Änderung sofort an Clients
			}

			GUI.changed |= before;
		}

		static void OnSaveGUI(UnityModManager.ModEntry modEntry)
		{
			LocalSettings.Save(modEntry);		// nie die Host-Werte in die eigene Datei schreiben
		}

		// =========================
		// MULTIPLAYER
		// =========================
		private static void InitMultiplayer(UnityModManager.ModEntry modEntry, float dt)
		{
			modEntry.OnLateUpdate = null;
			Mp.Initialize(modEntry);
		}

		// Client: spielrelevante Werte vom Host übernehmen (eigene Datei bleibt unangetastet)
		public static void ApplyHostSettings(System.IO.BinaryReader r)
		{
			if (ReferenceEquals(Settings, LocalSettings))
				Settings = LocalSettings.CloneSettings();

			Settings.ReadGameplay(r);
			Settings.CopyDisplayPrefsFrom(LocalSettings);
			Schedule.Rebuild();
		}

		public static void RestoreLocalSettings()
		{
			Settings = LocalSettings;
			Schedule.Rebuild();
		}
	}
}
