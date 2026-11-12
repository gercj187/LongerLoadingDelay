//FILE:		CargoVisuals.cs

using System;
using System.Collections.Generic;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// LADUNGS-OPTIK ABGLEICHEN
	// =========================
	// Das Originalspiel be-/entlädt nur, während der Spieler an der Maschine steht. Die Mod arbeitet auch weiter,
	// wenn der Spieler weit weg ist – dann ist das Interior der Wagen oft ausgeladen (trainCar.interior == null).
	//
	// CargoModelController.OnCargoLoaded legt das Ladungsmodell unter trainCar.interior an → ohne Interior
	// gibt es eine NullReferenceException MITTEN im Lade-Ereignis (weitere Empfänger laufen nicht mehr).
	// Folge: Ladung in der Logik ≠ Optik ("fertig", aber Ladung sichtbar / unsichtbar).
	//
	// Lösung:
	//   1. Prefix: ohne Interior das Modell NICHT anlegen (kein Fehler), Wagen vormerken
	//   2. Sobald das Interior wieder da ist: Optik an den echten Ladezustand angleichen
	public static class CargoVisuals
	{
		private static readonly HashSet<DV.Logic.Job.Car> pending = new();
		private static readonly List<DV.Logic.Job.Car> buffer = new();
		private static readonly Dictionary<TrainCar, CargoModelController?> controllers = new();

		private static readonly AccessTools.FieldRef<CargoModelController, TrainCar> trainCarRef =
			AccessTools.FieldRefAccess<CargoModelController, TrainCar>("trainCar");

		private static readonly System.Reflection.MethodInfo? onLoaded =
			AccessTools.Method(typeof(CargoModelController), "OnCargoLoaded");
		private static readonly System.Reflection.MethodInfo? onUnloaded =
			AccessTools.Method(typeof(CargoModelController), "OnCargoUnloaded");

		private static float acc;

		/// <summary>Nach jedem von der Mod bearbeiteten Wagen aufrufen.</summary>
		public static void MarkProcessed(DV.Logic.Job.Car? car)
		{
			if (car != null)
				pending.Add(car);
		}

		// 2×/s: vorgemerkte Wagen prüfen, sobald ihr Interior geladen ist
		public static void Tick(float dt)
		{
			if (pending.Count == 0)
				return;

			acc += dt;
			if (acc < 0.5f)
				return;
			acc = 0f;

			var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
			if (registry == null)
				return;

			buffer.Clear();
			buffer.AddRange(pending);

			foreach (var car in buffer)
			{
				if (!registry.logicCarToTrainCar.TryGetValue(car, out var trainCar) || trainCar == null)
				{
					// Wagen gibt es nicht mehr (gelöscht) → vergessen
					if (!registry.logicCarToTrainCar.ContainsKey(car))
						pending.Remove(car);
					continue;
				}

				if (trainCar.interior == null)
					continue;		// noch ausgeladen → später

				try
				{
					Sync(trainCar, car);
				}
				catch (Exception e)
				{
					Main.Log($" CargoVisuals: sync failed for {car.ID}: {e.Message}");
				}

				pending.Remove(car);
			}
		}

		private static void Sync(TrainCar trainCar, DV.Logic.Job.Car car)
		{
			var ctrl = GetController(trainCar);
			if (ctrl == null)
				return;

			bool loaded = car.LoadedCargoAmount > 0f;
			bool hasModel = ctrl.GetCurrentCargoModel() != null;

			if (loaded && !hasModel)
			{
				// nur wenn es für diese Fracht überhaupt ein Modell gibt (sonst bei jedem Abgleich erneut)
				var prefabs = trainCar.LoadedCargo.ToV2()?.GetCargoPrefabsForCarType(trainCar.carLivery.parentType);
				if (prefabs != null && prefabs.Length > 0)
				{
					onLoaded?.Invoke(ctrl, new object[] { trainCar.LoadedCargo });
					Main.Log($" CargoVisuals: {car.ID} cargo model restored");
				}
			}
			else if (!loaded && hasModel)
			{
				onUnloaded?.Invoke(ctrl, null);
				Main.Log($" CargoVisuals: {car.ID} stale cargo model removed");
			}
		}

		private static CargoModelController? GetController(TrainCar trainCar)
		{
			if (controllers.TryGetValue(trainCar, out var c) && c != null)
				return c;

			c = trainCar.GetComponentInChildren<CargoModelController>(true);
			controllers[trainCar] = c;
			return c;
		}

		public static void Reset()
		{
			pending.Clear();
			controllers.Clear();
		}

		// =========================
		// PATCH: kein Modell ohne Interior (verhindert die Exception)
		// =========================
		[HarmonyPatch(typeof(CargoModelController), "OnCargoLoaded")]
		public static class OnCargoLoaded_Patch
		{
			[HarmonyPrefix]
			static bool Prefix(CargoModelController __instance)
			{
				try
				{
					var tc = trainCarRef(__instance);
					if (tc != null && tc.interior == null)
					{
						// Modell kommt, sobald das Interior wieder geladen ist
						if (tc.logicCar != null)
							pending.Add(tc.logicCar);
						return false;
					}
				}
				catch
				{
					// im Zweifel Originalverhalten
				}

				return true;
			}
		}
	}
}
