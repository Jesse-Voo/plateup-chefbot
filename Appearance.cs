using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Kitchen;
using KitchenData;
using UnityEngine;

namespace ChefBot
{
    /// <summary>Gives each bot its name, a colour and hat/outfit that match its job, through the game's own player profile.</summary>
    public partial class ChefBotSystem
    {
        float lastAppearanceTry;

        static Color RoleColour(BotRole r)
        {
            switch (r)
            {
                case BotRole.Server: return new Color(0.30f, 0.52f, 0.90f);       // blue
                case BotRole.Dishwasher: return new Color(0.25f, 0.75f, 0.70f);   // teal
                case BotRole.All: return new Color(0.65f, 0.45f, 0.85f);          // purple
                default: return new Color(0.96f, 0.62f, 0.25f);                   // cook: orange
            }
        }

        /// <summary>The look that fits each job, using names that exist in the game (see cosmetics.txt).</summary>
        static string DefaultCosmetic(BotRole r, CosmeticType type)
        {
            if (type == CosmeticType.Hat)
            {
                switch (r)
                {
                    case BotRole.Server: return "Visor Hat";
                    case BotRole.Dishwasher: return "Bandana 1";
                    case BotRole.All: return "Baseball Cap";
                    default: return "Tall Chef Hat";
                }
            }
            switch (r)
            {
                case BotRole.Server: return "Waiter Outfit";
                case BotRole.Dishwasher: return "Cleaner Chef";
                case BotRole.All: return "Handyman Chef";
                default: return "Chef Outfit";
            }
        }

        // keyword guesses for cosmetic names, per job (the real names are listed in cosmetics.txt)
        static string[] HatWords(BotRole r)
        {
            switch (r)
            {
                case BotRole.Server: return new[] { "waiter", "bow", "top", "bowler", "fedora", "cap" };
                case BotRole.Dishwasher: return new[] { "bandana", "hair", "net", "beanie", "cap", "headband" };
                default: return new[] { "chef", "toque", "cook" };
            }
        }

        static string[] OutfitWords(BotRole r)
        {
            switch (r)
            {
                case BotRole.Server: return new[] { "waiter", "tux", "suit", "formal", "vest", "smart" };
                case BotRole.Dishwasher: return new[] { "overall", "apron", "work", "dungaree", "rubber", "plumb" };
                default: return new[] { "chef", "cook", "apron", "whites", "jacket" };
            }
        }

        PlayerCosmetic PickCosmetic(BotRole role, CosmeticType type, string wanted)
        {
            var pool = Data.Get<PlayerCosmetic>()
                .Where(c => c.CosmeticType == type && !c.DisableInGame && c.RequiresDLC == ContentPack.Null)
                .OrderBy(c => c.name).ToList();
            if (pool.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(wanted))
            {
                var exact = pool.FirstOrDefault(c => string.Equals(c.name, wanted.Trim(), StringComparison.OrdinalIgnoreCase))
                            ?? pool.FirstOrDefault(c => c.name.IndexOf(wanted.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (exact != null) return exact;
            }
            foreach (var w in (type == CosmeticType.Hat ? HatWords(role) : OutfitWords(role)))
            {
                var m = pool.FirstOrDefault(c => c.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                if (m != null) return m;
            }
            // nothing matched: a stable, different choice per job
            int idx = ((int)role * 5 + (type == CosmeticType.Hat ? 1 : 3)) % pool.Count;
            return pool[idx];
        }

        /// <summary>Set the bot's name, colour and cosmetics in the local player registry and broadcast it like a profile change.</summary>
        void ApplyAppearance(BotAgent a)
        {
            if (Players.Main == null || !Players.Main.Has(a.UserId)) return;
            var info = Players.Main.Get(a.UserId);
            var prof = info.Profile;
            prof.Name = a.DisplayName;
            prof.Colour = RoleColour(a.Role);

            string[] ov = null;
            cfg.Outfits.TryGetValue(a.Role, out ov);
            var hat = PickCosmetic(a.Role, CosmeticType.Hat, ov != null && ov.Length > 0 && !string.IsNullOrWhiteSpace(ov[0]) ? ov[0] : DefaultCosmetic(a.Role, CosmeticType.Hat));
            var outfit = PickCosmetic(a.Role, CosmeticType.Outfit, ov != null && ov.Length > 1 && !string.IsNullOrWhiteSpace(ov[1]) ? ov[1] : DefaultCosmetic(a.Role, CosmeticType.Outfit));
            var list = prof.Cosmetics;
            if (hat != null) list = CPlayerCosmetics.Set(list, CosmeticType.Hat, hat.ID);
            if (outfit != null) list = CPlayerCosmetics.Set(list, CosmeticType.Outfit, outfit.ID);
            prof.Cosmetics = list;
            info.Profile = prof;

            var f = typeof(Players).GetField("PlayerData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var dict = f != null ? f.GetValue(Players.Main) as Dictionary<int, PlayerInfo> : null;
            if (dict == null) { Log("cannot set the bot's name: player registry not found"); a.AppearanceApplied = true; return; }
            dict[a.UserId] = info;

            // same notification the game sends when a profile changes: the host then applies colour + cosmetics to the entity
            var ev = typeof(Players).GetField("OnBroadcastLocalPlayerChange", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var del = ev != null ? ev.GetValue(Players.Main) as Action<int> : null;
            del?.Invoke(a.UserId);
            a.AppearanceApplied = true;
            Log($"{a.DisplayName}: {RoleKey(a.Role)} look (hat {(hat != null ? hat.name : "-")}, outfit {(outfit != null ? outfit.name : "-")})");
        }

        static string RoleKey(BotRole r) => ChefConfig.RoleKey(r);

        void TryApplyAppearance(BotAgent a)
        {
            if (a.AppearanceApplied || Now - lastAppearanceTry < 0.5f) return;
            lastAppearanceTry = Now;
            ApplyAppearance(a);
        }

        /// <summary>Writes every hat/outfit so you can pick names for the 'outfit' lines in config.txt.</summary>
        public void WriteCosmetics()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Hats and outfits in this game (use these names in the 'outfit' lines of config.txt). Disabled ones are never used.");
            foreach (var c in Data.Get<PlayerCosmetic>().OrderBy(x => x.CosmeticType).ThenBy(x => x.name))
                sb.AppendLine($"{c.CosmeticType,-7} {c.name}{(c.RequiresDLC != ContentPack.Null ? "   [special edition]" : "")}{(c.DisableInGame ? "   [disabled]" : "")}");
            File.WriteAllText(Path.Combine(Dump.Dir, "cosmetics.txt"), sb.ToString());
        }
    }
}
