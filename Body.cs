using System;
using System.Collections.Generic;
using System.Linq;
using Controllers;
using Kitchen;
using Kitchen.Layouts;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    /// <summary>The bot's "hands and feet": what inputs it wants to press this frame.</summary>
    public class Body
    {
        public Vector2 MoveWorld;   // desired world-space direction (x,z), magnitude 0..1
        public bool Grab;           // hold grab button (interact 1)
        public bool Act;            // hold act button (interact 2)
        public bool Active;
        public bool Ready;          // press 'ready up' (interact 3) once

        public void Reset()
        {
            MoveWorld = Vector2.zero;
            Grab = false;
            Act = false;
            Ready = false;
        }

        static (Vector3 up, Vector3 right) CameraVectors()
        {
            Camera main = Camera.main;
            bool has = main != null;
            Vector3 up = Vector3.ProjectOnPlane(has ? main.transform.forward : Vector3.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(has ? main.transform.right : Vector3.right, Vector3.up).normalized;
            return (up, right);
        }

        public InputState ToInputState()
        {
            InputState s = InputState.Neutral;
            if (MoveWorld.sqrMagnitude > 0.0001f)
            {
                var (up, right) = CameraVectors();
                var d = new Vector3(MoveWorld.x, 0f, MoveWorld.y);
                // Movement = up * y + right * x  (see PlayerWalkingComponent)  =>  invert with dot products
                s.Movement = new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, up));
            }
            s.GrabAction = Grab ? ButtonState.Held : ButtonState.Up;
            s.InteractAction = Act ? ButtonState.Held : ButtonState.Up;
            s.SecondaryAction1 = Ready ? ButtonState.Held : ButtonState.Up;
            return s;
        }
    }

    /// <summary>Tile-grid pathfinding over the kitchen layout.</summary>
    public class Nav
    {
        struct TileInfo { public int Room; public RoomType Type; public CLayoutRoomTile Raw; }

        readonly Dictionary<Vector2Int, TileInfo> tiles = new Dictionary<Vector2Int, TileInfo>();
        Func<Vector3, bool> isBlocked;
        public Func<Vector3, Vector3, bool> CanReach;   // the game's own 'can this tile interact with that tile' rule

        public static Vector2Int Cell(Vector3 p) => new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z));
        public static Vector3 Center(Vector2Int c) => new Vector3(c.x, 0f, c.y);

        public int TileCount => tiles.Count;
        public List<Vector2Int> RoomCells() => tiles.Where(kv => kv.Value.Type != RoomType.NoRoom).Select(kv => kv.Key).ToList();

        public void Rebuild(DynamicBuffer<CLayoutRoomTile> buf, Func<Vector3, bool> blocked)
        {
            tiles.Clear();
            isBlocked = blocked;
            for (int i = 0; i < buf.Length; i++)
            {
                var t = buf[i];
                tiles[Cell(t.Position)] = new TileInfo { Room = t.RoomID, Type = t.Type, Raw = t };
            }
        }

        readonly Dictionary<Vector2Int, float> tempBlocked = new Dictionary<Vector2Int, float>();
        public void BlockTemp(Vector2Int c, float seconds) { tempBlocked[c] = Time.realtimeSinceStartup + seconds; }
        public void ClearTempBlocks() { tempBlocked.Clear(); }

        public bool Walkable(Vector2Int c)
        {
            if (tempBlocked.TryGetValue(c, out float until) && until > Time.realtimeSinceStartup) return false;
            if (!tiles.TryGetValue(c, out var t)) return false;
            if (t.Type == RoomType.NoRoom) return false;
            if (isBlocked != null && isBlocked(Center(c))) return false;
            return true;
        }

        bool CanStep(Vector2Int from, Vector2Int to)
        {
            if (!tiles.TryGetValue(from, out var a) || !tiles.TryGetValue(to, out var b)) return false;
            if (!Walkable(to)) return false;
            if (a.Room == b.Room) return true;
            int dx = to.x - from.x, dz = to.y - from.y;
            return a.Raw.Reachability[dx, dz];
        }

        static readonly Vector2Int[] Dirs = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };

        /// <summary>BFS from start to any goal cell. Returns list of cell centres (excluding the start). Null when unreachable.</summary>
        public List<Vector3> Path(Vector3 from, HashSet<Vector2Int> goals, HashSet<Vector2Int> extraBlocked = null)
        {
            var start = Cell(from);
            if (goals.Contains(start)) return new List<Vector3>();
            var prev = new Dictionary<Vector2Int, Vector2Int>();
            var q = new Queue<Vector2Int>();
            q.Enqueue(start);
            prev[start] = start;
            Vector2Int found = default;
            bool ok = false;
            int guard = 0;
            while (q.Count > 0 && guard++ < 6000)
            {
                var cur = q.Dequeue();
                foreach (var d in Dirs)
                {
                    var n = cur + d;
                    if (prev.ContainsKey(n)) continue;
                    if (extraBlocked != null && extraBlocked.Contains(n) && !goals.Contains(n)) continue;
                    if (!CanStep(cur, n)) continue;
                    prev[n] = cur;
                    if (goals.Contains(n)) { found = n; ok = true; break; }
                    q.Enqueue(n);
                }
                if (ok) break;
            }
            if (!ok) return null;
            var path = new List<Vector3>();
            var c = found;
            while (c != start) { path.Add(Center(c)); c = prev[c]; }
            path.Reverse();
            return path;
        }

        /// <summary>Walkable cells orthogonally adjacent to a target position (where the bot may stand to use it).</summary>
        public string Describe(Vector2Int c)
        {
            if (!tiles.TryGetValue(c, out var t)) return $"{c}:notile";
            return $"{c}:{t.Type}/room{t.Room}/feat{t.Raw.HasFeature}/blocked{(isBlocked != null && isBlocked(Center(c)))}";
        }

        public List<Vector2Int> StandingCells(Vector3 target, bool filtered = true)
        {
            var res = new List<Vector2Int>();
            var t = Cell(target);
            foreach (var d in Dirs)
            {
                var c = t + d;
                if (!Walkable(c)) continue;
                // only tiles from which the game lets us reach the target (counters can act as walls between rooms)
                if (filtered && CanReach != null && !CanReach(Center(c), Center(t))) continue;
                res.Add(c);
            }
            return res;
        }
    }
}
