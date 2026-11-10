using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KitchenData;
using UnityEngine;

namespace ChefBot
{
    /// <summary>One line of "what to keep stocked": an item, optional toppings, and how many.</summary>
    public class MakeEntry
    {
        public string Raw;
        public int Item;
        public string ItemName;
        public List<int> Extras = new List<int>();
        public List<string> ExtraNames = new List<string>();
        public int Count = 1;
        public string Error;
        public string Label => ItemName + (ExtraNames.Count > 0 ? " with " + string.Join(", ", ExtraNames) : "") + " x" + Count;
    }

    /// <summary>One bot: its job, its name and its own list of recipes (empty = use the shared list).</summary>
    public class BotSpec
    {
        public BotRole Role = BotRole.Cook;
        public string Name = "";
        public int Knife = 1;                       // sharp knife priority: 2 = high (first pick), 1 = normal, 0 = never
        public List<MakeEntry> Make = new List<MakeEntry>();
    }

    /// <summary>User settings: bots, what to make (in order), and which counters are for what. Stored in ChefBot/config.txt.</summary>
    public class ChefConfig
    {
        public bool CookOnly = true;               // old setting, only read for upgrading old files
        public int MaxPlayers = 8;                 // game default is 4
        public bool BotsCountAsPlayers = false;    // false: bots do not raise customers per day / difficulty
        public List<BotSpec> Bots = new List<BotSpec> { new BotSpec { Role = BotRole.Cook } };
        bool botsParsed;
        public bool MopFloors = true;              // an idle dishwasher mops the floors (needs a Mop Bucket)
        public bool EmptyBins = true;              // cooks / dishwashers empty full bins (the bag goes to the outside bin)
        public bool UseKnife = true;               // cooks pick up the Sharp Knife (chopping x3) if there is a provider
        public bool ServeSides = true;             // servers add the side a customer ordered to the plate
        public bool MakeSides = true;              // cooks keep side dishes (corn on the cob ...) in stock
        public bool UseTray = true;                // the dishwasher carries two plates per trip on a tray (if a Tray Stand exists)
        public bool OutputFallback = false;        // when every marked output counter is full: false = wait, true = use any free counter
        public string DishRackNames = "Rack, Soak"; // appliances the dishwasher puts dirty plates in first
        public Dictionary<BotRole, string[]> Outfits = new Dictionary<BotRole, string[]>();   // role -> { hat name, outfit name }
        public List<MakeEntry> Make = new List<MakeEntry>();                                  // shared list (bots without their own list use it)
        public Dictionary<string, HashSet<Vector2Int>> Outputs = new Dictionary<string, HashSet<Vector2Int>>(StringComparer.OrdinalIgnoreCase);
        public HashSet<Vector2Int> Stash = new HashSet<Vector2Int>();
        public List<string> Problems = new List<string>();

        public static string FilePath => Path.Combine(Dump.Dir, "config.txt");

        public IEnumerable<Vector2Int> AllOutputCells => Outputs.Values.SelectMany(v => v);

        public HashSet<Vector2Int> OutputCellsFor(string itemName)
        {
            var res = new HashSet<Vector2Int>();
            if (itemName != null && Outputs.TryGetValue(itemName, out var specific)) res.UnionWith(specific);
            return res;
        }

        public HashSet<Vector2Int> AnyOutputCells()
        {
            var res = new HashSet<Vector2Int>();
            if (Outputs.TryGetValue("*", out var any)) res.UnionWith(any);
            return res;
        }

        static string Cell(Vector2Int c) => c.x + "," + c.y;

        static bool ParseCell(string t, out Vector2Int c)
        {
            c = default;
            var p = t.Split(',');
            if (p.Length != 2) return false;
            if (!int.TryParse(p[0].Trim(), out int x) || !int.TryParse(p[1].Trim(), out int z)) return false;
            c = new Vector2Int(x, z);
            return true;
        }

        public static BotRole ParseRole(string r)
        {
            r = (r ?? "").Trim().ToLowerInvariant();
            if (r.StartsWith("serv")) return BotRole.Server;
            if (r.StartsWith("dish") || r.StartsWith("wash")) return BotRole.Dishwasher;
            if (r.StartsWith("all")) return BotRole.All;
            return BotRole.Cook;
        }

        public static string RoleKey(BotRole b) => b == BotRole.Server ? "server" : b == BotRole.Dishwasher ? "dishwasher" : b == BotRole.All ? "all" : "cook";

        /// <summary>Find an item by (case-insensitive) name. Exact match first, then a unique partial match.</summary>
        public static int ResolveItem(string name, IEnumerable<Item> items, out string canonical)
        {
            canonical = null;
            name = name.Trim();
            var all = items.ToList();
            var exact = all.Where(i => string.Equals(i.name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count >= 1) { canonical = exact[0].name; return exact[0].ID; }
            var part = all.Where(i => i.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (part.Count == 1) { canonical = part[0].name; return part[0].ID; }
            return 0;
        }

        static MakeEntry ParseMake(string raw, int lineNo, IEnumerable<Item> items)
        {
            var e = new MakeEntry { Raw = raw.Trim() };
            string spec = e.Raw;
            var words = spec.Split(' ');
            if (words.Length > 1 && words[words.Length - 1].Length > 1 && (words[words.Length - 1][0] == 'x' || words[words.Length - 1][0] == 'X') && int.TryParse(words[words.Length - 1].Substring(1), out int cnt))
            { e.Count = Mathf.Max(1, cnt); spec = string.Join(" ", words.Take(words.Length - 1)); }
            string itemPart = spec, extraPart = "";
            int w = spec.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
            if (w >= 0) { itemPart = spec.Substring(0, w); extraPart = spec.Substring(w + 6); }
            e.Item = ResolveItem(itemPart, items, out e.ItemName);
            if (e.Item == 0) e.Error = $"line {lineNo}: unknown item '{itemPart.Trim()}'";
            foreach (var ex in extraPart.Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int id = ResolveItem(ex, items, out string cn);
                if (id == 0) { e.Error = $"line {lineNo}: unknown topping '{ex.Trim()}'"; continue; }
                e.Extras.Add(id); e.ExtraNames.Add(cn);
            }
            return e;
        }

        public static ChefConfig Load(IEnumerable<Item> items)
        {
            var cfg = new ChefConfig();
            try
            {
                if (!File.Exists(FilePath))
                {
                    cfg.Save();
                    return cfg;
                }
                var itemList = items.ToList();
                BotSpec currentBot = null;    // 'make' lines after a 'bot' line belong to that bot; before the first bot they are shared
                int lineNo = 0;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    lineNo++;
                    string line = raw.Trim();
                    int hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash).Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("cook_only", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.CookOnly = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase);
                        if (!cfg.CookOnly && !cfg.botsParsed) cfg.Bots = new List<BotSpec> { new BotSpec { Role = BotRole.All } };   // old setting: one bot that does everything
                        continue;
                    }
                    if (line.StartsWith("max_players", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(line.IndexOf('=') + 1).Trim();
                        if (int.TryParse(v, out int mp)) cfg.MaxPlayers = Mathf.Clamp(mp, 4, 16);
                        continue;
                    }
                    if (line.StartsWith("bots_count_as_players", StringComparison.OrdinalIgnoreCase)) { cfg.BotsCountAsPlayers = line.EndsWith("true", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("mop_floors", StringComparison.OrdinalIgnoreCase)) { cfg.MopFloors = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("empty_bins", StringComparison.OrdinalIgnoreCase)) { cfg.EmptyBins = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("use_knife", StringComparison.OrdinalIgnoreCase)) { cfg.UseKnife = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("serve_sides", StringComparison.OrdinalIgnoreCase)) { cfg.ServeSides = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("make_sides", StringComparison.OrdinalIgnoreCase)) { cfg.MakeSides = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("use_tray", StringComparison.OrdinalIgnoreCase)) { cfg.UseTray = !line.EndsWith("false", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("output_fallback", StringComparison.OrdinalIgnoreCase)) { cfg.OutputFallback = line.EndsWith("true", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("dish_rack_names", StringComparison.OrdinalIgnoreCase)) { cfg.DishRackNames = line.Substring(line.IndexOf('=') + 1).Trim(); continue; }

                    if (line.StartsWith("outfit ", StringComparison.OrdinalIgnoreCase))
                    {
                        // outfit <role> : <hat name> , <outfit name>
                        string rest = line.Substring(7);
                        int colon = rest.IndexOf(':');
                        if (colon < 0) { cfg.Problems.Add($"line {lineNo}: outfit needs ':'"); continue; }
                        var parts = rest.Substring(colon + 1).Split(',');
                        cfg.Outfits[ParseRole(rest.Substring(0, colon))] = new[] { parts.Length > 0 ? parts[0].Trim() : "", parts.Length > 1 ? parts[1].Trim() : "" };
                        continue;
                    }

                    if (line.StartsWith("bot ", StringComparison.OrdinalIgnoreCase) || line.Equals("bot", StringComparison.OrdinalIgnoreCase))
                    {
                        // bot <role> ["name"]
                        string rest = line.Length > 4 ? line.Substring(4).Trim() : "";
                        int knifeLevel = 1;
                        var km = System.Text.RegularExpressions.Regex.Match(rest, @"\bknife\s*=\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (km.Success)
                        {
                            string kv = km.Groups[1].Value.ToLowerInvariant();
                            knifeLevel = kv.StartsWith("hi") || kv == "2" ? 2 : kv.StartsWith("off") || kv.StartsWith("no") || kv == "0" ? 0 : 1;
                            rest = rest.Remove(km.Index, km.Length).Trim();
                        }
                        string roleWord = rest, name = "";
                        int sp = rest.IndexOf(' ');
                        if (sp > 0) { roleWord = rest.Substring(0, sp); name = rest.Substring(sp + 1).Trim().Trim('"'); }
                        if (!cfg.botsParsed) { cfg.Bots.Clear(); cfg.botsParsed = true; }
                        currentBot = new BotSpec { Role = ParseRole(roleWord), Name = name, Knife = knifeLevel };
                        cfg.Bots.Add(currentBot);
                        continue;
                    }

                    if (line.StartsWith("make ", StringComparison.OrdinalIgnoreCase))
                    {
                        var e = ParseMake(line.Substring(5), lineNo, itemList);
                        if (e.Error != null) cfg.Problems.Add(e.Error);
                        else (currentBot != null ? currentBot.Make : cfg.Make).Add(e);
                        continue;
                    }

                    if (line.StartsWith("output ", StringComparison.OrdinalIgnoreCase))
                    {
                        // output <item name or *> : x,z x,z ...
                        string rest = line.Substring(7);
                        int colon = rest.IndexOf(':');
                        if (colon < 0) { cfg.Problems.Add($"line {lineNo}: output needs ':'"); continue; }
                        string key = rest.Substring(0, colon).Trim();
                        if (!cfg.Outputs.TryGetValue(key, out var set)) cfg.Outputs[key] = set = new HashSet<Vector2Int>();
                        foreach (var t in rest.Substring(colon + 1).Split(' ', '\t'))
                            if (t.Length > 0 && ParseCell(t, out var c)) set.Add(c);
                        continue;
                    }

                    if (line.StartsWith("stash", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = line.Substring(5).TrimStart(':', ' ');
                        foreach (var t in rest.Split(' ', '\t'))
                            if (t.Length > 0 && ParseCell(t, out var c)) cfg.Stash.Add(c);
                        continue;
                    }
                    cfg.Problems.Add($"line {lineNo}: not understood: {raw.Trim()}");
                }
            }
            catch (Exception ex) { cfg.Problems.Add("config error: " + ex.Message); }
            return cfg;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ChefBot settings. Edit this file, then press F2 in the game to reload it.");
            sb.AppendLine();
            sb.AppendLine("# players: how many players (you + friends + bots) a restaurant holds. The game's own limit is 4; 4-16 allowed.");
            sb.AppendLine("max_players = " + MaxPlayers);
            sb.AppendLine("# bots_count_as_players = false : bots do NOT raise the customers per day (or the other player-count scaling).");
            sb.AppendLine("bots_count_as_players = " + (BotsCountAsPlayers ? "true" : "false"));
            sb.AppendLine("# output_fallback = false : if all counters you marked as OUTPUT are full the bots wait; true = use any free counter.");
            sb.AppendLine("output_fallback = " + (OutputFallback ? "true" : "false"));
            sb.AppendLine("# use_tray = true : the dishwasher picks up a tray from a Tray Stand (if you have one) and carries 2 dirty plates per trip.");
            sb.AppendLine("use_tray = " + (UseTray ? "true" : "false"));
            sb.AppendLine("# mop_floors = true : a dishwasher with nothing to wash mops the floors (needs a Mop Bucket).");
            sb.AppendLine("mop_floors = " + (MopFloors ? "true" : "false"));
            sb.AppendLine("# empty_bins = true : a full bin cannot take rubbish (corn husks ...), so cooks/dishwashers empty it into the outside bin.");
            sb.AppendLine("empty_bins = " + (EmptyBins ? "true" : "false"));
            sb.AppendLine("# use_knife = true : cooks pick up the Sharp Knife from a Sharp Knife Provider (chops 3x faster).");
            sb.AppendLine("use_knife = " + (UseKnife ? "true" : "false"));
            sb.AppendLine("# serve_sides = true : servers add a customer's side order (corn on the cob ...) to the plate.");
            sb.AppendLine("serve_sides = " + (ServeSides ? "true" : "false"));
            sb.AppendLine("# make_sides = true : cooks keep the side dishes on the menu in stock.");
            sb.AppendLine("make_sides = " + (MakeSides ? "true" : "false"));
            sb.AppendLine("# dish_rack_names : the dishwasher puts dirty plates in appliances whose name contains one of these words first.");
            sb.AppendLine("dish_rack_names = " + DishRackNames);
            sb.AppendLine();
            sb.AppendLine("# SHARED STOCK LIST, in priority order (top = most important). Bots without their own list use this one.");
            sb.AppendLine("# Empty everywhere = the bots decide by themselves.");
            sb.AppendLine("# Format:   make <item name> [with <topping>, <topping>] [x<how many>]");
            sb.AppendLine("# Item names are the game's names - see ChefBot/cookbook.txt or gamedata.txt. Examples:");
            sb.AppendLine("#   make Pizza - Cooked x2                  (plain pizza, keep 2 ready)");
            sb.AppendLine("#   make Pizza - Cooked with Mushroom - Chopped x1");
            sb.AppendLine("#   make Steak - Medium x2");
            foreach (var m in Make) sb.AppendLine("make " + m.ItemName + (m.ExtraNames.Count > 0 ? " with " + string.Join(", ", m.ExtraNames) : "") + " x" + m.Count);
            sb.AppendLine();
            sb.AppendLine("# OUTFITS by job (optional). Default: picked automatically. Cosmetic names are listed in ChefBot/cosmetics.txt.");
            sb.AppendLine("#   outfit cook : <hat name> , <outfit name>");
            foreach (var kv in Outfits) sb.AppendLine("outfit " + RoleKey(kv.Key) + " : " + (kv.Value.Length > 0 ? kv.Value[0] : "") + " , " + (kv.Value.Length > 1 ? kv.Value[1] : ""));
            sb.AppendLine();
            sb.AppendLine("# WHERE THINGS GO (set these with the hotkeys / the F1 map):");
            sb.AppendLine("#   output <item name or *> : x,z x,z     finished products go on these counters  (* = any product)");
            sb.AppendLine("#   stash : x,z x,z                         the bots work / assemble on these counters");
            foreach (var kv in Outputs.OrderBy(k => k.Key))
                if (kv.Value.Count > 0) sb.AppendLine("output " + kv.Key + " : " + string.Join(" ", kv.Value.Select(Cell)));
            if (Stash.Count > 0) sb.AppendLine("stash : " + string.Join(" ", Stash.Select(Cell)));
            sb.AppendLine();
            sb.AppendLine("# THE BOTS (max = max_players - 1). One 'bot' line each, with an optional name. 'make' lines right below a bot");
            sb.AppendLine("# are THAT bot's own recipes. Roles: cook / server / dishwasher / all.");
            sb.AppendLine("# Knife priority per bot (there is usually only ONE sharp knife): bot cook \"Name\" knife=high | normal | off");
            sb.AppendLine("#   bot cook \"Mushroom Chef\"");
            sb.AppendLine("#     make Pizza - Cooked with Mushroom - Chopped x1");
            foreach (var b in Bots)
            {
                sb.AppendLine("bot " + RoleKey(b.Role) + (string.IsNullOrWhiteSpace(b.Name) ? "" : " \"" + b.Name.Trim() + "\"") + (b.Knife == 2 ? " knife=high" : b.Knife == 0 ? " knife=off" : ""));
                foreach (var m in b.Make) sb.AppendLine("  make " + m.ItemName + (m.ExtraNames.Count > 0 ? " with " + string.Join(", ", m.ExtraNames) : "") + " x" + m.Count);
            }
            File.WriteAllText(FilePath, sb.ToString());
        }
    }

    /// <summary>On-screen overlay: status text and markers over the counters you marked.</summary>
    public class ChefBotHud : MonoBehaviour
    {
        public static bool Show;
        public static List<string> Lines = new List<string>();
        public struct Marker { public Vector3 World; public string Text; public Color Colour; }
        public static List<Marker> Markers = new List<Marker>();
        GUIStyle box, label;

        void OnGUI()
        {
            if (!Show) return;
            if (box == null)
            {
                box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                label = new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };
            }
            float y = 8;
            foreach (var l in Lines)
            {
                var r = new Rect(10, y, 760, 20);
                GUI.color = new Color(0, 0, 0, 0.55f);
                GUI.DrawTexture(new Rect(r.x - 4, r.y, r.width, r.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(r, l, label);
                y += 20;
            }
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var m in Markers)
            {
                var sp = cam.WorldToScreenPoint(m.World);
                if (sp.z < 0) continue;
                var rect = new Rect(sp.x - 45, Screen.height - sp.y - 12, 90, 22);
                GUI.color = m.Colour;
                GUI.Box(rect, m.Text, box);
                GUI.color = Color.white;
            }
        }
    }
}
