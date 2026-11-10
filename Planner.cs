using System;
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    public enum NK { Fetch, Pickup, Process, Compose, Split }

    /// <summary>One node of a recipe tree: "how do I get item X".</summary>
    public class Node
    {
        public NK Kind;
        public int Item;
        public Entity Provider;        // Fetch
        public Entity Holder;          // Pickup
        public Node Child;             // Process: source item / Split: parent item
        public int Process;            // Process
        public List<Node> Parts = new List<Node>();   // Compose (Parts[0] is the base/container)
        public int Cost;

        public string Describe(int indent = 0)
        {
            string pad = new string(' ', indent * 2);
            string s = pad + Kind + " " + Dump.Name(Item) + (Kind == NK.Process ? " via " + Dump.Name(Process) : "") + "\n";
            if (Child != null) s += Child.Describe(indent + 1);
            foreach (var p in Parts) s += p.Describe(indent + 1);
            return s;
        }
    }

    /// <summary>One appliance in the kitchen, with everything the planner needs.</summary>
    public class Appl
    {
        public Entity E;
        public int Id;
        public Vector3 Pos;
        public string Name;
        public Appliance Def;
        public bool HasHolder, IsProvider, IsTable, IsBin, IsSink, HasStorage, IsConveyor, NeedsActivation;
        public Entity Held;
        public int HeldId;
        public CItemProvider Prov;
    }

    public partial class ChefBotSystem
    {
        // ---------- static recipe indexes (built once from GameData) ----------
        struct Deriv { public Item Src; public int Process; public bool Bad; public bool Wrap; }
        Dictionary<int, List<Deriv>> madeBy;
        Dictionary<int, List<Item>> splitOf;
        HashSet<int> dirtyIds;
        Dictionary<int, List<int>> wrapResults;
        List<Item> allItems;

        void BuildIndexes()
        {
            if (madeBy != null) return;
            madeBy = new Dictionary<int, List<Deriv>>();
            splitOf = new Dictionary<int, List<Item>>();
            dirtyIds = new HashSet<int>();
            wrapResults = new Dictionary<int, List<int>>();
            allItems = Data.Get<Item>().ToList();
            foreach (var it in allItems)
            {
                if ((it.ItemStorageFlags & ItemStorage.DirtyDish) != 0) dirtyIds.Add(it.ID);
                if (it.DerivedProcesses != null)
                    foreach (var p in it.DerivedProcesses)
                    {
                        if (p.Result == null || p.Process == null) continue;
                        if (!madeBy.TryGetValue(p.Result.ID, out var l)) madeBy[p.Result.ID] = l = new List<Deriv>();
                        l.Add(new Deriv { Src = it, Process = p.Process.ID, Bad = p.IsBad, Wrap = p.RequiresWrapper });
                        if (p.RequiresWrapper && !p.IsBad)
                        {
                            if (!wrapResults.TryGetValue(it.ID, out var wl)) wrapResults[it.ID] = wl = new List<int>();
                            wl.Add(p.Result.ID);
                        }
                    }
                if (it.SplitSubItem != null)
                {
                    if (!splitOf.TryGetValue(it.SplitSubItem.ID, out var l)) splitOf[it.SplitSubItem.ID] = l = new List<Item>();
                    l.Add(it);
                }
            }
            Log($"indexes built: {allItems.Count} items, {madeBy.Count} derivable, {dirtyIds.Count} dirty types");
        }

        // ---------- world snapshot ----------
        public List<Appl> Appls = new List<Appl>();

        public void Snapshot()
        {
            var em = EntityManager;
            solved = false;
            Appls.Clear();
            foreach (var e in AllAppliances())
            {
                var a = new Appl { E = e, Pos = PosOf(e) };
                a.Id = em.GetComponentData<CAppliance>(e).ID;
                Data.TryGet<Appliance>(a.Id, out a.Def);
                a.Name = a.Def != null ? a.Def.name : "#" + a.Id;
                a.HasHolder = em.HasComponent<CItemHolder>(e);
                a.IsProvider = em.HasComponent<CItemProvider>(e);
                if (a.IsProvider) a.Prov = em.GetComponentData<CItemProvider>(e);
                a.IsTable = em.HasComponent<CApplianceTable>(e);
                a.IsBin = em.HasComponent<CApplianceBin>(e);
                a.IsSink = em.HasComponent<CApplianceSinkBin>(e);
                a.HasStorage = em.HasComponent<CItemStorage>(e);
                a.IsConveyor = em.HasComponent<CConveyPushItems>(e) || em.HasComponent<CAutomatedInteractor>(e);
                a.NeedsActivation = em.HasComponent<CRequiresActivation>(e);
                a.Held = HeldBy(e);
                a.HeldId = a.Held != Entity.Null && em.HasComponent<CItem>(a.Held) ? em.GetComponentData<CItem>(a.Held).ID : 0;
                Appls.Add(a);
            }
        }

        Appl AppOf(Entity e) => Appls.FirstOrDefault(a => a.E == e);

        bool IsStash(Appl a)
        {
            if (!a.HasHolder || a.IsProvider || a.IsTable || a.IsBin || a.IsSink || a.HasStorage || a.IsConveyor || a.Def == null) return false;
            foreach (var p in a.Def.Processes ?? new List<Appliance.ApplianceProcesses>())
                if (p.IsAutomatic) return false;
            return true;
        }

        /// <summary>Topping/extra ingredient ids that a stocked (loose) item must NOT contain, set per order.</summary>
        HashSet<int> banToppings = new HashSet<int>();

        bool LooseOk(Appl a)
        {
            if (banToppings.Count == 0 || a.Held == Entity.Null || !EntityManager.HasComponent<CItem>(a.Held)) return true;
            var il = EntityManager.GetComponentData<CItem>(a.Held).Items;
            for (int i = 0; i < il.Count; i++) if (banToppings.Contains(il[i])) return false;
            return true;
        }

        Entity FreeStash(Vector3 near, Entity avoid = default) => BestStash(new[] { near }, avoid);

        /// <summary>Free counter that minimises total walking distance to all the given points.</summary>
        Entity BestStash(IEnumerable<Vector3> pts, Entity avoid = default)
        {
            var list = pts.ToList();
            if (list.Count == 0) list.Add(MyPos);
            var outputCells = new HashSet<Vector2Int>(cfg.AllOutputCells);
            Appl best = null, bestMarked = null; float bd = 1e9f, bdm = 1e9f;
            foreach (var a in Appls)
            {
                if (!IsStash(a) || a.Held != Entity.Null || Reserved.Contains(a.E) || a.E == avoid) continue;
                var cell = Nav.Cell(a.Pos);
                if (outputCells.Contains(cell)) continue;          // reserved for finished products
                if (!Reachable(a)) continue;
                float d = 0;
                foreach (var p in list) d += Mathf.Abs(a.Pos.x - p.x) + Mathf.Abs(a.Pos.z - p.z);
                if (d < bd) { bd = d; best = a; }
                if (cfg.Stash.Contains(cell) && d < bdm) { bdm = d; bestMarked = a; }
            }
            var pick = bestMarked ?? best;                          // counters you marked as STASH win
            return pick != null ? pick.E : Entity.Null;
        }

        int FreeStashCount() { var oc = new HashSet<Vector2Int>(cfg.AllOutputCells); return Appls.Count(a => IsStash(a) && a.Held == Entity.Null && !Reserved.Contains(a.E) && !oc.Contains(Nav.Cell(a.Pos)) && Reachable(a)); }

        /// <summary>Positions of the appliances a recipe node will walk to (for choosing where to work).</summary>
        List<Vector3> NodePositions(Node n)
        {
            var l = new List<Vector3>();
            void W(Node k)
            {
                if (k.Kind == NK.Fetch && k.Provider != Entity.Null && EntityManager.Exists(k.Provider)) l.Add(PosOf(k.Provider));
                if (k.Kind == NK.Pickup && k.Holder != Entity.Null && EntityManager.Exists(k.Holder)) l.Add(PosOf(k.Holder));
                if (k.Child != null) W(k.Child);
                foreach (var p in k.Parts) W(p);
            }
            W(n);
            return l;
        }

        bool Reachable(Appl a) => nav.StandingCells(a.Pos).Count > 0;

        public static Node Clone(Node n)
        {
            if (n == null) return null;
            var c = new Node { Kind = n.Kind, Item = n.Item, Provider = n.Provider, Holder = n.Holder, Process = n.Process, Cost = n.Cost, Child = Clone(n.Child) };
            foreach (var p in n.Parts) c.Parts.Add(Clone(p));
            return c;
        }

        System.Text.StringBuilder explainSb;
        void ExLog(string t) { if (explainSb != null) explainSb.AppendLine(t); else Log(t); }

        public void Explain(int x, int depth, HashSet<int> seen)
        {
            if (depth > 4 || !seen.Add(x)) return;
            string pad = new string(' ', depth * 2);
            var n = Plan(x, new HashSet<int>(), 0);
            ExLog($"{pad}[{(n != null ? "ok" : "FAIL")}] {Dump.Name(x)}");
            if (n != null) return;
            if (Data.TryGet<Item>(x, out var it))
            {
                if (it is ItemGroup g)
                    foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                        foreach (var m in set.Items) { if (Plan(m.ID, new HashSet<int>(), 0) == null && (set.IsMandatory || set.Min > 0)) Explain(m.ID, depth + 1, seen); }
                if (madeBy.TryGetValue(x, out var ds))
                    foreach (var d in ds) { ExLog($"{pad}  made by {Dump.Name(d.Src.ID)} via {Dump.Name(d.Process)} bad={d.Bad} wrap={d.Wrap} ws={(FindWorkstation(d.Src.ID, out _, out _) != null)}"); }
                if (splitOf.TryGetValue(x, out var ps)) foreach (var pr in ps) ExLog($"{pad}  split of {Dump.Name(pr.ID)}");
            }
        }

        /// <summary>All optional (topping-like) ingredient ids in a recipe: optional set members and their cooked/wrapped forms.</summary>
        HashSet<int> ToppingsOf(Node n)
        {
            var res = new HashSet<int>();
            void Walk(Node k)
            {
                if (k.Kind == NK.Compose && Data.TryGet<ItemGroup>(k.Item, out var g))
                    foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                    {
                        if (set.OrderingOnly || set.IsMandatory || set.Min > 0) continue;
                        foreach (var m in set.Items)
                        {
                            res.Add(m.ID);
                            if (wrapResults.TryGetValue(m.ID, out var wr2)) foreach (var r2 in wr2) res.Add(r2);
                        }
                    }
                if (k.Child != null) Walk(k.Child);
                foreach (var p in k.Parts) Walk(p);
            }
            Walk(n);
            return res;
        }

        bool MatchesExtra(int member, int extra)
        {
            if (member == extra) return true;
            if (madeBy.TryGetValue(member, out var mdv)) foreach (var dd in mdv) if (!dd.Bad && !dd.Wrap && dd.Src.ID == extra) return true;
            if (madeBy.TryGetValue(extra, out var ds)) foreach (var d in ds) if (d.Wrap && d.Src.ID == member) return true;
            return false;
        }

        /// <summary>Add a requested extra ingredient to the right sub-recipe (e.g. mushroom goes onto the raw pizza before baking).</summary>
        bool AddExtra(Node n, int extra)
        {
            if (n.Kind == NK.Fetch || n.Kind == NK.Pickup) return false;
            if (n.Kind == NK.Compose && Data.TryGet<ItemGroup>(n.Item, out var g))
                foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                {
                    if (set.OrderingOnly) continue;
                    foreach (var m in set.Items)
                    {
                        if (!MatchesExtra(m.ID, extra)) continue;
                        var pn = Plan(m.ID, new HashSet<int>(), 0);
                        if (pn == null) continue;
                        n.Parts.Add(pn);
                        n.Cost += pn.Cost;
                        return true;
                    }
                }
            foreach (var part in n.Parts) if (AddExtra(part, extra)) return true;
            if (n.Child != null)
            {
                if (n.Child.Kind == NK.Pickup)
                {
                    var fresh = Plan(n.Child.Item, new HashSet<int>(), 0, noLoose: true);
                    if (fresh != null) n.Child = fresh;
                }
                if (AddExtra(n.Child, extra)) return true;
            }
            return false;
        }

        /// <summary>Full plan for a customer order including any requested extras/toppings.</summary>
        public Node PlanOrder(Order o)
        {
            BuildIndexes();
            var canon = Plan(o.ItemId, new HashSet<int>(), 0, noLoose: true);
            banToppings = canon != null ? ToppingsOf(canon) : new HashSet<int>();
            var wanted = new HashSet<int>(o.Comps ?? new List<int>());
            if (o.ExtraId != 0) wanted.Add(o.ExtraId);
            foreach (int w in wanted) banToppings.Remove(w);   // asked-for toppings may come from stock
            var root = Clone(Plan(o.ItemId, new HashSet<int>(), 0));
            if (root == null) { banToppings.Clear(); return null; }
            var inside = Closure(root);
            foreach (int w in wanted)
            {
                if (inside.Contains(w)) continue;
                if (AddExtra(root, w)) { Log("order extra added: " + Dump.Name(w)); inside = Closure(root); }
                else Log("could not add extra " + Dump.Name(w) + " to " + Dump.Name(o.ItemId));
            }
            return root;
        }

        /// <summary>
        /// The game only lets two items merge if the result can still become a valid group (mandatory sets under their
        /// minimum are "impossible"), so e.g. pizza crust + cheese is refused but crust + sauce is fine. Find a valid order.
        /// </summary>
        void OrderParts(ItemGroup g, Node node)
        {
            var plain = node.Parts.Where(p => p.Kind != NK.Split).ToList();
            var splits = node.Parts.Where(p => p.Kind == NK.Split).ToList();
            if (plain.Count < 3) return;
            ItemGroupData gd;
            try { gd = GameData.Main.ItemSetView.GetGroupData(g.ID); } catch { return; }
            var order = new List<Node>();
            var used = new bool[plain.Count];
            bool Search()
            {
                if (order.Count == plain.Count) return true;
                for (int i = 0; i < plain.Count; i++)
                {
                    if (used[i]) continue;
                    order.Add(plain[i]); used[i] = true;
                    bool ok = true;
                    if (order.Count >= 2)
                    {
                        var il = new ItemList(order.Select(o => o.Item).ToList());
                        ok = GameData.Main.ItemSetView.IsGroupSatisfied(gd, il) != Satisfaction.Impossible;
                    }
                    if (ok && Search()) return true;
                    order.RemoveAt(order.Count - 1); used[i] = false;
                }
                return false;
            }
            if (Search())
            {
                node.Parts.Clear();
                node.Parts.AddRange(order);
                node.Parts.AddRange(splits);
            }
            else Log("warning: no valid merge order found for " + Dump.Name(g.ID));
        }

        /// <summary>Everything that ends up inside item X's recipe (including the wrapped/cooked variants).</summary>
        HashSet<int> Closure(Node n)
        {
            var set = new HashSet<int>();
            void Walk(Node k)
            {
                set.Add(k.Item);
                if (wrapResults != null && wrapResults.TryGetValue(k.Item, out var wr)) foreach (var r in wr) set.Add(r);
                if (k.Child != null) Walk(k.Child);
                foreach (var p in k.Parts) Walk(p);
            }
            Walk(n);
            return set;
        }

        /// <summary>Find an appliance (with a holder) that can process item 'src'. Returns whether processing is automatic.</summary>
        Appl FindWorkstation(int src, out bool auto, out int process, bool requireFree = false, Vector3? near = null)
        {
            auto = false; process = 0;
            Appl best = null; float bscore = -1e9f;
            foreach (var a in Appls)
            {
                if (!a.HasHolder || a.IsProvider || a.IsTable || a.IsBin || a.Def == null) continue;
                if (!GameData.Main.ProcessesView.GetRelevantProcess(src, a.Id, out var pair) || pair.IsBad) continue;
                if (requireFree && (a.Held != Entity.Null || Reserved.Contains(a.E))) continue;
                if (!Reachable(a)) continue;
                float score = 0;
                // prefer the oven for group items that must be baked, prefer free ones
                if (a.Name.Contains("Oven")) score += 2;
                if (a.Held == Entity.Null) score += 1;
                if (a.Name.Contains("Mixer") || a.Name.Contains("Weak")) score -= 1;
                var np = near ?? MyPos;
                score -= 0.15f * (Mathf.Abs(a.Pos.x - np.x) + Mathf.Abs(a.Pos.z - np.z));
                if (score > bscore) { bscore = score; best = a; auto = pair.IsAutomatic; process = pair.Process; }
            }
            return best;
        }

        /// <summary>Analyse every dish once, ahead of time, and write the recipes to cookbook.txt.</summary>
        public void WriteCookbook()
        {
            BuildIndexes();
            abstractMode = true; solved = false;
            var sb = new System.Text.StringBuilder();
            int ok = 0, bad = 0;
            try
            {
                foreach (var d in Data.Get<Dish>().OrderBy(x => x.name))
                    foreach (var mi in d.UnlocksMenuItems)
                    {
                        if (mi.Item == null) continue;
                        var n = Plan(mi.Item.ID, new HashSet<int>(), 0);
                        sb.AppendLine("=== " + d.name + " -> " + mi.Item.name + " (" + mi.Phase + ")");
                        if (n == null)
                        {
                            sb.AppendLine("  NO PLAN - why:");
                            explainSb = sb;
                            try { Explain(mi.Item.ID, 2, new HashSet<int>()); } finally { explainSb = null; }
                            bad++;
                        }
                        else { sb.Append(n.Describe(1)); ok++; }
                    }
            }
            finally { abstractMode = false; solved = false; }
            System.IO.File.WriteAllText(System.IO.Path.Combine(Dump.Dir, "cookbook.txt"), sb.ToString());
            Log($"cookbook written: {ok} dishes planned, {bad} without a plan");
        }

        // ---------- misc queries ----------
        public int HeldIdOf(Entity holder)
        {
            var h = HeldBy(holder);
            return h != Entity.Null && EntityManager.HasComponent<CItem>(h) ? EntityManager.GetComponentData<CItem>(h).ID : 0;
        }

        public List<int> MenuItems()
        {
            var res = new List<int>();
            using (var q = EntityManager.CreateEntityQuery(typeof(CMenuItem)))
            using (var cs = q.ToComponentDataArray<CMenuItem>(Allocator.TempJob))
                foreach (var c in cs) if (c.Item != 0 && !res.Contains(c.Item)) res.Add(c.Item);
            return res;
        }

        public struct Order { public int ItemId; public Vector3 TablePos; public Entity Group; public int ExtraId; public List<int> Comps; public int Key => ItemId * 31 + ExtraId; }

        /// <summary>Unserved orders with the table position they must be served to.</summary>
        public List<Order> PendingOrders()
        {
            var res = new List<Order>();
            var em = EntityManager;
            using (var q = em.CreateEntityQuery(typeof(CDisplayedItem)))
            using (var ents = q.ToEntityArray(Allocator.TempJob))
                foreach (var e in ents)
                {
                    var buf = em.GetBuffer<CDisplayedItem>(e);
                    for (int i = 0; i < buf.Length; i++)
                    {
                        var d = buf[i];
                        if (d.IsComplete || d.ItemID == 0 || d.IsSide) continue;
                        var comps = new List<int>();
                        if (d.Item != Entity.Null && em.Exists(d.Item) && em.HasComponent<CItem>(d.Item))
                        {
                            var il = em.GetComponentData<CItem>(d.Item).Items;
                            for (int k = 0; k < il.Count; k++) if (il[k] != 0 && il[k] != d.ItemID) comps.Add(il[k]);
                        }
                        res.Add(new Order { ItemId = d.ItemID, TablePos = d.TablePosition, Group = e, ExtraId = d.ExtraID, Comps = comps });
                    }
                }
            return res;
        }

        /// <summary>A holder somewhere holding a dirty dish.</summary>
        public Entity FindDirty(out int id)
        {
            id = 0;
            BuildIndexes();
            foreach (var a in Appls)
            {
                if (a.IsSink || a.IsBin || !a.HasHolder) continue;
                if (a.IsProvider && a.Prov.ProvidedItem != 0 && dirtyIds.Contains(a.Prov.ProvidedItem) && a.Prov.HasAvailableItems() && Reachable(a)) { id = a.Prov.ProvidedItem; return a.E; }
                if (a.HeldId != 0 && dirtyIds.Contains(a.HeldId) && Reachable(a)) { id = a.HeldId; return a.E; }
                if (a.HasStorage && EntityManager.HasComponent<CItemStored>(a.E))
                {
                    var buf = EntityManager.GetBuffer<CItemStored>(a.E);
                    for (int i = 0; i < buf.Length; i++)
                    {
                        var it = buf[i].StoredItem;
                        if (it != Entity.Null && EntityManager.Exists(it) && EntityManager.HasComponent<CItem>(it))
                        {
                            int cid = EntityManager.GetComponentData<CItem>(it).ID;
                            if (dirtyIds.Contains(cid) && Reachable(a)) { id = cid; return a.E; }
                        }
                    }
                }
            }
            return Entity.Null;
        }
    }
}
