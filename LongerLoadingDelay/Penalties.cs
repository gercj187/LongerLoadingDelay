//FILE:		Penalties.cs

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// LOHNABZÜGE PRO JOB
	// =========================
	// - Ausweichen auf Ersatzgleis: fester Prozentsatz, beim Umleiten festgeschrieben
	// - Standgeld: wächst, solange ein fertiger Zug am Ladegleis steht (nach freier Standzeit)
	// Die Summe wirkt über Job.GetBasePaymentForTheJob auf Lohn und Bonus.
	public class PenaltyInfo
	{
		public int AltPct;
		public bool AltOccupied;
		public string AltFrom = "";		// Anzeige, z. B. "D7L"
		public float IdlePct;
		public double IdleMinutes;		// Standzeit (Spielminuten) für die Anzeige

		// Abzüge anderer Mods (über DeductionApi): Quelle → (Prozent, Beschriftung)
		public readonly Dictionary<string, (float pct, string label)> External = new();

		[System.NonSerialized] public float MissingFor;

		public float ExternalPct
		{
			get
			{
				float sum = 0f;
				foreach (var e in External.Values)
					sum += e.pct;
				return sum;
			}
		}

		public bool IsEmpty => AltPct <= 0 && IdlePct <= 0.001f && External.Count == 0;
		public float TotalPct => Mathf.Clamp(AltPct + IdlePct + ExternalPct, 0f, 100f);
	}

	public static class Penalties
	{
		public static readonly Dictionary<string, PenaltyInfo> All = new();

		public static PenaltyInfo? Find(string jobId) => All.TryGetValue(jobId, out var p) ? p : null;

		private static PenaltyInfo Get(string jobId)
		{
			if (!All.TryGetValue(jobId, out var p))
			{
				p = new PenaltyInfo();
				All[jobId] = p;
			}
			return p;
		}

		public static void SetAlt(string jobId, int pct, bool occupied, string from)
		{
			var p = Get(jobId);
			p.AltPct = pct;
			p.AltOccupied = occupied;
			p.AltFrom = from;
		}

		public static void ClearAlt(string jobId)
		{
			if (!All.TryGetValue(jobId, out var p))
				return;

			p.AltPct = 0;
			p.AltFrom = "";

			if (p.IsEmpty)
				All.Remove(jobId);
		}

		// Standgeld wächst nur (nie zurück)
		public static void SetIdle(string jobId, float pct, double idleMinutes)
		{
			var p = Get(jobId);
			p.IdlePct = Mathf.Max(p.IdlePct, pct);
			p.IdleMinutes = System.Math.Max(p.IdleMinutes, idleMinutes);
		}

		public static void SetExternal(string jobId, string source, float pct, string label)
		{
			Get(jobId).External[source] = (Mathf.Clamp(pct, 0f, 100f), label ?? "");
		}

		public static void RemoveExternal(string jobId, string source)
		{
			if (!All.TryGetValue(jobId, out var p))
				return;

			p.External.Remove(source);

			if (p.IsEmpty)
				All.Remove(jobId);
		}

		public static float PayoutFactor(string jobId)
		{
			if (!Main.Enabled || !All.TryGetValue(jobId, out var p))
				return 1f;

			return 1f - p.TotalPct / 100f;
		}

		// Einträge erst 2 min nach Verschwinden des Jobs löschen (Auszahlung kommt nach Abschluss)
		public static void Tick(float dt)
		{
			foreach (var id in All.Keys.ToList())
			{
				var p = All[id];

				if (LoadingService.FindJobById(id) != null)
				{
					p.MissingFor = 0f;
					continue;
				}

				p.MissingFor += dt;
				if (p.MissingFor > 120f)
					All.Remove(id);
			}
		}

		// =========================
		// SPEICHERN / LADEN
		// =========================
		public static JArray Write()
		{
			var arr = new JArray();
			foreach (var kvp in All)
			{
				var p = kvp.Value;
				var o = new JObject
				{
					["job"] = kvp.Key,
					["alt"] = p.AltPct,
					["occ"] = p.AltOccupied,
					["from"] = p.AltFrom,
					["idle"] = p.IdlePct,
					["idleMin"] = p.IdleMinutes
				};

				if (p.External.Count > 0)
				{
					var ext = new JArray();
					foreach (var e in p.External)
						ext.Add(new JObject { ["src"] = e.Key, ["pct"] = e.Value.pct, ["label"] = e.Value.label });
					o["ext"] = ext;
				}

				arr.Add(o);
			}
			return arr;
		}

		public static void Read(JArray? arr)
		{
			All.Clear();
			if (arr == null)
				return;

			foreach (var t in arr)
			{
				if (t is not JObject o)
					continue;

				string id = o["job"]?.ToString() ?? "";
				if (id.Length == 0)
					continue;

				var p = new PenaltyInfo
				{
					AltPct = o["alt"]?.ToObject<int>() ?? 0,
					AltOccupied = o["occ"]?.ToObject<bool>() ?? true,
					AltFrom = o["from"]?.ToString() ?? "",
					IdlePct = o["idle"]?.ToObject<float>() ?? 0f,
					IdleMinutes = o["idleMin"]?.ToObject<double>() ?? 0.0
				};

				if (o["ext"] is JArray ext)
				{
					foreach (var e in ext)
					{
						if (e is not JObject eo)
							continue;

						string src = eo["src"]?.ToString() ?? "";
						if (src.Length > 0)
							p.External[src] = (eo["pct"]?.ToObject<float>() ?? 0f, eo["label"]?.ToString() ?? "");
					}
				}

				if (!p.IsEmpty)
					All[id] = p;
			}
		}

		// Format der vorherigen Version: "penalized": [ {job, pct} ] oder [ "jobId" ]
		public static void ReadLegacy(JArray? arr)
		{
			if (arr == null)
				return;

			foreach (var t in arr)
			{
				if (t is JObject o)
				{
					string id = o["job"]?.ToString() ?? "";
					int pct = o["pct"]?.ToObject<int>() ?? 0;
					if (id.Length > 0 && pct > 0)
						SetAlt(id, pct, true, "");
				}
				else
				{
					string id = t?.ToString() ?? "";
					if (id.Length > 0)
						SetAlt(id, Main.Settings.AltTrackPenaltyOccupied, true, "");
				}
			}
		}

		public static void Reset() => All.Clear();

		// =========================
		// MULTIPLAYER (Host → Client, binär)
		// =========================
		public static void WriteSync(System.IO.BinaryWriter w)
		{
			w.Write(All.Count);
			foreach (var kvp in All)
			{
				var p = kvp.Value;
				w.Write(kvp.Key);
				w.Write(p.AltPct);
				w.Write(p.AltOccupied);
				w.Write(p.AltFrom ?? "");
				w.Write(p.IdlePct);
				w.Write(p.IdleMinutes);

				w.Write(p.External.Count);
				foreach (var e in p.External)
				{
					w.Write(e.Key);
					w.Write(e.Value.pct);
					w.Write(e.Value.label ?? "");
				}
			}
		}

		public static void ReadSync(System.IO.BinaryReader r)
		{
			All.Clear();

			int count = r.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				string id = r.ReadString();
				var p = new PenaltyInfo
				{
					AltPct = r.ReadInt32(),
					AltOccupied = r.ReadBoolean(),
					AltFrom = r.ReadString(),
					IdlePct = r.ReadSingle(),
					IdleMinutes = r.ReadDouble()
				};

				int ext = r.ReadInt32();
				for (int j = 0; j < ext; j++)
				{
					string src = r.ReadString();
					float pct = r.ReadSingle();
					string label = r.ReadString();
					p.External[src] = (pct, label);
				}

				All[id] = p;
			}
		}
	}
}
