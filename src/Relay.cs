using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DeathMessage
{
	// The three messages between a player's game and the server, and the server's side of them.
	internal static class Relay
	{
		internal const string ReportRpc = "DeathMessage_Report";     // client -> server: I died, and of what
		internal const string HelloRpc = "DeathMessage_Hello";       // client -> server: I have the mod
		internal const string AnnounceRpc = "DeathMessage_Announce"; // server -> clients with the mod: the line to show

		private const int Protocol = 1;
		private const int MaxCause = 160;
		private const float MinSecondsBetweenReports = 3f;
		private const float HelloEverySeconds = 120f;

		// Peers that have the mod and show announcements themselves.
		private static readonly HashSet<long> modded = new HashSet<long>();
		private static readonly Dictionary<long, float> lastReport = new Dictionary<long, float>();
		private static ZRoutedRpc greeted;
		private static float greetedAt;

		private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

		internal static void NewSession()
		{
			modded.Clear();
			lastReport.Clear();
			DiscordBridge.Clear();
		}

		// ----- client -----

		internal static void Report(string cause, string biome)
		{
			if (ZRoutedRpc.instance == null) return;
			Hello();
			var pkg = new ZPackage();
			pkg.Write(Protocol);
			pkg.Write(cause);
			pkg.Write(biome);
			ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), ReportRpc, pkg);
		}

		// As soon as there is a character in the world, and again every couple of minutes: a server
		// whose copy of the mod was reloaded has forgotten who it heard from.
		internal static void Hello()
		{
			if (ZRoutedRpc.instance == null || IsServer || Player.m_localPlayer == null) return;
			if (ZRoutedRpc.instance == greeted && Time.unscaledTime - greetedAt < HelloEverySeconds) return;
			greeted = ZRoutedRpc.instance;
			greetedAt = Time.unscaledTime;
			ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), HelloRpc);
		}

		internal static void OnAnnounce(long sender, string text)
		{
			// Only the server may speak here; any client can address a routed message to any other.
			if (DeathMessagePlugin.Headless || ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
			Show(Clean(text, MaxCause + 80));
		}

		private static void Show(string text)
		{
			if (DeathMessagePlugin.OthersInChat.Value && Chat.instance != null)
			{
				Chat.instance.AddString($"<color=#ff8080>{text}</color>");
				Chat.instance.m_hideTimer = 0f; // brings the chat window up, as a chat line does
			}
			if (DeathMessagePlugin.OthersTopLeft.Value && MessageHud.instance != null)
			{
				MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
			}
		}

		// ----- server -----

		internal static void OnHello(long sender)
		{
			if (IsServer) modded.Add(sender);
		}

		internal static void OnReport(long sender, ZPackage pkg)
		{
			if (!IsServer) return;
			try
			{
				float now = Time.unscaledTime;
				if (lastReport.TryGetValue(sender, out float last) && now - last < MinSecondsBetweenReports) return;
				lastReport[sender] = now;

				if (pkg.ReadInt() != Protocol) return;
				string cause = Clean(pkg.ReadString(), MaxCause);
				string biome = new string(Clean(pkg.ReadString(), 24).Where(char.IsLetter).ToArray());
				if (cause.Length == 0) return;

				// The name comes from the connection, never from the message: a player reports only their own death.
				ZNetPeer peer = ZNet.instance.GetPeer(sender);
				string name = peer != null ? peer.m_playerName
					: sender == ZRoutedRpc.instance.m_id && Game.instance != null ? Game.instance.GetPlayerProfile().GetName()
					: null;
				if (string.IsNullOrEmpty(name)) return;
				modded.Add(sender);

				DeathMessagePlugin.Log.LogInfo($"{name} {cause} ({biome})");
				if (peer != null && DeathMessagePlugin.Discord.Value) DiscordBridge.Remember(sender, cause, biome);
				if (DeathMessagePlugin.Announce.Value) AnnounceAll(sender, Fill(DeathMessagePlugin.AnnounceFormat.Value, cause, biome).Replace("{player}", name));
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"bad death report from {sender}: {e.GetType().Name}: {e.Message}");
			}
		}

		// Everyone but the player who died: their own game has already told them.
		private static void AnnounceAll(long except, string text)
		{
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				if (!peer.IsReady() || peer.m_uid == except) continue;
				if (modded.Contains(peer.m_uid))
				{
					ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, AnnounceRpc, text);
				}
				else if (DeathMessagePlugin.AnnounceToUnmodded.Value)
				{
					// MessageHud registers "ShowMessage" (type, text) on every client, mod or no mod.
					ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)MessageHud.MessageType.TopLeft, text);
				}
			}
			// A player hosting the world is the server and has no peer of their own.
			if (!DeathMessagePlugin.Headless && except != ZRoutedRpc.instance.m_id) Show(text);
			DeathMessagePlugin.Debug($"announced: {text}");
		}

		internal static string Fill(string format, string cause, string biome) =>
			format.Replace("{cause}", cause).Replace("{biome}", Spaced(biome));

		// "BlackForest" -> "Black Forest"
		private static string Spaced(string biome)
		{
			var sb = new StringBuilder();
			foreach (char c in biome)
			{
				if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
				sb.Append(c);
			}
			return sb.ToString();
		}

		// What arrives from another machine is text someone else chose. Out go rich-text tags,
		// Discord mentions and DiscordConnector's %VARIABLES%; and it is cut to a sane length.
		private static string Clean(string text, int max)
		{
			if (string.IsNullOrEmpty(text)) return "";
			var sb = new StringBuilder(Math.Min(text.Length, max));
			foreach (char c in text)
			{
				if (sb.Length >= max) break;
				if (char.IsControl(c) || c == '<' || c == '>' || c == '@' || c == '%' || c == '{' || c == '}' || c == '`') continue;
				sb.Append(c);
			}
			return sb.ToString().Trim();
		}
	}
}
