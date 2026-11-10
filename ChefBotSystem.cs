using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Kitchen;
using KitchenData;
using KitchenMods;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChefBot
{
    /// <summary>
    /// The ChefBot: joins the game as a real extra player (through the game's own PlayerManager) and drives that player
    /// with the same InputState a human would produce.  F8 = toggle bot, F9 = dump kitchen state to disk.
    /// </summary>
    public partial class ChefBotSystem : GenericSystemBase, IModSystem
    {
        public const int BotUserId = 0x0B07C4E;            // first bot's user id; bot N uses BotUserId + N
        public static bool IsBotUser(int id) => id >= BotUserId && id < BotUserId + 64;

        EntityQuery playerQ, applianceQ;
        Body body = new Body();
        readonly Nav nav = new Nav();
        bool enabled;
        bool wasEnabled;
        bool dumped;
        float lastClientWarn;
        float navBuiltAt = -100f;
        public static ChefBotSystem Instance;

        protected override void Initialise()
        {
            base.Initialise();
            Instance = this;
            playerQ = GetEntityQuery(typeof(CPlayer));
            applianceQ = GetEntityQuery(typeof(CAppliance), typeof(CPosition));
            Debug.Log("[ChefBot] system initialised. F8 = start/stop bots, F1 = map, F9 = dump kitchen.");
        }

        static float Now => Time_.Now;
        static class Time_ { public static float Now => UnityEngine.Time.realtimeSinceStartup; }

        public static void Log(string s) => Debug.Log("[ChefBot] " + s);

        protected override void OnUpdate()
        {
            try { Tick(); }
            catch (Exception ex) { Debug.LogError("[ChefBot] exception: " + ex); }
        }

        void Tick()
        {
            if (!dumped && GameData.Main != null)
            {
                dumped = true;
                try { Dump.GameDataDump(); } catch (Exception e) { Debug.LogError("[ChefBot] dump failed " + e); }
                try { WriteCookbook(); } catch (Exception e) { Debug.LogError("[ChefBot] cookbook failed " + e); }
                try { WriteCosmetics(); } catch (Exception e) { Debug.LogError("[ChefBot] cosmetics dump failed " + e); }
                try { LoadConfig(); } catch (Exception e) { Debug.LogError("[ChefBot] config failed " + e); }
            }

            UpdateHud();
            DrainUiQueue();
            FlushConfig();
            try { PlayerRules(); } catch (Exception e) { if (Now - lastClientWarn > 30f) { lastClientWarn = Now; Log("player rules: " + e.Message); } }
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f8Key.wasPressedThisFrame) SetEnabled(!enabled);
                HandleControlKeys(kb);
                if (kb.f9Key.wasPressedThisFrame) Dump.KitchenDump(EntityManager);
            }

            if (!enabled)
            {
                if (wasEnabled)
                    foreach (var a in agents.ToList())
                    {
                        Enter(a);
                        try { SendRequest(GameStateRequest.Disconnect); } finally { Exit(a); }
                        a.Brain = null; a.Body.Reset(); a.Status = "stopped";
                    }
                wasEnabled = false;
                return;
            }
            wasEnabled = true;

            if (HasSingleton<SLayout>())
            {
                if (nav.CanReach == null)
                    nav.CanReach = (x, y) =>
                    {
                        try { return TileManager.CanReach(x, y); } catch { return true; }
                    };
                if (Now - navBuiltAt > 1.0f) { nav.Rebuild(Tiles, p => IsBlockingOccupant(p)); navBuiltAt = Now; }
            }

            foreach (var a in agents.ToList())
            {
                Enter(a);
                try { TickAgent(a); }
                catch (Exception ex)
                {
                    a.Errors++;
                    Debug.LogError("[ChefBot] " + a.Name + " exception: " + ex);
                    a.Brain = null; a.Body.Reset();
                    if (a.Errors > 30) { a.RemoveMe = true; Log(a.Name + " removed after too many errors"); }
                }
                finally { Exit(a); }
            }
            ProcessRemovals();
        }

        /// <summary>One frame for one bot: join the game, ready up at night, otherwise run its task loop.</summary>
        void TickAgent(BotAgent a)
        {
            Entity bot = FindPlayer(a.UserId);
            bool ready = bot != Entity.Null && EntityManager.HasComponent<CPosition>(bot) && EntityManager.HasComponent<CInputData>(bot)
                         && !EntityManager.HasComponent<CJoiningPlayer>(bot);

            if (!ready && !HasSingleton<SLayout>())
            {
                if (Now - lastClientWarn > 10f)
                {
                    lastClientWarn = Now;
                    Log("no kitchen simulation on this PC (you are probably a client in someone else's game). The bots must run on the HOST's PC - ask the host to install ChefBot and press F8.");
                }
                a.Body.Reset();
                return;
            }

            if (!ready)
            {
                // join the game: hold a button + instant join request
                var s = InputState.Neutral;
                s.GrabAction = ButtonState.Held;
                s.Request = GameStateRequest.InstantJoin;
                Send(s);
                a.Brain = null;
                a.Status = "joining...";
                if (Now - a.JoinStart > 8f && bot == Entity.Null) { Log(a.Name + " could not join (4 players already?)"); a.RemoveMe = true; }
                return;
            }

            if (!HasSingleton<SLayout>() || Time.IsPaused || EntityManager.HasComponent<CUnplacedPlayer>(bot))
            {
                a.Body.Reset();
                Send(InputState.Neutral);
                return;
            }

            Me = bot;
            TagAsBot(bot);
            TryApplyAppearance(a);
            bool night = HasSingleton<SIsNightTime>();
            if (night)
            {
                // Night = prep phase. Ready up once (interact 3 toggles consent, so only press once per night) then idle.
                a.Body.Reset();
                if (!a.ReadyPressed) { a.ReadyHold++; a.Body.Ready = true; if (a.ReadyHold >= 3) { a.ReadyPressed = true; Log(a.Name + " pressed ready-up"); } }
                a.Brain = null;
                a.Status = "ready for the day";
                Send(a.Body.ToInputState());
                return;
            }
            a.ReadyPressed = false; a.ReadyHold = 0;
            if (a.Brain == null) a.Brain = BrainFor(a.Role).GetEnumerator();
            a.Body.Reset();
            if (!a.Brain.MoveNext()) a.Brain = null;
            Send(a.Body.ToInputState());
        }

        // ---------- plumbing ----------
        public Entity Me;

        Entity FindPlayer(int userId)
        {
            using (var ents = playerQ.ToEntityArray(Allocator.TempJob))
            using (var ps = playerQ.ToComponentDataArray<CPlayer>(Allocator.TempJob))
                for (int i = 0; i < ps.Length; i++)
                    if (ps[i].ID == userId) return ents[i];
            return Entity.Null;
        }

        void SendRequest(GameStateRequest r)
        {
            var s = InputState.Neutral;
            s.Request = r;
            Send(s);
        }

        void Send(InputState state)
        {
            var upd = new UserInputUpdate();
            upd.SourceIdentifier = InputSourceIdentifier.Identifier;
            upd.Data = new InputUpdateEvent { User = cur != null ? cur.UserId : BotUserId, State = state };
            // Same path a human's controller takes: registers the local player, then reaches the host's PlayerManager
            // (directly when we are the host / playing locally).
            var rm = World.GetExistingSystem<RouterManager>();
            if (rm != null && rm.LocalInputEntrypoint != null)
            {
                rm.LocalInputEntrypoint.BroadcastCommand(upd);
                return;
            }
            var pm = World.GetExistingSystem<PlayerManager>();
            if (pm != null) pm.HandleNewInputData(upd);
        }

        // ---------- world queries ----------
        public Vector3 MyPos { get { return (Me != Entity.Null && EntityManager.Exists(Me) && EntityManager.HasComponent<CPosition>(Me)) ? EntityManager.GetComponentData<CPosition>(Me).Position : Vector3.zero; } }

        public Entity MyHeld()
        {
            if (!EntityManager.HasComponent<CItemHolder>(Me)) return Entity.Null;
            var h = EntityManager.GetComponentData<CItemHolder>(Me).HeldItem;
            return (h != Entity.Null && EntityManager.Exists(h)) ? h : Entity.Null;
        }

        public Entity HeldBy(Entity holder)
        {
            if (holder == Entity.Null || !EntityManager.Exists(holder) || !EntityManager.HasComponent<CItemHolder>(holder)) return Entity.Null;
            var h = EntityManager.GetComponentData<CItemHolder>(holder).HeldItem;
            return (h != Entity.Null && EntityManager.Exists(h)) ? h : Entity.Null;
        }

        public Entity AttemptTarget()
        {
            if (!EntityManager.HasComponent<CAttemptingInteraction>(Me)) return Entity.Null;
            return EntityManager.GetComponentData<CAttemptingInteraction>(Me).Target;
        }

        public Vector3 PosOf(Entity e) => EntityManager.GetComponentData<CPosition>(e).Position;

        public string ApplianceName(Entity e)
        {
            if (!EntityManager.HasComponent<CAppliance>(e)) return "?";
            int id = EntityManager.GetComponentData<CAppliance>(e).ID;
            return Data.TryGet<Appliance>(id, out var a) ? a.name : "#" + id;
        }

        public List<Entity> AllAppliances()
        {
            using (var ents = applianceQ.ToEntityArray(Allocator.TempJob)) return ents.ToArray().ToList();
        }

        // ---------- movement primitives (iterator based so recipes read sequentially) ----------

        /// <summary>Walk next to the target entity and face it. Returns true when "attempt target" == target.</summary>
        public IEnumerable<int> Approach(Entity target, Action<bool> result, float timeout = 25f)
        {
            float t0 = Now;
            List<Vector3> path = null;
            int wp = 0;
            float lastRepath = -10f;
            Vector3 lastPos = MyPos;
            float lastMoved = Now;
            while (true)
            {
                if (target == Entity.Null || !EntityManager.Exists(target)) { result(false); yield break; }
                if (Now - t0 > timeout) { Log("approach timeout to " + ApplianceName(target)); result(false); yield break; }
                var me = MyPos;
                var tpos = PosOf(target);
                var toT = new Vector2(tpos.x - me.x, tpos.z - me.z);

                bool nearEnough = toT.magnitude < 1.5f;
                if (nearEnough)
                {
                    // push towards the target so the interaction probe points at it
                    body.MoveWorld = toT.normalized;
                    if (AttemptTarget() == target && toT.magnitude < 1.45f)
                    {
                        // wait one extra frame of aiming to be safe
                        yield return 0;
                        body.MoveWorld = (new Vector2(PosOf(target).x - MyPos.x, PosOf(target).z - MyPos.z)).normalized;
                        if (AttemptTarget() == target) { result(true); yield break; }
                    }
                }

                if (path == null || Now - lastRepath > 1.0f || wp >= path.Count)
                {
                    var goals = new HashSet<Vector2Int>(nav.StandingCells(tpos));
                    if (goals.Count == 0) { var tc = Nav.Cell(tpos); Log("no standing cell for " + ApplianceName(target) + " at " + tpos.ToString("F1") + " nbrs: " + nav.Describe(tc + Vector2Int.right) + " " + nav.Describe(tc + Vector2Int.left) + " " + nav.Describe(tc + Vector2Int.up) + " " + nav.Describe(tc + Vector2Int.down)); result(false); yield break; }
                    path = nav.Path(me, goals);
                    wp = 0;
                    lastRepath = Now;
                    if (path == null) { Log("no path to " + ApplianceName(target) + " from " + me.ToString("F1")); result(false); yield break; }
                }

                if (path.Count > 0 && wp < path.Count)
                {
                    var wpPos = path[wp];
                    var d = new Vector2(wpPos.x - me.x, wpPos.z - me.z);
                    if (d.magnitude < 0.22f) wp++;
                    else if (!nearEnough || path.Count - wp > 0) body.MoveWorld = d.normalized;
                }

                // stuck detection
                if ((me - lastPos).magnitude > 0.05f) { lastPos = me; lastMoved = Now; }
                else if (Now - lastMoved > 2.0f && !nearEnough)
                {
                    // wiggle
                    body.MoveWorld = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)).normalized;
                    path = null;
                    lastMoved = Now;
                }
                yield return 0;
            }
        }

        public IEnumerable<int> Wait(float seconds)
        {
            float t0 = Now;
            while (Now - t0 < seconds) yield return 0;
        }

        /// <summary>Press grab once (held for a few frames), aimed at target. Result is whether either holder changed.</summary>
        public IEnumerable<int> PressGrab(Entity target, Action<bool> result)
        {
            var heldBefore = MyHeld();
            var targetBefore = HeldBy(target);
            for (int i = 0; i < 2; i++)
            {
                body.MoveWorld = TowardsDir(target);
                body.Grab = true;
                yield return 0;
            }
            for (int i = 0; i < 10; i++)
            {
                body.MoveWorld = TowardsDir(target);
                yield return 0;
                if (MyHeld() != heldBefore || HeldBy(target) != targetBefore) break;
            }
            result(MyHeld() != heldBefore || HeldBy(target) != targetBefore);
        }

        public Vector2 TowardsDir(Entity target)
        {
            var t = PosOf(target); var m = MyPos;
            var d = new Vector2(t.x - m.x, t.z - m.z);
            return d.sqrMagnitude < 0.0001f ? Vector2.zero : d.normalized;
        }
    }
}
