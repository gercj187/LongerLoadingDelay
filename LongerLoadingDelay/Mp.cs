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

		private const byte StateVersion = 2;		// 2: + Lampenzustände
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
		private static int attempts;
		private static UnityModManager.ModEntry? entry;

		/// <summary>true = fertig (aktiv oder endgültig Einzelspieler), false = später nochmal versuchen</summary>
		public static bool Initialize(UnityModManager.ModEntry modEntry)
		{
			entry = modEntry;
			attempts++;

			try
			{
				var multiplayer = UnityModManager.FindMod(MultiplayerModId);
				if (multiplayer == null || !multiplayer.Enabled)
				{
					modEntry.Logger.Log("Multiplayer mod not installed/enabled → single player mode.");
					return true;
				}

				bool apiLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == ApiAssemblyName);
				if (!apiLoaded)
				{
					// API evtl. noch nicht geladen → bis zu ~60 s erneut versuchen
					if (attempts < 30)
						return false;

					modEntry.Logger.Warning("MultiplayerAPI assembly not loaded – multiplayer sync disabled.");
					return true;
				}

				string path = Path.Combine(modEntry.Path, IntegrationDll);
				if (!File.Exists(path))
				{
					modEntry.Logger.Warning($"{IntegrationDll} not found in {modEntry.Path} – multiplayer sync disabled.");
					return true;
				}

				// Eine per LoadFile geladene DLL findet ihre Abhängigkeiten nicht immer selbst →
				// MultiplayerAPI ausdrücklich auf die bereits geladene Kopie der Multiplayer-Mod umbiegen
				// (eine zweite Kopie hätte eigene statische Events → Host/Client würden nie verbunden)
				AppDomain.CurrentDomain.AssemblyResolve -= ResolveApi;
				AppDomain.CurrentDomain.AssemblyResolve += ResolveApi;

				var asm = Assembly.LoadFile(path);
				var bootstrap = asm.GetType(BootstrapTypeName);
				if (bootstrap == null)
				{
					modEntry.Logger.Warning($"{BootstrapTypeName} not found – multiplayer sync disabled.");
					return true;
				}

				var init = bootstrap.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
				bool ok = init != null && (bool)init.Invoke(null, new object[]
				{
					modEntry.Info.Id,
					(Action<byte[]>)OnStateReceived,
					(Func<byte[]>)BuildState,
					(Action)OnClientStopped,
					(Action<string>)(msg => modEntry.Logger.Log("MP: " + msg))
				});

				if (!ok)
				{
					if (attempts < 30)
						return false;		// API-Instanz noch nicht bereit → später

					modEntry.Logger.Warning("Multiplayer API not ready – multiplayer sync disabled.");
					return true;
				}

				isClient = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), bootstrap.GetMethod("IsClient", BindingFlags.Public | BindingFlags.Static)!);
				isHost = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), bootstrap.GetMethod("IsHost", BindingFlags.Public | BindingFlags.Static)!);
				broadcast = (Action<byte[]>)Delegate.CreateDelegate(typeof(Action<byte[]>), bootstrap.GetMethod("Broadcast", BindingFlags.Public | BindingFlags.Static)!);

				Available = true;
				var apis = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == ApiAssemblyName).ToList();
				modEntry.Logger.Log($"Multiplayer sync active (MultiplayerAPI loaded {apis.Count}×: {string.Join(", ", apis.Select(a => a.GetName().Version + " @ " + SafeLocation(a)))}).");
				if (apis.Count > 1)
					modEntry.Logger.Warning("MultiplayerAPI is loaded more than once – remove any MultiplayerAPI.dll from the LongerLoadingDelay folder!");
				return true;
			}
			catch (Exception e)
			{
				var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
				modEntry.Logger.Warning("Multiplayer integration failed: " + inner.GetType().Name + ": " + inner.Message);
				isClient = isHost = null;
				broadcast = null;
				Available = false;
				return true;
			}
		}

		private static string SafeLocation(Assembly a)
		{
			try { return a.Location; } catch { return "?"; }
		}

		private static Assembly? ResolveApi(object sender, ResolveEventArgs args)
		{
			var name = new AssemblyName(args.Name).Name;
			if (name != ApiAssemblyName)
				return null;

			return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == ApiAssemblyName);
		}

		private static bool loggedFirstBroadcast;

		private static void LogOnce(string msg) => entry?.Logger.Log(msg);

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
				var data = BuildState();
				broadcast(data);

				if (!loggedFirstBroadcast)
				{
					loggedFirstBroadcast = true;
					LogOnce($"MP host: state broadcast started ({data.Length} bytes, {LoadingService.Sequences.Count} sequences)");
				}
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

			// Lampenzustände (Host rechnet mit Wagen-/Gleisdaten, die der Client nicht hat)
			var lamps = LoadingService.CurrentLampStates;
			w.Write(lamps.Count);
			foreach (var kv in lamps)
			{
				w.Write(kv.Key);
				w.Write((byte)kv.Value);
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
					LogOnce($" MP: unknown state version {version} (expected {StateVersion}) – host uses another mod version");
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

				int lampCount = r.ReadInt32();
				LoadingService.SyncedLampStates.Clear();
				for (int i = 0; i < lampCount; i++)
				{
					string track = r.ReadString();
					LoadingService.SyncedLampStates[track] = (LampState)r.ReadByte();
				}

				if (!receivedState)
				{
					receivedState = true;
					LogOnce($"MP client: first state from host received ({data.Length} bytes, {count} sequences) → settings are controlled by the host");
				}
			}
			catch (Exception e)
			{
				LogOnce("MP client: state could not be read: " + e.GetType().Name + ": " + e.Message);
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
			LoadingService.SyncedLampStates.Clear();
			Penalties.Reset();
			AltTracks.Redirects.Clear();
			Main.Log(" MP: disconnected → local settings restored");
		}
	}
}
