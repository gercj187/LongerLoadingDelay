//FILE:		RadioClock.cs

using DV;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// UHR IM FUNKGERÄT
	// =========================
	// Kopiert das Titel-Textfeld des CommsRadioDisplay (gleiche Schrift/Farbe/Leuchten),
	// verkleinert es und zeigt rechtsbündig in der Titelzeile die Spieluhrzeit.
	// Eigenes Objekt → wird von keinem Funkgerät-Modus (SetDisplay) überschrieben.
	[HarmonyPatch(typeof(CommsRadioController), "Start")]
	public static class CommsRadio_Start_Patch
	{
		[HarmonyPostfix]
		static void Postfix(CommsRadioController __instance)
		{
			if (__instance == null || __instance.GetComponent<RadioClock>() != null)
				return;

			__instance.gameObject.AddComponent<RadioClock>();
		}
	}

	public class RadioClock : MonoBehaviour
	{
		private const float UpdateInterval = 0.5f;

		private TextMeshPro? clockText;
		private float nextUpdate;
		private string lastText = "";
		private bool setupFailed;

		private void Setup()
		{
			var display = GetComponentInChildren<CommsRadioDisplay>(true);
			if (display == null || display.title == null)
			{
				setupFailed = true;
				Main.Log(" RadioClock: CommsRadioDisplay not found");
				return;
			}

			var source = display.title;

			var go = Instantiate(source.gameObject, source.transform.parent);
			go.name = "LongerLoadingDelay_RadioClock";
			go.SetActive(true);		// Titel kann beim ersten Frame noch deaktiviert sein (Start-Hack des Displays)

			clockText = go.GetComponent<TextMeshPro>();
			if (clockText == null)
			{
				Destroy(go);
				setupFailed = true;
				return;
			}

			// gleiche Zeile wie der Titel, aber rechtsbündig und kleiner
			clockText.alignment = TextAlignmentOptions.TopRight;
			clockText.enableAutoSizing = false;
			clockText.fontSize = source.fontSize * 0.6f;
			clockText.fontStyle = FontStyles.Normal;
			clockText.text = "";

			Main.Log(" RadioClock: attached");
		}

		void Update()
		{
			if (setupFailed)
				return;

			if (clockText == null)
			{
				Setup();
				if (clockText == null)
					return;
			}

			bool show = Main.Enabled && Main.Settings.RadioClock;
			if (clockText.gameObject.activeSelf != show)
				clockText.gameObject.SetActive(show);

			if (!show || Time.unscaledTime < nextUpdate)
				return;

			nextUpdate = Time.unscaledTime + UpdateInterval;

			string text = GameClock.TryGetNowAbsMinutes(out double now) ? GameClock.FormatTimeOfDay(now) : "--:--";

			if (text != lastText)
			{
				clockText.text = text;
				lastText = text;
			}
		}

		void OnDestroy()
		{
			if (clockText != null)
				Destroy(clockText.gameObject);
		}
	}
}
