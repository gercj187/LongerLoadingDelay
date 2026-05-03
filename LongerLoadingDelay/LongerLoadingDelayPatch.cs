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
    // GLOBAL UPDATER
    // =========================
    public class LongerLoadingDelay_Updater : MonoBehaviour
    {
		public static Dictionary<string, bool> syncTriggeredPerTrack = new();

		public static void ResetTerrainRegistration()
		{
			syncTriggeredPerTrack.Clear();
		}

        public static void TryProcess(WarehouseMachineController ctrl)
		{
			if (!Main.Enabled)
				return;

			if (ctrl == null || ctrl.warehouseMachine == null)
				return;

			string track = ctrl.warehouseTrackName;

			var sequences = Main.activeSequences
				.Where(s => s.trackID == track)
				.OrderBy(s => s.status == "active" ? 0 :
							  s.status == "done" ? 1 : 2)
				.ToList();

			if (sequences.Count == 0)
			{
				Main.Log($" {track} : NO SEQUENCES FOUND");
				return;
			}

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
								if (car.LoadedCargoAmount >= 0.99f)
									loaded++;
							}
							else
							{
								if (car.LoadedCargoAmount <= 0.01f)
									loaded++;
							}
						}
					}
				}

				return loaded;
			}

			// =========================
			// MAIN LOOP (ALLE JOBS!)
			// =========================
			foreach (var seq in sequences)
			{
				bool isLoading = seq.JobType == "LOAD";

				int actuallyLoaded = GetActuallyLoadedCars(machine, isLoading);

				Main.Log($" {track} [{seq.jobID}] : STATUS={seq.status} CARS={seq.CarsDone}/{seq.JobCars}");
				Main.Log($" {track} [{seq.jobID}] : LOADINGSTATE={actuallyLoaded}");

				// =========================
				// DONE → FINAL APPLY
				// =========================
				if (seq.status == "done")
				{
					int missing = seq.JobCars - actuallyLoaded;

					Main.Log($" {track} [{seq.jobID}] : MISSING={missing}");

					if (missing > 0)
					{
						int processed = 0;

						var jobs = machine.GetCurrentLoadUnloadData(
							isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading);

						if (jobs == null)
							continue;

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

									Main.Log($" {track} [{seq.jobID}] : DONE {processed}/{missing}");
								}

								if (processed >= missing)
									break;
							}

							if (processed >= missing)
								break;
						}
					}

					seq.status = "applied";
					Main.Log($" {track} [{seq.jobID}] : APPLIED!");

					continue;
				}

				// =========================
				// ACTIVE → CATCH-UP
				// =========================
				if (seq.status == "active")
				{
					actuallyLoaded = Mathf.Clamp(actuallyLoaded, 0, seq.JobCars);

					// =========================
					// CATCH-UP
					// =========================
					float totalBefore = seq.JobCars * seq.DelayPerCar;
					float progressedBefore = totalBefore - seq.Timer;

					int expectedCars = Mathf.FloorToInt(progressedBefore / seq.DelayPerCar);

					if (expectedCars > actuallyLoaded)
					{
						int missing = expectedCars - actuallyLoaded;

						Main.Log($" {track} [{seq.jobID}] : SYNC CARS={missing}");

						int processed = 0;

						var jobs = machine.GetCurrentLoadUnloadData(
							isLoading ? WarehouseTaskType.Loading : WarehouseTaskType.Unloading);

						if (jobs != null)
						{
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
									}

									if (processed >= missing)
										break;
								}

								if (processed >= missing)
									break;
							}
						}

						actuallyLoaded += processed;
					}
					
					float total = seq.JobCars * seq.DelayPerCar;

					float progressed = actuallyLoaded * seq.DelayPerCar;

					seq.Timer = total - progressed;

					seq.Timer = Mathf.Clamp(seq.Timer, 0f, total);

					seq.CarsDone = actuallyLoaded;

					Main.Log($" {track} [{seq.jobID}] : TIMER SYNC={seq.Timer:F2}");

					continue;
				}
			}
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

				// =========================
				// ACTIVE
				// =========================
				if (seq.status == "active")
				{
					seq.Timer -= deltaMinutes;
					seq.Timer = Mathf.Max(0f, seq.Timer);

					float total = seq.JobCars * seq.DelayPerCar;
					float progressed = total - seq.Timer;

					int newCars = Mathf.FloorToInt(progressed / seq.DelayPerCar);

					seq.CarsDone = Mathf.Clamp(newCars, 0, seq.JobCars);

					if (seq.status == "active" && seq.Timer <= 0f && seq.CarsDone >= seq.JobCars)
					{
						seq.CarsDone = seq.JobCars;

						seq.status = "review";
						seq.Timer = Mathf.Max(seq.DelayPerCar, 0.0001f);

						Main.Log($" {seq.trackID} [{seq.jobID}] : REVIEW");
					}

					continue;
				}

				// =========================
				// REVIEW
				// =========================
				if (seq.status == "review")
				{
					seq.Timer -= deltaMinutes;
					seq.Timer = Mathf.Max(0f, seq.Timer);

					if (seq.Timer <= 0f)
					{
						seq.status = "done";

						Main.Log($" {seq.trackID} [{seq.jobID}] : DONE");

						// =========================
						// NEXT JOB
						// =========================
						var next = Main.activeSequences
							.FirstOrDefault(s =>
								s.trackID == seq.trackID &&
								s.status == "waiting"
							);

						if (next != null)
						{
							next.status = "active";
							next.Timer = next.JobCars * next.DelayPerCar;
							next.CarsDone = 0;

							Main.Log($" [{next.trackID}] [{next.jobID}] : STARTED");
						}
						else
						{
							Main.Log($" {seq.trackID} : NO NEXT JOB FOUND");
						}
					}

					continue;
				}

				// =========================
				// DONE
				// =========================
				if (seq.status == "done")
				{
					continue;
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
		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			if (__instance == null)
				return;

			string track = __instance.warehouseTrackName;

			Main.Log($" {track} ACTIVATED!");

			var go = GameObject.Find("LongerLoadingDelay_Updater");

			if (go == null)
			{
				go = new GameObject("LongerLoadingDelay_Updater");
				GameObject.DontDestroyOnLoad(go);
				go.AddComponent<LongerLoadingDelay_Updater>();
			}

			// =========================
			// SYNC CHECK
			// =========================
			if (!Main.Enabled)
				return;

			string trackID = __instance.warehouseTrackName;

			if (!LongerLoadingDelay_Updater.syncTriggeredPerTrack.ContainsKey(trackID))
				LongerLoadingDelay_Updater.syncTriggeredPerTrack[trackID] = false;

			if (LongerLoadingDelay_Updater.syncTriggeredPerTrack[trackID])
				return;

			var seq = Main.activeSequences
				.FirstOrDefault(s => s.trackID == trackID);

			if (seq == null)
				return;

			LongerLoadingDelay_Updater.syncTriggeredPerTrack[trackID] = true;

			Main.Log($" {trackID} SYNCRONISING...");

			CoroutineManager.Instance.Run(DelayedInitialSync_Single(__instance));
		}
		
		static IEnumerator DelayedInitialSync_Single(WarehouseMachineController ctrl)
		{
			while (!WorldStreamingInit.IsStreamingDone)
				yield return null;

			while (!AStartGameData.carsAndJobsLoadingFinished)
				yield return null;

			yield return new WaitForSeconds(0.2f);

			if (ctrl == null)
				yield break;

			if (!ctrl.isActiveAndEnabled)
				yield break;

			if (ctrl.warehouseMachine == null)
				yield break;

			string track = ctrl.warehouseTrackName;

			var seq = Main.activeSequences
				.FirstOrDefault(s => s.trackID == track);

			if (seq == null)
				yield break;

			Main.Log($" {track} SYNCRONISED! ");

			ctrl.ActivateExternally();

			int safety = 0;

			while (safety < 20)
			{
				if (ctrl.warehouseMachine != null)
				{
					var data = ctrl.warehouseMachine.GetCurrentLoadUnloadData(
						WarehouseTaskType.Loading);

					if (data != null && data.Count > 0)
						break;
				}

				safety++;
				yield return new WaitForSeconds(0.1f);
			}

			LongerLoadingDelay_Updater.TryProcess(ctrl);
		}
	}
	
	[HarmonyPatch(typeof(WarehouseMachineController), "OnDisable")]
	public static class ControllerDisablePatch
	{
		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			if (__instance == null)
				return;

			string track = __instance.warehouseTrackName;

			if (LongerLoadingDelay_Updater.syncTriggeredPerTrack.ContainsKey(track))
			{
				LongerLoadingDelay_Updater.syncTriggeredPerTrack[track] = false;

				Main.Log($" {track} DEACTIVATED");
			}
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
			LongerLoadingDelay_Updater.ResetTerrainRegistration();
			
			Main.activeSequences.Clear();

			var root = __instance.GetSaveGameData()
				.GetJObject("LongerLoadingDelay_Data");

			if (root == null)
			{
				Main.Log(" No saved data");
				return;
			}

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

			Main.Log(" LOADED");
			CoroutineManager.Instance.Run(DelayedInitialSync());
		}
		
		static IEnumerator DelayedInitialSync()
		{
			while (!WorldStreamingInit.IsStreamingDone)
				yield return null;

			while (!AStartGameData.carsAndJobsLoadingFinished)
				yield return null;

			yield return new WaitForSeconds(0.2f);

			Main.Log(" INITIAL LOAD SYNC");

			foreach (var ctrl in WarehouseMachineController.allControllers)
			{
				if (ctrl == null)
					continue;

				if (!ctrl.isActiveAndEnabled)
					continue;

				if (ctrl.warehouseMachine == null)
					continue;

				bool shouldWork =
					ctrl.warehouseMachine.AnyTrainToLoadPresentOnTrack() ||
					ctrl.warehouseMachine.AnyTrainToUnloadPresentOnTrack();

				var seq = Main.activeSequences
					.FirstOrDefault(s => s.trackID == ctrl.warehouseTrackName);

				if (seq == null)
					continue;

				Main.Log($" {ctrl.warehouseTrackName} : LOAD SYNC");

				ctrl.ActivateExternally();
				int safety = 0;

				while (safety < 20)
				{
					if (ctrl.warehouseMachine != null)
					{
						var data = ctrl.warehouseMachine.GetCurrentLoadUnloadData(
							WarehouseTaskType.Loading);

						if (data != null && data.Count > 0)
							break;
					}

					safety++;
					yield return new WaitForSeconds(0.1f);
				}

				LongerLoadingDelay_Updater.TryProcess(ctrl);
			}
		}
	}
	
	[HarmonyPatch(typeof(StartGameData_NewCareer), "PrepareNewSaveData")]
	public static class NewCareer_ResetPatch
	{
		[HarmonyPostfix]
		static void Postfix()
		{
			LongerLoadingDelay_Updater.ResetTerrainRegistration();
			Main.Log(" Reset terrain registration (new career)");
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
			if (__instance == null)
				return;
			
			string track = __instance.warehouseTrackName ?? "UNKNOWN";
			Main.Log($" {track} : START LOADING");

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
			if (__instance == null)
				return;
			
			string track = __instance.warehouseTrackName ?? "UNKNOWN";
			Main.Log($" {track} : START UNLOADING");

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
				Main.Log(" CreateSequence aborted: instance not ready");
				return;
			}

			var data = __instance.warehouseMachine
				.GetCurrentLoadUnloadData(type == "LOAD"
					? WarehouseTaskType.Loading
					: WarehouseTaskType.Unloading);

			if (data == null || data.Count == 0)
			{
				Main.Log(" No jobs found for sequence creation");
				return;
			}

			string track = __instance.warehouseTrackName;
			float delayMinutes = Main.Settings.LoadingDelay / 60f;

			Main.Log($" {track} CREATE SEQUENCES:");

			foreach (var job in data)
			{
				bool exists = Main.activeSequences.Exists(s => s.jobID == job.id);
				if (exists)
				{
					Main.Log($" {track} [{job.id}] : SEQUENCE ALREADY EXISTS");
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

				float total = totalCars * delayMinutes;
				
				var seq = new LongerLoadingDelay_SequenceData
				{
					trackID = track,
					jobID = job.id,
					JobCars = totalCars,
					JobType = type,
					DelayPerCar = delayMinutes,
					Timer = total,
					CarsDone = 0,
					status = hasActive ? "waiting" : "active"
				};

				Main.activeSequences.Add(seq);

				Main.Log($" {track} [{job.id}] : START {type} CARS={totalCars} STATUS={seq.status}");
			}
		}
	}
	
	// =========================
	// SCREEN TIMER SYSTEM
	// =========================
	static class WarehouseScreenTimer
	{
		static Dictionary<WarehouseMachineController, Coroutine> activeLoops = new();

		public static void Start(WarehouseMachineController ctrl)
		{
			if (ctrl == null)
				return;

			if (activeLoops.TryGetValue(ctrl, out var oldLoop))
			{
				ctrl.StopCoroutine(oldLoop);
			}

			var newLoop = ctrl.StartCoroutine(UpdateLoop(ctrl));
			activeLoops[ctrl] = newLoop;
		}

		static IEnumerator UpdateLoop(WarehouseMachineController ctrl)
		{
			while (ctrl != null && ctrl.LoadOrUnloadOngoing)
			{
				UpdateText(ctrl);
				yield return new WaitForSeconds(1f);
			}

			if (ctrl != null)
				activeLoops.Remove(ctrl);
		}

		static void UpdateText(WarehouseMachineController ctrl)
		{
			if (ctrl.displayTitleText == null)
				return;

			string track = ctrl.warehouseTrackName;

			var seq = Main.activeSequences
				.Where(s => s.trackID == track)
				.OrderBy(s =>
					s.status == "active" ? 0 :
					s.status == "review" ? 1 :
					s.status == "done" ? 2 : 3)
				.FirstOrDefault();

			if (seq == null)
				return;

			// =========================
			// BASE TEXT CLEAN
			// =========================
			string fullText = ctrl.displayTitleText.text;

			// nur erste Zeile behalten (Vanilla Text)
			string baseText = fullText.Split('\n')[0];

			// =========================
			// COUNTDOWN
			// =========================
			float remainingMinutes;

			if (seq.status != "active")
			{
				remainingMinutes = 0f;
			}
			else
			{
				remainingMinutes = Mathf.Max(0f, seq.Timer);

				if (remainingMinutes < 0.01f)
					remainingMinutes = 0f;
			}

			int totalSeconds = Mathf.FloorToInt(remainingMinutes * 60f);

			int hours = totalSeconds / 3600;
			int minutes = (totalSeconds % 3600) / 60;
			int seconds = totalSeconds % 60;

			string countdown;

			if (seq.status == "review")
			{
				countdown = "CHECKING";
			}
			else
			{
				countdown = $"  {hours:00}:{minutes:00}:{seconds:00}";
			}
			string jobId = seq.jobID;

			// =========================
			// FINAL TEXT
			// =========================
			ctrl.displayTitleText.text =
		$@"{baseText}
[{jobId}]                                          {countdown}";
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

            Main.Log($" [{jobID}] : ABANDON DETECTED");

            Main.activeSequences.RemoveAll(s => s.jobID == jobID);

            Main.Log(" REMOVED FROM SAVE " + jobID);
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

			Main.Log($" [{jobID}] : COMPLETED");

			Main.activeSequences.RemoveAll(s => s.jobID == jobID);

			Main.Log(" REMOVED AFTER COMPLETE " + jobID);
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
				Main.Log(" ERROR: TimeLimit setter not found");
				return;
			}

			setter.Invoke(job, new object[] { final });

			Main.Log($" {type} FINAL {vanilla:F1} + {extraGameTime:F1} = {final:F1}");
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

			WarehouseScreenTimer.Start(__instance);
		}
	}
}