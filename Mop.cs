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
    /// <summary>
    /// Floor cleaning: a Mop Bucket provides a Mop (an equipped tool that stops the player carrying items). With it equipped,
    /// holding the interact button on a mess cleans it. The idle dishwasher does this, and puts the mop down as soon as there
    /// is plate work again.
    /// </summary>
    public partial class ChefBotSystem
    {
        HashSet<int> mopIds;
        float mopDisabledUntil;
        readonly Dictionary<Entity, float> messBadUntil = new Dictionary<Entity, float>();

        HashSet<int> MopIds()
        {
            if (mopIds != null) return mopIds;
            BuildIndexes();
            var set = new HashSet<int>();
            foreach (var it in allItems)
            {
                if (it.Properties == null) continue;
                bool clean = false, equip = false;
                foreach (var p in it.Properties)
                {
                    if (p is CToolClean) clean = true;
                    else if (p is CEquippableTool) equip = true;
                }
                if (clean && equip) set.Add(it.ID);
            }
            mopIds = set;
            return set;
        }

        bool HoldsMop()
        {
            var t = CurrentTool();
            return t != Entity.Null && EntityManager.HasComponent<CItem>(t) && MopIds().Contains(EntityManager.GetComponentData<CItem>(t).ID);
        }

        int HoldsMopFn() { return HoldsMop() ? 1 : 0; }

        Appl FindMopStand()
        {
            var ids = MopIds();
            foreach (var a in Appls)
                if (a.IsProvider && ids.Contains(a.Prov.ProvidedItem != 0 ? a.Prov.ProvidedItem : a.Prov.DefaultProvidedItem) && (a.Prov.Available > 0 || ids.Contains(a.HeldId)) && Reachable(a))
                    return a;
            return null;
        }

        /// <summary>The nearest mess on the floor that is not marked as a problem.</summary>
        Entity FindMess()
        {
            var em = EntityManager;
            Entity best = Entity.Null; float bd = 1e9f;
            using (var q = em.CreateEntityQuery(typeof(CMess), typeof(CPosition)))
            using (var ents = q.ToEntityArray(Allocator.TempJob))
                foreach (var e in ents)
                {
                    if (messBadUntil.TryGetValue(e, out float until) && Now < until) continue;
                    var pos = em.GetComponentData<CPosition>(e).Position;
                    if (!nav.Walkable(Nav.Cell(pos))) continue;
                    float d = (pos - MyPos).sqrMagnitude;
                    if (d < bd) { bd = d; best = e; }
                }
            return best;
        }

        /// <summary>Plate work waiting? (then the mop has to be put down first.)</summary>
        bool PlateWorkPending()
        {
            int id;
            return FindFinishedClean() != Entity.Null || FindDirty(out id, includeRacks: true) != Entity.Null;
        }

        /// <summary>Walk onto the tile of something lying on the floor.</summary>
        IEnumerable<int> WalkOnto(Vector3 target, float timeout)
        {
            R = false;
            float t0 = Now, lastPath = -10f, lastMoved = Now;
            var goals = new HashSet<Vector2Int> { Nav.Cell(target) };
            List<Vector3> path = null;
            Vector3 lastPos = MyPos;
            while (Now - t0 < timeout)
            {
                var me = MyPos;
                var flat = new Vector2(target.x - me.x, target.z - me.z);
                if (flat.magnitude < 0.3f) { R = true; yield break; }
                if (Now - lastPath > 0.4f) { lastPath = Now; path = nav.Path(me, goals); }
                if (path != null && path.Count > 0)
                {
                    var wp = path[0];
                    var d = new Vector2(wp.x - me.x, wp.z - me.z);
                    if (d.magnitude < 0.2f && path.Count > 1) path.RemoveAt(0);
                    body.MoveWorld = d.sqrMagnitude < 1e-4f ? flat.normalized : d.normalized;
                }
                else body.MoveWorld = flat.normalized;

                if ((me - lastPos).magnitude > 0.05f) { lastPos = me; lastMoved = Now; }
                else if (Now - lastMoved > 1.5f)
                {
                    body.MoveWorld = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)).normalized;
                    yield return 0; yield return 0; yield return 0;
                    lastMoved = Now; lastPath = -10f;
                }
                yield return 0;
            }
        }

        /// <summary>Equip a mop (if needed), walk onto one mess and hold interact until it is gone.</summary>
        IEnumerable<int> DoMop(Entity mess)
        {
            R = false;
            string who = cur != null ? cur.Name : "bot";
            if (!HoldsMop())
            {
                Snapshot();
                var stand = FindMopStand();
                if (stand == null || MyHeld() != Entity.Null || CurrentTool() != Entity.Null) { mopDisabledUntil = Now + 60f; yield break; }
                foreach (var _ in Go(stand.E)) yield return 0;
                if (!R) { mopDisabledUntil = Now + 60f; yield break; }
                for (int attempt = 0; attempt < 3 && !HoldsMop(); attempt++)
                {
                    if (attempt > 0) foreach (var _ in Pause(0.5f)) yield return 0;
                    foreach (var _ in GrabUntilChanged(stand.E, HoldsMopFn)) yield return 0;
                    foreach (var _ in Pause(0.3f)) yield return 0;
                }
                if (!HoldsMop())
                {
                    Log(who + ": could not pick up the mop (tool slot " + (CurrentTool() != Entity.Null ? "busy" : "empty") + ")");
                    mopDisabledUntil = Now + 300f;
                    yield break;
                }
                Log(who + " picked up the mop");
            }

            if (!EntityManager.Exists(mess)) { R = true; yield break; }
            var pos = EntityManager.GetComponentData<CPosition>(mess).Position;
            foreach (var _ in WalkOnto(pos, 25f)) yield return 0;
            if (!R) { messBadUntil[mess] = Now + 60f; Log(who + ": could not reach a mess at " + pos.ToString("F1")); yield break; }

            float t0 = Now;
            while (Now - t0 < 15f && EntityManager.Exists(mess))
            {
                var me = MyPos;
                var off = new Vector2(pos.x - me.x, pos.z - me.z);
                if (off.magnitude > 0.4f) { body.MoveWorld = off.normalized; }           // drifted off: step back onto it
                else { body.MoveWorld = Vector2.zero; body.Act = true; }                // standing on it: hold interact
                yield return 0;
            }
            R = !EntityManager.Exists(mess);
            if (!R) { messBadUntil[mess] = Now + 45f; Log(who + ": the mess at " + pos.ToString("F1") + " did not go away"); }
            else Status = "Mopped the floor";
        }

        /// <summary>Put the mop back on its bucket (or a free counter) so the hands can carry things again.</summary>
        IEnumerable<int> ReleaseMop()
        {
            R = true;
            for (int attempt = 0; attempt < 3 && HoldsMop(); attempt++)
            {
                Snapshot();
                Entity spot = Entity.Null;
                foreach (var a in Appls)
                    if (a.IsProvider && MopIds().Contains(a.Prov.ProvidedItem != 0 ? a.Prov.ProvidedItem : a.Prov.DefaultProvidedItem) && Reachable(a)) { spot = a.E; break; }
                if (spot == Entity.Null) spot = FreeStash(MyPos);
                if (spot == Entity.Null) break;
                foreach (var _ in Go(spot)) yield return 0;
                if (!R) continue;
                foreach (var _ in ActUntilChanged(spot, HoldsMopFn)) yield return 0;
            }
            if (HoldsMop()) { Log((cur != null ? cur.Name : "bot") + ": could not put the mop down"); mopDisabledUntil = Now + 300f; }
            R = !HoldsMop();
        }
    }
}
