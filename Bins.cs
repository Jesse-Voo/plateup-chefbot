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
    /// Bins: an inside bin takes only a few items (capacity), then refuses more. Grabbing at a bin with empty hands takes
    /// out a Bin Bag, which goes into the outside (wheelie) bin. So throwing junk away can mean emptying a bin first.
    /// </summary>
    public partial class ChefBotSystem
    {
        bool BinHasRoom(Appl a)
        {
            if (!EntityManager.HasComponent<CApplianceBin>(a.E)) return false;
            var b = EntityManager.GetComponentData<CApplianceBin>(a.E);
            return b.Capacity <= 0 || b.CurrentAmount < b.Capacity;
        }

        bool BinIsFull(Appl a)
        {
            if (!EntityManager.HasComponent<CApplianceBin>(a.E)) return false;
            var b = EntityManager.GetComponentData<CApplianceBin>(a.E);
            return b.Capacity > 0 && b.CurrentAmount >= b.Capacity && b.EmptyBinItem != 0;
        }

        /// <summary>Nearest inside bin (optionally only one that still has room). Reachability is relaxed: Go() has its own fallback.</summary>
        Appl BestBin(bool needRoom)
        {
            Appl best = null; float bd = 1e9f;
            foreach (var a in Appls)
            {
                if (!a.IsBin || a.IsExternalBin) continue;
                if (needRoom && !BinHasRoom(a)) continue;
                if (nav.StandingCells(a.Pos, filtered: false).Count == 0) continue;
                float d = (a.Pos - MyPos).sqrMagnitude;
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }

        Appl FindFullBin()
        {
            foreach (var a in Appls)
                if (a.IsBin && !a.IsExternalBin && BinIsFull(a) && nav.StandingCells(a.Pos, filtered: false).Count > 0) return a;
            return null;
        }

        /// <summary>Take the Bin Bag out of a bin (empty hands needed) and put it into the outside bin.</summary>
        IEnumerable<int> EmptyBin(Entity binE)
        {
            R = false;
            if (MyHeld() != Entity.Null) yield break;
            string who = cur != null ? cur.Name : "bot";
            foreach (var _ in Go(binE)) yield return 0;
            if (!R) yield break;
            foreach (var _ in Grab(binE)) yield return 0;                  // empty hands at a bin: out comes the bin bag
            var bag = MyHeld();
            if (bag == Entity.Null) { Log(who + ": could not take the bin bag out of " + ApplianceName(binE)); yield break; }
            Log(who + " took out " + Dump.DescribeItem(EntityManager, bag));

            Snapshot();
            Appl outside = null; float bd = 1e9f;
            foreach (var a in Appls)
                if (a.IsExternalBin) { float d = (a.Pos - MyPos).sqrMagnitude; if (d < bd) { bd = d; outside = a; } }
            if (outside != null)
            {
                foreach (var _ in PutOn(outside.E)) yield return 0;
                if (R) { Log(who + " emptied the bin"); yield break; }
                Log(who + ": the outside bin would not take the bag");
            }
            else Log(who + ": no outside bin found for the bag");
            // could not get rid of the bag: park it on a counter so the hands are free again
            foreach (var _ in DropHeld()) yield return 0;
            R = true;
        }

        /// <summary>Throw whatever is in hand into a bin that has room; empty a full bin first if needed.</summary>
        IEnumerable<int> DropJunk()
        {
            R = false;
            if (MyHeld() == Entity.Null) { R = true; yield break; }
            string who = cur != null ? cur.Name : "bot";
            string what = Dump.DescribeItem(EntityManager, MyHeld());
            for (int attempt = 0; attempt < 2 && MyHeld() != Entity.Null; attempt++)
            {
                Snapshot();
                var bin = BestBin(needRoom: true);
                Log(who + ": looking for a bin for " + what + " - " + string.Join(", ", Appls.Where(x => x.IsBin && !x.IsExternalBin).Select(x => x.Name + "@" + x.Pos.x.ToString("F0") + "," + x.Pos.z.ToString("F0") + (BinHasRoom(x) ? " room" : " FULL"))) + (bin != null ? " -> using " + bin.Name : " -> none with room"));
                if (bin != null)
                {
                    foreach (var _ in PutOn(bin.E)) yield return 0;
                    if (R) { Log(who + " threw away " + what); yield break; }
                    Log(who + ": the " + bin.Name + " would not take " + what);
                    break;
                }
                // every bin is full: park the junk on a counter, empty the nearest bin, then take the junk back and throw it away
                var full = BestBin(needRoom: false);
                var park = FreeStash(MyPos);
                if (full == null || park == Entity.Null) { Log(who + ": no bin to throw " + what + " into"); break; }
                Reserved.Add(park);
                foreach (var _ in PutOn(park)) yield return 0;
                if (!R) { Reserved.Remove(park); break; }
                foreach (var _ in EmptyBin(full.E)) yield return 0;
                foreach (var _ in TakeFrom(park, 0)) yield return 0;
                Reserved.Remove(park);
                if (!R) break;
            }
            if (MyHeld() != Entity.Null)
            {
                Log(who + ": could not throw away " + what + ", leaving it on a counter");
                foreach (var _ in DropHeld()) yield return 0;
            }
            R = MyHeld() == Entity.Null;
        }
    }
}
