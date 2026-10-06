using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace DeathMessage
{
	// Client half: what killed you. The last hits your character takes are remembered (source,
	// damage by type after armor / block / resistances, health left); when you die the killing hit
	// is turned into a cause, shown, added to a history file and reported to the server.
	// The method is the death log of Valheim Vanilla Plus; here every cause also has a wording
	// about somebody else ("was killed by Troll"), which is the one that leaves this game.
	internal static class DeathTracker
	{
		private const int KeepHits = 8, KeepDeaths = 30, ShownDeaths = 10;
		internal static string FilePath => Path.Combine(Paths.ConfigPath, DeathMessagePlugin.Guid + ".deaths.txt");

		internal static readonly Type[] Patches =
		{
			typeof(Hit_Patch), typeof(Source_Patch), typeof(EffectStats_Patch), typeof(EffectBurning_Patch),
			typeof(EffectPoison_Patch), typeof(EffectSmoke_Patch), typeof(Death_Patch),
		};

		private class Hit
		{
			public float At, HealthAfter, MaxHealth;
			public string From;      // who dealt it, or null
			public string Kind;      // the game's hit type: Fall, Drowning, Burning, ...
			public string Element;   // fire / poison / ... when the hit was only that
			public string Effect;    // the status effect that dealt it ("Puking", "Poison", ...), or null
			public string Damage;    // "42 blunt, 10 fire"
		}

		private class Death
		{
			public DateTime When;
			public string World, Biome, Cause;
			public Vector3 Pos;
			public List<string> Hits = new List<string>();
		}

		// One cause, said to the player it happened to and about them to everyone else.
		private struct Wording
		{
			public string You, They;
			public Wording(string you, string they) { You = you; They = they; }
			public Wording Plus(string note) => new Wording(You + note, They + note);
		}

		private static readonly List<Hit> Recent = new List<Hit>();
		private static readonly List<Death> Deaths = new List<Death>(); // newest first
		// Who last put fire / poison / ... on you directly, for the ticks that follow without an attacker.
		private static readonly Dictionary<string, KeyValuePair<string, float>> ElementFrom = new Dictionary<string, KeyValuePair<string, float>>();
		private static string announce;
		private static float announceAt;

		// ----- recording -----

		// The status effect whose update is running right now, if any: a hit taken inside it was dealt
		// by that effect (bukeperry puking, poison, burning, smoke, ...). Such hits carry no attacker
		// and often no hit type, so this is the only way to name them.
		private static StatusEffect effect;
		private static int effectDepth;

		private static void EnterEffect(StatusEffect se) { effectDepth++; effect = se; }
		private static void LeaveEffect() { if (--effectDepth <= 0) { effectDepth = 0; effect = null; } }

		private static bool On => DeathMessagePlugin.LocalLog.Value || DeathMessagePlugin.SendToServer.Value;

		private static string EffectName(StatusEffect se)
		{
			if (se == null) return null;
			string name = string.IsNullOrEmpty(se.m_name) ? "" : Localization.instance.Localize(se.m_name);
			return string.IsNullOrEmpty(name) || name.StartsWith("$") || name.StartsWith("[") ? se.name : name;
		}

		// Called when a hit arrives, before the game takes its poison / fire / spirit part off to turn
		// it into a lingering effect: remembers who that part came from, since the ticks that follow
		// carry no attacker.
		private static void NoteSource(Character target, HitData hit)
		{
			if (!On || hit == null || target == null || target != Player.m_localPlayer) return;
			Character attacker = hit.GetAttacker();
			if (attacker == null || attacker == target) return;
			HitData.DamageTypes d = hit.m_damage;
			string from = Name(attacker);
			void Note(string element, float amount) { if (amount > 0f) ElementFrom[element] = new KeyValuePair<string, float>(from, Time.time); }
			Note("poison", d.m_poison); Note("fire", d.m_fire); Note("spirit", d.m_spirit); Note("frost", d.m_frost); Note("lightning", d.m_lightning);
		}

		// Called just before a hit's damage is taken off a character's health.
		private static void Record(Character target, HitData hit)
		{
			if (!On || hit == null || target == null || target != Player.m_localPlayer) return;
			float total = hit.GetTotalDamage();
			if (total <= 0f) return;

			HitData.DamageTypes d = hit.m_damage;
			Character attacker = hit.GetAttacker();
			string from = attacker != null && attacker != target ? Name(attacker) : null;

			var elements = new[]
			{
				new KeyValuePair<string, float>("fire", d.m_fire), new KeyValuePair<string, float>("frost", d.m_frost),
				new KeyValuePair<string, float>("lightning", d.m_lightning), new KeyValuePair<string, float>("poison", d.m_poison),
				new KeyValuePair<string, float>("spirit", d.m_spirit),
			};
			float physical = d.m_damage + d.m_blunt + d.m_slash + d.m_pierce + d.m_chop + d.m_pickaxe;
			KeyValuePair<string, float> biggest = elements.OrderByDescending(e => e.Value).First();
			if (from != null)
			{
				foreach (KeyValuePair<string, float> e in elements)
				{
					if (e.Value > 0f) ElementFrom[e.Key] = new KeyValuePair<string, float>(from, Time.time);
				}
			}

			Recent.Add(new Hit
			{
				At = Time.time, From = from, Kind = hit.m_hitType.ToString(),
				Element = physical <= 0f && biggest.Value > 0f ? biggest.Key : null,
				Effect = EffectName(effect),
				Damage = Breakdown(d), HealthAfter = Mathf.Max(0f, target.GetHealth() - total), MaxHealth = target.GetMaxHealth(),
			});
			if (Recent.Count > KeepHits) Recent.RemoveAt(0);
		}

		private static string Name(Character c)
		{
			string name = Localization.instance.Localize(c.GetHoverName());
			if (c.IsPlayer()) return "player " + name;
			return c.GetLevel() > 1 ? $"{name} ★{c.GetLevel() - 1}" : name;
		}

		private static string Breakdown(HitData.DamageTypes d)
		{
			var parts = new List<string>();
			void Add(string n, float v) { if (v >= 0.05f) parts.Add($"{v:0.#} {n}"); }
			Add("damage", d.m_damage); Add("blunt", d.m_blunt); Add("slash", d.m_slash); Add("pierce", d.m_pierce);
			Add("chop", d.m_chop); Add("pickaxe", d.m_pickaxe); Add("fire", d.m_fire); Add("frost", d.m_frost);
			Add("lightning", d.m_lightning); Add("poison", d.m_poison); Add("spirit", d.m_spirit);
			return parts.Count > 0 ? string.Join(", ", parts) : "no damage";
		}

		private static Wording Cause(Hit h)
		{
			// Lingering fire / poison has no attacker of its own: name who put it on you, if that was recent.
			string fromNote = "";
			if (h.From == null && h.Element != null && ElementFrom.TryGetValue(h.Element, out KeyValuePair<string, float> src) && Time.time - src.Value < 90f)
			{
				fromNote = $" (from {src.Key})";
			}

			switch (h.Kind)
			{
				case "Fall": return new Wording("Fell to your death", "fell to their death");
				case "Drowning": return new Wording("Drowned", "drowned");
				case "Burning": case "CinderFire": return new Wording("Burned to death", "burned to death").Plus(fromNote);
				case "Freezing": return new Wording("Froze to death", "froze to death");
				case "Poisoned": return new Wording("Died from poison", "died from poison").Plus(fromNote);
				case "Smoke": return new Wording("Suffocated in smoke", "suffocated in smoke");
				case "Tree": return new Wording("Crushed by a falling tree", "was crushed by a falling tree");
				case "Cart": return new Wording("Run over by a cart", "was run over by a cart");
				case "Boat": return new Wording("Hit by a ship", "was hit by a ship");
				case "Stalagtite": return new Wording("Hit by a falling stalactite", "was hit by a falling stalactite");
				case "Structural": return new Wording("Crushed by a collapsing structure", "was crushed by a collapsing structure");
				case "Impact": return new Wording("Killed by an impact", "was killed by an impact");
				case "Turret": return new Wording("Shot by a ballista", "was shot by a ballista");
				case "Catapult": return new Wording("Hit by a catapult", "was hit by a catapult");
				case "EdgeOfWorld": return new Wording("Fell off the edge of the world", "fell off the edge of the world");
				case "AshlandsOcean": return new Wording("Boiled in the Ashlands sea", "boiled in the Ashlands sea");
				case "AshlandsLava": return new Wording("Burned in lava", "burned in lava");
				case "Incinerator": return new Wording("Struck by the obliterator", "was struck by the obliterator");
				case "DrawBridge": return new Wording("Crushed by a drawbridge", "was crushed by a drawbridge");
				case "Water": return new Wording("Killed by water", "was killed by water");
				case "Self": return new Wording("Killed by your own attack", "was killed by their own attack");
			}
			if (h.From != null)
			{
				string by = h.Element != null ? $"{h.From} ({h.Element})" : h.From;
				return new Wording("Killed by " + by, "was killed by " + by);
			}
			// No attacker and no telling hit type, but it came from a status effect: name the effect.
			var fromEffect = new Wording($"Died from the \"{h.Effect}\" effect", $"died from the \"{h.Effect}\" effect");
			if (h.Effect != null && h.Element == null) return fromEffect;
			switch (h.Element)
			{
				case "fire": return new Wording("Burned to death", "burned to death").Plus(fromNote);
				case "poison": return new Wording("Died from poison", "died from poison").Plus(fromNote);
				case "frost": return new Wording("Froze to death", "froze to death").Plus(fromNote);
				case "lightning": return new Wording("Killed by lightning", "was killed by lightning").Plus(fromNote);
				case "spirit": return new Wording("Killed by spirit damage", "was killed by spirit damage").Plus(fromNote);
			}
			return h.Effect != null ? fromEffect : new Wording("Killed by damage with no known source", "died of unknown causes");
		}

		private static void OnDeath(Player p)
		{
			if (!On || p == null || p != Player.m_localPlayer || !p.m_nview.IsOwner()) return;
			float now = Time.time;
			Hit last = Recent.Count > 0 ? Recent[Recent.Count - 1] : null;
			bool seen = last != null && now - last.At < 3f;
			Wording cause = seen ? Cause(last) : new Wording("Died with no damage seen just before (a command, or a hit this game did not process)", "died of unknown causes");
			string biome = p.GetCurrentBiome().ToString();

			if (DeathMessagePlugin.SendToServer.Value) Relay.Report(cause.They, biome);

			var death = new Death
			{
				When = DateTime.Now, World = ZNet.instance != null ? ZNet.instance.GetWorldName() : "",
				Biome = biome, Pos = p.transform.position,
				Cause = seen ? $"{cause.You} — last hit: {last.Damage}" : cause.You,
			};
			foreach (Hit h in Recent)
			{
				if (now - h.At < 30f)
				{
					death.Hits.Add($"{now - h.At:0.0} s before: {h.From ?? KindWords(h)}, {h.Damage} → {h.HealthAfter:0}/{h.MaxHealth:0} HP");
				}
			}
			Recent.Clear(); ElementFrom.Clear();
			if (!DeathMessagePlugin.LocalLog.Value) return;

			Deaths.Insert(0, death);
			if (Deaths.Count > KeepDeaths) Deaths.RemoveRange(KeepDeaths, Deaths.Count - KeepDeaths);
			Save();
			DeathMessagePlugin.Log.LogInfo($"{death.Cause} in {death.World} ({death.Biome}) at {death.Pos}" + (death.Hits.Count > 0 ? "\n  " + string.Join("\n  ", death.Hits) : ""));

			if (ShownByAnotherMod()) return;
			p.Message(MessageHud.MessageType.Center, $"<color=#ff6060>{death.Cause}</color>");
			announce = death.Cause; announceAt = Time.unscaledTime + 6f; // said again once you are back on your feet
		}

		// Valheim Vanilla Plus and Valheim God Mode carry the same death log and already put the
		// cause on screen. God Mode takes it over from Vanilla Plus when both are installed.
		private static bool ShownByAnotherMod()
		{
			bool Shows(string guid) =>
				Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info) && info.Instance != null
				&& (!info.Instance.Config.TryGetEntry("DeathLog", "Enabled", out BepInEx.Configuration.ConfigEntry<bool> on) || on.Value);
			if (Chainloader.PluginInfos.ContainsKey("local.valheimgodmode")) return Shows("local.valheimgodmode");
			return Shows("liekos47.valheimvanillaplus");
		}

		private static string KindWords(Hit h)
		{
			if (h.Effect != null) return $"\"{h.Effect}\" effect";
			if (h.Element != null) return h.Element;
			switch (h.Kind)
			{
				case "Fall": return "fall";
				case "Drowning": return "drowning";
				case "Burning": case "CinderFire": return "fire";
				case "AshlandsLava": return "lava";
				case "AshlandsOcean": return "boiling sea";
				case "Tree": return "falling tree";
				case "Freezing": return "freezing";
				case "Poisoned": return "poison";
				case "Smoke": return "smoke";
				default: return "unknown source";
			}
		}

		// Every frame: repeats the cause top-left once you have respawned.
		internal static void Update()
		{
			Relay.Hello();
			Player p = Player.m_localPlayer;
			if (announce == null || p == null || p.IsDead() || Time.unscaledTime < announceAt) return;
			p.Message(MessageHud.MessageType.TopLeft, "Last death: " + announce);
			announce = null;
		}

		// ----- file -----

		private const string HitSeparator = " || ";

		internal static void Load()
		{
			Deaths.Clear();
			if (!File.Exists(FilePath)) return;
			foreach (string line in File.ReadAllLines(FilePath))
			{
				string[] f = line.Split('\t');
				if (f.Length < 7) continue;
				try
				{
					var d = new Death
					{
						When = DateTime.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), World = f[1], Biome = f[2],
						Pos = new Vector3(P(f[3]), P(f[4]), P(f[5])), Cause = f[6],
					};
					if (f.Length > 7 && f[7].Length > 0) d.Hits.AddRange(f[7].Split(new[] { HitSeparator }, StringSplitOptions.RemoveEmptyEntries));
					Deaths.Add(d);
				}
				catch (FormatException) { }
			}
		}

		private static void Save()
		{
			try
			{
				File.WriteAllLines(FilePath, Deaths.Select(d => string.Join("\t", d.When.ToString("o", CultureInfo.InvariantCulture), T(d.World), T(d.Biome),
					S(d.Pos.x), S(d.Pos.y), S(d.Pos.z), T(d.Cause), string.Join(HitSeparator, d.Hits.Select(T)))));
			}
			catch (IOException e)
			{
				DeathMessagePlugin.Log.LogWarning($"could not write {FilePath}: {e.Message}");
			}
		}

		private static float P(string s) => float.Parse(s, CultureInfo.InvariantCulture);
		private static string S(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
		private static string T(string s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ');

		// ----- console -----

		// "deaths" in the F5 console lists the history; "deaths hits" adds the hits before each one.
		internal static void AddCommand()
		{
			new Terminal.ConsoleCommand("deaths", "[hits] - your recent deaths and what caused them", args =>
			{
				if (Deaths.Count == 0) { args.Context.AddString("No deaths recorded."); return; }
				bool hits = args.Length > 1 && args[1].ToLower() == "hits";
				foreach (Death d in Deaths.Take(ShownDeaths))
				{
					args.Context.AddString($"{d.When:yyyy-MM-dd HH:mm}  {d.World}, {d.Biome} ({d.Pos.x:0}, {d.Pos.y:0}, {d.Pos.z:0})  {d.Cause}");
					if (hits) foreach (string h in d.Hits) args.Context.AddString("    " + h);
				}
				if (Deaths.Count > ShownDeaths) args.Context.AddString($"{Deaths.Count - ShownDeaths} older ones are in {FilePath}");
			});
		}

		// ----- patches -----

		// ApplyDamage receives the final hit (after armor, block and resistances) just before health drops.
		[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
		private static class Hit_Patch
		{
			private static void Prefix(Character __instance, HitData hit) => Record(__instance, hit);
		}

		// RPC_Damage is where a hit lands on the client that owns the target, with its poison / fire part
		// still on it (it is split off into a lingering effect before ApplyDamage).
		[HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
		private static class Source_Patch
		{
			private static void Prefix(Character __instance, HitData hit) => NoteSource(__instance, hit);
		}

		// The status effects that deal damage of their own: while one updates, hits are credited to it.
		// (Bukeperry puking is SE_Puke, which runs its damage through SE_Stats.)
		[HarmonyPatch(typeof(SE_Stats), nameof(SE_Stats.UpdateStatusEffect))]
		private static class EffectStats_Patch
		{
			private static void Prefix(StatusEffect __instance) => EnterEffect(__instance);
			private static void Finalizer() => LeaveEffect();
		}

		[HarmonyPatch(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect))]
		private static class EffectBurning_Patch
		{
			private static void Prefix(StatusEffect __instance) => EnterEffect(__instance);
			private static void Finalizer() => LeaveEffect();
		}

		[HarmonyPatch(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect))]
		private static class EffectPoison_Patch
		{
			private static void Prefix(StatusEffect __instance) => EnterEffect(__instance);
			private static void Finalizer() => LeaveEffect();
		}

		[HarmonyPatch(typeof(SE_Smoke), nameof(SE_Smoke.UpdateStatusEffect))]
		private static class EffectSmoke_Patch
		{
			private static void Prefix(StatusEffect __instance) => EnterEffect(__instance);
			private static void Finalizer() => LeaveEffect();
		}

		[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
		private static class Death_Patch
		{
			private static void Prefix(Player __instance)
			{
				try { OnDeath(__instance); }
				catch (Exception e) { DeathMessagePlugin.Log.LogError($"death handling failed: {e}"); }
			}
		}
	}
}
