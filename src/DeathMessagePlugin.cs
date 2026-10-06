using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeathMessage
{
	/*
		Says what killed a player, to everyone.

		The same DLL runs on both ends. In a player's game it remembers the last hits their own
		character takes, turns the killing one into a cause ("was killed by Troll", "drowned"),
		keeps a local history, and reports the cause to the server. On the server it puts the
		player's name in front, tells the other players, and - if DiscordConnector is installed -
		swaps the cause into the death message DiscordConnector is about to post.

		Only the dying player's own game knows what hit them (damage to a character is worked out
		by the client that owns it), which is why the server cannot do this alone.
	*/
	[BepInPlugin(Guid, Name, Version)]
	public class DeathMessagePlugin : BaseUnityPlugin
	{
		public const string Guid = "liekos47.deathmessage";
		public const string Name = "DeathMessage";
		public const string Version = "0.1.0";

		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> LocalLog;
		internal static ConfigEntry<bool> SendToServer;
		internal static ConfigEntry<bool> OthersInChat;
		internal static ConfigEntry<bool> OthersTopLeft;

		internal static ConfigEntry<bool> Announce;
		internal static ConfigEntry<string> AnnounceFormat;
		internal static ConfigEntry<bool> AnnounceToUnmodded;
		internal static ConfigEntry<bool> Discord;
		internal static ConfigEntry<string> DiscordMessage;
		internal static ConfigEntry<string> DiscordFirstDeathMessage;
		internal static ConfigEntry<bool> DebugLog;

		// A dedicated server has no screen and no local player: the client half stays off there.
		internal static readonly bool Headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

		private Harmony harmony;
		private ZRoutedRpc registeredFor;
		private DateTime configStamp;

		private void Awake()
		{
			Log = Logger;

			LocalLog = Config.Bind("Client", "LocalLog", true,
				"Show what killed you on screen and keep a history in BepInEx/config/" + Path.GetFileName(DeathTracker.FilePath) + " (console command: deaths). The on-screen part is left to Vanilla Plus or God Mode when one of them is already showing it.");
			SendToServer = Config.Bind("Client", "SendToServer", true,
				"Report the cause of your deaths to the server, so it can tell the other players and Discord.");
			OthersInChat = Config.Bind("Client", "OthersInChat", true,
				"Show other players' deaths as a line in the chat window.");
			OthersTopLeft = Config.Bind("Client", "OthersTopLeft", false,
				"Show other players' deaths in the top-left message feed.");

			Announce = Config.Bind("Server", "Announce", true,
				"Tell the other players when someone dies and why.");
			AnnounceFormat = Config.Bind("Server", "AnnounceFormat", "{player} {cause}.",
				"The line players see. {player} = name, {cause} = e.g. \"was killed by Troll\", {biome} = where.");
			AnnounceToUnmodded = Config.Bind("Server", "AnnounceToUnmodded", true,
				"Players without this mod get the line in their top-left message feed. Players with it are shown it by the mod, as they have set it up.");
			Discord = Config.Bind("Server", "Discord", true,
				"When DiscordConnector is installed, put the cause into the death message it posts. Whether a death is posted at all, and whether the position is included, stays DiscordConnector's own setting.");
			DiscordMessage = Config.Bind("Server", "DiscordMessage", "%PLAYER_NAME% {cause}.",
				"Replaces DiscordConnector's death message when the cause is known. {cause} and {biome} are filled in here; %PLAYER_NAME% and DiscordConnector's other variables by DiscordConnector.");
			DiscordFirstDeathMessage = Config.Bind("Server", "DiscordFirstDeathMessage", "%PLAYER_NAME% {cause}. That was their first death.",
				"The same, for the message DiscordConnector posts on a player's first death.");
			DebugLog = Config.Bind("General", "DebugLog", false, "Log every report and announcement.");

			harmony = new Harmony(Guid);
			if (!Headless)
			{
				DeathTracker.Load();
				foreach (Type patch in DeathTracker.Patches) harmony.CreateClassProcessor(patch).Patch();
				DeathTracker.AddCommand();
			}
			DiscordBridge.Hook(harmony);
			Log.LogInfo($"{Name} {Version} loaded ({(Headless ? "server" : "client")})");

			// Re-read the config file when it changes, so a server's settings apply without a restart.
			configStamp = ConfigStamp();
			InvokeRepeating(nameof(ReloadConfig), 30f, 30f);
		}

		private void Update()
		{
			try
			{
				RegisterRpc();
				if (!Headless) DeathTracker.Update();
			}
			catch (Exception e)
			{
				Log.LogError($"tick failed: {e}");
			}
		}

		// ZRoutedRpc is recreated for every session. A hot-reloaded copy also has to replace the
		// handlers the previous copy left behind, since registering a name twice throws.
		private void RegisterRpc()
		{
			if (ZRoutedRpc.instance == null || ZRoutedRpc.instance == registeredFor) return;
			foreach (string name in new[] { Relay.ReportRpc, Relay.HelloRpc, Relay.AnnounceRpc })
			{
				ZRoutedRpc.instance.m_functions.Remove(name.GetStableHashCode());
			}
			ZRoutedRpc.instance.Register<ZPackage>(Relay.ReportRpc, Relay.OnReport);
			ZRoutedRpc.instance.Register(Relay.HelloRpc, Relay.OnHello);
			ZRoutedRpc.instance.Register<string>(Relay.AnnounceRpc, Relay.OnAnnounce);
			registeredFor = ZRoutedRpc.instance;
			Relay.NewSession();
		}

		private DateTime ConfigStamp() => File.GetLastWriteTimeUtc(Config.ConfigFilePath);

		private void ReloadConfig()
		{
			try
			{
				if (ConfigStamp() == configStamp) return;
				Config.Reload();
				// Taken after the reload: a changed value makes BepInEx write the file back.
				configStamp = ConfigStamp();
				Log.LogInfo("config file changed, settings re-read");
			}
			catch (Exception e)
			{
				Log.LogWarning($"config reload failed: {e.Message}");
			}
		}

		// Leave nothing behind, so the plugin can be unloaded and loaded again (ScriptEngine).
		private void OnDestroy()
		{
			CancelInvoke();
			harmony?.UnpatchSelf();
		}

		internal static void Debug(string text)
		{
			if (DebugLog.Value) Log.LogInfo(text);
		}
	}
}
