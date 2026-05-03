//FILE:		LongerLoadingDelayMain.cs

using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using UnityEngine;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;
using DV;
using DV.Utils;
using DV.TimeKeeping;
using DV.Logic.Job;
using DV.ThingTypes;

namespace LongerLoadingDelay
{
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry = null!;
        public static Settings Settings = null!;
        public static bool Enabled;
		
		public static void Log(string msg)
		{
			if (Settings != null && Settings.EnableDebug)
			{
				Debug.Log("[LongerLoadingDelay] " + msg);
			}
		}

        public static List<LongerLoadingDelay_SequenceData> activeSequences = new List<LongerLoadingDelay_SequenceData>();

        private static WorldClockController? cachedClock;
		
		// =========================
		// DEBUG: CLEAR ALL SEQUENCES
		// =========================
		public static void ClearAllSequences()
		{
			Log(" <<< RESET MOD >>>");

			activeSequences.Clear();

			LongerLoadingDelay_Updater.ResetTerrainRegistration();
		}

        // =========================
        // GAME TIME
        // =========================
        public static double GetGameTime()
        {
            if (cachedClock == null)
                cachedClock = UnityEngine.Object.FindObjectOfType<WorldClockController>();

            if (cachedClock == null)
                return 0;

            var tuple = cachedClock.GetCurrentAnglesAndTimeOfDay();

            if (!tuple.validTime)
                return 0;

            return tuple.timeOfDay.ToOADate();
        }

        // =========================
        // LOAD MOD
        // =========================
        static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings = Settings.Load<Settings>(modEntry);

            ModEntry.OnGUI = OnGUI;
            ModEntry.OnSaveGUI = OnSaveGUI;
            ModEntry.OnToggle = OnToggle;

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            return true;
        }

        // =========================
        // ENABLE / DISABLE
        // =========================
        static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            return true;
        }

        // =========================
        // GUI
        // =========================
        static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            Settings!.Draw(modEntry);
        }

        static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings!.Save(modEntry);
        }
		
		public static void CleanupAppliedSequences()
		{
			if (activeSequences == null)
				return;

			for (int i = activeSequences.Count - 1; i >= 0; i--)
			{
				var seq = activeSequences[i];

				if (seq.status != "applied")
					continue;

				var job = FindJobById(seq.jobID);

				if (job == null)
				{
					activeSequences.RemoveAt(i);
				}
			}
		}
		
		public static Job? FindJobById(string jobID)
		{
			var jm = SingletonBehaviour<JobsManager>.Instance;
			if (jm == null || string.IsNullOrEmpty(jobID))
				return null;

			var job = jm.currentJobs.FirstOrDefault(j => j.ID == jobID);
			if (job != null)
				return job;

			foreach (var st in StationController.allStations)
			{
				if (st?.logicStation?.availableJobs == null)
					continue;

				job = st.logicStation.availableJobs.FirstOrDefault(j => j.ID == jobID);
				if (job != null)
					return job;
			}

			return null;
		}
    }

    // =========================
    // SETTINGS
    // =========================
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        [Draw("Loading time per wagon (seconds)", Min = 10, Max = 600)]
		public int LoadingDelay = 30;
		public bool EnableDebug = false;

		public void Draw(UnityModManager.ModEntry modEntry)
		{
			GUILayout.BeginHorizontal();

			GUILayout.Label("Loading time per wagon:", GUILayout.ExpandWidth(false));

			float slider = GUILayout.HorizontalSlider(LoadingDelay, 10, 600, GUILayout.Width(200));

			LoadingDelay = Mathf.RoundToInt(slider / 10f) * 10;

			int minutes = LoadingDelay / 60;
			int seconds = LoadingDelay % 60;

			GUILayout.Label($" {minutes}m {seconds:00}s", GUILayout.ExpandWidth(false));

			GUILayout.EndHorizontal();

			GUILayout.Space(10);

			float extraMinutes = GetExtraTimeMinutes();

            GUILayout.BeginVertical("box");
            GUILayout.Label("<b>Estimated shunting time limits:</b>");
            GUILayout.Space(5);

            DrawTimeInfo("1 drop off", 18f, extraMinutes);
			DrawTimeInfo("2 drop offs", 36f, extraMinutes);
			DrawTimeInfo("3 drop offs", 54f, extraMinutes);

            GUILayout.EndVertical();
			
			// =========================
			// DEBUG TOGGLE (RIGHT ALIGNED)
			// =========================
			GUILayout.BeginHorizontal();

			// linker Platz (drückt Checkbox nach rechts)
			GUILayout.FlexibleSpace();

			// Checkbox + Label
			EnableDebug = GUILayout.Toggle(EnableDebug, "", GUILayout.ExpandWidth(false));

			GUILayout.EndHorizontal();
			
			// =========================
			// DEBUG SECTION
			// =========================
			if (EnableDebug)
			{
				GUILayout.Space(10);
				GUILayout.BeginVertical("box");

				GUILayout.Label("<b>DEBUG</b>");

				if (GUILayout.Button("RESET THE MOD!!!", GUILayout.Width(300)))
				{
					Main.ClearAllSequences();
				}

				GUILayout.EndVertical();
			}
        }

        private void DrawTimeInfo(string label, float baseMinutes, float extraMinutes)
		{
			float totalMinutes = baseMinutes + extraMinutes;

			GUILayout.Label($"{label}: <b>{totalMinutes:F1} min</b>");
		}

        public void OnChange() { }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public float GetExtraTimeMinutes()
		{
			float delaySeconds = LoadingDelay;

			float extraRealMinutes = (delaySeconds / 60f) * 20f;
			return extraRealMinutes;
		}
    }
}