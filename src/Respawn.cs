using HarmonyLib;
using UnityEngine;

namespace DeathMessage
{
	// After a death the game waits twice, both for a fixed time: 10 seconds looking at where you
	// fell before your character is taken away, then at least 8 seconds at the spawn point before
	// you are put there, however quickly the area loaded. These two settings shorten the waits.
	// The second is only a minimum: the game still holds you until the area is ready.
	[HarmonyPatch(typeof(Game), nameof(Game.RequestRespawn))]
	internal static class Respawn
	{
		internal const float VanillaDeathView = 10f, VanillaLoad = 8f;
		// Below this the game can put you down before the server has sent your base. It then finds
		// no bed, forgets it as your spawn point and sends you to the start of the world.
		private const float MinLoad = 3f;

		private static bool saved;
		private static float gameLoad, gameFade;

		private static void Prefix(Game __instance, ref float delay, bool afterDeath)
		{
			if (!saved)
			{
				saved = true;
				gameLoad = __instance.m_respawnLoadDuration;
				gameFade = __instance.m_fadeTimeDeath;
			}
			float view = Mathf.Clamp(DeathMessagePlugin.DeathViewSeconds.Value, 1f, VanillaDeathView);
			float load = Mathf.Clamp(DeathMessagePlugin.RespawnLoadSeconds.Value, MinLoad, VanillaLoad);
			// Only a respawn after a death, and only when asked to: anything else is the game's own.
			if (!afterDeath || (view >= VanillaDeathView && load >= VanillaLoad))
			{
				__instance.m_respawnLoadDuration = gameLoad;
				__instance.m_fadeTimeDeath = gameFade;
				return;
			}
			delay = Mathf.Min(delay, view);
			__instance.m_respawnLoadDuration = Mathf.Min(gameLoad, load);
			// The screen has to be black by the time the character is taken away.
			__instance.m_fadeTimeDeath = Mathf.Min(gameFade, Mathf.Max(0.5f, delay - 0.5f));
		}
	}
}
