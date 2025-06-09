using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using UnityEngine;
using DV.ThingTypes; // Für JobType
using DV.Logic.Job; // Für Job
using DV.Booklets;  // Für Job_data

namespace LongerLoadingDelay
{
    public class Main
    {
        public static bool enabled;
        public static Settings Settings = new Settings();

        public static void Load(UnityModManager.ModEntry modEntry)
        {
            Settings = Settings.Load<Settings>(modEntry);
            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            enabled = value;
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Draw(modEntry);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry);
        }

        public static void Log(string msg)
        {
            if (enabled)
                Debug.Log($"[LongerLoadingDelay] {msg}");
        }

        // -----------------------
        // Hilfsmethoden
        // -----------------------

        public static bool IsFree(object instance)
        {
            Coroutine? coro1 = GetFieldClass<Coroutine>(instance, "loadUnloadCoro");
            Coroutine? coro2 = GetFieldClass<Coroutine>(instance, "activateExternallyCoro");
            return coro1 == null && coro2 == null;
        }

        public static T? GetFieldClass<T>(object obj, string fieldName) where T : class
        {
            return obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(obj) as T;
        }

        public static T? GetFieldStruct<T>(object obj, string fieldName) where T : struct
        {
            var field = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null ? (T?)field.GetValue(obj) : null;
        }

        public static void SetField(object obj, string fieldName, object value)
        {
            obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(obj, value);
        }

        public static void CallMethod(object obj, string methodName)
        {
            obj.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(obj, null);
        }

        public static Coroutine CallCoroutine(object obj, string methodName, object[] args)
        {
            var method = obj.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            var enumerator = (IEnumerator?)method?.Invoke(obj, args);
            return ((MonoBehaviour)obj).StartCoroutine(enumerator);
        }

        public static float GetShuntingTimeMultiplier()
        {
            return 1f + (Settings.delayBetweenCars - 1f) / 59f;
        }
    }

    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        [Draw("Time to load/unload a freight car (vanilla = 1 second)", Min = 1, Max = 60, Precision = 0, Type = DrawType.Slider)]
        public int delayBetweenCars = 1;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange() { }
    }

    [HarmonyPatch(typeof(WarehouseMachineController), "StartLoadSequence")]
    class Patch_StartLoadSequence
    {
        static bool Prefix(object __instance)
        {
            if (!Main.IsFree(__instance)) return false;

            Main.CallMethod(__instance, "ClearTrainInRangeText");

            float delay = Main.Settings.delayBetweenCars;
            Main.Log($"Starte Laden mit Delay {delay} Sekunden");

            var coroutine = Main.CallCoroutine(__instance, "DelayedLoadUnload", new object[] { true, delay, false });
            Main.SetField(__instance, "loadUnloadCoro", coroutine);

            return false;
        }
    }

    [HarmonyPatch(typeof(WarehouseMachineController), "StartUnloadSequence")]
    class Patch_StartUnloadSequence
    {
        static bool Prefix(object __instance)
        {
            if (!Main.IsFree(__instance)) return false;

            Main.CallMethod(__instance, "ClearTrainInRangeText");

            float delay = Main.Settings.delayBetweenCars;
            Main.Log($"Starte Entladen mit Delay {delay} Sekunden");

            var coroutine = Main.CallCoroutine(__instance, "DelayedLoadUnload", new object[] { false, delay, false });
            Main.SetField(__instance, "loadUnloadCoro", coroutine);

            return false;
        }
    }

    [HarmonyPatch(typeof(Job_data), MethodType.Constructor, typeof(Job))]
    class Patch_JobData_Constructor
    {
        static void Postfix(Job_data __instance)
        {
            if (__instance.type == JobType.ShuntingLoad || __instance.type == JobType.ShuntingUnload)
            {
                float multiplier = Main.GetShuntingTimeMultiplier();
                __instance.timeLimit *= multiplier;
                Main.Log($"[Booklet] Zeitlimit in Job_data angepasst: {__instance.timeLimit:F1} Sekunden (Multiplikator {multiplier:F2})");
            }
        }
    }
}
