//FILE:		LongerLoadingDelayPatch.cs

using System;
using System.Collections;
using System.Reflection;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// CONTROLLER: Updater + Lampe anhängen
	// =========================
	[HarmonyPatch(typeof(WarehouseMachineController), "OnEnable")]
	public static class Controller_OnEnable_Patch
	{
		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			if (__instance == null)
				return;

			LoadingService.EnsureUpdater();
			LoadingLamp.Attach(__instance);
		}
	}

	// =========================
	// HEBEL: Vanilla-Coroutine ersetzen
	// =========================
	// Gilt für StartLoadSequence/StartUnloadSequence (Hebel) und ActivateExternally.
	[HarmonyPatch(typeof(WarehouseMachineController), "DelayedLoadUnload")]
	public static class Controller_DelayedLoadUnload_Patch
	{
		[HarmonyPrefix]
		static bool Prefix(WarehouseMachineController __instance, bool isLoading, ref IEnumerator __result)
		{
			if (!Main.Enabled)
				return true;

			// Multiplayer-Client: die Multiplayer-Mod leitet den Hebel an den Host weiter – hier nie selbst beladen
			__result = Mp.IsClient ? Nothing() : LoadingService.LeverCoroutine(__instance, isLoading);
			return false;
		}

		private static IEnumerator Nothing()
		{
			yield break;
		}
	}

	// =========================
	// DISPLAY
	// =========================
	[HarmonyPatch(typeof(WarehouseMachineController), nameof(WarehouseMachineController.UpdateScreen))]
	public static class Controller_UpdateScreen_Patch
	{
		[HarmonyPostfix]
		static void Postfix(WarehouseMachineController __instance)
		{
			if (!Main.Enabled || __instance == null)
				return;

			LoadingService.ApplyDisplay(__instance);
			MachineClock.Apply(__instance);
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
			if (Mp.IsClient)
				return;		// gespeichert wird nur beim Host

			try
			{
				__instance.data.SetJObject(SaveData.Key, SaveData.Write());
			}
			catch (Exception e)
			{
				Debug.LogError("[LongerLoadingDelay] Save failed: " + e);
			}
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
			LoadingService.ResetRuntime();
			LoadingService.Sequences.Clear();
			AltTracks.Reset();
			Penalties.Reset();

			try
			{
				var data = __instance.GetSaveGameData();

				var v2 = data.GetJObject(SaveData.Key);
				if (v2 != null)
				{
					SaveData.Read(v2);
					return;
				}

				var v1 = data.GetJObject(SaveData.LegacyKey);
				if (v1 != null)
					SaveData.ReadLegacy(v1);
				else
					Main.Log(" No saved data");
			}
			catch (Exception e)
			{
				Debug.LogError("[LongerLoadingDelay] Load failed: " + e);
			}
		}
	}

	[HarmonyPatch(typeof(StartGameData_NewCareer), "PrepareNewSaveData")]
	public static class NewCareer_ResetPatch
	{
		[HarmonyPostfix]
		static void Postfix()
		{
			LoadingService.ResetRuntime();
			LoadingService.Sequences.Clear();
			AltTracks.Reset();
			Penalties.Reset();
			Main.Log(" Reset (new career)");
		}
	}

	// =========================
	// JOB-ZEITLIMIT
	// =========================
	[HarmonyPatch(typeof(JobsGenerator), nameof(JobsGenerator.CreateShuntingLoadJob))]
	public static class ShuntingLoadJobLimit_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job __result) => JobTimeHelper.Apply(__result);
	}

	[HarmonyPatch(typeof(JobsGenerator), nameof(JobsGenerator.CreateShuntingUnloadJob))]
	public static class ShuntingUnloadJobLimit_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job __result) => JobTimeHelper.Apply(__result);
	}

	static class JobTimeHelper
	{
		private static readonly MethodInfo? setter = AccessTools.PropertySetter(typeof(Job), nameof(Job.TimeLimit));

		public static void Apply(Job job)
		{
			if (!Main.Enabled || job == null || setter == null || Mp.IsClient)
				return;		// Client: Zeitlimit kommt fertig vom Host

			float vanilla = job.TimeLimit;
			if (vanilla <= 0f)
				return;		// Job ohne Zeitlimit

			int cars = LoadingService.CountWarehouseCars(job);
			if (cars <= 0)
				return;

			float extra = LoadingService.ExtraTimeLimitSeconds(cars);
			setter.Invoke(job, new object[] { vanilla + extra });

			Main.Log($" [{job.ID}] TIME LIMIT {vanilla:F0} + {extra:F0} s ({cars} cars)");
		}
	}

	// =========================
	// LOHNABZUG FÜR ERSATZGLEIS
	// =========================
	// Bonus = Basis × 0.5 und nutzt GetBasePaymentForTheJob → Abzug wirkt auf Lohn UND Bonus.
	[HarmonyPatch(typeof(Job), nameof(Job.GetBasePaymentForTheJob))]
	public static class JobPayment_AltTrackPenalty_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job __instance, ref float __result)
		{
			if (__instance == null)
				return;

			__result *= LoadPay.Factor(__instance);
			__result *= Penalties.PayoutFactor(__instance.ID);
		}
	}

	// =========================
	// SPEICHERFORMAT
	// =========================
	public static class SaveData
	{
		public const string Key = "LongerLoadingDelay_Data_v2";
		public const string LegacyKey = "LongerLoadingDelay_Data";

		public static JObject Write()
		{
			var arr = new JArray();

			foreach (var s in LoadingService.Sequences)
			{
				// Abgebrochene Sequenzen werden als "wartend" gespeichert
				var state = s.State == SeqState.Interrupted ? SeqState.Queued : s.State;

				arr.Add(new JObject
				{
					["job"] = s.JobId,
					["track"] = s.TrackName,
					["loading"] = s.IsLoading,
					["cars"] = s.JobCars,
					["done"] = s.CarsDone,
					["work"] = s.WorkDone,
					["delay"] = s.DelaySeconds,
					["state"] = state.ToString(),
					["finished"] = s.FinishedAt
				});
			}

			return new JObject
			{
				["version"] = 3,
				["sequences"] = arr,
				["redirects"] = AltTracks.Write(),
				["penalties"] = Penalties.Write()
			};
		}

		public static void Read(JObject root)
		{
			AltTracks.Read(root["redirects"] as JArray);
			Penalties.Read(root["penalties"] as JArray);
			Penalties.ReadLegacy(root["penalized"] as JArray);

			if (root["sequences"] is not JArray arr)
				return;

			int version = root["version"]?.ToObject<int>() ?? 2;

			foreach (var t in arr)
			{
				if (t is not JObject o)
					continue;

				string state = o["state"]?.ToString() ?? "Queued";
				if (!Enum.TryParse(state, out SeqState parsed))
					parsed = SeqState.Queued;

				var seq = new LoadSequence
				{
					JobId = o["job"]?.ToString() ?? "",
					TrackName = o["track"]?.ToString() ?? "",
					IsLoading = o["loading"]?.ToObject<bool>() ?? true,
					JobCars = o["cars"]?.ToObject<int>() ?? 0,
					CarsDone = o["done"]?.ToObject<int>() ?? 0,
					WorkDone = o["work"]?.ToObject<double>() ?? 0,
					DelaySeconds = o["delay"]?.ToObject<double>() ?? Main.Settings.CarDelayGameSeconds,
					State = parsed,
					FinishedAt = o["finished"]?.ToObject<double>() ?? -1
				};

				if (string.IsNullOrEmpty(seq.JobId) || string.IsNullOrEmpty(seq.TrackName) || seq.JobCars <= 0)
					continue;

				seq.DelaySeconds = Math.Max(1.0, seq.DelaySeconds);
				seq.CarsDone = Mathf.Clamp(seq.CarsDone, 0, seq.JobCars);
				// Version 2 rechnete die Zeit pro Wagen aus Echtzeit × gemessener Uhrgeschwindigkeit (teils verfälscht).
				// Ab Version 3 gilt die Einstellung direkt in Spielminuten → laufende Sequenzen umrechnen,
				// angefangener Wagen bleibt anteilig erhalten.
				if (version < 3 && seq.State != SeqState.Done)
				{
					double newDelay = Main.Settings.CarDelayGameSeconds;
					double fraction = seq.WorkDone / seq.DelaySeconds;
					seq.DelaySeconds = newDelay;
					seq.WorkDone = fraction * newDelay;
				}

				seq.WorkDone = Math.Max(seq.WorkDone, seq.CarsDone * seq.DelaySeconds);		// CarsDone nie reduzieren

				LoadingService.Sequences.Add(seq);
			}

			Main.Log($" LOADED {LoadingService.Sequences.Count} sequences (v2)");
		}

		// Altes Format (bis 1.05): { "<track>": [ { JobID, JobCars, JobType, CarsDone, status, ... } ] }
		public static void ReadLegacy(JObject root)
		{
			double delay = Main.Settings.CarDelayGameSeconds;

			foreach (var prop in root.Properties())
			{
				if (prop.Value is not JArray arr)
					continue;

				foreach (var t in arr)
				{
					if (t is not JObject o)
						continue;

					string status = o["status"]?.ToString() ?? "";
					int cars = o["JobCars"]?.ToObject<int>() ?? 0;
					int done = Mathf.Clamp(o["CarsDone"]?.ToObject<int>() ?? 0, 0, Math.Max(0, cars));

					var seq = new LoadSequence
					{
						JobId = o["JobID"]?.ToString() ?? "",
						TrackName = prop.Name,
						IsLoading = (o["JobType"]?.ToString() ?? "LOAD") == "LOAD",
						JobCars = cars,
						CarsDone = done,
						DelaySeconds = delay,
						WorkDone = done * delay,
						State = status == "done" || status == "applied" ? SeqState.Done : SeqState.Queued
					};

					if (string.IsNullOrEmpty(seq.JobId) || seq.JobCars <= 0)
						continue;

					LoadingService.Sequences.Add(seq);
				}
			}

			Main.Log($" MIGRATED {LoadingService.Sequences.Count} sequences from v1");
		}
	}
}
