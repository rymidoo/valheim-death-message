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
	[BepInDependency("nwesterhausen.DiscordConnector", BepInDependency.DependencyFlags.SoftDependency)]
	[BepInPlugin(Guid, Name, Version)]
	public class DeathMessagePlugin : BaseUnityPlugin
	{
		public const string Guid = "liekos47.deathmessage";
		public const string Name = "DeathMessage";
		public const string Version = "0.1.3";

		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> LocalLog;
		internal static ConfigEntry<bool> SendToServer;
		internal static ConfigEntry<bool> ShoutDeath;
		internal static ConfigEntry<string> ShoutFormat;
		internal static ConfigEntry<float> DeathViewSeconds;
		internal static ConfigEntry<float> RespawnLoadSeconds;

		internal static ConfigEntry<bool> Announce;
		internal static ConfigEntry<string> AnnounceFormat;
		internal static ConfigEntry<MessageHud.MessageType> AnnounceWhere;
		internal static ConfigEntry<bool> Discord;
		internal static ConfigEntry<string> DiscordMessage;
		internal static ConfigEntry<string> DiscordFirstDeathMessage;
		internal static ConfigEntry<bool> GraveTeleport;
		internal static ConfigEntry<string> GraveTeleportItem;
		internal static ConfigEntry<int> GraveTeleportCost;
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
				"Show what killed you in the middle of the screen and in the chat box, and keep a history in BepInEx/config/" + Path.GetFileName(DeathTracker.FilePath) + " (/deaths in chat lists it).");
			SendToServer = Config.Bind("Client", "SendToServer", true,
				"Report the cause of your deaths to the server, so it can announce it to the other players and put it in the Discord death message.");
			ShoutDeath = Config.Bind("Client", "ShoutDeath", true,
				"When you die, shout the cause, so every player reads it in their chat - also those without this mod. It appears as a shout from you.");
			ShoutFormat = Config.Bind("Client", "ShoutFormat", "{cause} at {pos}",
				"What is shouted; the game puts your name in front. {cause} = e.g. \"was killed by Troll\", {pos} = coordinates (x, height, z), {biome} = biome name.");

			Announce = Config.Bind("Server", "Announce", true,
				"When a player's game reports a death, show the other players a message on screen. It reaches every player, with this mod or without.");
			AnnounceFormat = Config.Bind("Server", "AnnounceFormat", "{player} {cause} at {pos}.",
				"The message. {player} = name, {cause} = e.g. \"was killed by Troll\", {pos} = coordinates (x, height, z), {biome} = biome name.");
			AnnounceWhere = Config.Bind("Server", "AnnounceWhere", MessageHud.MessageType.TopLeft,
				"TopLeft = the message feed, which queues and is kept in the Compendium's log. Center = across the middle of the screen for a few seconds.");
			DeathViewSeconds = Config.Bind("Client", "DeathViewSeconds", Respawn.VanillaDeathView,
				"Seconds you look at where you fell before the respawn starts. The game's own value is 10; 1 is the least.");
			RespawnLoadSeconds = Config.Bind("Client", "RespawnLoadSeconds", Respawn.VanillaLoad,
				"The least number of seconds the game then waits at your spawn point before putting you there. The game's own value is 8; 3 is the least. It still waits longer when the area is not ready. Set too low on a slow connection, the game can arrive before your base has, find no bed and reset your spawn point.");
			Discord = Config.Bind("Server", "Discord", true,
				"When DiscordConnector is installed, put the cause into the death message it posts, and keep the shouted copy of it out of Discord. Whether a death is posted at all stays DiscordConnector's own setting.");
			DiscordMessage = Config.Bind("Server", "DiscordMessage", "%PLAYER_NAME% {cause}. Coords: {pos}",
				"Replaces DiscordConnector's death message when the cause is known. {cause}, {biome} and {pos} (where they died) are filled in here. DiscordConnector's own position is left off these messages: it is taken after the respawn, so it is the bed, not the grave; %PLAYER_NAME% and DiscordConnector's other variables by DiscordConnector.");
			DiscordFirstDeathMessage = Config.Bind("Server", "DiscordFirstDeathMessage", "%PLAYER_NAME% {cause}. That was their first death. Coords: {pos}",
				"The same, for the message DiscordConnector posts on a player's first death.");
			GraveTeleport = Config.Bind("Server", "GraveTeleport", false,
				"Let players with the mod pay to be taken back to where they last died, once per death: /grave in chat. The usual portal rule about ore and the like applies.");
			GraveTeleportItem = Config.Bind("Server", "GraveTeleportItem", "Coins",
				"The item it costs, by prefab name. It is taken from what the player carries, and a player who has just died carries nothing: they have to fetch it first.");
			GraveTeleportCost = Config.Bind("Server", "GraveTeleportCost", 100,
				"How many of that item one trip costs. 0 = free.");
			DebugLog = Config.Bind("General", "DebugLog", false, "Log every report and what is done with it.");

			harmony = new Harmony(Guid);
			if (!Headless)
			{
				DeathTracker.Load();
				foreach (Type patch in DeathTracker.Patches) harmony.CreateClassProcessor(patch).Patch();
				harmony.CreateClassProcessor(typeof(Respawn)).Patch();
				DeathTracker.AddCommand();
				Grave.AddCommand();
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
			foreach (string name in new[] { Relay.ReportRpc, Relay.HelloRpc, Relay.SettingsRpc })
			{
				ZRoutedRpc.instance.m_functions.Remove(name.GetStableHashCode());
			}
			ZRoutedRpc.instance.Register<ZPackage>(Relay.ReportRpc, Relay.OnReport);
			ZRoutedRpc.instance.Register(Relay.HelloRpc, Relay.OnHello);
			ZRoutedRpc.instance.Register<ZPackage>(Relay.SettingsRpc, Relay.OnSettings);
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
