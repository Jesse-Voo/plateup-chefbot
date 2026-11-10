using System;
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using KitchenData;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    /// <summary>
    /// Bottom-up recipe solver. Instead of searching recursively (which can explode), it computes the cheapest way to
    /// obtain EVERY item in a few relaxation passes (Bellman-Ford style), then rebuilds a plan tree by following the
    /// chosen option of each item. Cost is bounded: passes x items x options.
    /// </summary>
    public partial class ChefBotSystem
    {
        const float INF = 1e9f;

        class Opt
        {
            public NK Kind;           // Fetch, Pickup, Process, Split, Compose; Slice is encoded as Compose + Parent
            public bool IsSlice;
            public float Cost;
            public Entity Ent;
            public Deriv D;
            public Item Parent;
            public ItemGroup G;
            public Item Container;
        }

        readonly Dictionary<int, float> cost = new Dictionary<int, float>(), costNL = new Dictionary<int, float>();
        readonly Dictionary<int, Opt> choice = new Dictionary<int, Opt>(), choiceNL = new Dictionary<int, Opt>();
        readonly Dictionary<int, bool> wsCache = new Dictionary<int, bool>();
        readonly Dictionary<int, bool> absWs = new Dictionary<int, bool>();
        bool solved;
        bool abstractMode;   // cookbook mode: pretend every needed appliance/provider exists
        int solvedBanKey = -1;

        float C(int id) => cost.TryGetValue(id, out var v) ? v : INF;
        float CNL(int id) => costNL.TryGetValue(id, out var v) ? v : INF;

        int BanKey()
        {
            int k = banToppings.Count * 7919;
            foreach (int i in banToppings) k ^= i;
            return k;
        }

        bool HasWorkstation(int src)
        {
            if (abstractMode)
            {
                if (absWs.TryGetValue(src, out bool v)) return v;
                v = false;
                foreach (var ap in Data.Get<Appliance>())
                    if (GameData.Main.ProcessesView.GetRelevantProcess(src, ap.ID, out var pair) && !pair.IsBad && ap.Properties != null && ap.Properties.Any(pp => pp is CItemHolder)) { v = true; break; }
                absWs[src] = v;
                return v;
            }
            if (wsCache.TryGetValue(src, out bool r)) return r;
            r = FindWorkstation(src, out _, out _) != null;
            wsCache[src] = r;
            return r;
        }

        void Solve()
        {
            BuildIndexes();
            cost.Clear(); costNL.Clear(); choice.Clear(); choiceNL.Clear(); wsCache.Clear();
            solved = true;
            solvedBanKey = BanKey();

            // ---- leaves: things we can simply take
            var fetch = new Dictionary<int, Opt>();
            var pickup = new Dictionary<int, Opt>();
            if (abstractMode)
            {
                foreach (var it in allItems) if (it.DedicatedProvider != null) fetch[it.ID] = new Opt { Kind = NK.Fetch, Cost = 2 };
            }
            else
            {
                foreach (var a in Appls)
                {
                    if (a.IsProvider && a.Prov.ProvidedItem != 0 && a.Prov.ProvidedComponents.IsNonGroup && a.Prov.HasAvailableItems() && !fetch.ContainsKey(a.Prov.ProvidedItem) && Reachable(a))
                        fetch[a.Prov.ProvidedItem] = new Opt { Kind = NK.Fetch, Cost = 2, Ent = a.E };
                    if (a.HeldId != 0 && !a.IsTable && !a.IsBin && !a.IsProvider && !a.IsSink && !Reserved.Contains(a.E) && !pickup.ContainsKey(a.HeldId) && LooseOk(a) && Reachable(a))
                        pickup[a.HeldId] = new Opt { Kind = NK.Pickup, Cost = 1, Ent = a.E };
                }
            }

            var sliceParents = allItems.Where(i => i.SplitSubItem != null).ToList();
            var closureCache = new Dictionary<int, HashSet<int>>();

            bool changed = true;
            for (int pass = 0; pass < 40 && changed; pass++)
            {
                changed = false;
                closureCache.Clear();

                void Try(int id, Opt o)
                {
                    if (o.Cost < C(id)) { cost[id] = o.Cost; choice[id] = o; changed = true; }
                    if (o.Kind != NK.Pickup && o.Cost < CNL(id)) { costNL[id] = o.Cost; choiceNL[id] = o; changed = true; }
                }

                foreach (var item in allItems)
                {
                    int id = item.ID;
                    if (fetch.TryGetValue(id, out var fo)) Try(id, fo);
                    if (pickup.TryGetValue(id, out var po)) Try(id, po);

                    if (madeBy.TryGetValue(id, out var derivs))
                        foreach (var d in derivs)
                        {
                            if (d.Bad || d.Wrap) continue;
                            float cc = C(d.Src.ID);
                            if (cc >= INF || !HasWorkstation(d.Src.ID)) continue;
                            Try(id, new Opt { Kind = NK.Process, Cost = cc + 3, D = d });
                        }

                    if (splitOf.TryGetValue(id, out var parents))
                        foreach (var par in parents)
                        {
                            float cc = C(par.ID);
                            if (cc < INF) Try(id, new Opt { Kind = NK.Split, Cost = cc + 2, Parent = par });
                        }

                    if (item is ItemGroup g)
                    {
                        float total = 2; bool any = false, ok = true;
                        Item container = null;
                        foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                        {
                            if (set.OrderingOnly) continue;
                            int need = Mathf.Max(set.Min, set.IsMandatory ? 1 : 0);
                            if (need <= 0) continue;
                            any = true;
                            var cs = set.Items.Select(m => C(m.ID)).Where(v => v < INF).OrderBy(v => v).ToList();
                            if (cs.Count < need) { ok = false; break; }
                            for (int i = 0; i < need; i++) total += cs[i];
                        }
                        if (ok && any) Try(id, new Opt { Kind = NK.Compose, Cost = total, G = g });

                        // slice route: container (plate) + a slice cut from a bigger dish that already contains our components
                        foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                            if (!set.OrderingOnly && set.IsMandatory && set.Items.Count == 1) { container = set.Items[0]; break; }
                        if (container != null && C(container.ID) < INF)
                            foreach (var parent in sliceParents)
                            {
                                if (parent.SplitSubItem.ID == id || C(parent.ID) >= INF) continue;
                                if (!closureCache.TryGetValue(parent.ID, out var cl))
                                {
                                    var tree = Build(parent.ID, true, 0);   // canonical recipe, not a stocked item that hides its ingredients
                                    cl = tree != null ? Closure(tree) : new HashSet<int>();
                                    closureCache[parent.ID] = cl;
                                }
                                if (cl.Count == 0 || !CoversMandatory(g, container, cl)) continue;
                                Try(id, new Opt { Kind = NK.Compose, IsSlice = true, Cost = C(container.ID) + C(parent.ID) + 4, G = g, Parent = parent, Container = container });
                            }
                    }
                }
            }
        }

        bool CoversMandatory(ItemGroup g, Item container, HashSet<int> cl)
        {
            foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
            {
                if (set.OrderingOnly || !(set.IsMandatory || set.Min > 0)) continue;
                if (set.IsMandatory && set.Items.Count == 1 && set.Items[0].ID == container.ID) continue;
                int need = Mathf.Max(set.Min, 1), have = set.Items.Count(m => cl.Contains(m.ID));
                if (have < need) return false;
            }
            return true;
        }

        /// <summary>Public entry: plan to obtain item x (null when impossible in this kitchen).</summary>
        Node Plan(int x, HashSet<int> unused, int unused2, bool noLoose = false)
        {
            if (!solved || solvedBanKey != BanKey()) Solve();
            float c = noLoose ? CNL(x) : C(x);
            if (c >= INF) return null;
            return Build(x, noLoose, 0);
        }

        Node Build(int x, bool nl, int depth)
        {
            if (depth > 25) return null;
            var dict = nl ? choiceNL : choice;
            if (!dict.TryGetValue(x, out var o)) return null;
            switch (o.Kind)
            {
                case NK.Fetch: return new Node { Kind = NK.Fetch, Item = x, Provider = o.Ent, Cost = 2 };
                case NK.Pickup: return new Node { Kind = NK.Pickup, Item = x, Holder = o.Ent, Cost = 1 };
                case NK.Process:
                {
                    var child = Build(o.D.Src.ID, false, depth + 1);
                    if (child == null) return null;
                    return new Node { Kind = NK.Process, Item = x, Child = child, Process = o.D.Process, Cost = child.Cost + 3 };
                }
                case NK.Split:
                {
                    var child = Build(o.Parent.ID, false, depth + 1);
                    if (child == null) return null;
                    return new Node { Kind = NK.Split, Item = x, Child = child, Cost = child.Cost + 2 };
                }
                case NK.Compose:
                    if (o.IsSlice)
                    {
                        var cn = Build(o.Container.ID, false, depth + 1);
                        var pn = Build(o.Parent.ID, false, depth + 1);
                        if (cn == null || pn == null) return null;
                        var split = new Node { Kind = NK.Split, Item = o.Parent.SplitSubItem.ID, Child = pn, Cost = pn.Cost + 2 };
                        var node = new Node { Kind = NK.Compose, Item = x, Cost = cn.Cost + split.Cost + 2 };
                        node.Parts.Add(cn); node.Parts.Add(split);
                        return node;
                    }
                    return BuildCompose(o.G, depth);
            }
            return null;
        }

        Node BuildCompose(ItemGroup g, int depth)
        {
            var parts = new List<Node>();
            Node baseNode = null;
            foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
            {
                if (set.OrderingOnly) continue;
                int need = Mathf.Max(set.Min, set.IsMandatory ? 1 : 0);
                if (need <= 0) continue;
                var cands = set.Items.Where(m => C(m.ID) < INF).OrderBy(m => C(m.ID)).Take(need).ToList();
                if (cands.Count < need) return null;
                foreach (var m in cands)
                {
                    var n = Build(m.ID, false, depth + 1);
                    if (n == null) return null;
                    // the single mandatory member (plate, board, tray...) is the container => base
                    if (baseNode == null && set.IsMandatory && set.Items.Count == 1) baseNode = n;
                    else parts.Add(n);
                }
            }
            if (baseNode == null)
            {
                if (parts.Count == 0) return null;
                int gi = parts.FindIndex(pp => pp.Kind != NK.Split && Data.TryGet<Item>(pp.Item, out var pit) && pit is ItemGroup);
                if (gi < 0) gi = parts.FindIndex(pp => pp.Kind != NK.Split);
                if (gi < 0) return null;
                baseNode = parts[gi];
                parts.RemoveAt(gi);
            }
            if (baseNode.Kind == NK.Split)
            {
                int bi = parts.FindIndex(pp => pp.Kind != NK.Split);
                if (bi < 0) return null;
                var tmpN = parts[bi]; parts[bi] = baseNode; baseNode = tmpN;
            }
            var node = new Node { Kind = NK.Compose, Item = g.ID };
            node.Parts.Add(baseNode);
            node.Parts.AddRange(parts);
            OrderParts(g, node);
            node.Cost = node.Parts.Sum(p => p.Cost) + 2;
            return node;
        }
    }
}
