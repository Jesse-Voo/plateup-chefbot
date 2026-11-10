using System;
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using KitchenData;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    public partial class ChefBotSystem
    {
        bool R;   // result of the last primitive (iterators cannot return values)
        readonly Dictionary<int, float> failUntil = new Dictionary<int, float>();
        readonly HashSet<int> loggedPlans = new HashSet<int>();

        // ====================================================== primitives

        /// <summary>Walk to a cell next to the target and face it until the game's interaction probe points at it.</summary>
        IEnumerable<int> Go(Entity target, float timeout = 30f)
        {
            R = false;
            float t0 = Now;
            var banned = new HashSet<Vector2Int>();
            float lastTel = Now;
            while (true)
            {
                if (target == Entity.Null || !EntityManager.Exists(target)) yield break;
                if (Now - t0 > timeout) { Log("Go: timeout"); yield break; }
                var tpos = PosOf(target);
                var goals = new HashSet<Vector2Int>(nav.StandingCells(tpos).Where(c => !banned.Contains(c)));
                if (goals.Count == 0) goals = new HashSet<Vector2Int>(nav.StandingCells(tpos, filtered: false).Where(c => !banned.Contains(c)));
                if (goals.Count == 0)
                {
                    if (banned.Count > 0) { banned.Clear(); continue; }
                    Log("Go: no standing cell for " + ApplianceName(target) + " " + nav.Describe(Nav.Cell(tpos)));
                    yield break;
                }
                var path = nav.Path(MyPos, goals);
                if (path == null)
                {
                    // the strict 'game can reach' filter may have hidden the only walkable side: retry with every adjacent cell
                    var all = new HashSet<Vector2Int>(nav.StandingCells(tpos, filtered: false));
                    path = nav.Path(MyPos, all);
                    if (path != null) goals = all;
                }
                if (path == null) { Log("Go: no path to " + ApplianceName(target) + " from " + MyPos.ToString("F1")); yield break; }

                int wp = 0;
                Vector3 lastPos = MyPos; float lastMoved = Now;
                while (wp < path.Count)
                {
                    if (Now - t0 > timeout) { Log("Go: timeout walking"); yield break; }
                    var me = MyPos;
                    var d = new Vector2(path[wp].x - me.x, path[wp].z - me.z);
                    if (Now - lastTel > 4f) { lastTel = Now; Log($"walking to {ApplianceName(target)}: at {me.ToString("F1")} waypoint {wp}/{path.Count} {path[wp].ToString("F0")}"); }
                    if (d.magnitude < 0.2f) { wp++; continue; }
                    body.MoveWorld = d.normalized;
                    if ((me - lastPos).magnitude > 0.05f) { lastPos = me; lastMoved = Now; }
                    else if (Now - lastMoved > 1.5f)
                    {
                        // something (customer, player, a corner) is in the way: avoid that cell for a while, sidestep and re-plan
                        nav.BlockTemp(Nav.Cell(path[wp]), 3f);
                        body.MoveWorld = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)).normalized;
                        yield return 0; yield return 0; yield return 0;
                        path = nav.Path(MyPos, goals); wp = 0; lastMoved = Now;
                        if (path == null) yield break;
                        continue;
                    }
                    yield return 0;
                }

                // face the target
                var cell = Nav.Cell(MyPos);
                float f0 = Now; int hits = 0;
                while (Now - f0 < 1.3f)
                {
                    var me = MyPos; var t = PosOf(target);
                    var dir = new Vector2(t.x - me.x, t.z - me.z);
                    body.MoveWorld = dir.sqrMagnitude < 1e-4f ? Vector2.zero : dir.normalized;
                    yield return 0;
                    if (AttemptTarget() == target) { if (++hits >= 3) { R = true; yield break; } }
                    else hits = 0;
                }
                banned.Add(cell);
            }
        }

        /// <summary>Press grab once while aimed at target. R = something changed hands.</summary>
        IEnumerable<int> Grab(Entity target)
        {
            var heldBefore = MyHeld(); var targetBefore = HeldBy(target);
            int idBefore = heldBefore != Entity.Null ? ItemId(heldBefore) : 0;
            for (int i = 0; i < 3; i++) { body.MoveWorld = TowardsDir(target); body.Grab = true; yield return 0; }
            for (int i = 0; i < 12; i++)
            {
                body.MoveWorld = TowardsDir(target);
                yield return 0;
                if (MyHeld() != heldBefore || HeldBy(target) != targetBefore) break;
            }
            for (int i = 0; i < 3; i++) { body.MoveWorld = TowardsDir(target); yield return 0; }   // release
            int idAfter = MyHeld() != Entity.Null ? ItemId(MyHeld()) : 0;
            R = MyHeld() != heldBefore || HeldBy(target) != targetBefore || idAfter != idBefore;
        }

        int ItemId(Entity item) => item != Entity.Null && EntityManager.Exists(item) && EntityManager.HasComponent<CItem>(item) ? EntityManager.GetComponentData<CItem>(item).ID : 0;

        /// <summary>Go to a holder and take what it holds. expect = required item id (0 = anything). R = holding it.</summary>
        IEnumerable<int> TakeFrom(Entity holder, int expect)
        {
            foreach (var _ in Go(holder)) yield return 0;
            if (!R) yield break;
            foreach (var _ in Grab(holder)) yield return 0;
            var h = MyHeld();
            R = h != Entity.Null && (expect == 0 || ItemId(h) == expect);
            if (!R) Log($"TakeFrom {ApplianceName(holder)} failed: holding {Dump.DescribeItem(EntityManager, h)} expected {Dump.Name(expect)}");
        }

        /// <summary>Go to a holder and put the held item on it. R = hands empty afterwards.</summary>
        IEnumerable<int> PutOn(Entity holder)
        {
            foreach (var _ in Go(holder)) yield return 0;
            if (!R) yield break;
            foreach (var _ in Grab(holder)) yield return 0;
            R = MyHeld() == Entity.Null;
            if (!R) Log($"PutOn {ApplianceName(holder)} failed: still holding {Dump.DescribeItem(EntityManager, MyHeld())}; holder has {Dump.DescribeItem(EntityManager, HeldBy(holder))}");
        }

        /// <summary>Hold the act button on a holder (chop / knead / wash ...) until done() or timeout.</summary>
        IEnumerable<int> Work(Entity target, Func<bool> done, float timeout)
        {
            foreach (var _ in Go(target)) yield return 0;
            if (!R) yield break;
            float t0 = Now;
            while (Now - t0 < timeout)
            {
                if (done()) { R = true; yield break; }
                body.MoveWorld = TowardsDir(target);
                body.Act = true;
                yield return 0;
            }
            R = done();
        }

        IEnumerable<int> WaitFor(Func<bool> done, float timeout)
        {
            float t0 = Now;
            while (Now - t0 < timeout) { if (done()) { R = true; yield break; } yield return 0; }
            R = done();
        }

        IEnumerable<int> Pause(float s)
        {
            float t0 = Now;
            while (Now - t0 < s) yield return 0;
        }

        // ====================================================== recipe execution

        /// <summary>Build node's item and end with it IN HAND. R = success. Nothing is stashed unless it has to be.</summary>
        IEnumerable<int> ExecHand(Node n, int depth)
        {
            R = false;
            Snapshot();
            Log(new string(' ', depth * 2) + "make " + n.Kind + " " + Dump.Name(n.Item));
            switch (n.Kind)
            {
                case NK.Fetch:
                    foreach (var _ in TakeFrom(n.Provider, n.Item)) yield return 0;
                    yield break;

                case NK.Pickup:
                    foreach (var _ in TakeFrom(n.Holder, n.Item)) yield return 0;
                    yield break;

                case NK.Process:
                {
                    // a chain of the same automatic process (cook -> cook -> cook) is one placement: start from the deepest source
                    var srcNode = n.Child;
                    while (srcNode.Kind == NK.Process && srcNode.Process == n.Process && IsAutoProcess(srcNode.Child.Item)) srcNode = srcNode.Child;
                    if (srcNode != n.Child) n = new Node { Kind = NK.Process, Item = n.Item, Child = srcNode, Process = n.Process, Cost = n.Cost };
                    int srcId = n.Child.Item;
                    var near = NodePositions(n.Child);
                    var centre = near.Count > 0 ? new Vector3(near.Average(v => v.x), 0, near.Average(v => v.z)) : MyPos;
                    // is there a suitable workstation at all? (it may be busy with another bot right now - that is fine)
                    var wsAny = FindWorkstation(srcId, out bool auto, out int proc, requireFree: false, near: centre);
                    if (wsAny == null) { Log("no workstation for processing " + Dump.Name(srcId)); yield break; }
                    // build the input first, so a second cook can assemble while the first one's food is in the only oven
                    foreach (var _ in ExecHand(n.Child, depth + 1)) yield return 0;
                    if (!R) yield break;
                    // now wait for a free one
                    Appl ws = null;
                    float waitStart = Now, lastLook = -10f;
                    while (true)
                    {
                        if (Now - lastLook > 0.4f)
                        {
                            lastLook = Now;
                            Snapshot();
                            ws = FindWorkstation(srcId, out auto, out proc, requireFree: true, near: MyPos);
                            if (ws != null) break;
                        }
                        if (Now - waitStart > 180f) break;
                        Status = "Waiting for the " + wsAny.Name;
                        yield return 0;
                    }
                    if (ws == null) { Log("no free " + wsAny.Name + " after waiting"); R = false; yield break; }
                    var wsE = ws.E;
                    Reserved.Add(wsE);
                    foreach (var _ in PutOn(wsE)) yield return 0;
                    if (!R)
                    {
                        // something else is sitting on it: try another free workstation once
                        Reserved.Remove(wsE);
                        Snapshot();
                        var ws2 = FindWorkstation(srcId, out bool auto2, out int proc2, requireFree: true, near: centre);
                        if (ws2 == null || ws2.E == wsE) yield break;
                        ws = ws2; wsE = ws2.E; auto = auto2; Reserved.Add(wsE);
                        Log("first workstation was blocked, trying " + ws2.Name);
                        foreach (var _ in PutOn(wsE)) yield return 0;
                        if (!R) { Reserved.Remove(wsE); yield break; }
                    }
                    if (auto)
                    {
                        if (ws.NeedsActivation)
                            for (int tries = 0; tries < 3 && EntityManager.HasComponent<CIsInactive>(wsE); tries++)
                                foreach (var _ in Work(wsE, () => !EntityManager.HasComponent<CIsInactive>(wsE), 0.6f)) yield return 0;
                        foreach (var _ in WaitFor(() => HeldIdOf(wsE) == n.Item, 120f)) yield return 0;
                        // an oven only lets you take food out while it is switched OFF: stop it first
                        if (ws.NeedsActivation)
                        {
                            for (int tries = 0; tries < 5 && !EntityManager.HasComponent<CIsInactive>(wsE); tries++)
                            {
                                foreach (var _ in Work(wsE, () => EntityManager.HasComponent<CIsInactive>(wsE), 1.0f)) yield return 0;
                                foreach (var _ in Pause(0.15f)) yield return 0;
                            }
                            Log("oven stopped: " + EntityManager.HasComponent<CIsInactive>(wsE));
                        }
                    }
                    else
                        foreach (var _ in Work(wsE, () => HeldIdOf(wsE) != srcId, 60f)) yield return 0;
                    int now = HeldIdOf(wsE);
                    if (now != n.Item) { Log($"process produced {Dump.Name(now)} instead of {Dump.Name(n.Item)}"); Reserved.Remove(wsE); R = false; yield break; }
                    foreach (var _ in TakeFrom(wsE, n.Item)) yield return 0;
                    Reserved.Remove(wsE);
                    yield break;
                }

                case NK.Compose:
                {
                    // 1) make sure the bulk items we will slice from exist (they take longest, so start them first)
                    for (int i = 1; i < n.Parts.Count; i++)
                        if (n.Parts[i].Kind == NK.Split)
                        {
                            foreach (var _ in EnsureParent(n.Parts[i], depth)) yield return 0;
                            if (!R) yield break;
                        }
                    // 2) base in hand
                    foreach (var _ in ExecHand(n.Parts[0], depth + 1)) yield return 0;
                    if (!R) yield break;
                    bool handHasBase = true;
                    Entity d = Entity.Null;
                    for (int i = 1; i < n.Parts.Count; i++)
                    {
                        var comp = n.Parts[i];
                        if (comp.Kind == NK.Split)
                        {
                            if (!handHasBase) { foreach (var _ in TakeFrom(d, 0)) yield return 0; if (!R) yield break; handHasBase = true; }
                            foreach (var _ in MergeSliceHeld(comp)) yield return 0;
                            if (!R) yield break;
                        }
                        else
                        {
                            if (handHasBase)
                            {
                                Snapshot();
                                if (d == Entity.Null)
                                {
                                    var pts = NodePositions(n); pts.Add(MyPos);
                                    d = BestStash(pts);
                                    if (d == Entity.Null) { Log("no free stash counter"); R = false; yield break; }
                                    Reserved.Add(d);
                                }
                                foreach (var _ in PutOn(d)) yield return 0;
                                if (!R) { Reserved.Remove(d); yield break; }
                                handHasBase = false;
                            }
                            foreach (var _ in ExecHand(comp, depth + 1)) yield return 0;
                            if (!R) { if (d != Entity.Null) Reserved.Remove(d); yield break; }
                            foreach (var _ in PutOn(d)) yield return 0;
                            if (!R) { Reserved.Remove(d); yield break; }
                        }
                    }
                    if (!handHasBase) { foreach (var _ in TakeFrom(d, 0)) yield return 0; }
                    else R = true;
                    if (d != Entity.Null) Reserved.Remove(d);
                    yield break;
                }

                case NK.Depleted:
                {
                    // e.g. corn on the cob: put the raw cob on a counter, hold act until the husk comes off, take the husk
                    // away (bin) and what is left on the counter is the husked cob.
                    foreach (var _ in ExecHand(n.Child, depth + 1)) yield return 0;
                    if (!R) yield break;
                    Snapshot();
                    var dpts = NodePositions(n); dpts.Add(MyPos);
                    var dspot = BestStash(dpts);
                    if (dspot == Entity.Null) { Log("no free counter for " + Dump.Name(n.Child.Item)); R = false; yield break; }
                    Reserved.Add(dspot);
                    foreach (var _ in PutOn(dspot)) yield return 0;
                    if (!R) { Log("husking: could not put " + Dump.Name(n.Child.Item) + " on the counter"); Reserved.Remove(dspot); yield break; }
                    Log("husking: " + Dump.DescribeItem(EntityManager, HeldBy(dspot)) + " is on the counter, holding act");
                    foreach (var _ in Work(dspot, () => SplitReady(HeldBy(dspot)), 25f)) yield return 0;
                    Log("husking: split ready=" + SplitReady(HeldBy(dspot)) + ", counter has " + Dump.DescribeItem(EntityManager, HeldBy(dspot)));

                    // take the portion (the husk) into the hand; try twice, the game needs a moment after the split completes
                    for (int tryTake = 0; tryTake < 2 && MyHeld() == Entity.Null; tryTake++)
                    {
                        if (tryTake > 0) foreach (var _ in Pause(0.5f)) yield return 0;
                        foreach (var _ in Grab(dspot)) yield return 0;
                    }
                    var portion = MyHeld();
                    int portionId = ItemId(portion);
                    Log("husking: took " + (portion != Entity.Null ? Dump.DescribeItem(EntityManager, portion) : "nothing") + "; counter now has " + Dump.DescribeItem(EntityManager, HeldBy(dspot)));

                    if (portion != Entity.Null && portionId == n.Item)
                    {
                        // we ended up holding the husked cob itself: that is what we wanted
                        Reserved.Remove(dspot);
                        R = true;
                        yield break;
                    }
                    if (portion != Entity.Null)
                    {
                        // the husk (or whatever came off): throw it away
                        foreach (var _ in DropJunk()) yield return 0;
                        Log("husking: after throwing away, hand=" + (MyHeld() != Entity.Null ? Dump.DescribeItem(EntityManager, MyHeld()) : "empty"));
                        if (MyHeld() != Entity.Null) { Status = "Cannot get rid of " + Dump.Name(ItemId(MyHeld())); Reserved.Remove(dspot); R = false; yield break; }
                    }
                    int leftOver = HeldIdOf(dspot);
                    if (leftOver != n.Item) { Log("husking: expected " + Dump.Name(n.Item) + " on the counter but found " + (leftOver != 0 ? Dump.Name(leftOver) : "nothing")); Reserved.Remove(dspot); R = false; yield break; }
                    foreach (var _ in TakeFrom(dspot, n.Item)) yield return 0;
                    Reserved.Remove(dspot);
                    yield break;
                }

                default:
                    Log("unsupported node " + n.Kind);
                    R = false;
                    yield break;
            }
        }

        bool IsAutoProcess(int src)
        {
            FindWorkstation(src, out bool auto, out _);
            return auto;
        }

        bool SplitReady(Entity item)
        {
            if (item == Entity.Null || !EntityManager.Exists(item) || !EntityManager.HasComponent<CItemUndergoingProcess>(item)) return false;
            var p = EntityManager.GetComponentData<CItemUndergoingProcess>(item);
            return p.IsBeingSplit && p.Progress >= 1f;
        }

        /// <summary>Make sure the bulk item a slice comes from is lying on some holder (builds + stashes it if not).</summary>
        IEnumerable<int> EnsureParent(Node split, int depth)
        {
            R = true;
            Snapshot();
            int parentId = split.Child.Item;
            if (FindHolderOf(parentId) != Entity.Null) yield break;
            foreach (var _ in ExecHand(split.Child, depth + 1)) yield return 0;
            if (!R) yield break;
            Snapshot();
            var spot = BestStash(NodePositions(split.Child).Concat(new[] { MyPos }));
            if (spot == Entity.Null) { Log("no stash for " + Dump.Name(parentId)); R = false; yield break; }
            foreach (var _ in PutOn(spot)) yield return 0;
        }

        Entity FindHolderOf(int itemId)
        {
            Entity best = Entity.Null; float bd = 1e9f;
            foreach (var a in Appls)
                if (a.HeldId == itemId && !a.IsTable && !a.IsBin && Reachable(a))
                {
                    float d = (a.Pos - MyPos).sqrMagnitude;
                    if (d < bd) { bd = d; best = a.E; }
                }
            return best;
        }

        /// <summary>We hold a base (plate, dough ...): take a slice/portion from the bulk item directly into it.</summary>
        IEnumerable<int> MergeSliceHeld(Node split)
        {
            R = false;
            Snapshot();
            var holder = FindHolderOf(split.Child.Item);
            if (holder == Entity.Null) { Log("slice parent vanished: " + Dump.Name(split.Child.Item)); yield break; }
            string before = Dump.DescribeItem(EntityManager, MyHeld());
            foreach (var _ in Go(holder)) yield return 0;
            if (!R) yield break;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                foreach (var _ in Grab(holder)) yield return 0;
                string after = Dump.DescribeItem(EntityManager, MyHeld());
                if (after != before) { R = true; break; }
                foreach (var _ in Pause(0.8f)) yield return 0;
                R = false;
            }
            string heldHolder = Dump.DescribeItem(EntityManager, HeldBy(holder));
            Log($"slice merge: {before} + [{heldHolder}] -> {Dump.DescribeItem(EntityManager, MyHeld())} ok={R}");
        }

        // ====================================================== jobs

        IEnumerable<int> DoOrder(Order order)
        {
            Snapshot();
            Reserved.Clear();
            var root = PlanOrder(order);
            if (root == null)
            {
                if (loggedPlans.Add(-order.ItemId)) { Log("no recipe plan possible for " + Dump.Name(order.ItemId)); Explain(order.ItemId, 0, new HashSet<int>()); }
                failUntil[order.Key] = Now + 8f;
                yield break;
            }
            if (cur != null && cur.Role == BotRole.Server && NeedsCooking(root))
            {
                Status = "Waiting for the cook: " + Dump.Name(order.ItemId);
                failUntil[order.Key] = Now + 3f;
                yield break;
            }
            { var pk = new HashSet<Entity>(); CollectPickups(root, pk); foreach (var e in pk) Reserved.Add(e); }
            Log(cur?.Name + " ORDER " + Dump.Name(order.ItemId) + " extra=" + Dump.Name(order.ExtraId) + " comps=[" + string.Join(",", (order.Comps ?? new List<int>()).Select(Dump.Name)) + "] plan:\n" + root.Describe());
            foreach (var _ in ExecHand(root, 0)) yield return 0;
            Reserved.Clear();
            if (!R) { Log("order build failed: " + Dump.Name(order.ItemId)); failUntil[order.Key] = Now + 10f; yield break; }
            Log("built " + Dump.DescribeItem(EntityManager, MyHeld()) + ", serving to " + order.TablePos.ToString("F1"));
            Snapshot();
            var table = ChooseTable(order.TablePos);
            if (table == Entity.Null) { Log("no table found"); yield break; }
            foreach (var _ in PutOn(table)) yield return 0;
            Log("served -> " + R);
            if (!R) failUntil[order.Key] = Now + 6f;
        }

        Entity ChooseTable(Vector3 pos)
        {
            var occ = TileManager.GetOccupant(pos);
            if (occ != Entity.Null && EntityManager.HasComponent<CItemHolder>(occ)) return occ;
            Appl best = null; float bd = 1e9f;
            foreach (var a in Appls)
            {
                if (!a.IsTable || !a.HasHolder) continue;
                float d = (a.Pos - pos).sqrMagnitude;
                if (d < bd) { bd = d; best = a; }
            }
            return best != null ? best.E : Entity.Null;
        }

        /// <summary>Ingredients currently unlocked for a menu item (toppings/sauces the customers may ask for).</summary>
        HashSet<int> AvailableIngredientsFor(int menuItem)
        {
            var res = new HashSet<int>();
            using (var q = EntityManager.CreateEntityQuery(typeof(CAvailableIngredient)))
            using (var cs = q.ToComponentDataArray<CAvailableIngredient>(Unity.Collections.Allocator.TempJob))
                foreach (var c in cs) if (c.MenuItem == menuItem) res.Add(c.Ingredient);
            return res;
        }

        bool AvailMatches(HashSet<int> av, int member)
        {
            if (av.Contains(member)) return true;
            if (madeBy.TryGetValue(member, out var ds)) foreach (var d in ds) if (!d.Bad && !d.Wrap && av.Contains(d.Src.ID)) return true;
            return false;
        }

        public struct PrepT { public int item; public int want; public string why; public List<int> extras; }

        /// <summary>Optional (topping-like) ingredient members that can be added to a recipe, and are makeable in this kitchen.</summary>
        List<Item> OptionalMembers(Node canon)
        {
            var res = new List<Item>();
            var seen = new HashSet<int>();
            void Walk(Node k)
            {
                if (k.Kind == NK.Compose && Data.TryGet<ItemGroup>(k.Item, out var g))
                    foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                    {
                        if (set.OrderingOnly || set.IsMandatory || set.Min > 0) continue;
                        foreach (var m in set.Items) if (C(m.ID) < INF && seen.Add(m.ID)) res.Add(m);
                    }
                if (k.Child != null) Walk(k.Child);
                foreach (var p in k.Parts) Walk(p);
            }
            if (canon != null) Walk(canon);
            return res;
        }

        bool AvailMatches2(HashSet<int> av, int member)
        {
            if (av.Contains(member)) return true;
            foreach (int f in ToppingForms(member)) if (av.Contains(f)) return true;
            if (madeBy.TryGetValue(member, out var ds)) foreach (var d in ds) if (!d.Bad && !d.Wrap && av.Contains(d.Src.ID)) return true;
            return false;
        }

        /// <summary>
        /// What should be kept ready for a dish: (item, how many, why, toppings). Alternatives (steak doneness) get one of each,
        /// and dishes cut from a bulk item (pizza) get one bulk item per topping variant that is unlocked.
        /// all=true lists every makeable variant (used by the "add to list" panel), false only unlocked ones.
        /// </summary>
        List<PrepT> PrepTargets(int menuItem, bool all = false)
        {
            var res = new List<PrepT>();
            var root = Plan(menuItem, new HashSet<int>(), 0, noLoose: true);
            if (root == null || root.Kind != NK.Compose || !Data.TryGet<ItemGroup>(menuItem, out var g)) return res;

            var containers = new HashSet<int>();
            foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
                if (!set.OrderingOnly && set.IsMandatory && set.Items.Count == 1) containers.Add(set.Items[0].ID);

            var avail = AvailableIngredientsFor(menuItem);

            // dishes cut from a bigger item (pizza): keep the cooked bulk item stocked, plain and with each unlocked topping
            var split = root.Parts.FirstOrDefault(p => p.Kind == NK.Split);
            if (split != null)
            {
                int bulk = split.Child.Item;
                res.Add(new PrepT { item = bulk, want = 2, why = "bulk item to slice", extras = new List<int>() });
                var bulkCanon = Plan(bulk, new HashSet<int>(), 0, noLoose: true);
                foreach (var m in OptionalMembers(bulkCanon))
                    if (all || AvailMatches2(avail, m.ID))
                        res.Add(new PrepT { item = bulk, want = 1, why = "with " + m.name, extras = new List<int> { m.ID } });
                return res;
            }

            foreach (var set in g.DerivedSets ?? new List<ItemGroup.ItemSet>())
            {
                if (set.OrderingOnly) continue;
                bool required = set.IsMandatory || set.Min > 0;
                int need = Mathf.Max(set.Min, set.IsMandatory ? 1 : 0);
                var feasible = set.Items.Where(m => !containers.Contains(m.ID) && C(m.ID) < INF).ToList();
                if (feasible.Count == 0) continue;
                foreach (var m in feasible)
                {
                    if (!required && !all && !AvailMatches2(avail, m.ID)) continue;     // topping nobody can order yet
                    var node = Plan(m.ID, new HashSet<int>(), 0, noLoose: true);
                    if (node == null || node.Kind == NK.Fetch || node.Kind == NK.Pickup || node.Kind == NK.Split) continue;
                    if (Closure(node).Overlaps(containers)) continue;                  // never involve the plate
                    bool alternative = required && feasible.Count > need;              // e.g. rare / medium / well-done
                    res.Add(new PrepT { item = m.ID, want = alternative ? 1 : 2, why = alternative ? "alternative" : (required ? "required" : "topping"), extras = new List<int>() });
                }
            }
            return res;
        }

        float lastPrepLog;

        IEnumerable<int> DoWash(Entity source, bool alreadyHolding, bool allowRack = false)
        {
            Snapshot();
            if (!alreadyHolding)
            {
                foreach (var _ in TakeFrom(source, 0)) yield return 0;
                if (!R) yield break;
            }
            int dirty = ItemId(MyHeld());
            if (allowRack)
            {
                // dirty plates go into a rack / soaking sink first (if there is one with room)
                var rack = FindRackFor();
                if (rack != null)
                {
                    foreach (var _ in PutOn(rack.E)) yield return 0;
                    if (R) { Status = "Plate put in the " + rack.Name; yield break; }
                    rackBadUntil[rack.E] = Now + 120f;
                    Log(cur?.Name + ": the " + rack.Name + " would not take the plate, washing it directly");
                    if (MyHeld() == Entity.Null) yield break;
                }
            }
            Appl sink = null; float bd = 1e9f; bool auto = false;
            foreach (var a in Appls)
            {
                if (!a.HasHolder || a.IsProvider && !a.IsSink || a.Held != Entity.Null || a.Def == null) continue;
                if (!GameData.Main.ProcessesView.GetRelevantProcess(dirty, a.Id, out var pair) || pair.IsBad) continue;
                if (!Reachable(a)) continue;
                float d = (a.Pos - MyPos).sqrMagnitude;
                if (d < bd) { bd = d; sink = a; auto = pair.IsAutomatic; }
            }
            if (sink == null) { Log("no free sink for " + Dump.Name(dirty)); foreach (var _ in DropHeld()) yield return 0; yield break; }
            var sinkE = sink.E;
            foreach (var _ in PutOn(sinkE)) yield return 0;
            if (!R) yield break;
            if (auto) { foreach (var _ in WaitFor(() => HeldIdOf(sinkE) != dirty, 60f)) yield return 0; }
            else { foreach (var _ in Work(sinkE, () => HeldIdOf(sinkE) != dirty, 60f)) yield return 0; }
            if (!R) yield break;
            foreach (var _ in TakeFrom(sinkE, 0)) yield return 0;
            if (!R) yield break;
            Log("washed -> " + Dump.DescribeItem(EntityManager, MyHeld()));
            foreach (var _ in DropHeld(preferProvider: true)) yield return 0;
        }

        /// <summary>Get rid of whatever we hold: back into a provider (plates), else onto a free counter, else the bin.</summary>
        IEnumerable<int> DropHeld(bool preferProvider = false)
        {
            var held = MyHeld();
            if (held == Entity.Null) { R = true; yield break; }
            Snapshot();
            int id = ItemId(held);
            BuildIndexes();
            if (dirtyIds.Contains(id)) { foreach (var _ in DoWash(Entity.Null, true)) yield return 0; yield break; }
            if (preferProvider)
                foreach (var a in Appls)
                    if (a.IsProvider && a.Prov.ProvidedItem == id && a.Prov.Available < a.Prov.Maximum && Reachable(a))
                    {
                        foreach (var _ in PutOn(a.E)) yield return 0;
                        if (R) yield break;
                        break;
                    }
            var stash = FreeStash(MyPos);
            if (stash != Entity.Null)
            {
                foreach (var _ in PutOn(stash)) yield return 0;
                if (R) yield break;
            }
            foreach (var a in Appls)
                if (a.IsBin && Reachable(a))
                {
                    foreach (var _ in PutOn(a.E)) yield return 0;
                    yield break;
                }
            R = false;
        }

        // ====================================================== main loop
        IEnumerable<int> BrainFor(BotRole role)
        {
            bool doCook = role == BotRole.Cook || role == BotRole.All;
            bool doServe = role == BotRole.Server || role == BotRole.All;
            bool doWashJob = role == BotRole.Dishwasher || role == BotRole.All;
            Log(cur.Name + " started; pos=" + MyPos.ToString("F2") + " tiles=" + nav.TileCount);
            BuildIndexes();
            float lastIdleLog = 0, lastPrepCheck = 0, lastBinCheck = 0, lastMopCheck = 0;
            while (true)
            {
                Snapshot();
                Reserved.Clear();

                if (MyHeld() != Entity.Null)
                {
                    Log(cur.Name + " holding " + Dump.DescribeItem(EntityManager, MyHeld()) + " -> dropping");
                    foreach (var _ in DropHeld()) yield return 0;
                    if (MyHeld() != Entity.Null) foreach (var _ in Pause(2f)) yield return 0;
                    continue;
                }

                // safety net: never wander around with a tray equipped unless a tray round is running
                if (HoldsTray())
                {
                    foreach (var _ in ReleaseTray()) yield return 0;
                    if (HoldsTray()) foreach (var _ in Pause(2f)) yield return 0;
                    continue;
                }

                // a mop in the tool slot means no carrying: put it down when there is plate work (or this bot does not mop)
                if (HoldsMop() && (!(doWashJob && role == BotRole.Dishwasher) || !cfg.MopFloors || PlateWorkPending()))
                {
                    foreach (var _ in ReleaseMop()) yield return 0;
                    if (HoldsMop()) foreach (var _ in Pause(2f)) yield return 0;
                    continue;
                }

                // customer orders (servers plate and serve from what the cooks stocked)
                if (doServe)
                {
                    Order? pick = null;
                    foreach (var o in PendingOrders())
                    {
                        if (failUntil.TryGetValue(o.Key, out float until) && Now < until) continue;
                        if (ClaimedByOther(OrderKeyOf(o))) continue;
                        pick = o; break;
                    }
                    if (pick.HasValue)
                    {
                        string key = OrderKeyOf(pick.Value);
                        Claim(key);
                        Status = "Serving " + Dump.Name(pick.Value.ItemId);
                        try { foreach (var _ in DoOrder(pick.Value)) yield return 0; }
                        finally { Release(key); }
                        foreach (var _ in Pause(0.1f)) yield return 0;
                        continue;
                    }
                }

                // dirty dishes
                if (doWashJob)
                {
                    // 1) plates that finished cleaning in a rack / soak sink
                    var fin = FindFinishedClean();
                    if (fin != Entity.Null && !ClaimedByOther("clean:" + fin.Index))
                    {
                        string ck = "clean:" + fin.Index;
                        Claim(ck);
                        Status = "Collecting clean plates";
                        try
                        {
                            foreach (var _ in TakeFrom(fin, 0)) yield return 0;
                            if (R) foreach (var _ in DropHeld(preferProvider: true)) yield return 0;
                        }
                        finally { Release(ck); }
                        continue;
                    }

                    // 1b) two or more dirty plates and a tray stand: carry two per trip
                    if (cfg.UseTray && Now > trayDisabledUntil && MyHeld() == Entity.Null && CurrentTool() == Entity.Null)
                    {
                        Snapshot();
                        if (FindTrayStand() != null && DirtyPlateCount() >= 2 && RackRoomTotal() >= 2)
                        {
                            Status = "Carrying dirty plates on a tray";
                            foreach (var _ in DoTrayRound()) yield return 0;
                            continue;
                        }
                    }

                    // 2) dirty plates from tables etc. go to a rack first; 3) only then wash what is waiting in the racks
                    int dirtyId;
                    var dirty = FindDirty(out dirtyId, includeRacks: false);
                    bool fromRack = false;
                    if (dirty == Entity.Null) { dirty = FindDirty(out dirtyId, includeRacks: true); fromRack = dirty != Entity.Null; }
                    if (dirty != Entity.Null && !ClaimedByOther("wash:" + dirty.Index))
                    {
                        string wkey = "wash:" + dirty.Index;
                        Claim(wkey);
                        Status = fromRack ? "Washing plates from the rack" : "Collecting dirty plates";
                        try { foreach (var _ in DoWash(dirty, false, allowRack: !fromRack)) yield return 0; }
                        finally { Release(wkey); }
                        if (!R) foreach (var _ in Pause(2f)) yield return 0;
                        continue;
                    }
                }

                // an idle dishwasher mops the floors
                if (role == BotRole.Dishwasher && cfg.MopFloors && Now > mopDisabledUntil && MyHeld() == Entity.Null && Now - lastMopCheck > 1.5f)
                {
                    lastMopCheck = Now;
                    Snapshot();
                    if (!PlateWorkPending())
                    {
                        var mess = FindMess();
                        if (mess != Entity.Null && (HoldsMop() || (CurrentTool() == Entity.Null && FindMopStand() != null)))
                        {
                            Status = "Mopping the floor";
                            foreach (var _ in DoMop(mess)) yield return 0;
                            continue;
                        }
                    }
                }

                // a full bin cannot take rubbish: empty it (the bag goes to the outside bin)
                if ((doCook || doWashJob) && cfg.EmptyBins && MyHeld() == Entity.Null && CurrentTool() == Entity.Null && Now - lastBinCheck > 3f)
                {
                    lastBinCheck = Now;
                    Snapshot();
                    var fb = FindFullBin();
                    if (fb != null && !ClaimedByOther("bin:" + fb.E.Index))
                    {
                        string bk = "bin:" + fb.E.Index;
                        Claim(bk);
                        Status = "Emptying the bin";
                        try { foreach (var _ in EmptyBin(fb.E)) yield return 0; }
                        finally { Release(bk); }
                        continue;
                    }
                }

                // cooks pick up the sharp knife (chopping x3) from its provider, once per day
                if (doCook && cfg.UseKnife && Now > knifeDisabledUntil && CurrentTool() == Entity.Null && MyHeld() == Entity.Null && MayTakeKnife())
                {
                    Snapshot();
                    if (FindKnifeStand() != null)
                    {
                        Status = "Picking up the sharp knife";
                        foreach (var _ in EnsureKnife()) yield return 0;
                        continue;
                    }
                }

                // prepare ahead (checked every few seconds only - planning is not free)
                if (doCook && Now - lastPrepCheck > 3f)
                {
                    lastPrepCheck = Now;
                    foreach (var _ in DoPrep()) yield return 0;
                    if (R) { lastPrepCheck = 0; continue; }
                }

                if (!doCook) Status = doServe ? "Waiting for orders" : (doWashJob && LastDirtyOutOfReach > 0 ? LastDirtyOutOfReach + " place(s) with dirty plates are out of reach" : "Waiting for dirty plates");
                if (Now - lastIdleLog > 20f) { lastIdleLog = Now; Log(cur.Name + " idle - " + Status); }
                foreach (var _ in Pause(0.5f)) yield return 0;
            }
        }
    }
}
