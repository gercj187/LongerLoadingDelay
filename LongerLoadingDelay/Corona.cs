//FILE:		Corona.cs

using System;
using DV;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// LENSFLARE IN DER MAST-LEUCHTE
	// =========================
	// Zwei Billboards (eigenes Quad + weicher Radialverlauf, additiv):
	//   Halo  = großer, weicher Lichthof in Lampenfarbe
	//   Kern  = kleiner, fast weißer Kern (überstrahlt wie bei den Straßenlaternen)
	// Die Billboards sitzen ein Stück vor der Leuchte (Richtung Kamera) → werden vom Lampenkörper nicht verdeckt.
	// In großer Entfernung wachsen sie leicht mit, damit die Lampe auch von weitem als Lichtpunkt sichtbar bleibt.
	public class Corona
	{
		private const float HaloSize = 0.55f;			// Durchmesser in Metern
		private const float CoreSize = 0.17f;
		private const float HaloIntensity = 1.1f;
		private const float CoreIntensity = 1.5f;
		private const float CoreWhite = 0.5f;			// Anteil Weiß im Kern

		// Shader-Kandidaten (additiv zuerst); "Sprites/Default" ist immer im Build
		private static readonly string[] ShaderNames =
		{
			"Legacy Shaders/Particles/Additive",
			"Particles/Additive",
			"Mobile/Particles/Additive",
			"Legacy Shaders/Particles/Additive (Soft)",
			"Particles/Standard Unlit",
			"Sprites/Default"
		};

		private static Material? material;
		private static int colorId;
		private static float colorScale = 1f;
		private static Mesh? quad;

		private readonly GameObject root;
		private readonly Renderer halo, core;
		private readonly MaterialPropertyBlock mpb = new();
		private Color lastColor = new Color(-1, -1, -1, -1);
		private bool visible = true;

		private Corona(GameObject root, Renderer halo, Renderer core)
		{
			this.root = root;
			this.halo = halo;
			this.core = core;
		}

		// =========================
		// ERZEUGEN
		// =========================
		public static Corona? Create(Transform parent, Vector3 worldPos)
		{
			try
			{
				if (!EnsureResources())
					return null;

				var go = new GameObject("LongerLoadingDelay_Flare");
				go.transform.SetParent(parent, false);
				go.transform.position = worldPos;

				var bb = go.AddComponent<Billboard>();
				bb.Init(parent, parent.InverseTransformPoint(worldPos), HaloSize);

				var h = CreateQuad(go.transform, "Halo", HaloSize);
				var c = CreateQuad(go.transform, "Core", CoreSize);
				c.transform.localPosition = new Vector3(0f, 0f, -0.01f);		// Kern minimal vor dem Halo

				return new Corona(go, h, c);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[LongerLoadingDelay] Flare creation failed: " + e.Message);
				return null;
			}
		}

		private static Renderer CreateQuad(Transform parent, string name, float size)
		{
			var go = new GameObject(name);
			go.transform.SetParent(parent, false);
			go.transform.localScale = Vector3.one * size;

			go.AddComponent<MeshFilter>().sharedMesh = quad!;
			var r = go.AddComponent<MeshRenderer>();
			r.sharedMaterial = material!;
			r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			r.receiveShadows = false;
			r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
			r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
			return r;
		}

		private static bool EnsureResources()
		{
			if (material != null && quad != null)
				return true;

			Shader? shader = null;

			foreach (string name in ShaderNames)
			{
				shader = Shader.Find(name);

				if (shader == null)
				{
					// nicht über Find erreichbar, aber evtl. geladen (von Partikeln des Spiels benutzt)
					foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
					{
						if (s != null && s.name == name)
						{
							shader = s;
							break;
						}
					}
				}

				if (shader != null && shader.isSupported)
					break;

				shader = null;
			}

			if (shader == null)
			{
				Debug.LogWarning("[LongerLoadingDelay] Flare: no usable shader found");
				return false;
			}

			material = new Material(shader)
			{
				name = "LongerLoadingDelay_Flare",
				mainTexture = CreateTexture(),
				renderQueue = 3100
			};

			// Legacy-Partikel: _TintColor (× 2 im Shader), sonst _Color
			if (material.HasProperty("_TintColor"))
			{
				colorId = Shader.PropertyToID("_TintColor");
				colorScale = 0.5f;
			}
			else
			{
				colorId = Shader.PropertyToID("_Color");
				colorScale = 1f;
			}

			quad = CreateQuadMesh();

			Main.Log($" Flare shader: {shader.name}");
			return true;
		}

		// Heller Kern mit weichem, langem Auslauf (wie Bloom einer Straßenlaterne)
		private static Texture2D CreateTexture()
		{
			const int n = 128;
			var tex = new Texture2D(n, n, TextureFormat.RGBA32, true)
			{
				wrapMode = TextureWrapMode.Clamp,
				name = "LongerLoadingDelay_FlareTex"
			};

			var px = new Color[n * n];

			for (int y = 0; y < n; y++)
			{
				for (int x = 0; x < n; x++)
				{
					float dx = (x + 0.5f) / n * 2f - 1f;
					float dy = (y + 0.5f) / n * 2f - 1f;
					float r = Mathf.Sqrt(dx * dx + dy * dy);

					float core = Mathf.Exp(-(r / 0.22f) * (r / 0.22f));
					float glow = Mathf.Exp(-(r / 0.5f) * (r / 0.5f)) * 0.45f;
					float a = Mathf.Clamp01(core + glow) * Mathf.Clamp01((1f - r) * 4f);		// Rand sauber auf 0

					px[y * n + x] = new Color(1f, 1f, 1f, a);
				}
			}

			tex.SetPixels(px);
			tex.Apply(true, true);
			return tex;
		}

		private static Mesh CreateQuadMesh()
		{
			var m = new Mesh { name = "LongerLoadingDelay_FlareQuad" };
			m.vertices = new[]
			{
				new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
				new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f)
			};
			m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
			m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
			m.RecalculateNormals();
			m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
			return m;
		}

		// =========================
		// FARBE / SICHTBARKEIT
		// =========================
		public void Set(Color color, bool on)
		{
			if (root == null)
				return;

			SetVisible(on);
			if (!on || color == lastColor)
				return;

			lastColor = color;

			// Helle Farben (vor allem Weiß) wirken additiv viel massiver → nach Helligkeit dämpfen
			float lum = 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
			float damp = Mathf.Min(1f, 1f / (0.5f + lum));

			SetColor(halo, color * (HaloIntensity * colorScale * damp));
			SetColor(core, Color.Lerp(color, Color.white, CoreWhite) * (CoreIntensity * colorScale * damp));
		}

		private void SetColor(Renderer r, Color c)
		{
			if (r == null)
				return;

			c.a = 1f;
			r.GetPropertyBlock(mpb);
			mpb.SetColor(colorId, c);
			r.SetPropertyBlock(mpb);
		}

		public void SetVisible(bool value)
		{
			if (root == null || visible == value)
				return;

			visible = value;
			root.SetActive(value);
		}
	}

	// Richtet den Flare vor dem Rendern JEDER Kamera neu aus (Spieler, Freecam, Fotomodus …),
	// schiebt ihn vor die Leuchte und lässt ihn auf Distanz leicht mitwachsen.
	public class Billboard : MonoBehaviour
	{
		private const float TowardsCamera = 0.2f;		// Meter vor der Leuchte
		private const float MinAngularSize = 0.006f;	// Mindestgröße relativ zur Entfernung

		private static readonly System.Collections.Generic.List<Billboard> active = new();
		private static bool hooked;

		private Transform? anchor;
		private Vector3 anchorLocal;
		private float baseSize = 1f;

		public void Init(Transform anchorParent, Vector3 localPos, float size)
		{
			anchor = anchorParent;
			anchorLocal = localPos;
			baseSize = Mathf.Max(0.01f, size);

			if (!hooked)
			{
				hooked = true;
				Camera.onPreCull += OnPreCullAny;
			}
		}

		void OnEnable() => active.Add(this);
		void OnDisable() => active.Remove(this);

		private static void OnPreCullAny(Camera cam)
		{
			if (cam == null || active.Count == 0)
				return;

			for (int i = active.Count - 1; i >= 0; i--)
			{
				var b = active[i];
				if (b == null)
				{
					active.RemoveAt(i);
					continue;
				}

				// nur Kameras, die den Flare überhaupt sehen (keine Schatten-/Spezialkameras)
				if ((cam.cullingMask & (1 << b.gameObject.layer)) == 0)
					continue;

				b.Orient(cam.transform.position, cam.transform.rotation);
			}
		}

		private void Orient(Vector3 camPos, Quaternion camRot)
		{
			if (anchor == null)
				return;

			Vector3 center = anchor.TransformPoint(anchorLocal);
			Vector3 toCam = camPos - center;
			float dist = toCam.magnitude;

			transform.position = dist > 0.001f ? center + toCam / dist * Mathf.Min(TowardsCamera, dist * 0.5f) : center;
			transform.rotation = camRot;

			// Elternskalierung neutralisieren, auf Distanz mindestens MinAngularSize groß
			float size = Mathf.Max(baseSize, dist * MinAngularSize);
			Vector3 ps = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
			float k = size / baseSize;
			transform.localScale = new Vector3(k / Mathf.Max(0.0001f, ps.x), k / Mathf.Max(0.0001f, ps.y), k / Mathf.Max(0.0001f, ps.z));
		}
	}
}
