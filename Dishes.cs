using System;
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using KitchenData;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    /// <summary>Dishwasher helpers: rack-style appliances, and finished (clean) plates waiting in them.</summary>
    public partial class ChefBotSystem
    {
        readonly Dictionary<Entity, float> rackBadUntil = new Dictionary<Entity, float>();
        HashSet<int> cleanIdsCache;

        /// <summary>True for appliances the dishwasher should use as a rack (name contains a word from dish_rack_names).</summary>
        bool IsRackName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf("Shoe", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (var w in (cfg.DishRackNames ?? "").Split(','))
            {
                var k = w.Trim();
                if (k.Length > 0 && name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>A rack-style place to stage dirty plates: the Dirty Plate Stack, or an appliance matching dish_rack_names.</summary>
        bool IsRackAppl(Appl a) { return a.IsDirtyStack || IsRackName(a.Name); }

        /// <summary>Chairs, ghosts and mop water do not stop a walking player, so they must not block the bots' paths.</summary>
        bool IsBlockingOccupant(Vector3 p)
        {
            var occ = TileManager.GetOccupant(p);
            if (occ == Entity.Null) return false;
            var em = EntityManager;
            return !(em.HasComponent<CApplianceChair>(occ) || em.HasComponent<CApplianceGhostChair>(occ) || em.HasComponent<CApplianceGhost>(occ) || em.HasComponent<CDoesNotOccupy>(occ));
        }

        bool RackHasRoom(Appl a)
        {
            if (a.IsDirtyStack) return a.Prov.Available < a.Prov.Maximum;
            var em = EntityManager;
            if (a.HasStorage && em.HasComponent<CItemStored>(a.E) && em.HasComponent<CItemStorage>(a.E))
                return em.GetBuffer<CItemStored>(a.E).Length < em.GetComponentData<CItemStorage>(a.E).Capacity;
            return a.Held == Entity.Null;
        }

        /// <summary>The nearest rack-style appliance with room for a dirty plate.</summary>
        Appl FindRackFor()
        {
            Appl best = null; float bd = 1e9f;
            foreach (var a in Appls)
            {
                if ((!a.HasHolder && !a.IsDirtyStack) || a.IsTable || a.IsBin || !IsRackAppl(a)) continue;
                if (rackBadUntil.TryGetValue(a.E, out float until) && Now < until) continue;
                if (Reserved.Contains(a.E) || !RackHasRoom(a) || !Reachable(a)) continue;
                float d = (a.Pos - MyPos).sqrMagnitude - (a.IsDirtyStack ? 1000f : 0f);   // the dirty plate stack comes first
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }

        /// <summary>Item ids a dirty dish turns into when cleaned (e.g. Plate - Dirty -> Plate).</summary>
        HashSet<int> CleanIds()
        {
            if (cleanIdsCache != null) return cleanIdsCache;
            BuildIndexes();
            var res = new HashSet<int>();
            var clean = Data.Get<Process>().FirstOrDefault(p => p.name == "Clean");
            if (clean != null)
                foreach (int d in dirtyIds)
                    if (GameData.Main.ProcessesView.GetResultOfProcess(d, clean.ID, out int r) && r != 0) res.Add(r);
            cleanIdsCache = res;
            return res;
        }

        /// <summary>A rack / soak sink that now holds a finished clean plate.</summary>
        Entity FindFinishedClean()
        {
            var ids = CleanIds();
            foreach (var a in Appls)
                if (a.HasHolder && a.HeldId != 0 && IsRackName(a.Name) && ids.Contains(a.HeldId) && Reachable(a)) return a.E;
            return Entity.Null;
        }
    }
}
