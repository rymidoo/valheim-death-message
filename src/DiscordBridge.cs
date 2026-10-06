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
	// DiscordConnector notices a death by itself, on the server, but only when the player's new
	// character arrives after the respawn: ten seconds or more after the death, and with the
	// player standing at their bed again, so its message is late and gives the spawn point as the
	// place. A player's report arrives the moment they die. So, on a report, this runs
	// DiscordConnector's own death handling there and then - first-death event, stats, webhook
	// and all - with the message swapped for one that carries the cause and the real place, and
	// lets DiscordConnector's own late notice of the same death pass by unsent.
	//
	// A death nobody reported goes out exactly as DiscordConnector would have sent it.
	// Found by name at run time, so nothing breaks when DiscordConnector is absent or changes.
	internal static class DiscordBridge
	{
		private const float KeepSeconds = 300f;
		private const float ShoutSeconds = 15f;

		private class Reported
		{
			public string Cause, Biome, Pos;
			public float At;
			public bool Swapped;  // the message for it has been through here
			public bool Posted;   // DiscordConnector's handling has been run for it already
		}

		private static readonly Dictionary<long, Reported> reported = new Dictionary<long, Reported>();
		private static MethodInfo death;
		private static bool posting;

		internal static void Hook(Harmony harmony)
		{
			try
			{
				Type handlers = AccessTools.TypeByName("DiscordConnector.Handlers");
				if (handlers == null) return; // not installed, e.g. in a player's game
				List<MethodInfo> methods = AccessTools.GetDeclaredMethods(handlers);
				// The overload every join / leave / death message passes through: (peer, playerHostName, preFormattedMessage, posEnabled, ev).
				MethodInfo send = methods.FirstOrDefault(m =>
				{
					ParameterInfo[] p = m.GetParameters();
					return m.Name == "FinalizeFormattingAndSend" && p.Length == 5 && p[0].ParameterType == typeof(ZNetPeer)
						&& p[2].ParameterType == typeof(string) && p[2].Name == "preFormattedMessage" && p[3].ParameterType == typeof(bool) && p[4].ParameterType.IsEnum;
				});
				if (send == null)
				{
					DeathMessagePlugin.Log.LogWarning("DiscordConnector is installed but its message hook was not found (different version?). Deaths go to Discord without the cause.");
					return;
				}
				harmony.Patch(send, prefix: new HarmonyMethod(typeof(DiscordBridge), nameof(SendPrefix)));

				// Death(peer): DiscordConnector's whole handling of one death.
				death = methods.FirstOrDefault(m => m.Name == "Death" && m.IsStatic && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(ZNetPeer));
				if (death != null) harmony.Patch(death, prefix: new HarmonyMethod(typeof(DiscordBridge), nameof(DeathPrefix)));
				else DeathMessagePlugin.Log.LogWarning("DiscordConnector's death handler was not found: the cause is added, but the message only goes out after the respawn.");

				// Shout(peer, pos, text): where DiscordConnector forwards a player's shout.
				MethodInfo shout = methods.FirstOrDefault(m =>
				{
					ParameterInfo[] p = m.GetParameters();
					return m.Name == "Shout" && p.Length == 3 && p[0].ParameterType == typeof(ZNetPeer) && p[2].ParameterType == typeof(string);
				});
				if (shout != null) harmony.Patch(shout, prefix: new HarmonyMethod(typeof(DiscordBridge), nameof(ShoutPrefix)));
				else DeathMessagePlugin.Log.LogWarning("DiscordConnector's shout hook was not found: a shouted cause of death will also be posted as a shout.");

				DeathMessagePlugin.Log.LogInfo("DiscordConnector found: death messages will carry the cause");
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"could not hook DiscordConnector: {e.GetType().Name}: {e.Message}");
			}
		}

		// A player's game has reported a death.
		internal static void Report(ZNetPeer peer, string cause, string biome, string pos)
		{
			var r = new Reported { Cause = cause, Biome = biome, Pos = pos, At = Time.unscaledTime };
			reported[peer.m_uid] = r;
			if (death == null) return; // then the message is swapped when DiscordConnector gets round to it
			try
			{
				posting = true;
				death.Invoke(null, new object[] { peer });
				r.Posted = true;
			}
			catch (Exception e)
			{
				Exception inner = e.InnerException ?? e;
				DeathMessagePlugin.Log.LogWarning($"could not post the death through DiscordConnector, it will post it itself after the respawn: {inner.GetType().Name}: {inner.Message}");
			}
			finally
			{
				posting = false;
			}
		}

		internal static void Clear() => reported.Clear();

		private static bool Fresh(long uid, float seconds, out Reported r) =>
			reported.TryGetValue(uid, out r) && Time.unscaledTime - r.At <= seconds;

		// DiscordConnector's own notice of a death, after the respawn: not again, if it is the one already posted.
		private static bool DeathPrefix(ZNetPeer __0)
		{
			try
			{
				if (posting || __0 == null || !Fresh(__0.m_uid, KeepSeconds, out Reported r) || !r.Posted) return true;
				r.Posted = false; // this one death only; the next unreported one is DiscordConnector's again
				DeathMessagePlugin.Debug($"discord: late death notice for {__0.m_playerName} skipped, already posted");
				return false;
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"death check failed: {e.GetType().Name}: {e.Message}");
				return true;
			}
		}

		private static void SendPrefix(ZNetPeer __0, ref string __2, ref bool __3, object __4)
		{
			try
			{
				string ev = __4?.ToString() ?? "";
				if (__0 == null || (ev != "PlayerDeath" && ev != "PlayerFirstDeath")) return;
				if (!DeathMessagePlugin.Discord.Value || !Fresh(__0.m_uid, KeepSeconds, out Reported r) || r.Swapped) return;
				string format = ev == "PlayerFirstDeath" ? DeathMessagePlugin.DiscordFirstDeathMessage.Value : DeathMessagePlugin.DiscordMessage.Value;
				if (string.IsNullOrWhiteSpace(format)) return;
				r.Swapped = true;
				__2 = Relay.Fill(format, r.Cause, r.Biome, r.Pos);
				__3 = false; // the place is in the message; DiscordConnector's own would be wherever the player is now
				DeathMessagePlugin.Debug($"discord: {__0.m_playerName}: {__2}");
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"discord message left unchanged: {e.GetType().Name}: {e.Message}");
			}
		}

		// The dying player's game shouts the cause for the other players to read. Discord gets the
		// cause in the death message, so the shout is not passed on a second time.
		private static bool ShoutPrefix(ZNetPeer __0, string __2)
		{
			try
			{
				if (__0 == null || __2 == null || !DeathMessagePlugin.Discord.Value) return true;
				if (!Fresh(__0.m_uid, ShoutSeconds, out Reported r)) return true;
				// DiscordConnector may hand the text over in capitals.
				if (__2.IndexOf(r.Cause, StringComparison.OrdinalIgnoreCase) < 0) return true;
				DeathMessagePlugin.Debug($"discord: shout by {__0.m_playerName} held back, it is their cause of death");
				return false;
			}
			catch (Exception e)
			{
				DeathMessagePlugin.Log.LogWarning($"shout check failed: {e.GetType().Name}: {e.Message}");
				return true;
			}
		}
	}
}
