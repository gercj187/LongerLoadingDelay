//FILE:		Mp.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;

namespace LongerLoadingDelay
{
	// =========================
	// MULTIPLAYER (optional, "Shim with Integration")
	// =========================
	// Die Hauptmod referenziert MultiplayerAPI NICHT. Nur wenn die Multiplayer-Mod aktiv ist, wird
	// LongerLoadingDelay.Multiplayer.dll (liegt im Mod-Ordner) geladen; sie kapselt alles, was die API braucht.
	//
	// Modell: host-autoritativ
	//   Host / Einzelspieler → rechnet alles wie bisher (Beladung, Abzüge, Umleitungen, Speichern)
	//                          und sendet 1×/s den kompletten Zustand an alle Clients.
	//   Client               → rechnet NICHTS selbst, übernimmt Einstellungen, Uhrzeit, Sequenzen, Abzüge
	//                          und Umleitungen vom Host und zeigt sie nur an (Display, Lampe, Bericht).
	// Ohne Multiplayer-Mod verhält sich alles exakt wie im Einzelspieler.
	public static class Mp
	{
		private const string MultiplayerModId = "Multiplayer";
		private const string ApiAssemblyName = "MultiplayerAPI";
		private const string IntegrationDll = "LongerLoadingDelay.Multiplayer.dll";
		private const string BootstrapTypeName = "LongerLoadingDelay.Multiplayer.Bootstrap";

		private const byte StateVersion = 1;
		private const float BroadcastInterval = 1f;

		private static Func<bool>? isClient, isHost;
		private static Action<byte[]>? broadcast;
		private static float broadcastAcc;
		private static bool receivedState;

		public static bool Available { get; private set; }

		/// <summary>Mit einem fremden Host verbunden → nur anzeigen, nichts selbst berechnen.</summary>
		public static bool IsClient
		{
			get
			{
				try { return isClient != null && isClient(); }
				catch { return false; }
			}
		}

		/// <summary>Eigene Multiplayer-Sitzung läuft (Host).</summary>
		public static bool IsHost
		{
			get
			{
				try { return isHost != null && isHost(); }
				catch { return false; }
			}
		}

		/// <summary>Client hat schon Daten vom Host (Einstellungen usw.)</summary>
		public static bool HasHostState => IsClient && receivedState;

		// =========================
		// LADEN DER INTEGRATION
		// =========================
		public static void Initialize(UnityModManager.ModEntry modEntry)
		{
			try
			{
				var multiplayer = UnityModManager.FindMod(MultiplayerModId);
				bool apiLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == ApiAssemblyName);

				if (multiplayer == null || !multiplayer.Enabled || !apiLoaded)
				{
					Main.Log(" Multiplayer not active → single player mode");
					return;
				}

				string path = Path.Combine(modEntry.Path, IntegrationDll);
				if (!File.Exists(path))
				{
					modEntry.Logger.Warning($"{IntegrationDll} not found – multiplayer sync disabled.");
					return;
				}

				var asm = Assembly.LoadFile(path);
				var bootstrap = asm.GetType(BootstrapTypeName);
				if (bootstrap == null)
				{
					modEntry.Logger.Warning($"{BootstrapTypeName} not found – multiplayer sync disabled.");
					return;
				}

				var init = bootstrap.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
				bool ok = init != null && (bool)init.Invoke(null, new object[]
				{
					modEntry.Info.Id,
					(Action<byte[]>)OnStateReceived,
					(Func<byte[]>)BuildState,
					(Action)OnClientStopped,
					(Action<string>)(msg => Main.Log(" MP: " + msg))
				});

				if (!ok)
				{
					Main.Log(" Multiplayer API not ready → single player mode");
					return;
				}

				isClient = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), bootstrap.GetMethod("IsClient", BindingFlags.Public | BindingFlags.Static)!);
				isHost = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), bootstrap.GetMethod("IsHost", BindingFlags.Public | BindingFlags.Static)!);
				broadcast = (Action<byte[]>)Delegate.CreateDelegate(typeof(Action<byte[]>), bootstrap.GetMethod("Broadcast", BindingFlags.Public | BindingFlags.Static)!);

				Available = true;
				modEntry.Logger.Log("Multiplayer sync active.");
			}
			catch (Exception e)
			{
				modEntry.Logger.Warning("Multiplayer integration failed: " + e.Message);
				isClient = isHost = null;
				broadcast = null;
				Available = false;
			}
		}

		// =========================
		// HOST: Zustand senden
		// =========================
		public static void HostTick(float dt)
		{
			if (!Available || broadcast == null)
				return;

			broadcastAcc += dt;
			if (broadcastAcc < BroadcastInterval)
				return;

			broadcastAcc = 0f;

			if (!IsHost)
				return;

			try
			{
				broadcast(BuildState());
			}
			catch (Exception e)
			{
				Main.Log(" MP broadcast failed: " + e.Message);
			}
		}

		/// <summary>Nach Änderung der Einstellungen sofort senden.</summary>
		public static void SendSoon() => broadcastAcc = BroadcastInterval;

		private static byte[] BuildState()
		{
			using var ms = new MemoryStream();
			using var w = new BinaryWriter(ms);

			w.Write(StateVersion);

			// Einstellungen (nur spielrelevante)
			Main.Settings.WriteGameplay(w);

			// Uhr des Hosts
			bool hasClock = GameClock.TryGetNowAbsMinutes(out double now);
			w.Write(hasClock);
			w.Write(now);
			w.Write(Main.Settings.EffectiveClockRate);

			// Sequenzen
			var seqs = LoadingService.Sequences;
			w.Write(seqs.Count);
			foreach (var s in seqs)
			{
				w.Write(s.JobId);
				w.Write(s.TrackName);
				w.Write(s.IsLoading);
				w.Write(s.JobCars);
				w.Write(s.CarsDone);
				w.Write(s.WorkDone);
				w.Write(s.DelaySeconds);
				w.Write((byte)s.State);
				w.Write(s.FinishedAt);
				w.Write((byte)s.Reason);
			}

			// Abzüge
			Penalties.WriteSync(w);

			// Umleitungen
			w.Write(AltTracks.Redirects.Count);
			foreach (var kvp in AltTracks.Redirects)
			{
				w.Write(kvp.Key);
				w.Write(kvp.Value.from ?? "");
				w.Write(kvp.Value.to ?? "");
			}

			w.Flush();
			return ms.ToArray();
		}

		// =========================
		// CLIENT: Zustand übernehmen
		// =========================
		private static void OnStateReceived(byte[] data)
		{
			if (data == null || data.Length == 0 || !IsClient)
				return;

			try
			{
				using var ms = new MemoryStream(data);
				using var r = new BinaryReader(ms);

				byte version = r.ReadByte();
				if (version != StateVersion)
				{
					Main.Log($" MP: unknown state version {version} (expected {StateVersion}) – host uses another mod version");
					return;
				}

				Main.ApplyHostSettings(r);

				bool hasClock = r.ReadBoolean();
				double now = r.ReadDouble();
				float rate = r.ReadSingle();
				if (hasClock)
					GameClock.SetHostTime(now, rate);

				int count = r.ReadInt32();
				var list = new List<LoadSequence>(count);
				for (int i = 0; i < count; i++)
				{
					list.Add(new LoadSequence
					{
						JobId = r.ReadString(),
						TrackName = r.ReadString(),
						IsLoading = r.ReadBoolean(),
						JobCars = r.ReadInt32(),
						CarsDone = r.ReadInt32(),
						WorkDone = r.ReadDouble(),
						DelaySeconds = r.ReadDouble(),
						State = (SeqState)r.ReadByte(),
						FinishedAt = r.ReadDouble(),
						Reason = (InterruptReason)r.ReadByte()
					});
				}
				LoadingService.ApplySyncedSequences(list);

				Penalties.ReadSync(r);

				int redirects = r.ReadInt32();
				AltTracks.Redirects.Clear();
				for (int i = 0; i < redirects; i++)
				{
					string job = r.ReadString();
					string from = r.ReadString();
					string to = r.ReadString();
					AltTracks.Redirects[job] = (from, to);
				}

				if (!receivedState)
				{
					receivedState = true;
					Main.Log(" MP: first state from host received → settings are controlled by the host");
				}
			}
			catch (Exception e)
			{
				Main.Log(" MP: state could not be read: " + e.Message);
			}
		}

		private static void OnClientStopped()
		{
			if (!receivedState)
				return;

			receivedState = false;
			GameClock.ClearHostTime();
			Main.RestoreLocalSettings();
			LoadingService.ApplySyncedSequences(new List<LoadSequence>());
			Penalties.Reset();
			AltTracks.Redirects.Clear();
			Main.Log(" MP: disconnected → local settings restored");
		}
	}
}
