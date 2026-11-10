using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Kitchen;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChefBot
{
    /// <summary>Player-facing controls: config, hotkeys for marking counters, the on-screen overlay and the stock list.</summary>
    public partial class ChefBotSystem
    {
        ChefConfig cfg = new ChefConfig();
        public ChefConfig Cfg => cfg;
        public int MarkIndex => markIndex;
        float mapAt;
        bool cfgLoaded;
        int markIndex;
        public string Status = "";
        float hudAt, hudVisibleUntil;
        GameObject hudGo;

        void EnsureHud()
        {
            if (hudGo != null) return;
            hudGo = new GameObject("ChefBotHud");
            UnityEngine.Object.DontDestroyOnLoad(hudGo);
            hudGo.AddComponent<ChefBotHud>();
            hudGo.AddComponent<ChefBotMapUi>();
        }

        void LoadConfig()
        {
            BuildIndexes();
            cfg = ChefConfig.Load(allItems);
            BuildAgentsFromConfig();
            cfgLoaded = true;
            Log($"config loaded from {ChefConfig.FilePath}: {cfg.Make.Count} make lines, {cfg.AllOutputCells.Count()} output cells, {cfg.Stash.Count} stash cells");
            foreach (var p in cfg.Problems) Log("config problem: " + p);
        }

        static string RawName(int id) => GameData.Main != null && GameData.Main.TryGet<Item>(id, out var it) ? it.name : "#" + id;

        List<string> MarkTargets()
        {
            var l = new List<string> { "*" };
            foreach (var m in cfg.Make) if (!l.Contains(m.ItemName)) l.Add(m.ItemName);
            try
            {
                Snapshot();
                foreach (int mi in MenuItems()) foreach (var t in PrepTargets(mi)) { string n = RawName(t.item); if (!l.Contains(n)) l.Add(n); }
            }
            catch (Exception e) { Log("mark targets: " + e.Message); }
            return l;
        }

        string CurrentMarkTarget()
        {
            var t = MarkTargets();
            if (markIndex >= t.Count) markIndex = 0;
            return t[markIndex];
        }

        /// <summary>The appliance a human (not the bot) is currently facing / aiming at.</summary>
        Entity HumanFaced()
        {
            var em = EntityManager;
            using (var q = em.CreateEntityQuery(typeof(CPlayer), typeof(CPosition)))
            using (var ents = q.ToEntityArray(Allocator.TempJob))
            using (var ps = q.ToComponentDataArray<CPlayer>(Allocator.TempJob))
                for (int i = 0; i < ps.Length; i++)
                {
                    if (IsBotUser(ps[i].ID) || ps[i].InputSource != InputSourceIdentifier.Identifier) continue;
                    if (!em.HasComponent<CAttemptingInteraction>(ents[i])) continue;
                    var t = em.GetComponentData<CAttemptingInteraction>(ents[i]).Target;
                    if (t != Entity.Null && em.Exists(t) && em.HasComponent<CAppliance>(t)) return t;
                }
            return Entity.Null;
        }

        void MarkFaced(bool output)
        {
            Snapshot();
            var t = HumanFaced();
            if (t == Entity.Null) { Status = "Stand next to a counter and look at it, then press the key again."; Log(Status); return; }
            var a = AppOf(t);
            if (a == null || !IsStash(a)) { Status = "That is not a plain counter (" + (a != null ? a.Name : "?") + ")."; Log(Status); return; }
            var cell = Nav.Cell(a.Pos);
            string key = CurrentMarkTarget();
            if (output)
            {
                bool had = cfg.Outputs.TryGetValue(key, out var mine) && mine.Contains(cell);
                foreach (var s in cfg.Outputs.Values) s.Remove(cell);
                cfg.Stash.Remove(cell);
                if (!had)
                {
                    if (!cfg.Outputs.TryGetValue(key, out mine)) cfg.Outputs[key] = mine = new HashSet<Vector2Int>();
                    mine.Add(cell);
                }
                Status = (had ? "Unmarked " : "Marked ") + "counter " + cell.x + "," + cell.y + (had ? "" : " as OUTPUT for " + (key == "*" ? "any product" : key));
            }
            else
            {
                bool had = cfg.Stash.Contains(cell);
                foreach (var s in cfg.Outputs.Values) s.Remove(cell);
                if (had) cfg.Stash.Remove(cell); else cfg.Stash.Add(cell);
                Status = (had ? "Unmarked " : "Marked ") + "counter " + cell.x + "," + cell.y + (had ? "" : " as STASH (working counter)");
            }
            cfg.Save();
            Log(Status);
        }

        void HandleControlKeys(Keyboard kb)
        {
            bool used = false;
            if (kb.f1Key.wasPressedThisFrame) { MapData.Open = !MapData.Open; mapAt = 0; used = true; }
            if (kb.f2Key.wasPressedThisFrame) { LoadConfig(); Status = "Config reloaded" + (cfg.Problems.Count > 0 ? " - " + cfg.Problems.Count + " problem(s), see below" : ""); used = true; }
            if (kb.f3Key.wasPressedThisFrame) { var t = MarkTargets(); markIndex = (markIndex + 1) % t.Count; Status = "Marking counters for: " + (t[markIndex] == "*" ? "any product" : t[markIndex]); used = true; }
            if (kb.f4Key.wasPressedThisFrame) { cfg.Outputs.Clear(); cfg.Stash.Clear(); cfg.Save(); Status = "All counter marks cleared"; used = true; }
            if (kb.f5Key.wasPressedThisFrame) { MarkFaced(true); used = true; }
            if (kb.f6Key.wasPressedThisFrame) { MarkFaced(false); used = true; }
            if (kb.f7Key.wasPressedThisFrame) { UiAddBot(new[] { BotRole.Cook, BotRole.Server, BotRole.Dishwasher }[agents.Count % 3]); used = true; }
            if (used) hudVisibleUntil = Now + 25f;
        }

        void UpdateMap()
        {
            if (!MapData.Open || Now - mapAt < 0.5f) return;
            mapAt = Now;
            if (!HasSingleton<SLayout>()) return;
            try { RefreshMapData(); } catch (Exception e) { Log("map refresh: " + e.Message); }
        }

        void RefreshMapData()
        {
            nav.Rebuild(Tiles, p => TileManager.GetOccupant(p) != Entity.Null);
            Snapshot();
            var cells = new List<MapCell>();
            foreach (var a in Appls)
            {
                string kind = null;
                if (IsStash(a)) kind = "counter";
                else if (a.IsTable) kind = "table";
                else if (a.IsBin) kind = "bin";
                else if (a.IsProvider && !a.IsSink) kind = "source";
                else if (a.IsSink || (a.HasHolder && a.Def != null && a.Def.Processes != null && a.Def.Processes.Count > 0)) kind = "station";
                if (kind == null) continue;
                cells.Add(new MapCell { C = Nav.Cell(a.Pos), Name = a.Name, Kind = kind, Held = a.HeldId != 0 ? RawName(a.HeldId) : "" });
            }
            MapData.Cells = cells;
            MapData.Floor = nav.RoomCells();

            var botMarks = new List<KeyValuePair<Vector3, string>>();
            foreach (var ag in agents)
            {
                var pe = FindPlayer(ag.UserId);
                if (pe != Entity.Null && EntityManager.HasComponent<CPosition>(pe))
                    botMarks.Add(new KeyValuePair<Vector3, string>(EntityManager.GetComponentData<CPosition>(pe).Position, (ag.Index + 1) + (ag.Role == BotRole.Cook ? "C" : ag.Role == BotRole.Server ? "S" : ag.Role == BotRole.Dishwasher ? "W" : "A")));
            }
            MapData.Bots = botMarks;
            var humans = new List<Vector3>();
            using (var q = EntityManager.CreateEntityQuery(typeof(CPlayer), typeof(CPosition)))
            using (var ps = q.ToComponentDataArray<CPlayer>(Allocator.TempJob))
            using (var pos = q.ToComponentDataArray<CPosition>(Allocator.TempJob))
                for (int i = 0; i < ps.Length; i++) if (!IsBotUser(ps[i].ID)) humans.Add(pos[i].Position);
            MapData.Humans = humans;

            MapData.Products = MarkTargets();
            if (markIndex >= MapData.Products.Count) markIndex = 0;

            // things that can be added to the stock list
            var cand = new List<AddCand>();
            foreach (int mi in MenuItems())
                foreach (var t in PrepTargets(mi, all: true))
                {
                    var ex = t.extras ?? new List<int>();
                    bool dup = cfg.Make.Any(m => m.Item == t.item && m.Extras.Count == ex.Count && !m.Extras.Except(ex).Any())
                               || cand.Any(c => c.Item == t.item && c.Extras.Count == ex.Count && !c.Extras.Except(ex).Any());
                    if (dup) continue;
                    cand.Add(new AddCand { Item = t.item, Extras = ex, Label = RawName(t.item) + (ex.Count > 0 ? "  +  " + string.Join(", ", ex.Select(RawName)) : "") });
                }
            MapData.AddCandidates = cand;

            // toppings for the selected line
            var tops = new List<KeyValuePair<int, string>>();
            if (MapData.SelectedEntry >= 0 && MapData.SelectedEntry < cfg.Make.Count)
            {
                banToppings.Clear();
                var canon = Plan(cfg.Make[MapData.SelectedEntry].Item, new HashSet<int>(), 0, noLoose: true);
                if (canon != null)
                {
                    var seen = new HashSet<int>();
                    void Walk(Node k)
                    {
                        if (k.Kind == NK.Compose && Data.TryGet<ItemGroup>(k.Item, out var g))
                            foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                            {
                                if (set.OrderingOnly || set.IsMandatory || set.Min > 0) continue;
                                foreach (var m in set.Items) if (C(m.ID) < INF && seen.Add(m.ID)) tops.Add(new KeyValuePair<int, string>(m.ID, m.name));
                            }
                        if (k.Child != null) Walk(k.Child);
                        foreach (var p in k.Parts) Walk(p);
                    }
                    Walk(canon);
                }
            }
            MapData.ToppingChoices = tops;
        }

        // ---------- actions from the map window ----------
        public void UiClickCell(Vector2Int cell, bool left)
        {
            bool isOut = cfg.Outputs.Values.Any(s => s.Contains(cell));
            bool isStash = cfg.Stash.Contains(cell);
            foreach (var s in cfg.Outputs.Values) s.Remove(cell);
            cfg.Stash.Remove(cell);
            string key = markIndex < MapData.Products.Count ? MapData.Products[markIndex] : "*";
            if (left)
            {
                if (isOut) { cfg.Stash.Add(cell); Status = $"Counter {cell.x},{cell.y}: STASH"; }
                else if (!isStash)
                {
                    if (!cfg.Outputs.TryGetValue(key, out var set)) cfg.Outputs[key] = set = new HashSet<Vector2Int>();
                    set.Add(cell);
                    Status = $"Counter {cell.x},{cell.y}: OUTPUT for " + (key == "*" ? "any product" : key);
                }
                else Status = $"Counter {cell.x},{cell.y}: unmarked";
            }
            else Status = $"Counter {cell.x},{cell.y}: unmarked";
            cfg.Save();
        }

        public void UiSetProduct(int i) { markIndex = i; }
        public void UiClearMarks() { cfg.Outputs.Clear(); cfg.Stash.Clear(); cfg.Save(); Status = "All counter marks cleared"; }
        public void UiToggleFallback() { cfg.OutputFallback = !cfg.OutputFallback; cfg.Save(); }
        public void UiSelectEntry(int i) { MapData.SelectedEntry = i; mapAt = 0; }
        public void UiCount(int i, int d) { if (i < 0 || i >= cfg.Make.Count) return; cfg.Make[i].Count = Mathf.Clamp(cfg.Make[i].Count + d, 1, 20); cfg.Save(); }
        public void UiMove(int i, int d)
        {
            int j = i + d;
            if (i < 0 || j < 0 || i >= cfg.Make.Count || j >= cfg.Make.Count) return;
            var t = cfg.Make[i]; cfg.Make[i] = cfg.Make[j]; cfg.Make[j] = t;
            if (MapData.SelectedEntry == i) MapData.SelectedEntry = j; else if (MapData.SelectedEntry == j) MapData.SelectedEntry = i;
            cfg.Save();
        }
        public void UiRemove(int i)
        {
            if (i < 0 || i >= cfg.Make.Count) return;
            cfg.Make.RemoveAt(i);
            if (MapData.SelectedEntry >= cfg.Make.Count) MapData.SelectedEntry = -1;
            cfg.Save(); mapAt = 0;
        }
        public void UiAdd(int itemId, List<int> extras)
        {
            var me = new MakeEntry { Item = itemId, ItemName = RawName(itemId), Count = (extras != null && extras.Count > 0) ? 1 : 2, Raw = RawName(itemId) };
            if (extras != null) foreach (int x in extras) { me.Extras.Add(x); me.ExtraNames.Add(RawName(x)); }
            cfg.Make.Add(me);
            MapData.SelectedEntry = cfg.Make.Count - 1;
            cfg.Save(); mapAt = 0;
            Status = "Added " + RawName(itemId) + " - the bot now follows your list";
        }
        public void UiToggleTopping(int entry, int toppingId)
        {
            if (entry < 0 || entry >= cfg.Make.Count) return;
            var m = cfg.Make[entry];
            int idx = m.Extras.IndexOf(toppingId);
            if (idx >= 0) { m.Extras.RemoveAt(idx); m.ExtraNames.RemoveAt(idx); }
            else { m.Extras.Add(toppingId); m.ExtraNames.Add(RawName(toppingId)); }
            cfg.Save();
        }

        void UpdateHud()
        {
            UpdateMap();
            if (Now - hudAt < 0.2f) return;
            hudAt = Now;
            EnsureHud();
            ChefBotHud.Show = (enabled || Now < hudVisibleUntil) && !MapData.Open;
            if (!ChefBotHud.Show) return;
            var lines = new List<string>
            {
                "ChefBot " + (enabled ? "ON" : "OFF") + "  |  " + agents.Count + " bot(s)  |  marking for: " + (cfgLoaded ? (CurrentMarkTarget() == "*" ? "any product" : CurrentMarkTarget()) : "-"),
                "F1 MAP   F8 start/stop   F7 add bot   F5 mark OUTPUT counter   F6 mark STASH counter   F3 next product   F4 clear marks   F2 reload config",
            };
            foreach (var ag in agents) lines.Add(ag.Name + ": " + (string.IsNullOrEmpty(ag.Status) ? "-" : ag.Status));
            if (!string.IsNullOrEmpty(Status)) lines.Add(Status);
            foreach (var p in cfg.Problems.Take(3)) lines.Add("config: " + p);
            ChefBotHud.Lines = lines;

            var marks = new List<ChefBotHud.Marker>();
            foreach (var kv in cfg.Outputs)
                foreach (var c in kv.Value)
                    marks.Add(new ChefBotHud.Marker { World = new Vector3(c.x, 1.1f, c.y), Text = "OUT " + (kv.Key == "*" ? "any" : kv.Key.Length > 12 ? kv.Key.Substring(0, 12) : kv.Key), Colour = new Color(0.2f, 0.8f, 0.3f, 0.9f) });
            foreach (var c in cfg.Stash)
                marks.Add(new ChefBotHud.Marker { World = new Vector3(c.x, 1.1f, c.y), Text = "STASH", Colour = new Color(0.2f, 0.6f, 0.9f, 0.9f) });
            ChefBotHud.Markers = marks;
        }

        // ---------- where things go ----------

        /// <summary>A free counter marked as OUTPUT for this product (specific marks first, then 'any product' marks).</summary>
        Entity BestOutput(string itemName, IEnumerable<Vector3> pts, out bool anyConfigured)
        {
            var spec = cfg.OutputCellsFor(itemName);
            var any = cfg.AnyOutputCells();
            anyConfigured = spec.Count + any.Count > 0;
            var list = pts.ToList();
            if (list.Count == 0) list.Add(MyPos);
            foreach (var cells in new[] { spec, any })
            {
                if (cells.Count == 0) continue;
                Appl best = null; float bd = 1e9f;
                foreach (var a in Appls)
                {
                    if (!IsStash(a) || a.Held != Entity.Null || Reserved.Contains(a.E) || !cells.Contains(Nav.Cell(a.Pos)) || !Reachable(a)) continue;
                    float d = 0;
                    foreach (var p in list) d += Mathf.Abs(a.Pos.x - p.x) + Mathf.Abs(a.Pos.z - p.z);
                    if (d < bd) { bd = d; best = a; }
                }
                if (best != null) return best.E;
            }
            return Entity.Null;
        }

        // ---------- toppings / variants ----------
        readonly Dictionary<int, HashSet<int>> formsCache = new Dictionary<int, HashSet<int>>();

        /// <summary>All the forms an ingredient can take inside a dish (raw, chopped, cooked-wrapped ...).</summary>
        HashSet<int> ToppingForms(int extra)
        {
            if (formsCache.TryGetValue(extra, out var c)) return c;
            BuildIndexes();
            var set = new HashSet<int> { extra };
            for (int round = 0; round < 3; round++)
                foreach (var x in set.ToList())
                {
                    if (wrapResults.TryGetValue(x, out var wr)) foreach (var r in wr) set.Add(r);
                    foreach (var kv in madeBy) foreach (var d in kv.Value) if (!d.Bad && d.Src.ID == x) set.Add(kv.Key);
                }
            formsCache[extra] = set;
            return set;
        }

        bool ItemHasTopping(Entity held, int extra)
        {
            if (held == Entity.Null || !EntityManager.HasComponent<CItem>(held)) return false;
            var forms = ToppingForms(extra);
            var il = EntityManager.GetComponentData<CItem>(held).Items;
            for (int i = 0; i < il.Count; i++) if (forms.Contains(il[i])) return true;
            return false;
        }

        // ---------- the stock list ----------
        class PrepEntry { public int Item; public List<int> Extras = new List<int>(); public int Want; public string Why; public int MenuItem; public string Name; }

        List<PrepEntry> BuildPrepList()
        {
            var list = new List<PrepEntry>();
            if (cfg.Make.Count > 0)
            {
                foreach (var m in cfg.Make) list.Add(new PrepEntry { Item = m.Item, Extras = m.Extras, Want = m.Count, Why = "your list", Name = m.ItemName });
                return list;
            }
            foreach (int menuItem in MenuItems())
                foreach (var t in PrepTargets(menuItem))
                    list.Add(new PrepEntry { Item = t.item, Extras = t.extras ?? new List<int>(), Want = t.want, Why = t.why, MenuItem = menuItem, Name = RawName(t.item) });
            return list;
        }

        /// <summary>Quietly keep things stocked, in the order of your list. The humans plate and serve.</summary>
        IEnumerable<int> DoPrep()
        {
            R = false;
            Snapshot();
            foreach (var e in BuildPrepList())
            {
                int key = e.Item * 31 + e.Extras.Aggregate(7, (h, x) => h * 17 + x);
                if (failUntil.TryGetValue(key, out float until) && Now < until) continue;
                string claimKey = "prep:" + key;
                if (ClaimedByOther(claimKey)) continue;

                // which toppings must NOT be in stock items of this kind (plain pizza must not contain salami ...)
                banToppings.Clear();
                var canon = Plan(e.Item, new HashSet<int>(), 0, noLoose: true);
                var ban = canon != null ? ToppingsOf(canon) : new HashSet<int>();
                foreach (int x in e.Extras) ban.ExceptWith(ToppingForms(x));
                banToppings = ban;

                int stock = Appls.Count(a => a.HeldId == e.Item && LooseOk(a) && e.Extras.All(x => ItemHasTopping(a.Held, x)));
                if (stock >= e.Want) continue;

                var fresh = Clone(Plan(e.Item, new HashSet<int>(), 0, noLoose: true));
                if (fresh == null) { if (Now - lastPrepLog > 30f) { lastPrepLog = Now; Log("cannot make " + e.Name + " in this kitchen"); } continue; }
                bool extrasOk = true;
                foreach (int x in e.Extras)
                {
                    if (Closure(fresh).Contains(x)) continue;
                    if (!AddExtra(fresh, x)) { Log($"cannot add {RawName(x)} to {e.Name}"); extrasOk = false; }
                }
                if (!extrasOk) { failUntil[key] = Now + 60f; continue; }

                // is there somewhere to put the result?
                var pts = NodePositions(fresh); pts.Add(MyPos);
                var outSpot = BestOutput(e.Name, pts, out bool anyConfigured);
                if (outSpot == Entity.Null)
                {
                    if (anyConfigured && !cfg.OutputFallback)
                    {
                        Status = "Waiting: output counters for " + e.Name + " are full";
                        if (Now - lastPrepLog > 15f) { lastPrepLog = Now; Log(Status); }
                        yield break;
                    }
                    if (FreeStashCount() < 2)
                    {
                        Status = "Waiting: no free counter for " + e.Name;
                        if (Now - lastPrepLog > 15f) { lastPrepLog = Now; Log(Status + " (clear a counter or mark OUTPUT counters with F5)"); }
                        yield break;
                    }
                }

                Claim(claimKey);
                try
                {
                    Reserved.Clear();
                    if (outSpot != Entity.Null) Reserved.Add(outSpot);
                    Status = $"Making {e.Name}" + (e.Extras.Count > 0 ? " with " + string.Join(", ", e.Extras.Select(RawName)) : "") + $" ({stock}/{e.Want} ready)";
                    Log($"PREP {e.Name} (stock {stock}/{e.Want}, {e.Why})" + (e.Extras.Count > 0 ? " with " + string.Join(", ", e.Extras.Select(RawName)) : ""));
                    foreach (var _ in ExecHand(fresh, 1)) yield return 0;
                    if (!R) { failUntil[key] = Now + 20f; Reserved.Clear(); yield break; }
                    if (outSpot != Entity.Null) Reserved.Remove(outSpot);
                    Snapshot();
                    var spot = BestOutput(e.Name, NodePositions(fresh).Concat(new[] { MyPos }), out _);
                    if (spot == Entity.Null) spot = BestStash(NodePositions(fresh).Concat(new[] { MyPos }));
                    if (spot == Entity.Null) { foreach (var _ in DropHeld()) yield return 0; Reserved.Clear(); yield break; }
                    foreach (var _ in PutOn(spot)) yield return 0;
                    Reserved.Clear();
                    Log("stocked " + e.Name + " -> " + R);
                    R = true;
                    yield break;
                }
                finally { Release(claimKey); }
            }
            Status = "Idle - everything on your list is stocked";
            R = false;
        }
    }
}
