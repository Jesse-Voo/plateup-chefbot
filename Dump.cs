using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Kitchen;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace ChefBot
{
    /// <summary>Writes human-readable dumps of the static game data and of the live kitchen. Used for diagnosing.</summary>
    public static class Dump
    {
        public static string Dir
        {
            get
            {
                string d = Path.Combine(Application.persistentDataPath, "ChefBot");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string Name(int id)
        {
            if (id == 0) return "-";
            try
            {
                if (GameData.Main != null && GameData.Main.TryGet<GameDataObject>(id, out var o)) return o.name + "#" + id;
            }
            catch { }
            return "#" + id;
        }

        static string Obj(object o)
        {
            if (o == null) return "null";
            if (o is GameDataObject g) return g.name;
            if (o is UnityEngine.Object u) return u.name;
            return o.ToString();
        }

        public static string Fields(object o)
        {
            if (o == null) return "";
            var sb = new StringBuilder();
            sb.Append(o.GetType().Name);
            var parts = new List<string>();
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    var v = f.GetValue(o);
                    if (v == null) continue;
                    var t = f.FieldType;
                    if (t.IsPrimitive || t.IsEnum || t == typeof(string)) parts.Add(f.Name + "=" + v);
                    else if (v is GameDataObject gdo) parts.Add(f.Name + "=" + gdo.name);
                }
                catch { }
            }
            if (parts.Count > 0) sb.Append("{").Append(string.Join(",", parts)).Append("}");
            return sb.ToString();
        }

        public static void GameDataDump()
        {
            var gd = GameData.Main;
            if (gd == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("=== PROCESSES");
            foreach (var p in gd.Get<Process>())
                sb.AppendLine($"PROC {p.ID} {p.name} enabling={Obj(p.BasicEnablingAppliance)} count={p.EnablingApplianceCount} pseudoFor={Obj(p.IsPseudoprocessFor)} counteractedBy={Obj(p.IsCounteractedBy)}");

            sb.AppendLine("=== APPLIANCES");
            foreach (var a in gd.Get<Appliance>().OrderBy(x => x.name))
            {
                sb.Append($"APPL {a.ID} {a.name} layer={a.Layer} purchasable={a.IsPurchasable}");
                sb.Append(" procs=[" + string.Join("; ", (a.Processes ?? new List<Appliance.ApplianceProcesses>()).Select(p => $"{Obj(p.Process)}{(p.IsAutomatic ? "(auto)" : "")}x{p.Speed}/{p.Validity}")) + "]");
                sb.Append(" props=[" + string.Join("; ", (a.Properties ?? new List<IApplianceProperty>()).Select(Fields)) + "]");
                sb.AppendLine();
            }

            sb.AppendLine("=== ITEMS");
            foreach (var i in gd.Get<Item>().OrderBy(x => x.name))
            {
                sb.Append($"ITEM {i.ID} {i.name} {(i is ItemGroup ? "GROUP" : "")} cat={i.ItemCategory} storage={i.ItemStorageFlags}");
                if (i.DerivedProcesses != null && i.DerivedProcesses.Count > 0)
                    sb.Append(" procs=[" + string.Join("; ", i.DerivedProcesses.Select(p => $"{Obj(p.Process)}->{Obj(p.Result)}{(p.IsBad ? " BAD" : "")}{(p.RequiresWrapper ? " WRAP" : "")} {p.Duration}")) + "]");
                if (i.SplitSubItem != null) sb.Append($" split={Obj(i.SplitSubItem)}x{i.SplitCount} depleted=[{string.Join(",", (i.SplitDepletedItems ?? new List<Item>()).Select(Obj))}] byComp={i.SplitByComponents}");
                if (i.SplitByComponents || i.SplitByCopying || i.SplitByComponentsHolder != null || i.SplitByComponentsWrapper != null)
                    sb.Append($" splitComp=[byComp={i.SplitByComponents} copy={i.SplitByCopying} holder={Obj(i.SplitByComponentsHolder)} wrapper={Obj(i.SplitByComponentsWrapper)} count={i.SplitCount} allowMerge={i.AllowSplitMerging} noExplicit={i.PreventExplicitSplit}]");
                if (i.SatisfiedBy != null && i.SatisfiedBy.Count > 0) sb.Append(" satisfiedBy=[" + string.Join(",", i.SatisfiedBy.Select(Obj)) + "]");
                if (i.NeedsIngredients != null && i.NeedsIngredients.Count > 0) sb.Append(" needs=[" + string.Join(",", i.NeedsIngredients.Select(Obj)) + "]");
                if (i.DirtiesTo != null) sb.Append(" dirtiesTo=" + Obj(i.DirtiesTo));
                if (i.DisposesTo != null) sb.Append(" disposesTo=" + Obj(i.DisposesTo));
                if (i.DedicatedProvider != null) sb.Append(" dedicated=" + Obj(i.DedicatedProvider));
                if (i.IsConsumedByCustomer) sb.Append(" consumed");
                if (i.IsMergeableSide) sb.Append(" mergeableSide");
                if (i.RequiresCleaning) sb.Append(" requiresCleaning");
                if (i.IsIndisposable) sb.Append(" indisposable");
                if (i.IsSinkDisposable) sb.Append(" sinkDisposable");
                if (i.Properties != null && i.Properties.Count > 0) sb.Append(" props=[" + string.Join("; ", i.Properties.Select(Fields)) + "]");
                if (i is ItemGroup g)
                {
                    sb.Append($" applyToComps={g.ApplyProcessesToComponents} canSide={g.CanContainSide} autoCollapse={g.AutoCollapsing}");
                    if (g.DerivedSets != null)
                        sb.Append(" sets=[" + string.Join(" | ", g.DerivedSets.Select(s => $"{(s.IsMandatory ? "MAND " : "")}{s.Min}-{s.Max}{(s.OrderingOnly ? " ORDERONLY" : "")}: " + string.Join(",", s.Items.Select(Obj)))) + "]");
                }
                sb.AppendLine();
            }

            sb.AppendLine("=== DISHES");
            foreach (var d in gd.Get<Dish>().OrderBy(x => x.name))
            {
                sb.Append($"DISH {d.ID} {d.name} type={d.Type} diff={d.Difficulty}");
                sb.Append(" menu=[" + string.Join(", ", d.UnlocksMenuItems.Select(m => $"{Obj(m.Item)}({m.Phase})")) + "]");
                sb.Append(" minIngr=[" + string.Join(",", d.MinimumIngredients.Select(Obj)) + "]");
                sb.Append(" reqProc=[" + string.Join(",", d.RequiredProcesses.Select(Obj)) + "]");
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine(Dir, "gamedata.txt"), sb.ToString());
            Debug.Log("[ChefBot] wrote gamedata.txt (" + sb.Length + " chars) to " + Dir);
        }

        public static void KitchenDump(EntityManager em)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== PLAYERS");
            var pq = em.CreateEntityQuery(typeof(CPlayer));
            using (var ents = pq.ToEntityArray(Allocator.TempJob))
                foreach (var e in ents)
                {
                    var p = em.GetComponentData<CPlayer>(e);
                    string pos = em.HasComponent<CPosition>(e) ? em.GetComponentData<CPosition>(e).Position.ToString("F2") : "nopos";
                    string held = "";
                    if (em.HasComponent<CItemHolder>(e))
                    {
                        var h = em.GetComponentData<CItemHolder>(e).HeldItem;
                        if (h != Entity.Null && em.Exists(h) && em.HasComponent<CItem>(h)) held = DescribeItem(em, h);
                    }
                    sb.AppendLine($"PLAYER e={e.Index} id={p.ID} idx={p.Index} src={p.InputSource} pos={pos} holding={held} comps=[{Comps(em, e)}]");
                }
            pq.Dispose();

            sb.AppendLine("=== APPLIANCES");
            var aq = em.CreateEntityQuery(typeof(CAppliance), typeof(CPosition));
            using (var ents = aq.ToEntityArray(Allocator.TempJob))
                foreach (var e in ents)
                {
                    var a = em.GetComponentData<CAppliance>(e);
                    var pos = em.GetComponentData<CPosition>(e).Position;
                    string held = "";
                    if (em.HasComponent<CItemHolder>(e))
                    {
                        var h = em.GetComponentData<CItemHolder>(e).HeldItem;
                        if (h != Entity.Null && em.Exists(h) && em.HasComponent<CItem>(h)) held = DescribeItem(em, h);
                    }
                    sb.AppendLine($"APPL e={e.Index} {Name(a.ID)} pos={pos.ToString("F1")} holds={held} comps=[{Comps(em, e)}]");
                }
            aq.Dispose();

            sb.AppendLine("=== ENTITIES WITH CItemHolder (non-appliance)");
            var hq = em.CreateEntityQuery(new EntityQueryDesc { All = new ComponentType[] { typeof(CItemHolder), typeof(CPosition) }, None = new ComponentType[] { typeof(CAppliance), typeof(CPlayer) } });
            using (var ents = hq.ToEntityArray(Allocator.TempJob))
                foreach (var e in ents)
                {
                    var pos = em.GetComponentData<CPosition>(e).Position;
                    var h = em.GetComponentData<CItemHolder>(e).HeldItem;
                    string held = (h != Entity.Null && em.Exists(h) && em.HasComponent<CItem>(h)) ? DescribeItem(em, h) : "";
                    sb.AppendLine($"HOLDER e={e.Index} pos={pos.ToString("F1")} holds={held} comps=[{Comps(em, e)}]");
                }
            hq.Dispose();

            sb.AppendLine("=== GROUPS WITH ORDERS");
            var gq = em.CreateEntityQuery(typeof(CWaitingForItem));
            using (var ents = gq.ToEntityArray(Allocator.TempJob))
                foreach (var e in ents)
                {
                    var buf = em.GetBuffer<CWaitingForItem>(e);
                    var parts = new List<string>();
                    for (int i = 0; i < buf.Length; i++)
                        parts.Add($"{Name(buf[i].ItemID)} sat={buf[i].Satisfied} member={buf[i].MemberIndex} side={buf[i].IsSide} extra={Name(buf[i].Extra)}");
                    sb.AppendLine($"GROUP e={e.Index} orders=[{string.Join("; ", parts)}] comps=[{Comps(em, e)}]");
                }
            gq.Dispose();

            File.WriteAllText(Path.Combine(Dir, "kitchen.txt"), sb.ToString());
            Debug.Log("[ChefBot] wrote kitchen.txt to " + Dir);
        }

        public static string DescribeItem(EntityManager em, Entity item)
        {
            if (item == Entity.Null || !em.Exists(item) || !em.HasComponent<CItem>(item)) return "";
            var c = em.GetComponentData<CItem>(item);
            string s = Name(c.ID);
            if (c.Items.Count > 1 || c.IsGroup)
            {
                var l = new List<string>();
                for (int i = 0; i < c.Items.Count; i++) l.Add(Name(c.Items[i]));
                s += "{" + string.Join(",", l) + "}";
            }
            if (em.HasComponent<CItemUndergoingProcess>(item))
            {
                var p = em.GetComponentData<CItemUndergoingProcess>(item);
                s += $"[proc {Name(p.Process)} {p.Progress:F2}{(p.IsBad ? " BAD" : "")}]";
            }
            return s;
        }

        static string Comps(EntityManager em, Entity e)
        {
            try
            {
                using (var types = em.GetComponentTypes(e, Allocator.Temp))
                    return string.Join(",", types.ToArray().Select(t => { try { return t.GetManagedType()?.Name ?? "?"; } catch { return "?"; } }));
            }
            catch { return "?"; }
        }
    }
}
