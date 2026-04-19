//FILE:		LongerLoadingDelayPatch.cs

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Newtonsoft.Json.Linq;
using DV;
using DV.Utils;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.TerrainSystem;

namespace LongerLoadingDelay
{
    // =========================
    // DATA
    // =========================
    [Serializable]
    public class LongerLoadingDelay_SequenceData
    {
        public string trackID = "";
        public string jobID = "";
        public int JobCars;
        public string JobType = "LOAD";
        public float DelayPerCar;
        public float Timer;
        public int CarsDone;
        public string status = "";
    }

    // =========================
    // TILE LIST
    // =========================
    public static class WarehouseTiles
    {
        public static readonly HashSet<Vector2Int> Tiles = new()
        {
            new Vector2Int(11,17),
            new Vector2Int(12,21),
            new Vector2Int(3,17),
            new Vector2Int(4,21),
            new Vector2Int(4,26),
            new Vector2Int(18,26),
            new Vector2Int(22,22),
            new Vector2Int(25,21),
            new Vector2Int(23,28),
            new Vector2Int(29,30),
            new Vector2Int(30,21),
            new Vector2Int(25,6),
            new Vector2Int(24,6),
            new Vector2Int(24,5),
            new Vector2Int(19,2),
            new Vector2Int(16,6),
            new Vector2Int(15,14),
            new Vector2Int(11,13),
            new Vector2Int(9,12),
            new Vector2Int(3,10),
            new Vector2Int(2,4),
            new Vector2Int(10,7)
        };
    }

    // =========================
    // GLOBAL UPDATER
    // =========================
    public class LongerLoadingDelay_Updater : MonoBehaviour
    {
        private Vector2Int? lastCoord;

        void Awake()
        {
            StartCoroutine(RegisterTerrain());
        }

        IEnumerator RegisterTerrain()
        {
            while (TerrainGrid.Instance == null || !TerrainGrid.Instance.IsInitialized)
                yield return null;

            TerrainGrid.Instance.TerrainsMoved += OnTerrainsMoved;

            Main.Log("[LongerLoadingDelay] TerrainGrid registered");
        }

        void OnTerrainsMoved()
        {
            var coord = TerrainGrid.Instance?.currentCenterCoord;

            if (!coord.HasValue)
                return;

            if (coord == lastCoord)
                return;

            lastCoord = coord;

            if (!IsNearWarehouseTile(coord.Value))
				return;

            Main.Log("[LongerLoadingDelay] Entered warehouse tile → " + coord.Value);

            CoroutineManager.Instance.Run(WaitForStreaming());
        }
		
		static bool IsNearWarehouseTile(Vector2Int coord)
		{
			foreach (var t in WarehouseTiles.Tiles)
			{
				if (Mathf.Abs(t.x - coord.x) <= 1 &&
					Mathf.Abs(t.y - coord.y) <= 1)
				{
					return true;
				}
			}
			return false;
		}

        static IEnumerator WaitForStreaming()
        {
            yield return new WaitForSeconds(0.5f);

            while (!WorldStreamingInit.IsStreamingDone)
                yield return null;

            Main.Log("[LongerLoadingDelay] Streaming ready → processing warehouses");

            ProcessAllWarehouses();
        }

        static void ProcessAllWarehouses()
        {
            var all = UnityEngine.Object.FindObjectsOfType<WarehouseMachineController>();

            Main.Log("[LongerLoadingDelay] Found controllers: " + all.Length);

            foreach (var ctrl in all)
            {
                TryProcess(ctrl);
            }
        }

        static void TryProcess(WarehouseMachineController ctrl)
		{
			if (!Main.Enabled)
				return;

			if (ctrl == null || ctrl.warehouseMachine == null)
				return;

			string track = ctrl.warehouseTrackName;

			var seq = Main.activeSequences.Find(s => s.trackID == track);

			if (seq == null)
			{
				Main.Log("[LongerLoadingDelay] No sequence for " + track);
				return;
			}

			Main.Log($"[LongerLoadingDelay] PROCESS {track} status={seq.status} CarsDone={seq.CarsDone}/{seq.JobCars}");

			bool isLoading = seq.JobType == "LOAD";
			var machine = ctrl.warehouseMachine;

			// =========================
			// HELPER: REAL STATE CHECK
			// =========================
			int GetActuallyLoadedCars(WarehouseMachine m, bool loading)
			{
				int loaded = 0;

				var jobsLocal = m.GetCurrentLoadUnloadData(
					loading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading);

				if (jobsLocal == null)
					return 0;

				foreach (var job in jobsLocal)
				{
					if (job.tasksAvailableToProcess == null)
						continue;

					foreach (var task in job.tasksAvailableToProcess)
					{
						foreach (var car in task.cars)
						{
							if (loading)
							{
								if (car.LoadedCargoAmount > 0f)
									loaded++;
							}
							else
							{
								if (car.LoadedCargoAmount < 1f)
									loaded++;
							}
						}
					}
				}

				return loaded;
			}

			// =========================
			// COMMON STATE
			// =========================
			int actuallyLoaded = GetActuallyLoadedCars(machine, isLoading);

			Main.Log($"[LongerLoadingDelay] REAL STATE loaded={actuallyLoaded}");

			// =========================
			// DONE → VERIFY & FIX
			// =========================
			if (seq.status == "done")
			{
				int missing = seq.JobCars - actuallyLoaded;

				Main.Log($"[LongerLoadingDelay] DONE CHECK target={seq.JobCars} actual={actuallyLoaded} missing={missing}");

				if (missing <= 0)
				{
					ctrl.ActivateExternally();
					
					seq.status = "applied";

					Main.Log("[LongerLoadingDelay] MARKED APPLIED " + seq.jobID);
					return;
				}

				Main.Log("[LongerLoadingDelay] DONE → applying missing cars");

				var jobs = machine.GetCurrentLoadUnloadData(
					isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading);

				if (jobs == null || jobs.Count == 0)
				{
					Main.Log("[LongerLoadingDelay] DONE but no jobs found");
					return;
				}

				int processed = 0;

				foreach (var job in jobs)
				{
					if (job.tasksAvailableToProcess == null)
						continue;

					foreach (var task in job.tasksAvailableToProcess)
					{
						foreach (var car in task.cars)
						{
							if (processed >= missing)
								break;

							if (isLoading)
								machine.LoadOneCarOfTask(task);
							else
								machine.UnloadOneCarOfTask(task);

							processed++;

							Main.Log($"[LongerLoadingDelay] DONE APPLY {processed}/{missing}");
						}

						if (processed >= missing)
							break;
					}

					if (processed >= missing)
						break;
				}
				ctrl.ActivateExternally();
				
				seq.status = "applied";

				Main.Log("[LongerLoadingDelay] DONE APPLIED " + seq.jobID);
				return;
			}

			// =========================
			// ACTIVE → CATCH-UP
			// =========================
			int targetCars = seq.CarsDone;
			int missingActive = targetCars - actuallyLoaded;

			Main.Log($"[LongerLoadingDelay] ACTIVE target={targetCars} actual={actuallyLoaded} missing={missingActive}");

			if (missingActive <= 0)
			{
				Main.Log("[LongerLoadingDelay] No catch-up needed");

				ctrl.ActivateExternally();
				return;
			}

			var jobsActive = machine.GetCurrentLoadUnloadData(
				isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading);

			if (jobsActive == null || jobsActive.Count == 0)
			{
				Main.Log("[LongerLoadingDelay] No jobs found during active apply");
				return;
			}

			int processedActive = 0;

			foreach (var job in jobsActive)
			{
				if (job.tasksAvailableToProcess == null)
					continue;

				foreach (var task in job.tasksAvailableToProcess)
				{
					foreach (var car in task.cars)
					{
						if (processedActive >= missingActive)
							break;

						if (isLoading)
							machine.LoadOneCarOfTask(task);
						else
							machine.UnloadOneCarOfTask(task);

						processedActive++;

						Main.Log($"[LongerLoadingDelay] APPLY {(isLoading ? "LOAD" : "UNLOAD")} {processedActive}/{missingActive}");
					}

					if (processedActive >= missingActive)
						break;
				}

				if (processedActive >= missingActive)
					break;
			}

			Main.Log($"[LongerLoadingDelay] ACTIVE applied {processedActive}/{missingActive}");

			ctrl.ActivateExternally();
		}

        void Update()
        {
            if (!Main.Enabled) return;
			
			if (!WorldStreamingInit.IsStreamingDone)
				return;

			var jm = SingletonBehaviour<JobsManager>.Instance;

			if (jm == null || jm.currentJobs == null)
				return;

			Main.CleanupAppliedSequences();

            float deltaMinutes = Time.deltaTime / 60f;

            for (int i = Main.activeSequences.Count - 1; i >= 0; i--)
			{
				var seq = Main.activeSequences[i];

				var job = Main.FindJobById(seq.jobID);

				if (job == null)
					continue;

				if (seq.status != "active") continue;

				seq.Timer += deltaMinutes;

				int newCars = Mathf.FloorToInt(seq.Timer / seq.DelayPerCar);

				if (newCars > seq.CarsDone)
					seq.CarsDone = newCars;

				if (seq.CarsDone >= seq.JobCars)
				{
					seq.CarsDone = seq.JobCars;
					seq.status = "done";

					Main.Log("[LongerLoadingDelay] DONE " + seq.jobID);

					// =========================
					// NEXT JOB ACTIVATION
					// =========================
					var next = Main.activeSequences
						.FirstOrDefault(s =>
							s.trackID == seq.trackID &&
							s.status == "waiting"
						);

					if (next != null)
					{
						next.status = "active";
						next.Timer = 0f;
						next.CarsDone = 0;

						Main.Log("[LongerLoadingDelay] ACTIVATED NEXT JOB " + next.jobID);
					}
					else
					{
						Main.Log("[LongerLoadingDelay] NO NEXT JOB FOUND");
					}
				}
			}
        }
    }

    // =========================
    // INIT
    // =========================
    [HarmonyPatch(typeof(WarehouseMachineController), "OnEnable")]
    public static class InitUpdater
    {
        static bool created = false;

        [HarmonyPostfix]
        static void Postfix()
        {
            if (created) return;
            created = true;

            var go = new GameObject("LongerLoadingDelay_Updater");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<LongerLoadingDelay_Updater>();

            Main.Log("[LongerLoadingDelay] Updater created");
        }
    }

    // =========================
    // DELAY PATCH
    // =========================
    [HarmonyPatch(typeof(WarehouseMachineController), "DelayedLoadUnload")]
    public static class DelayPatch
    {
        [HarmonyPrefix]
        static void Prefix(ref float delayBetweenActions)
        {
            if (Main.Enabled)
                delayBetweenActions = Main.Settings.LoadingDelay;
        }
    }

    // =========================
    // SAVE
    // =========================
    [HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
    public static class SavePatch
    {
        [HarmonyPostfix]
        static void Postfix(SaveGameManager __instance)
        {
            var root = new JObject();
            var grouped = new Dictionary<string, JArray>();

            foreach (var s in Main.activeSequences)
            {
                if (!grouped.ContainsKey(s.trackID))
                    grouped[s.trackID] = new JArray();

                grouped[s.trackID].Add(new JObject
                {
                    ["JobID"] = s.jobID,
                    ["JobCars"] = s.JobCars,
                    ["JobType"] = s.JobType,
                    ["DelayPerCar"] = s.DelayPerCar,
                    ["Timer"] = s.Timer,
                    ["CarsDone"] = s.CarsDone,
                    ["status"] = s.status
                });
            }

            foreach (var kvp in grouped)
                root[kvp.Key] = kvp.Value;

            __instance.data.SetJObject("LongerLoadingDelay_Data", root);
        }
    }

    // =========================
    // LOAD
    // =========================
    [HarmonyPatch(typeof(StartGameData_FromSaveGame), "DoLoad")]
    public static class LoadPatch
    {
        [HarmonyPostfix]
        static void Postfix(StartGameData_FromSaveGame __instance)
        {
            var root = __instance.GetSaveGameData()
                .GetJObject("LongerLoadingDelay_Data");

            if (root == null) return;

            Main.activeSequences.Clear();

            foreach (var prop in root.Properties())
            {
                string track = prop.Name;
                var arr = prop.Value as JArray;

                if (arr == null) continue;

                foreach (var t in arr)
                {
                    if (t is not JObject obj) continue;

                    Main.activeSequences.Add(new LongerLoadingDelay_SequenceData
                    {
                        trackID = track,
                        jobID = obj["JobID"]?.ToString() ?? "",
                        JobCars = obj["JobCars"]?.ToObject<int>() ?? 0,
                        JobType = obj["JobType"]?.ToString() ?? "LOAD",
                        DelayPerCar = obj["DelayPerCar"]?.ToObject<float>() ?? 1f,
                        Timer = obj["Timer"]?.ToObject<float>() ?? 0f,
                        CarsDone = obj["CarsDone"]?.ToObject<int>() ?? 0,
                        status = obj["status"]?.ToString() ?? "active"
                    });
                }
            }

            Main.Log("[LongerLoadingDelay] LOADED");
        }
    }	
	
	// =========================
	// SEQUENCE CREATION
	// =========================
	[HarmonyPatch(typeof(WarehouseMachineController), "StartLoadSequence")]
	public static class SequenceStart_Load
	{
		[HarmonyPrefix]
		static void Prefix(WarehouseMachineController __instance)
		{
			Main.Log("[LongerLoadingDelay] StartLoadSequence TRIGGERED");
			SequenceHelper.CreateSequence(__instance, "LOAD");
		}

		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			ForceImmediateScreenUpdate(__instance);
		}
		
		static void ForceImmediateScreenUpdate(WarehouseMachineController ctrl)
		{
			if (ctrl == null)
				return;

			ctrl.UpdateScreen();
		}
	}

	[HarmonyPatch(typeof(WarehouseMachineController), "StartUnloadSequence")]
	public static class SequenceStart_Unload
	{
		[HarmonyPrefix]
		static void Prefix(WarehouseMachineController __instance)
		{
			Main.Log("[LongerLoadingDelay] StartUnloadSequence TRIGGERED");
			SequenceHelper.CreateSequence(__instance, "UNLOAD");
		}

		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			ForceImmediateScreenUpdate(__instance);
		}
		
		static void ForceImmediateScreenUpdate(WarehouseMachineController ctrl)
		{
			if (ctrl == null)
				return;

			ctrl.UpdateScreen();
		}
	}
	
	// =========================
	// SEQUENCE HELPER
	// =========================
	static class SequenceHelper
	{
		public static void CreateSequence(WarehouseMachineController __instance, string type)
		{
			if (!Main.Enabled) return;

			if (__instance == null || __instance.warehouseMachine == null)
			{
				Main.Log("[LongerLoadingDelay] CreateSequence aborted: instance not ready");
				return;
			}

			var data = __instance.warehouseMachine
				.GetCurrentLoadUnloadData(type == "LOAD"
					? WarehouseTaskType.Loading
					: WarehouseTaskType.Unloading);

			if (data == null || data.Count == 0)
			{
				Main.Log("[LongerLoadingDelay] No jobs found for sequence creation");
				return;
			}

			string track = __instance.warehouseTrackName;
			float delayMinutes = Main.Settings.LoadingDelay / 60f;

			Main.Log($"[LongerLoadingDelay] Creating sequences for track={track}");

			foreach (var job in data)
			{
				bool exists = Main.activeSequences.Exists(s => s.jobID == job.id);
				if (exists)
				{
					Main.Log($"[LongerLoadingDelay] Sequence already exists for job={job.id}");
					continue;
				}

				int totalCars = 0;

				if (job.tasksAvailableToProcess != null)
				{
					foreach (var t in job.tasksAvailableToProcess)
						totalCars += t.cars.Count;
				}

				bool hasActive = Main.activeSequences.Exists(
					s => s.trackID == track && s.status == "active"
				);

				var seq = new LongerLoadingDelay_SequenceData
				{
					trackID = track,
					jobID = job.id,
					JobCars = totalCars,
					JobType = type,
					DelayPerCar = delayMinutes,
					Timer = 0f,
					CarsDone = 0,
					status = hasActive ? "waiting" : "active"
				};

				Main.activeSequences.Add(seq);

				Main.Log($"[LongerLoadingDelay] START {type} job={job.id} cars={totalCars} status={seq.status}");
			}
		}
	}
	
	// =========================
	// ABANDON JOB
	// =========================	
    [HarmonyPatch(typeof(JobAbandoner), nameof(JobAbandoner.AbandonJob))]
    public static class JobAbandoner_Patch
    {
        [HarmonyPostfix]
        static void Postfix(JobBooklet jobBooklet)
        {
            if (jobBooklet?.job == null)
                return;

            string jobID = jobBooklet.job.ID;

            Main.Log("[LongerLoadingDelay] ABANDON DETECTED → " + jobID);

            Main.activeSequences.RemoveAll(s => s.jobID == jobID);

            Main.Log("[LongerLoadingDelay] REMOVED FROM SAVE → " + jobID);
        }
    }
	
	// =========================
	// COMPLETE JOB
	// =========================	
	[HarmonyPatch(typeof(JobsManager), nameof(JobsManager.CompleteTheJob))]
	public static class JobCompleted_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job job)
		{
			if (job == null)
				return;

			string jobID = job.ID;

			Main.Log("[LongerLoadingDelay] COMPLETED → " + jobID);

			Main.activeSequences.RemoveAll(s => s.jobID == jobID);

			Main.Log("[LongerLoadingDelay] REMOVED AFTER COMPLETE → " + jobID);
		}
	}
	
	// =========================
	// SHUNTING LOAD
	// =========================
	[HarmonyPatch(typeof(JobsGenerator), nameof(JobsGenerator.CreateShuntingLoadJob))]
	public static class ShuntingLoadJobLimit_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job __result)
		{
			JobTimeHelper.ApplyTime(__result, "LOAD");
		}
	}

	// =========================
	// SHUNTING UNLOAD
	// =========================
	[HarmonyPatch(typeof(JobsGenerator), nameof(JobsGenerator.CreateShuntingUnloadJob))]
	public static class ShuntingUnloadJobLimit_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job __result)
		{
			JobTimeHelper.ApplyTime(__result, "UNLOAD");
		}
	}

	// =========================
	// CORE
	// =========================
	static class JobTimeHelper
	{
		static MethodInfo? setter;

		public static void ApplyTime(Job job, string type)
		{
			if (!Main.Enabled || job == null)
				return;

			float delaySeconds = Main.Settings.LoadingDelay;

			float extraRealMinutes = (delaySeconds / 60f) * 20f;
			float extraGameTime = extraRealMinutes * 60f;

			float vanilla = job.TimeLimit;
			float final = vanilla + extraGameTime;

			if (setter == null)
			{
				setter = typeof(Job)
					.GetProperty("TimeLimit", BindingFlags.Instance | BindingFlags.Public)?
					.GetSetMethod(true);
			}

			if (setter == null)
			{
				Main.Log("[LongerLoadingDelay] ERROR: TimeLimit setter not found");
				return;
			}

			setter.Invoke(job, new object[] { final });

			Main.Log($"[LongerLoadingDelay] {type} FINAL → {vanilla:F1} + {extraGameTime:F1} = {final:F1}");
		}
	}
	
	// =========================
	// SCREEN COUNTDOWN
	// =========================
	[HarmonyPatch(typeof(WarehouseMachineController), "UpdateScreen")]
	public static class WarehouseDisplay_Countdown
	{
		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			if (!Main.Enabled)
				return;

			if (!__instance.LoadOrUnloadOngoing)
				return;

			string track = __instance.warehouseTrackName;

			var seq = Main.activeSequences
				.FirstOrDefault(s =>
					s.trackID == track &&
					(s.status == "active" || s.status == "done"));

			if (seq == null)
				return;

			int remainingCars = seq.JobCars - seq.CarsDone;
			float minutesLeft = remainingCars * seq.DelayPerCar;

			int min = Mathf.FloorToInt(minutesLeft);
			int sec = Mathf.FloorToInt((minutesLeft - min) * 60f);

			string countdown = $"~ {min}:{sec:00} Min";

			if (__instance.displayTitleText == null)
				return;

			string baseText = __instance.displayTitleText.text;

			int idx = baseText.LastIndexOf("~ ");
			if (idx >= 0)
				baseText = baseText.Substring(0, idx).TrimEnd();

			string padded = baseText.PadRight(38);

			__instance.displayTitleText.text = padded + countdown;
		}
	}
}