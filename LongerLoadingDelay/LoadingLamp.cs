//FILE:		LoadingLamp.cs

using TMPro;
using UnityEngine;

namespace LongerLoadingDelay
{
	// =========================
	// STATUSLAMPE
	// =========================
	// Nutzt die vorhandene Mast-Leuchte der Lademaschine:
	//   WarehouseMachine/light  (Mesh, Material "OrangeBeacon", emissiv)
	// Farbe per MaterialPropertyBlock → kein Material-Klon, Original bleibt unangetastet.
	//   weiß = bereit · gelb blinkend = Beladung · blau = Pause · rot schnell = Fehler · grün = fertig
	// Fallback, falls die Leuchte fehlt: Gleisschild (trackIdText) einfärben.
	// Läuft nur, solange der Controller aktiv ist.
	public class LoadingLamp : MonoBehaviour
	{
		private const string BeaconChildName = "light";
		private const string BeaconMaterialPrefix = "OrangeBeacon";

		private static readonly Color White = new Color(1f, 1f, 1f);
		private static readonly Color Yellow = new Color(1f, 0.72f, 0.0f);
		private static readonly Color Red = new Color(1f, 0.05f, 0.02f);
		private static readonly Color Green = new Color(0.1f, 1f, 0.2f);
		private static readonly Color Blue = new Color(0.15f, 0.4f, 1f);

		private static readonly int ColorId = Shader.PropertyToID("_Color");
		private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

		// Leuchtstärke wie die grüne Bahnhofs-Beacon (Material "GreenBeacon", HDR-Emission → Bloom auch am Tag)
		private const string ReferenceMaterial = "GreenBeacon";
		private const float Brightness = 3f;		// × Leuchtstärke der Bahnhofs-Beacon
		private const float FallbackIntensity = 4f;
		private static float referenceIntensity = -1f;

		private static float ReferenceIntensity
		{
			get
			{
				if (referenceIntensity > 0f)
					return referenceIntensity;

				referenceIntensity = FallbackIntensity;

				foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
				{
					if (m == null || m.name != ReferenceMaterial || !m.HasProperty(EmissionId))
						continue;

					Color e = m.GetColor(EmissionId);
					float max = Mathf.Max(e.r, Mathf.Max(e.g, e.b));
					if (max > 0.01f)
					{
						referenceIntensity = max;
						Main.Log($" Lamp: emission intensity taken from {ReferenceMaterial}: {max:F2}");
					}
					break;
				}

				return referenceIntensity;
			}
		}

		private WarehouseMachineController? ctrl;
		private bool searched;

		// Mast-Leuchte
		private Renderer? beacon;
		private MaterialPropertyBlock? mpb;
		private bool hasColorProp, hasEmissionProp;

		// Fallback Gleisschild
		private TextMeshPro? text;
		private Color textOriginal;

		private Light? glow;
		private Corona? corona;
		private bool coronaTried;
		private Color lastShown = new Color(-1, -1, -1, -1);
		private bool overridden;

		public static void Attach(WarehouseMachineController ctrl)
		{
			// Klone (z. B. TrackPlacer) bringen die Komponente schon mit – aber ohne Laufzeitdaten
			var lamp = ctrl.GetComponent<LoadingLamp>() ?? ctrl.gameObject.AddComponent<LoadingLamp>();
			lamp.ctrl = ctrl;
		}

		private void FindTargets()
		{
			searched = true;
			if (ctrl == null)
				return;

			// Mitgeklonte Objekte der Original-Maschine (Lichtschein, Corona) entfernen –
			// sie gehören zur Quelle, nicht zu dieser Maschine, und würden sonst eingefroren weiterleuchten.
			foreach (var child in ctrl.GetComponentsInChildren<Transform>(true))
			{
				if (child != null && child != ctrl.transform && child.name.StartsWith("LongerLoadingDelay_"))
					Destroy(child.gameObject);
			}

			// 1. direktes Kind "light"
			var t = ctrl.transform.Find(BeaconChildName);
			if (t != null)
				beacon = t.GetComponent<Renderer>();

			// 2. sonst irgendein Kind-Renderer mit OrangeBeacon-Material
			if (beacon == null)
			{
				foreach (var r in ctrl.GetComponentsInChildren<Renderer>(true))
				{
					var m = r.sharedMaterial;
					if (m != null && m.name.StartsWith(BeaconMaterialPrefix))
					{
						beacon = r;
						break;
					}
				}
			}

			if (beacon != null)
			{
				var m = beacon.sharedMaterial;
				hasColorProp = m != null && m.HasProperty(ColorId);
				hasEmissionProp = m != null && m.HasProperty(EmissionId);
				mpb = new MaterialPropertyBlock();

				Main.Log($" {ctrl.warehouseTrackName} : beacon found ({beacon.name}, material {m?.name}, shader {m?.shader?.name}, color={hasColorProp}, emission={hasEmissionProp})");
			}
			else
			{
				text = ctrl.trackIdText;
				if (text != null)
					textOriginal = text.color;

				Main.Log($" {ctrl.warehouseTrackName} : no beacon found → using track sign");
			}
		}

		void Update()
		{
			// Klon: privates Feld wird von Instantiate nicht kopiert → selbst holen
			if (ctrl == null)
			{
				ctrl = GetComponent<WarehouseMachineController>();
				if (ctrl == null)
					return;
			}

			if (!searched)
				FindTargets();

			bool active = Main.Enabled;		// Statuslampe immer aktiv
			var state = active ? LoadingService.GetLampState(ctrl.warehouseTrackName) : LampState.None;

			if (state == LampState.None)
			{
				Restore();
				return;
			}

			(Color color, float hz) = state switch
			{
				LampState.Loading => (Yellow, 1f),
				LampState.Paused => (Blue, 0f),
				LampState.Error => (Red, 4f),
				LampState.Finished => (Green, 0f),
				_ => (White, 0f)
			};

			bool on = hz <= 0f || Mathf.Repeat(Time.unscaledTime * hz, 1f) < 0.5f;

			Color shown = on ? color : Color.black;
			shown.a = 1f;

			if (shown != lastShown)
			{
				Apply(shown, on);
				lastShown = shown;
			}

			UpdateGlow(state, color, on);
			UpdateCorona(state, color, on);
		}

		private void UpdateCorona(LampState state, Color color, bool on)
		{
			if (corona == null && !coronaTried && ctrl != null)
			{
				Vector3? pos = beacon != null ? beacon.bounds.center : text != null ? text.transform.position : (Vector3?)null;
				if (pos == null)
					return;

				corona = Corona.Create(ctrl.transform, pos.Value);
				coronaTried = true;
			}

			corona?.Set(color, on);
		}

		private void Apply(Color c, bool on)
		{
			overridden = true;

			if (beacon != null && mpb != null)
			{
				beacon.GetPropertyBlock(mpb);

				// aus = dunkler Glaskörper statt schwarzem Loch
				if (hasColorProp)
					mpb.SetColor(ColorId, on ? c : new Color(0.12f, 0.12f, 0.12f, 1f));

				if (hasEmissionProp)
					mpb.SetColor(EmissionId, on ? c * (ReferenceIntensity * Brightness) : Color.black);

				beacon.SetPropertyBlock(mpb);
				return;
			}

			if (text != null)
				text.color = on ? c : Color.Lerp(Color.black, c, 0.2f);
		}

		private void UpdateGlow(LampState state, Color color, bool on)
		{
			bool want = state != LampState.Idle;		// Lichtschein immer (außer im Ruhezustand)

			if (!want)
			{
				if (glow != null && glow.enabled)
					glow.enabled = false;
				return;
			}

			if (glow == null)
			{
				Transform? anchor = beacon != null ? beacon.transform : text != null ? text.transform : null;
				if (anchor == null)
					return;

				var go = new GameObject("LongerLoadingDelay_Glow");
				go.transform.SetParent(anchor, false);
				go.transform.localPosition = Vector3.zero;
				go.transform.localScale = new Vector3(1f / Mathf.Max(0.0001f, anchor.lossyScale.x),
					1f / Mathf.Max(0.0001f, anchor.lossyScale.y),
					1f / Mathf.Max(0.0001f, anchor.lossyScale.z));		// Skalierung der Leuchte (0.078/0.03) neutralisieren

				glow = go.AddComponent<Light>();
				glow.type = LightType.Point;
				glow.range = 2.5f;
				glow.intensity = 1.2f;
				glow.shadows = LightShadows.None;
			}

			glow.color = color;
			glow.enabled = on;
		}

		private void Restore()
		{
			if (!overridden)
				return;

			overridden = false;
			lastShown = new Color(-1, -1, -1, -1);

			if (beacon != null)
				beacon.SetPropertyBlock(null);		// zurück zum Original (orange)

			if (text != null)
				text.color = textOriginal;

			if (glow != null)
				glow.enabled = false;

			corona?.SetVisible(false);
		}

		void OnDisable()
		{
			Restore();
		}
	}
}
