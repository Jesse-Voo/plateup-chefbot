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
    /// Equippable tools: the Sharp Knife (chopping x3) from the Sharp Knife Provider. A knife lets the player keep holding
    /// items normally, so cooks equip it once and keep it (tools are removed at night, so it is picked up again each day).
    /// </summary>
    public partial class ChefBotSystem
    {
        HashSet<int> knifeIds;
        float knifeDisabledUntil;

        HashSet<int> KnifeIds()
        {
            if (knifeIds != null) return knifeIds;
            BuildIndexes();
            var set = new HashSet<int>();
            int chop = GameData.Main.ReferableObjects.ChopProcess != null ? GameData.Main.ReferableObjects.ChopProcess.ID : 0;
            foreach (var it in allItems)
            {
                if (it.Properties == null) continue;
                bool equip = false, chops = false;
                foreach (var p in it.Properties)
                {
                    if (p is CEquippableTool) equip = true;
                    else if (p is CProcessTool pt && pt.Process == chop && pt.Factor > 1f) chops = true;
                }
                if (equip && chops) set.Add(it.ID);
            }
            knifeIds = set;
            return set;
        }

        bool AgentHoldsKnife(BotAgent o)
        {
            if (o.Me == Entity.Null || !EntityManager.Exists(o.Me) || !EntityManager.HasComponent<CToolUser>(o.Me)) return false;
            var t = EntityManager.GetComponentData<CToolUser>(o.Me).CurrentTool;
            return t != Entity.Null && EntityManager.Exists(t) && EntityManager.HasComponent<CItem>(t) && KnifeIds().Contains(EntityManager.GetComponentData<CItem>(t).ID);
        }

        /// <summary>
        /// There is usually one knife. The bot with the highest knife priority (ties: the first bot) gets it; the others do not
        /// walk to the provider while a higher-priority cook is active or someone already holds the knife.
        /// </summary>
        bool MayTakeKnife()
        {
            if (cur == null || cur.Spec.Knife <= 0) return false;
            foreach (var o in agents)
            {
                if (o == cur) continue;
                if (AgentHoldsKnife(o)) return false;
                bool cookLike = o.Role == BotRole.Cook || o.Role == BotRole.All;
                if (!cookLike || o.Spec.Knife <= 0 || o.Brain == null) continue;
                if (o.Spec.Knife > cur.Spec.Knife || (o.Spec.Knife == cur.Spec.Knife && o.Index < cur.Index)) return false;
            }
            return true;
        }

        bool HoldsKnife()
        {
            var t = CurrentTool();
            return t != Entity.Null && EntityManager.HasComponent<CItem>(t) && KnifeIds().Contains(EntityManager.GetComponentData<CItem>(t).ID);
        }

        /// <summary>The Sharp Knife Provider (or anything holding a knife) that the bot can reach.</summary>
        Appl FindKnifeStand()
        {
            var ids = KnifeIds();
            foreach (var a in Appls)
            {
                if (!Reachable(a)) continue;
                if (a.IsProvider && ids.Contains(a.Prov.ProvidedItem != 0 ? a.Prov.ProvidedItem : a.Prov.DefaultProvidedItem) && (a.Prov.Available > 0 || ids.Contains(a.HeldId))) return a;
                if (!a.IsProvider && a.HeldId != 0 && ids.Contains(a.HeldId)) return a;
            }
            return null;
        }

        /// <summary>Pick up the sharp knife (hands and tool slot must be empty).</summary>
        IEnumerable<int> EnsureKnife()
        {
            R = false;
            Snapshot();
            var stand = FindKnifeStand();
            if (stand == null || MyHeld() != Entity.Null || CurrentTool() != Entity.Null) yield break;
            string who = cur != null ? cur.Name : "bot";
            foreach (var _ in Go(stand.E)) yield return 0;
            if (!R) { knifeDisabledUntil = Now + 120f; yield break; }
            for (int attempt = 0; attempt < 3 && !HoldsKnife(); attempt++)
            {
                if (attempt > 0) foreach (var _ in Pause(0.6f)) yield return 0;
                foreach (var _ in GrabUntilChanged(stand.E, () => CurrentTool() != Entity.Null ? 1 : 0)) yield return 0;
                foreach (var _ in Pause(0.3f)) yield return 0;      // the tool slot is filled a frame or two after the grab
            }
            if (HoldsKnife()) { Log(who + " picked up the sharp knife"); R = true; yield break; }
            var snap = AppOf(stand.E);
            Log(who + ": could not pick up the sharp knife. tool slot=" + (CurrentTool() != Entity.Null ? Dump.DescribeItem(EntityManager, CurrentTool()) : "empty")
                + ", hand=" + (MyHeld() != Entity.Null ? Dump.DescribeItem(EntityManager, MyHeld()) : "empty")
                + ", stand " + (snap != null ? snap.Name + " available=" + snap.Prov.Available + "/" + snap.Prov.Maximum + " providing=" + Dump.Name(snap.Prov.ProvidedItem) + " holds=" + Dump.Name(snap.HeldId) : "gone")
                + ", aimed at " + (AttemptTarget() == stand.E ? "the stand" : ApplianceName(AttemptTarget())));
            knifeDisabledUntil = Now + 300f;
            R = false;
        }
    }
}
