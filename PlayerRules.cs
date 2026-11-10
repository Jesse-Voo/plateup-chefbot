using System;
using System.Collections.Generic;
using System.Reflection;
using Kitchen;
using KitchenMods;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    /// <summary>Marks a player entity as a ChefBot so the game's difficulty maths can ignore it.</summary>
    public struct CChefBotPlayer : IModComponent { }

    /// <summary>Rules about player counts: the player limit and "bots do not count as players".</summary>
    public partial class ChefBotSystem
    {
        readonly HashSet<object> patchedSystems = new HashSet<object>();
        float lastIndexFix;
        const int VanillaMaxPlayers = 4;

        /// <summary>Called every frame: keeps the player limit raised and the difficulty counters bot-blind.</summary>
        void PlayerRules()
        {
            if (!cfgLoaded) return;
            ApplyPlayerLimit();
            PatchPlayerCountQueries();
            if (Now - lastIndexFix > 1f) { lastIndexFix = Now; FixPlayerIndices(); }
        }

        /// <summary>PlayerManager.MaxPlayers is a readonly field (4); change it with reflection. Network lobbies may have their own cap.</summary>
        void ApplyPlayerLimit()
        {
            var pm = World.GetExistingSystem<PlayerManager>();
            if (pm == null) return;
            int want = Mathf.Clamp(cfg.MaxPlayers, VanillaMaxPlayers, 16);
            if (pm.MaxPlayers == want) return;
            var f = typeof(PlayerManager).GetField("MaxPlayers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) { Log("cannot change the player limit: field not found"); return; }
            f.SetValue(pm, want);
            Log("player limit set to " + want + " (game default is 4)");
        }

        /// <summary>
        /// Spawn points exist for player indices 0-3 only. A fifth+ player gets index % 4 on its entity, so it is placed
        /// like the others (the game's own bookkeeping keeps the real index).
        /// </summary>
        void FixPlayerIndices()
        {
            if (cfg.MaxPlayers <= VanillaMaxPlayers) return;
            var em = EntityManager;
            using (var q = em.CreateEntityQuery(typeof(CPlayer)))
            using (var ents = q.ToEntityArray(Unity.Collections.Allocator.TempJob))
                foreach (var e in ents)
                {
                    var p = em.GetComponentData<CPlayer>(e);
                    if (p.Index >= VanillaMaxPlayers) { p.Index %= VanillaMaxPlayers; em.SetComponentData(e, p); }
                }
        }

        /// <summary>
        /// The game scales customers per day (and money, queue patience, fire spread) by how many CPlayer entities exist.
        /// Replace those systems' player queries with ones that skip entities carrying CChefBotPlayer.
        /// </summary>
        void PatchPlayerCountQueries()
        {
            if (cfg.BotsCountAsPlayers) return;
            PatchQuery<CreateCustomerSchedule>(excludeJoining: true);   // customers per day
            PatchQuery<CreateEndOfDayPopup>(false);                      // money reward per player count
            PatchQuery<UpdateQueuePatience>(false);                      // queue patience per player count
            PatchQuery<SpreadFire>(false);                               // fire spread per player count
        }

        void PatchQuery<T>(bool excludeJoining) where T : ComponentSystemBase
        {
            var sys = World.GetExistingSystem<T>();
            if (sys == null || patchedSystems.Contains(sys)) return;
            patchedSystems.Add(sys);
            var f = typeof(T).GetField("Players", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null || f.FieldType != typeof(EntityQuery)) { Log("cannot patch the player count of " + typeof(T).Name); return; }
            var none = excludeJoining ? new ComponentType[] { typeof(CJoiningPlayer), typeof(CChefBotPlayer) } : new ComponentType[] { typeof(CChefBotPlayer) };
            var q = EntityManager.CreateEntityQuery(new EntityQueryDesc { All = new ComponentType[] { typeof(CPlayer) }, None = none });
            f.SetValue(sys, q);
            Log("bots no longer count as players in " + typeof(T).Name);
        }

        void TagAsBot(Entity bot)
        {
            if (cfg.BotsCountAsPlayers || bot == Entity.Null) return;
            if (!EntityManager.HasComponent<CChefBotPlayer>(bot)) EntityManager.AddComponent<CChefBotPlayer>(bot);
        }
    }
}
