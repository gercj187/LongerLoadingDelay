//FILE:		SceneDump.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DV;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// DEBUG: OBJEKTE RUND UM DIE NÄCHSTE LADEMASCHINE
	// =========================
	// Zweck: die Mast-Lampe finden. Sie ist nicht Teil des LoadMachine-Meshes,
	// sondern ein eigenes Objekt im Bahnhof (evtl. statisch gebatcht / anderes Elternobjekt).
	// Ausgabe: <Mod-Ordner>/lamp_dump.txt
	public static class SceneDump
	{
		private const float Radius = 6f;

		public static void DumpNearestMachine()
		{
			try
			{
				Camera? cam = PlayerManager.PlayerCamera != null ? PlayerManager.PlayerCamera : Camera.main;
				if (cam == null)
				{
					Debug.LogWarning("[LongerLoadingDelay] Dump: no player camera");
					return;
				}

				Vector3 playerPos = cam.transform.position;

				WarehouseMachineController? nearest = null;
				float best = float.MaxValue;

				foreach (var c in WarehouseMachineController.allControllers)
				{
					if (c == null) continue;
					float d = Vector3.Distance(c.transform.position, playerPos);
					if (d < best) { best = d; nearest = c; }
				}

				if (nearest == null)
				{
					Debug.LogWarning("[LongerLoadingDelay] Dump: no WarehouseMachineController found");
					return;
				}

				Vector3 origin = nearest.transform.position;
				var sb = new StringBuilder();

				sb.AppendLine("=== LongerLoadingDelay lamp dump ===");
				sb.AppendLine($"Machine: {nearest.warehouseTrackName}  path: {PathOf(nearest.transform)}");
				sb.AppendLine($"Machine pos: {origin}  rot: {nearest.transform.eulerAngles}  player dist: {best:F1} m");
				sb.AppendLine();

				// --- Hierarchie der Maschine selbst ---
				sb.AppendLine("--- Machine hierarchy ---");
				DumpHierarchy(nearest.transform, sb, 0);
				sb.AppendLine();

				// --- Alle Renderer im Umkreis ---
				sb.AppendLine($"--- Renderers within {Radius} m (horizontal), sorted by distance ---");

				var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>()
					.Select(r => (r, d: HorizontalDistance(r.bounds.center, origin)))
					.Where(x => x.d <= Radius)
					.OrderBy(x => x.d);

				foreach (var (r, d) in renderers)
				{
					Vector3 rel = r.bounds.center - origin;
					sb.AppendLine($"[{d:F2} m] {PathOf(r.transform)}");
					sb.AppendLine($"    type={r.GetType().Name}  mesh={MeshName(r)}  staticBatch={r.isPartOfStaticBatch}  layer={LayerMask.LayerToName(r.gameObject.layer)}");
					sb.AppendLine($"    boundsCenter(rel)={rel}  size={r.bounds.size}  top(rel)={r.bounds.max.y - origin.y:F2}");

					foreach (var m in r.sharedMaterials)
					{
						if (m == null) continue;

						string emission = m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor").ToString() : "-";
						string color = m.HasProperty("_Color") ? m.GetColor("_Color").ToString() : "-";

						sb.AppendLine($"    material={m.name}  shader={m.shader?.name}  _EMISSION={m.IsKeywordEnabled("_EMISSION")}  emission={emission}  color={color}");
					}
				}

				sb.AppendLine();

				// --- Lichter im Umkreis ---
				sb.AppendLine($"--- Lights within {Radius} m ---");
				foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
				{
					float d = HorizontalDistance(l.transform.position, origin);
					if (d > Radius) continue;

					sb.AppendLine($"[{d:F2} m] {PathOf(l.transform)}  type={l.type}  color={l.color}  intensity={l.intensity}  range={l.range}  rel={l.transform.position - origin}");
				}

				string file = Path.Combine(Main.ModEntry.Path, "lamp_dump.txt");
				File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));

				Debug.Log("[LongerLoadingDelay] Dump written: " + file);
				Main.ModEntry.Logger.Log("Dump written: " + file);
			}
			catch (Exception e)
			{
				Debug.LogError("[LongerLoadingDelay] Dump failed: " + e);
			}
		}

		private static void DumpHierarchy(Transform t, StringBuilder sb, int depth)
		{
			var comps = t.GetComponents<Component>()
				.Where(c => c != null && !(c is Transform))
				.Select(c => c.GetType().Name);

			sb.Append(' ', depth * 2).Append(t.name)
				.Append("  [").Append(string.Join(", ", comps)).Append(']')
				.Append("  localPos=").Append(t.localPosition)
				.AppendLine();

			foreach (Transform child in t)
				DumpHierarchy(child, sb, depth + 1);
		}

		private static float HorizontalDistance(Vector3 a, Vector3 b)
		{
			a.y = 0; b.y = 0;
			return Vector3.Distance(a, b);
		}

		private static string MeshName(Renderer r)
		{
			if (r is SkinnedMeshRenderer smr)
				return smr.sharedMesh != null ? smr.sharedMesh.name : "-";

			var mf = r.GetComponent<MeshFilter>();
			return mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "-";
		}

		private static string PathOf(Transform t)
		{
			var parts = new List<string>();
			for (Transform? p = t; p != null; p = p.parent)
				parts.Add(p.name);
			parts.Reverse();
			return string.Join("/", parts);
		}
	}
}
