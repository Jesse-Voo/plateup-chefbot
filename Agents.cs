using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Kitchen;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    public enum BotRole { Cook, Server, Dishwasher, All }

    /// <summary>One chef: its own player, position in its task loop, role and reservations.</summary>
    public class BotAgent
    {
        public int Index;
        public int UserId;
        public BotSpec Spec = new BotSpec();
        public BotRole Role { get { return Spec.Role; } set { Spec.Role = value; } }
        public bool AppearanceApplied;
        public string DisplayName { get { return string.IsNullOrWhiteSpace(Spec.Name) ? RoleLabel(Spec.Role) + " " + (Index + 1) : Spec.Name.Trim(); } }
        public Body Body = new Body();
        public IEnumerator<int> Brain;
        public Entity Me;
        public bool R;
        public HashSet<Entity> Reserved = new HashSet<Entity>();
        public HashSet<int> Ban = new HashSet<int>();
        public string Status = "";
        public bool ReadyPressed; public int ReadyHold;
        public float JoinStart;
        public int Errors;
        public bool RemoveMe;
        public string Name => DisplayName + " (" + RoleLabel(Role) + ")";

        public static string RoleLabel(BotRole r)
        {
            switch (r)
            {
                case BotRole.Cook: return "Cook";
                case BotRole.Server: return "Server";
                case BotRole.Dishwasher: return "Dishwasher";
                default: return "All-rounder";
            }
        }
    }

    /// <summary>Reservations are per bot, but every bot respects every other bot's reservations.</summary>
    public class ReserveSet
    {
        readonly ChefBotSystem sys;
        public ReserveSet(ChefBotSystem s) { sys = s; }
        HashSet<Entity> Mine => sys.CurrentAgent != null ? sys.CurrentAgent.Reserved : sys.FallbackReserved;
        public bool Add(Entity e) => Mine.Add(e);
        public bool Remove(Entity e) => Mine.Remove(e);
        public void Clear() => Mine.Clear();
        public bool Contains(Entity e)
        {
            foreach (var a in sys.Agents) if (a.Reserved.Contains(e)) return true;
            return Mine.Contains(e);
        }
    }

    public partial class ChefBotSystem
    {
        readonly List<BotAgent> agents = new List<BotAgent>();
        BotAgent cur;
        int agentCounter;
        readonly Queue<Action> uiQueue = new Queue<Action>();
        public readonly HashSet<Entity> FallbackReserved = new HashSet<Entity>();
        public List<BotAgent> Agents => agents;
        public BotAgent CurrentAgent => cur;
        public bool Enabled => enabled;
        ReserveSet reserved;
        public ReserveSet Reserved => reserved ?? (reserved = new ReserveSet(this));

        // ---------- context switching: the shared helper methods use these fields, so each bot swaps its own in ----------
        void Enter(BotAgent a)
        {
            cur = a;
            Me = a.Me; body = a.Body; R = a.R; banToppings = a.Ban; Status = a.Status;
        }

        void Exit(BotAgent a)
        {
            a.Me = Me; a.R = R; a.Ban = banToppings; a.Status = Status;
            cur = null;
        }

        BotAgent NewAgent(BotRole role) { return NewAgent(new BotSpec { Role = role }); }

        BotAgent NewAgent(BotSpec spec)
        {
            var a = new BotAgent { Index = agentCounter, UserId = BotUserId + (agentCounter % 60), Spec = spec, JoinStart = Now };
            agentCounter++;
            agents.Add(a);
            return a;
        }

        /// <summary>Make the bot list match the config (used when the config is (re)loaded while the bots are off).</summary>
        void BuildAgentsFromConfig()
        {
            if (enabled) return;
            agents.Clear();
            agentCounter = 0;
            var specs = cfg.Bots.Count > 0 ? cfg.Bots : new List<BotSpec> { new BotSpec() };
            foreach (var sp in specs) NewAgent(sp);
        }

        void SyncBotsToCfg()
        {
            cfg.Bots = agents.Select(a => a.Spec).ToList();
            try { cfg.Save(); } catch { }
        }

        void SetEnabled(bool on)
        {
            enabled = on;
            Log("bots " + (on ? "STARTED" : "STOPPED"));
            if (!on) return;
            if (agents.Count == 0) BuildAgentsFromConfig();
            if (agents.Count == 0) NewAgent(BotRole.Cook);
            foreach (var a in agents) { a.JoinStart = Now; a.Errors = 0; a.ReadyPressed = false; a.ReadyHold = 0; a.RemoveMe = false; a.AppearanceApplied = false; }
            try { Dump.KitchenDump(EntityManager); } catch (Exception e) { Log("dump failed " + e.Message); }
        }

        void ProcessRemovals()
        {
            bool any = false;
            foreach (var a in agents.Where(x => x.RemoveMe).ToList())
            {
                Enter(a);
                try { SendRequest(GameStateRequest.Disconnect); } finally { Exit(a); }
                agents.Remove(a);
                any = true;
                Log(a.Name + " left");
            }
            if (any) SyncBotsToCfg();
        }

        void DrainUiQueue()
        {
            while (uiQueue.Count > 0)
            {
                var act = uiQueue.Dequeue();
                try { act(); } catch (Exception e) { Log("ui action failed: " + e.Message); }
            }
        }

        // ---------- actions from the map window / hotkeys (run on the main thread, in Tick) ----------
        public void UiToggleEnabled() { uiQueue.Enqueue(() => SetEnabled(!enabled)); }

        public void UiAddBot(BotRole role)
        {
            uiQueue.Enqueue(() =>
            {
                int cap = Mathf.Max(1, Mathf.Clamp(cfg.MaxPlayers, 4, 16) - 1);
                if (agents.Count >= cap) { Status = "The restaurant holds " + (cap + 1) + " players (max_players in config.txt): at most " + cap + " bots next to you."; Log(Status); return; }
                var a = NewAgent(role);
                a.JoinStart = Now;
                SyncBotsToCfg();
                Status = "Added " + a.Name;
            });
        }

        public void UiSetRole(int i, BotRole role)
        {
            uiQueue.Enqueue(() =>
            {
                if (i < 0 || i >= agents.Count) return;
                var a = agents[i];
                a.Role = role; a.Brain = null; a.AppearanceApplied = false;     // new job: new task loop and new look
                SyncBotsToCfg();
            });
        }

        bool cfgDirty; float cfgSavedAt;
        void SaveSoon() { cfgDirty = true; }
        void FlushConfig()
        {
            if (!cfgDirty || Now - cfgSavedAt < 1f) return;
            cfgSavedAt = Now; cfgDirty = false;
            try { cfg.Save(); } catch { }
        }

        public void UiRename(int i, string name)
        {
            if (i < 0 || i >= agents.Count) return;
            agents[i].Spec.Name = name ?? "";
            agents[i].AppearanceApplied = false;       // show the new name in the game
            SaveSoon();
        }

        public void UiCycleKnife(int i)
        {
            if (i < 0 || i >= agents.Count) return;
            var sp = agents[i].Spec;
            sp.Knife = sp.Knife == 1 ? 2 : sp.Knife == 2 ? 0 : 1;       // normal -> high -> off -> normal
            SaveSoon();
        }

        public void UiSelectList(int bot) { MapData.SelectedBot = bot; MapData.SelectedEntry = -1; mapAt = 0; }

        public void UiRemoveBot(int i)
        {
            uiQueue.Enqueue(() =>
            {
                if (i < 0 || i >= agents.Count) return;
                if (enabled) agents[i].RemoveMe = true;
                else { agents.RemoveAt(i); SyncBotsToCfg(); }
            });
        }

        // ---------- shared work: one bot claims an order / a prep line so the others skip it ----------
        readonly Dictionary<string, (int agent, float time)> claims = new Dictionary<string, (int, float)>();

        bool ClaimedByOther(string key)
        {
            if (cur == null) return false;
            return claims.TryGetValue(key, out var c) && c.agent != cur.Index && Now - c.time < 150f && agents.Any(a => a.Index == c.agent);
        }

        void Claim(string key) { if (cur != null) claims[key] = (cur.Index, Now); }
        void Release(string key) { if (cur != null && claims.TryGetValue(key, out var c) && c.agent == cur.Index) claims.Remove(key); }
        static string OrderKeyOf(Order o) => o.ItemId + "@" + Mathf.RoundToInt(o.TablePos.x) + "," + Mathf.RoundToInt(o.TablePos.z) + "#" + o.ExtraId;

        /// <summary>True when the plan needs actual cooking/chopping (a server only plates, it does not cook).</summary>
        static bool NeedsCooking(Node n)
        {
            if (n == null) return false;
            if (n.Kind == NK.Process || n.Kind == NK.Depleted) return true;
            if (NeedsCooking(n.Child)) return true;
            foreach (var p in n.Parts) if (NeedsCooking(p)) return true;
            return false;
        }

        static void CollectPickups(Node n, HashSet<Entity> into)
        {
            if (n == null) return;
            if (n.Kind == NK.Pickup && n.Holder != Entity.Null) into.Add(n.Holder);
            CollectPickups(n.Child, into);
            foreach (var p in n.Parts) CollectPickups(p, into);
        }
    }
}
