using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChefBot
{
    public class AddCand { public int Item; public List<int> Extras = new List<int>(); public string Label; }
    public struct MapCell { public Vector2Int C; public string Name; public string Kind; public string Held; }

    /// <summary>Data the map window shows. Filled by the system on the main thread, read by the window.</summary>
    public static class MapData
    {
        public static bool Open;
        public static List<MapCell> Cells = new List<MapCell>();
        public static List<Vector2Int> Floor = new List<Vector2Int>();
        public static List<KeyValuePair<Vector3, string>> Bots = new List<KeyValuePair<Vector3, string>>();
        public static List<Vector3> Humans = new List<Vector3>();
        public static List<string> Products = new List<string> { "*" };
        public static List<AddCand> AddCandidates = new List<AddCand>();
        public static List<KeyValuePair<int, string>> ToppingChoices = new List<KeyValuePair<int, string>>();
        public static int SelectedEntry = -1;
    }

    /// <summary>Top-down kitchen map: click counters to mark them, pick what to stock, choose toppings. F1 opens/closes.</summary>
    public class ChefBotMapUi : MonoBehaviour
    {
        Vector2 prodScroll, listScroll, addScroll, panelScroll;
        bool wasOpen;
        bool cursorWasVisible; CursorLockMode lockWas;
        GUIStyle small, bold, tipStyle, btn;

        static Texture2D Tex => Texture2D.whiteTexture;

        void Fill(Rect r, Color c) { var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, Tex); GUI.color = old; }
        void Frame(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c); Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c); Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        void Update()
        {
            if (MapData.Open)
            {
                if (!wasOpen) { cursorWasVisible = Cursor.visible; lockWas = Cursor.lockState; }
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else if (wasOpen)
            {
                Cursor.visible = cursorWasVisible;
                Cursor.lockState = lockWas;
            }
            wasOpen = MapData.Open;
        }

        static Color KindColour(string kind)
        {
            switch (kind)
            {
                case "counter": return new Color(0.62f, 0.45f, 0.28f);
                case "source": return new Color(0.85f, 0.75f, 0.25f);
                case "station": return new Color(0.80f, 0.32f, 0.28f);
                case "table": return new Color(0.45f, 0.45f, 0.5f);
                case "bin": return new Color(0.25f, 0.25f, 0.25f);
                default: return Color.gray;
            }
        }

        void OnGUI()
        {
            if (!MapData.Open) return;
            var sys = ChefBotSystem.Instance;
            if (sys == null) return;
            if (small == null)
            {
                small = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.white } };
                bold = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
                tipStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white } };
                btn = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            }

            var win = new Rect(Screen.width * 0.04f, Screen.height * 0.06f, Screen.width * 0.92f, Screen.height * 0.88f);
            Fill(win, new Color(0.08f, 0.08f, 0.1f, 0.96f));
            GUI.Label(new Rect(win.x + 12, win.y + 6, 600, 26), "ChefBot map   -   click a counter to mark it   (F1 closes)", bold);

            var panel = new Rect(win.xMax - 360, win.y + 40, 350, win.height - 50);
            var mapR = new Rect(win.x + 10, win.y + 40, win.width - 390, win.height - 90);
            DrawMap(sys, mapR);
            DrawPanel(sys, panel);

            // legend
            float lx = win.x + 12, ly = win.yMax - 38;
            foreach (var kv in new[] { ("counter", "counter"), ("source", "ingredient"), ("station", "oven/hob/sink"), ("table", "table"), ("bin", "bin") })
            {
                Fill(new Rect(lx, ly + 4, 14, 14), KindColour(kv.Item1));
                GUI.Label(new Rect(lx + 18, ly, 110, 22), kv.Item2, small);
                lx += 120;
            }
            Frame(new Rect(lx, ly + 4, 14, 14), new Color(0.2f, 1f, 0.3f), 3); GUI.Label(new Rect(lx + 18, ly, 100, 22), "OUTPUT", small); lx += 100;
            Frame(new Rect(lx, ly + 4, 14, 14), new Color(0.3f, 0.7f, 1f), 3); GUI.Label(new Rect(lx + 18, ly, 100, 22), "STASH", small);
        }

        void DrawMap(ChefBotSystem sys, Rect mapR)
        {
            Fill(mapR, new Color(0.04f, 0.04f, 0.05f));
            var all = new List<Vector2Int>(MapData.Floor);
            all.AddRange(MapData.Cells.Select(c => c.C));
            if (all.Count == 0) { GUI.Label(mapR, "No kitchen loaded yet - open a restaurant.", small); return; }
            int minX = all.Min(c => c.x), maxX = all.Max(c => c.x), minZ = all.Min(c => c.y), maxZ = all.Max(c => c.y);
            int w = maxX - minX + 1, h = maxZ - minZ + 1;
            float cs = Mathf.Clamp(Mathf.Min(mapR.width / w, mapR.height / h), 12f, 56f);
            float ox = mapR.x + (mapR.width - cs * w) / 2f, oy = mapR.y + (mapR.height - cs * h) / 2f;

            Rect CellRect(Vector2Int c) => new Rect(ox + (c.x - minX) * cs, oy + (maxZ - c.y) * cs, cs - 1, cs - 1);

            foreach (var c in MapData.Floor) Fill(CellRect(c), new Color(0.16f, 0.16f, 0.2f));

            var cfg = sys.Cfg;
            var outputs = new Dictionary<Vector2Int, string>();
            foreach (var kv in cfg.Outputs) foreach (var c in kv.Value) outputs[c] = kv.Key;

            Vector2 mp = Event.current.mousePosition;
            string tip = null;
            foreach (var cell in MapData.Cells)
            {
                var r = CellRect(cell.C);
                Fill(r, KindColour(cell.Kind));
                if (cs >= 22) GUI.Label(r, Short(cell.Name), small);
                if (outputs.TryGetValue(cell.C, out string outFor))
                {
                    Frame(r, new Color(0.2f, 1f, 0.3f), 3);
                    if (cs >= 22) GUI.Label(new Rect(r.x, r.y - 1, r.width, 12), outFor == "*" ? "OUT" : "OUT*", small);
                }
                else if (cfg.Stash.Contains(cell.C)) Frame(r, new Color(0.3f, 0.7f, 1f), 3);
                if (!string.IsNullOrEmpty(cell.Held)) Fill(new Rect(r.xMax - 9, r.y + 1, 8, 8), new Color(1f, 1f, 1f, 0.9f));
                if (r.Contains(mp))
                {
                    tip = cell.Name + (cell.Kind == "counter" ? "  (click to mark)" : "") + (string.IsNullOrEmpty(cell.Held) ? "" : "\nholding: " + cell.Held)
                          + (outputs.TryGetValue(cell.C, out string o2) ? "\nOUTPUT for " + (o2 == "*" ? "any product" : o2) : cfg.Stash.Contains(cell.C) ? "\nSTASH" : "");
                }
            }

            foreach (var bm in MapData.Bots)
            {
                var br = CellRect(new Vector2Int(Mathf.RoundToInt(bm.Key.x), Mathf.RoundToInt(bm.Key.z)));
                Fill(new Rect(br.x + br.width * 0.15f, br.y + br.height * 0.15f, br.width * 0.7f, br.height * 0.7f), new Color(0.1f, 0.9f, 0.9f));
                GUI.Label(br, bm.Value, small);
            }
            foreach (var hp in MapData.Humans)
            {
                var hr = CellRect(new Vector2Int(Mathf.RoundToInt(hp.x), Mathf.RoundToInt(hp.z)));
                Fill(new Rect(hr.x + hr.width * 0.3f, hr.y + hr.height * 0.3f, hr.width * 0.4f, hr.height * 0.4f), Color.white);
            }

            // clicks
            var e = Event.current;
            if (e.type == EventType.MouseDown && mapR.Contains(e.mousePosition))
            {
                foreach (var cell in MapData.Cells)
                    if (CellRect(cell.C).Contains(e.mousePosition))
                    {
                        if (cell.Kind == "counter") sys.UiClickCell(cell.C, e.button == 0);
                        e.Use();
                        break;
                    }
            }

            if (tip != null)
            {
                var size = tipStyle.CalcSize(new GUIContent(tip));
                var tr = new Rect(Mathf.Min(mp.x + 14, Screen.width - size.x - 14), mp.y + 14, size.x + 12, size.y + 6);
                GUI.Box(tr, tip, tipStyle);
            }
        }

        static string Short(string n)
        {
            n = n.Replace("Source - ", "").Replace(" - Starting", "").Replace("Countertop", "Ctr");
            return n.Length > 9 ? n.Substring(0, 9) : n;
        }

        void DrawPanel(ChefBotSystem sys, Rect panel)
        {
            var cfg = sys.Cfg;
            GUILayout.BeginArea(panel);
            panelScroll = GUILayout.BeginScrollView(panelScroll);

            GUILayout.Label("Chefs", bold);
            if (GUILayout.Button(sys.Enabled ? "STOP all bots" : "START bots (F8)")) sys.UiToggleEnabled();
            for (int i = 0; i < sys.Agents.Count; i++)
            {
                var ag = sys.Agents[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1) + ".", small, GUILayout.Width(16));
                foreach (var r in new[] { BotRole.Cook, BotRole.Server, BotRole.Dishwasher, BotRole.All })
                {
                    bool on = ag.Role == r;
                    string lbl = r == BotRole.Cook ? "Cook" : r == BotRole.Server ? "Serve" : r == BotRole.Dishwasher ? "Wash" : "All";
                    if (GUILayout.Toggle(on, lbl, btn, GUILayout.Width(58)) && !on) sys.UiSetRole(i, r);
                }
                if (GUILayout.Button("x", GUILayout.Width(22))) { sys.UiRemoveBot(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(ag.Status)) GUILayout.Label("     " + ag.Status, small);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Add:", small, GUILayout.Width(30));
            if (GUILayout.Button("+ Cook")) sys.UiAddBot(BotRole.Cook);
            if (GUILayout.Button("+ Server")) sys.UiAddBot(BotRole.Server);
            if (GUILayout.Button("+ Wash")) sys.UiAddBot(BotRole.Dishwasher);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);
            GUILayout.Label("Left-click a counter = OUTPUT for the product below -> STASH -> off.  Right-click = off.", small);
            GUILayout.Label("Marking for:", bold);
            prodScroll = GUILayout.BeginScrollView(prodScroll, GUILayout.Height(96));
            for (int i = 0; i < MapData.Products.Count; i++)
            {
                bool sel = sys.MarkIndex == i;
                if (GUILayout.Toggle(sel, " " + (MapData.Products[i] == "*" ? "Any product" : MapData.Products[i]), btn) && !sel) sys.UiSetProduct(i);
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Clear all counter marks")) sys.UiClearMarks();

            GUILayout.Space(8);
            GUILayout.Label("What to keep stocked (top = first)", bold);
            if (cfg.Make.Count == 0) GUILayout.Label("Empty = the bot decides by itself. Add something to take control.", small);
            listScroll = GUILayout.BeginScrollView(listScroll, GUILayout.Height(140));
            for (int i = 0; i < cfg.Make.Count; i++)
            {
                var m = cfg.Make[i];
                GUILayout.BeginHorizontal();
                bool selected = MapData.SelectedEntry == i;
                if (GUILayout.Toggle(selected, " " + (i + 1) + ". " + m.ItemName + (m.ExtraNames.Count > 0 ? " +" + string.Join("+", m.ExtraNames) : ""), btn, GUILayout.Width(170)) && !selected) sys.UiSelectEntry(i);
                if (GUILayout.Button("-", GUILayout.Width(22))) sys.UiCount(i, -1);
                GUILayout.Label("x" + m.Count, small, GUILayout.Width(26));
                if (GUILayout.Button("+", GUILayout.Width(22))) sys.UiCount(i, +1);
                if (GUILayout.Button("^", GUILayout.Width(22))) sys.UiMove(i, -1);
                if (GUILayout.Button("v", GUILayout.Width(22))) sys.UiMove(i, +1);
                if (GUILayout.Button("x", GUILayout.Width(22))) { sys.UiRemove(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (MapData.SelectedEntry >= 0 && MapData.SelectedEntry < cfg.Make.Count)
            {
                var m = cfg.Make[MapData.SelectedEntry];
                GUILayout.Label("Toppings for the selected line:", bold);
                if (MapData.ToppingChoices.Count == 0) GUILayout.Label("(none available for this item in your kitchen yet)", small);
                foreach (var t in MapData.ToppingChoices)
                {
                    bool on = m.Extras.Contains(t.Key);
                    if (GUILayout.Toggle(on, " " + t.Value) != on) sys.UiToggleTopping(MapData.SelectedEntry, t.Key);
                }
            }

            GUILayout.Space(6);
            GUILayout.Label("Add to the list:", bold);
            addScroll = GUILayout.BeginScrollView(addScroll, GUILayout.Height(150));
            foreach (var c in MapData.AddCandidates)
                if (GUILayout.Button(c.Label, btn)) sys.UiAdd(c.Item, c.Extras);
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(sys.Status)) GUILayout.Label(sys.Status, small);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
