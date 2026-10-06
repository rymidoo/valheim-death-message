using UnityEngine;

namespace DeathMessage
{
	// "/grave" in chat (or "grave" in the console): pay to be taken back to where you last died.
	// Off unless the server turns it on; the server also sets the price, and tells each game with
	// the mod when it says hello. Like a portal, the payment and the trip happen in the player's
	// own game - that is where Valheim keeps a player's inventory and position.
	internal static class Grave
	{
		private const string Command = "grave";

		// What the server said. Stays off until it has spoken.
		private static bool serverEnabled;
		private static string serverItem = "";
		private static int serverCost;
		// One trip per death: the game never forgets a death point, also not once the grave is emptied.
		private static Vector3? used;

		private static bool Hosting => ZNet.instance != null && ZNet.instance.IsServer();
		private static bool Enabled => Hosting ? DeathMessagePlugin.GraveTeleport.Value : serverEnabled;
		private static string ItemName => Hosting ? DeathMessagePlugin.GraveTeleportItem.Value : serverItem;
		private static int Cost => Mathf.Max(0, Hosting ? DeathMessagePlugin.GraveTeleportCost.Value : serverCost);

		internal static void NewSession()
		{
			serverEnabled = false;
			used = null;
		}

		internal static ZPackage Settings()
		{
			var pkg = new ZPackage();
			pkg.Write(DeathMessagePlugin.GraveTeleport.Value);
			pkg.Write(DeathMessagePlugin.GraveTeleportItem.Value);
			pkg.Write(DeathMessagePlugin.GraveTeleportCost.Value);
			return pkg;
		}

		internal static void OnSettings(ZPackage pkg)
		{
			serverEnabled = pkg.ReadBool();
			serverItem = pkg.ReadString();
			serverCost = pkg.ReadInt();
			DeathMessagePlugin.Debug($"grave teleport: {(serverEnabled ? $"on, {serverCost} {serverItem}" : "off")}");
		}

		internal static void AddCommand()
		{
			new Terminal.ConsoleCommand(Command, "- pay to be taken back to where you last died, when the server allows it", args =>
			{
				string result = Go();
				if (result != null) args.Context.AddString(result);
			});
		}

		// Returns what to tell the player, or null when they are on their way.
		private static string Go()
		{
			Player p = Player.m_localPlayer;
			if (p == null || Game.instance == null || ZNet.instance == null) return "Not in a world.";
			if (!Enabled) return "Going back to your grave is not enabled on this server.";
			if (p.IsDead() || p.IsTeleporting() || p.InCutscene()) return "Not right now.";

			PlayerProfile profile = Game.instance.GetPlayerProfile();
			if (!profile.HaveDeathPoint()) return "You have not died in this world.";
			Vector3 grave = profile.GetDeathPoint();
			if (used.HasValue && Vector3.Distance(used.Value, grave) < 1f) return "You have already gone back to this grave.";
			if (!p.IsTeleportable(ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll))) return "You are carrying something that cannot be teleported.";

			int cost = Cost;
			string shared = null, shown = ItemName;
			if (cost > 0)
			{
				GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(ItemName) : null;
				ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
				if (drop == null) return $"The server asks for an item this game does not know (\"{ItemName}\").";
				shared = drop.m_itemData.m_shared.m_name;
				shown = Localization.instance.Localize(shared);
				// Any quality and world level counts, as it does when the item is taken below.
				int have = p.GetInventory().CountItems(shared, -1, false);
				if (have < cost) return $"Going back to your grave costs {cost} {shown}. You are carrying {have}.";
			}

			if (!p.TeleportTo(grave + Vector3.up * 0.5f, p.transform.rotation, true)) return "Not right now - try again in a moment.";
			if (cost > 0) p.GetInventory().RemoveItem(shared, cost, -1, false);
			used = grave;
			DeathMessagePlugin.Log.LogInfo($"went back to the grave at {grave}" + (cost > 0 ? $" for {cost} {shown}" : ""));
			p.Message(MessageHud.MessageType.TopLeft, cost > 0 ? $"Paid {cost} {shown} to return to your grave" : "Returning to your grave");
			return null;
		}
	}
}
