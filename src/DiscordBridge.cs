using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DeathMessage
{
	// Hands the cause to DiscordConnector (nwesterhausen, 3.x) when it is installed.
	//
	// DiscordConnector notices a death by itself, on the server, when the player's new character
	// arrives after the respawn - ten seconds or more after the death, so the player's report is
	// already here. It then formats its "Player Death Message" and sends it. This steps in between
	// and swaps that message for one with the cause; the sending, the position, the first-death
	// event, the stats and the webhook all stay DiscordConnector's. A death nobody reported goes
	// out exactly as DiscordConnector would have sent it.
	//
	// Found by name at run time, so nothing breaks when DiscordConnector is absent or changes.
	internal static class DiscordBridge
	{
		private const float KeepSeconds = 300f;

		private struct Pending { public string Cause, Biome; public float At; }
		private static readonly Dictionary<long, Pending> pending = new Dictionary<long, Pending>();

		internal static void Hook(Harmony harmony)
		{
			try
			{
				Type handlers = AccessTools.TypeByName("DiscordConnector.Handlers");
				if (handlers == null) return; // not installed, e.g. in a player's game
				// The overload every join / leave / death message passes through: (peer, playerHostName, preFormattedMessage, posEnabled, ev).
				MethodInfo send = AccessTools.GetDeclaredMethods(handlers).FirstOrDefault(m =>
				{
					ParameterInfo[] p = m.GetParameters();
					return m.Name == "FinalizeFormattingAndSend" && p.Length == 5 && p[0].ParameterType == typeof(ZNetPeer)
						&& p[2].ParameterType == typeof(string) && p[2].Name == "preFormattedMessage" && p[4].ParameterType.IsEnum;
				});
				if (send == null)
				{
					DeathMessagePlugin.Log.LogWarning("DiscordConnector is installed but its message hook was not found (different version?). Deaths go to Discord without the cause.");
					return;
				}
				harmony.Patch(send, prefix: new HarmonyMethod(typeof(DiscordBridge), nameof(Prefix)));
				DeathMessagePlugin.Log.LogInfo("DiscordConnector found: death messages will carry the cause");
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"could not hook DiscordConnector: {e.GetType().Name}: {e.Message}");
			}
		}

		internal static void Remember(long peer, string cause, string biome) =>
			pending[peer] = new Pending { Cause = cause, Biome = biome, At = Time.unscaledTime };

		internal static void Clear() => pending.Clear();

		private static void Prefix(ZNetPeer __0, ref string __2, object __4)
		{
			try
			{
				string ev = __4?.ToString() ?? "";
				if (__0 == null || (ev != "PlayerDeath" && ev != "PlayerFirstDeath")) return;
				if (!pending.TryGetValue(__0.m_uid, out Pending p)) return;
				pending.Remove(__0.m_uid);
				if (!DeathMessagePlugin.Discord.Value || Time.unscaledTime - p.At > KeepSeconds) return;
				string format = ev == "PlayerFirstDeath" ? DeathMessagePlugin.DiscordFirstDeathMessage.Value : DeathMessagePlugin.DiscordMessage.Value;
				if (string.IsNullOrWhiteSpace(format)) return;
				__2 = Relay.Fill(format, p.Cause, p.Biome);
				DeathMessagePlugin.Debug($"discord: {__0.m_playerName}: {__2}");
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"discord message left unchanged: {e.GetType().Name}: {e.Message}");
			}
		}
	}
}
