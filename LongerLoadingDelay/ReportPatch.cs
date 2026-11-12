//FILE:		ReportPatch.cs

using System;
using System.Collections.Generic;
using DV.Booklets;
using DV.Localization;
using DV.RenderTextureSystem.BookletRender;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// AUFTRAGSBERICHT: ABZÜGE ALS EIGENE ZEILE
	// =========================
	// Die Vergütungsseite hat die Zeile "Strafe für Zeitüberziehung", die das Spiel fest mit "/" füllt.
	// Hat ein Job Abzüge (Ersatzgleis / Standgeld), wird diese Zeile dafür genutzt:
	//   Basis / Zeitbonus  → wieder ungekürzt angezeigt
	//   Abzugszeile        → Beschriftung + "-$Betrag" (rot)
	//   Gesamt             → unverändert (= ausgezahlter Betrag)
	public static class ReportPenalties
	{
		// Daten-Objekt → neue Beschriftung (wird in FillInData eingesetzt)
		internal static readonly Dictionary<JobReportPaymentTemplatePaperData, string> Labels = new();

		internal static string Money(float value) => value.ToString("N0", LocalizationAPI.CC);

		// Eine Abzugsart → ausführliche Zeile. Mehrere → Kurzform "Abzüge: Gleis -10 %, Standgeld -2.5 %, ..."
		internal static string BuildLabel(PenaltyInfo p)
		{
			string Pct(float v) => v.ToString("0.#");

			var longParts = new List<string>();
			var shortParts = new List<string>();

			if (p.AltPct > 0)
			{
				longParts.Add(Loc.T("report.alt", string.IsNullOrEmpty(p.AltFrom) ? "?" : p.AltFrom, Pct(p.AltPct)));
				shortParts.Add(Loc.T("report.short.alt", Pct(p.AltPct)));
			}

			if (p.IdlePct > 0.001f)
			{
				longParts.Add(Loc.T("report.idle", Schedule.FormatDuration(p.IdleMinutes), Pct(p.IdleEffective)));
				shortParts.Add(Loc.T("report.short.idle", Pct(p.IdleEffective)));
			}

			foreach (var e in p.External.Values)
			{
				if (e.pct <= 0.001f)
					continue;

				string label = string.IsNullOrEmpty(e.label) ? "?" : e.label;
				longParts.Add(label + " (-" + Pct(e.pct) + " %)");
				shortParts.Add(label + " -" + Pct(e.pct) + " %");
			}

			if (longParts.Count == 1)
				return longParts[0];

			return Loc.T("report.multi", string.Join(", ", shortParts));
		}
	}

	[HarmonyPatch(typeof(BookletCreator_JobReport), "GetReportTemplateData")]
	public static class JobReport_TemplateData_Patch
	{
		[HarmonyPostfix]
		static void Postfix(Job_data data, List<TemplatePaperData> __result)
		{
			try
			{
				if (!Main.Enabled || data == null || __result == null)
					return;

				var p = Penalties.Find(data.ID);
				if (p == null || p.TotalPct <= 0.001f)
					return;

				float factor = 1f - p.TotalPct / 100f;
				if (factor <= 0.0001f)
					return;

				foreach (var page in __result)
				{
					if (page is not JobReportPaymentTemplatePaperData pay)
						continue;

					// Ungekürzte Werte zurückrechnen (Basis und Bonus sind über GetBasePaymentForTheJob bereits gekürzt)
					float origBase = Mathf.Round(data.basePayment / factor);
					float origBonus = Mathf.Round(data.bonusPayment / factor);
					float total = Mathf.Round(data.totalPayment);
					float deduction = Mathf.Max(0f, origBase + origBonus - total);

					pay.basePayment = "+$" + ReportPenalties.Money(origBase);
					pay.bonusPayment = "+$" + ReportPenalties.Money(origBonus);
					pay.expirationPenalty = "-$" + ReportPenalties.Money(deduction);

					ReportPenalties.Labels[pay] = ReportPenalties.BuildLabel(p);

					Main.Log($" [{data.ID}] REPORT: base {origBase} bonus {origBonus} deduction {deduction} ({p.TotalPct:0.#} %)");
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[LongerLoadingDelay] Job report patch failed: " + e.Message);
			}
		}
	}

	[HarmonyPatch(typeof(JobReportPaymentTemplatePaper), nameof(JobReportPaymentTemplatePaper.FillInData))]
	public static class JobReport_FillInData_Patch
	{
		[HarmonyPostfix]
		static void Postfix(JobReportPaymentTemplatePaper __instance)
		{
			try
			{
				var data = __instance.data;
				if (data == null || !ReportPenalties.Labels.TryGetValue(data, out string label))
					return;

				ReportPenalties.Labels.Remove(data);

				var value = __instance.expirationPenalty;
				if (value == null)
					return;

				// Beschriftung = das TMP-Feld auf derselben Höhe wie der Wert, das kein Datenfeld ist
				TextMeshProUGUI? best = null;
				float bestDist = float.MaxValue;
				float y = value.transform.position.y;

				foreach (var t in __instance.GetComponentsInChildren<TextMeshProUGUI>(true))
				{
					if (t == value || t == __instance.basePayment || t == __instance.bonusPayment
						|| t == __instance.totalPayment || t == __instance.totalPaymentText)
						continue;

					float d = Mathf.Abs(t.transform.position.y - y);
					if (d < bestDist)
					{
						bestDist = d;
						best = t;
					}
				}

				if (best == null)
					return;

				// Lokalisierungs-Komponente (I2) würde den Text sonst zurücksetzen
				foreach (var c in best.GetComponents<MonoBehaviour>())
					if (c != null && c.GetType().Name == "Localize")
						c.enabled = false;

				best.enableAutoSizing = true;
				best.fontSizeMax = best.fontSize;
				best.fontSizeMin = best.fontSize * 0.6f;
				best.textWrappingMode = TextWrappingModes.NoWrap;
				best.text = label;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[LongerLoadingDelay] Job report label failed: " + e.Message);
			}
		}
	}
}
