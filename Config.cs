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

    /// <summary>User settings: what to make (in order), and which counters are for what. Stored in ChefBot/config.txt.</summary>
    public class ChefConfig
    {
        public bool CookOnly = true;
        public List<BotRole> Bots = new List<BotRole> { BotRole.Cook };
        bool botsParsed;
        public bool OutputFallback = false;   // when every marked output counter is full: false = wait, true = use any free counter
        public List<MakeEntry> Make = new List<MakeEntry>();
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
                        if (!cfg.CookOnly && !cfg.botsParsed) cfg.Bots = new List<BotRole> { BotRole.All };   // old setting: one bot that does everything
                        continue;
                    }
                    if (line.StartsWith("bot ", StringComparison.OrdinalIgnoreCase))
                    {
                        string r = line.Substring(4).Trim().ToLowerInvariant();
                        BotRole role = r.StartsWith("serv") ? BotRole.Server : r.StartsWith("dish") || r.StartsWith("wash") ? BotRole.Dishwasher : r.StartsWith("all") ? BotRole.All : BotRole.Cook;
                        if (!cfg.botsParsed) { cfg.Bots.Clear(); cfg.botsParsed = true; }
                        cfg.Bots.Add(role);
                        continue;
                    }
                    if (line.StartsWith("output_fallback", StringComparison.OrdinalIgnoreCase)) { cfg.OutputFallback = line.EndsWith("true", StringComparison.OrdinalIgnoreCase); continue; }

                    if (line.StartsWith("make ", StringComparison.OrdinalIgnoreCase))
                    {
                        var e = new MakeEntry { Raw = line.Substring(5).Trim() };
                        string spec = e.Raw;
                        // trailing "x3"
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
                        if (e.Error != null) cfg.Problems.Add(e.Error); else cfg.Make.Add(e);
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
            sb.AppendLine("#");
            sb.AppendLine("# cook_only = true  : the bot only cooks and stocks things; YOU plate, serve and wash.");
            sb.AppendLine("cook_only = " + (CookOnly ? "true" : "false") + "   # (old setting, now replaced by the 'bot' lines below)");
            sb.AppendLine();
            sb.AppendLine("# THE BOTS. One line per bot (max 3 next to you). Roles:");
            sb.AppendLine("#   bot cook        cooks and keeps the stock list ready");
            sb.AppendLine("#   bot server      plates what the cooks made and serves the customers");
            sb.AppendLine("#   bot dishwasher  collects dirty plates, washes them, restocks clean plates");
            sb.AppendLine("#   bot all         does all three (use alone)");
            foreach (var b in Bots) sb.AppendLine("bot " + (b == BotRole.Server ? "server" : b == BotRole.Dishwasher ? "dishwasher" : b == BotRole.All ? "all" : "cook"));
            sb.AppendLine("# output_fallback = false : if all counters you marked as OUTPUT are full the bot waits;");
            sb.AppendLine("#                    true : it uses any free counter instead.");
            sb.AppendLine("output_fallback = " + (OutputFallback ? "true" : "false"));
            sb.AppendLine();
            sb.AppendLine("# WHAT TO KEEP STOCKED, in priority order (top = most important). Empty list = the bot decides by itself.");
            sb.AppendLine("# Format:   make <item name> [with <topping>, <topping>] [x<how many>]");
            sb.AppendLine("# Item names are the game's names - see ChefBot/cookbook.txt or gamedata.txt. Examples:");
            sb.AppendLine("#   make Pizza - Cooked x2                  (plain pizza, keep 2 ready)");
            sb.AppendLine("#   make Pizza - Cooked with Salami x1      (pizza with salami baked in, keep 1 ready)");
            sb.AppendLine("#   make Steak - Medium x2");
            foreach (var m in Make) sb.AppendLine("make " + m.Label.Replace(" x" + m.Count, "") + " x" + m.Count);
            sb.AppendLine();
            sb.AppendLine("# WHERE THINGS GO (set these in the game with the hotkeys; F3 picks the product, F5 = output, F6 = stash):");
            sb.AppendLine("#   output <item name or *> : x,z x,z     finished products go on these counters  (* = any product)");
            sb.AppendLine("#   stash : x,z x,z                         the bot works / assembles on these counters");
            foreach (var kv in Outputs.OrderBy(k => k.Key))
                if (kv.Value.Count > 0) sb.AppendLine("output " + kv.Key + " : " + string.Join(" ", kv.Value.Select(Cell)));
            if (Stash.Count > 0) sb.AppendLine("stash : " + string.Join(" ", Stash.Select(Cell)));
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
