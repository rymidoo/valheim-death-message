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
		internal const string SettingsRpc = "DeathMessage_Settings"; // server -> a client that said hello: what the server allows

		private const int Protocol = 2; // 2 added the position
		private const float MaxPosDrift = 300f;
		private const int MaxCause = 160;
		private const float MinSecondsBetweenReports = 3f;
		private const float HelloEverySeconds = 120f;

		private static readonly Dictionary<long, float> lastReport = new Dictionary<long, float>();
		private static ZRoutedRpc greeted;
		private static float greetedAt;

		private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

		internal static void NewSession()
		{
			lastReport.Clear();
			DiscordBridge.Clear();
			Grave.NewSession();
		}

		// ----- client -----

		// Said as a shout by the player who died, so it is an ordinary chat line in every player's
		// game, with this mod or without. The server cannot say it: a chat line is shown under the
		// sender's platform identity, and a server has none.
		internal static void Shout(string cause, string biome, Vector3 at)
		{
			if (Chat.instance == null) return;
			string text = Fill(DeathMessagePlugin.ShoutFormat.Value, cause, biome, $"({at.x:0}, {at.y:0}, {at.z:0})").Trim();
			if (text.Length > 0) Chat.instance.SendText(Talker.Type.Shout, text);
		}

		internal static void Report(string cause, string biome, Vector3 at)
		{
			if (ZRoutedRpc.instance == null) return;
			Hello();
			var pkg = new ZPackage();
			pkg.Write(Protocol);
			pkg.Write(cause);
			pkg.Write(biome);
			pkg.Write(at);
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

		internal static void OnSettings(long sender, ZPackage pkg)
		{
			if (DeathMessagePlugin.Headless || ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
			try { Grave.OnSettings(pkg); }
			catch (Exception e) { DeathMessagePlugin.Log.LogWarning($"bad settings from the server: {e.GetType().Name}: {e.Message}"); }
		}

		internal static void ChatLine(string text)
		{
			if (Chat.instance == null) return;
			Chat.instance.AddString($"<color=#ff8080>{text}</color>");
			Chat.instance.m_hideTimer = 0f; // brings the chat window up, as a chat line does
		}

		// ----- server -----

		internal static void OnHello(long sender)
		{
			if (!IsServer) return;
			ZRoutedRpc.instance.InvokeRoutedRPC(sender, SettingsRpc, Grave.Settings());
		}

		internal static void OnReport(long sender, ZPackage pkg)
		{
			if (!IsServer) return;
			try
			{
				float now = Time.unscaledTime;
				if (lastReport.TryGetValue(sender, out float last) && now - last < MinSecondsBetweenReports) return;
				lastReport[sender] = now;

				int protocol = pkg.ReadInt();
				if (protocol < 1 || protocol > Protocol) return;
				string cause = Clean(pkg.ReadString(), MaxCause);
				string biome = new string(Clean(pkg.ReadString(), 24).Where(char.IsLetter).ToArray());
				if (cause.Length == 0) return;

				// The name comes from the connection, never from the message: a player reports only their own death.
				ZNetPeer peer = ZNet.instance.GetPeer(sender);
				string name = peer != null ? peer.m_playerName
					: sender == ZRoutedRpc.instance.m_id && Game.instance != null ? Game.instance.GetPlayerProfile().GetName()
					: null;
				if (string.IsNullOrEmpty(name)) return;
				// The spot the player's game gives, as long as it is near where the server last saw
				// them (it hears every couple of seconds); otherwise the server's own view.
				Vector3 seen = peer != null ? peer.m_refPos : Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
				Vector3 at = seen;
				if (protocol >= 2)
				{
					Vector3 told = pkg.ReadVector3();
					if (Vector3.Distance(told, seen) <= MaxPosDrift) at = told;
				}
				string pos = $"({at.x:0}, {at.y:0}, {at.z:0})";

				DeathMessagePlugin.Log.LogInfo($"{name} {cause} ({biome}) at {pos}");
				if (peer != null && DeathMessagePlugin.Discord.Value) DiscordBridge.Report(peer, cause, biome, pos);
				if (DeathMessagePlugin.Announce.Value) Announce(sender, Fill(DeathMessagePlugin.AnnounceFormat.Value, cause, biome, pos).Replace("{player}", name));
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"bad death report from {sender}: {e.GetType().Name}: {e.Message}");
			}
		}

		// To everyone but the player who died: their own game has already told them. MessageHud
		// registers "ShowMessage" (type, text) in every game, so this needs no mod on the other end.
		private static void Announce(long except, string text)
		{
			int where = (int)DeathMessagePlugin.AnnounceWhere.Value;
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				if (peer.IsReady() && peer.m_uid != except) ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", where, text);
			}
			// A player hosting the world is the server and has no peer of their own.
			if (!DeathMessagePlugin.Headless && except != ZRoutedRpc.instance.m_id && MessageHud.instance != null)
			{
				MessageHud.instance.ShowMessage(DeathMessagePlugin.AnnounceWhere.Value, text);
			}
			DeathMessagePlugin.Debug($"announced: {text}");
		}

		internal static string Fill(string format, string cause, string biome, string pos) =>
			format.Replace("{cause}", cause).Replace("{biome}", Spaced(biome)).Replace("{pos}", pos);

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
