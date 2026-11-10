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
    /// Trays: the Tray Stand provides a "Tray" item that is an equipped tool (not held in the hand). While it is equipped,
    /// grabbed items go into its 2-slot storage and each grab at a counter/rack drops one back out.
    /// Only the dishwasher uses it: it collects two dirty plates per trip.
    /// </summary>
    public partial class ChefBotSystem
    {
        HashSet<int> trayIds;
        float trayDisabledUntil;

        HashSet<int> TrayIds()
        {
            if (trayIds != null) return trayIds;
            BuildIndexes();
            var set = new HashSet<int>();
            foreach (var it in allItems)
            {
                if (it.Properties == null) continue;
                bool storage = false, equip = false, noTools = false;
                foreach (var p in it.Properties)
                {
                    if (p is CToolStorage ts && ts.Capacity >= 2) storage = true;
                    else if (p is CEquippableTool) equip = true;
                    else if (p is CToolStorageNoTools) noTools = true;
                }
                if (storage && equip && !noTools) set.Add(it.ID);
            }
            trayIds = set;
            return set;
        }

        // ---------- what the bot is carrying ----------
        Entity CurrentTool()
        {
            if (!EntityManager.HasComponent<CToolUser>(Me)) return Entity.Null;
            var t = EntityManager.GetComponentData<CToolUser>(Me).CurrentTool;
            return t != Entity.Null && EntityManager.Exists(t) ? t : Entity.Null;
        }

        bool HoldsTray()
        {
            var t = CurrentTool();
            return t != Entity.Null && EntityManager.HasComponent<CItem>(t) && TrayIds().Contains(EntityManager.GetComponentData<CItem>(t).ID);
        }

        int ToolStoredCount()
        {
            var t = CurrentTool();
            return t != Entity.Null && EntityManager.HasComponent<CItemStored>(t) ? EntityManager.GetBuffer<CItemStored>(t).Length : 0;
        }

        // ---------- what is around ----------
        Appl FindTrayStand()
        {
            var ids = TrayIds();
            foreach (var a in Appls)
                if (a.IsProvider && ids.Contains(a.Prov.ProvidedItem != 0 ? a.Prov.ProvidedItem : a.Prov.DefaultProvidedItem) && (a.Prov.Available > 0 || ids.Contains(a.HeldId)) && Reachable(a))
                    return a;
            return null;
        }

        /// <summary>Dirty plates lying on tables / counters (not in racks) that the bot can reach.</summary>
        int DirtyPlateCount()
        {
            BuildIndexes();
            int n = 0;
            var seen = new HashSet<Entity>();
            foreach (var a in Appls)
            {
                if (a.IsSink || a.IsBin || !a.HasHolder || IsRackAppl(a) || !Reachable(a)) continue;
                // the plate on top of a table is its held item, the others are in the storage list: count both once
                if (a.Held != Entity.Null && a.HeldId != 0 && dirtyIds.Contains(a.HeldId) && seen.Add(a.Held)) n++;
                if (a.HasStorage && EntityManager.HasComponent<CItemStored>(a.E))
                {
                    var buf = EntityManager.GetBuffer<CItemStored>(a.E);
                    for (int i = 0; i < buf.Length; i++)
                    {
                        var it = buf[i].StoredItem;
                        if (it != Entity.Null && EntityManager.Exists(it) && EntityManager.HasComponent<CItem>(it) && dirtyIds.Contains(EntityManager.GetComponentData<CItem>(it).ID) && seen.Add(it)) n++;
                    }
                }
            }
            return n;
        }

        /// <summary>How many more dirty plates the racks can take right now.</summary>
        int RackRoomTotal()
        {
            int room = 0;
            foreach (var a in Appls)
            {
                if ((!a.HasHolder && !a.IsDirtyStack) || a.IsTable || a.IsBin || !IsRackAppl(a)) continue;
                if (rackBadUntil.TryGetValue(a.E, out float until) && Now < until) continue;
                if (!Reachable(a)) continue;
                if (a.IsDirtyStack) room += Mathf.Max(0, a.Prov.Maximum - a.Prov.Available);
                else if (RackHasRoom(a)) room += 1;
            }
            return room;
        }

        // ---------- primitives ----------
        /// <summary>Press grab at a target until counter() changes. R = it changed.</summary>
        IEnumerable<int> GrabUntilChanged(Entity target, Func<int> counter)
        {
            int before = counter();
            for (int i = 0; i < 3; i++) { body.MoveWorld = TowardsDir(target); body.Grab = true; yield return 0; }
            for (int i = 0; i < 16; i++)
            {
                body.MoveWorld = TowardsDir(target);
                yield return 0;
                if (counter() != before) break;
            }
            for (int i = 0; i < 3; i++) { body.MoveWorld = TowardsDir(target); yield return 0; }
            R = counter() != before;
        }

        /// <summary>Press the act button at a target until counter() changes (used to put the tray down).</summary>
        IEnumerable<int> ActUntilChanged(Entity target, Func<int> counter)
        {
            int before = counter();
            for (int i = 0; i < 3; i++) { body.MoveWorld = TowardsDir(target); body.Act = true; yield return 0; }
            for (int i = 0; i < 16; i++)
            {
                body.MoveWorld = TowardsDir(target);
                yield return 0;
                if (counter() != before) break;
            }
            for (int i = 0; i < 3; i++) { body.MoveWorld = TowardsDir(target); yield return 0; }
            R = counter() != before;
        }

        // ---------- the tray round ----------
        /// <summary>Pick up the tray, collect up to two dirty plates, deliver them to a rack, put the tray back.</summary>
        IEnumerable<int> DoTrayRound()
        {
            R = false;
            Snapshot();
            var stand = FindTrayStand();
            if (stand == null || MyHeld() != Entity.Null) yield break;
            string who = cur != null ? cur.Name : "bot";

            // 1) equip the tray (hands must be empty)
            foreach (var _ in Go(stand.E)) yield return 0;
            if (!R) { trayDisabledUntil = Now + 120f; yield break; }
            foreach (var _ in GrabUntilChanged(stand.E, () => HoldsTray() ? 1 : 0)) yield return 0;
            if (!HoldsTray()) { Log(who + ": could not pick up the tray, carrying plates by hand from now on for a while"); trayDisabledUntil = Now + 300f; yield break; }
            Log(who + " picked up a tray");

            // 2) collect up to two dirty plates
            for (int k = 0; k < 2; k++)
            {
                Snapshot();
                int did;
                var dirty = FindDirty(out did, includeRacks: false);
                if (dirty == Entity.Null) break;
                foreach (var _ in Go(dirty)) yield return 0;
                if (!R) break;
                foreach (var _ in GrabUntilChanged(dirty, ToolStoredCountFn)) yield return 0;
                if (!R) { Log(who + ": the tray would not take a plate from " + ApplianceName(dirty)); break; }
            }

            // 3) deliver what is on the tray: rack first, otherwise a free counter (the normal dishwashing then finds them)
            int guard = 0;
            while (ToolStoredCount() > 0 && guard++ < 4)
            {
                Snapshot();
                Entity target = Entity.Null;
                var rack = FindRackFor();
                if (rack != null) target = rack.E;
                else target = FindFreeCounterNear(MyPos);
                if (target == Entity.Null) break;
                foreach (var _ in Go(target)) yield return 0;
                if (!R) break;
                foreach (var _ in GrabUntilChanged(target, ToolStoredCountFn)) yield return 0;
                if (!R) { if (rack != null) rackBadUntil[rack.E] = Now + 120f; break; }
            }

            // 4) put the tray back so normal one-at-a-time work is possible again
            foreach (var _ in ReleaseTray()) yield return 0;
            R = !HoldsTray();
        }

        int ToolStoredCountFn() { return ToolStoredCount(); }
        int HoldsTrayFn() { return HoldsTray() ? 1 : 0; }

        Entity FindFreeCounterNear(Vector3 pos)
        {
            return FreeStash(pos);
        }

        /// <summary>Empty the tray (if anything is on it) and put it down on the tray stand or a free counter.</summary>
        IEnumerable<int> ReleaseTray()
        {
            R = true;
            if (!HoldsTray()) yield break;
            for (int attempt = 0; attempt < 3 && HoldsTray(); attempt++)
            {
                Snapshot();
                // anything still on the tray goes onto a free counter first
                if (ToolStoredCount() > 0)
                {
                    var c = FreeStash(MyPos);
                    if (c != Entity.Null)
                    {
                        foreach (var _ in Go(c)) yield return 0;
                        if (R) foreach (var _ in GrabUntilChanged(c, ToolStoredCountFn)) yield return 0;
                    }
                    continue;
                }
                // tray stand if there is one, else a free counter
                Entity spot = Entity.Null;
                foreach (var a in Appls)
                    if (a.IsProvider && TrayIds().Contains(a.Prov.ProvidedItem != 0 ? a.Prov.ProvidedItem : a.Prov.DefaultProvidedItem) && Reachable(a)) { spot = a.E; break; }
                if (spot == Entity.Null) spot = FreeStash(MyPos);
                if (spot == Entity.Null) break;
                foreach (var _ in Go(spot)) yield return 0;
                if (!R) continue;
                foreach (var _ in ActUntilChanged(spot, HoldsTrayFn)) yield return 0;
            }
            if (HoldsTray())
            {
                Log((cur != null ? cur.Name : "bot") + ": could not put the tray down; will keep trying");
                trayDisabledUntil = Now + 300f;
            }
            R = !HoldsTray();
        }
    }
}
