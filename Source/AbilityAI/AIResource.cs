using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

// Pools abilities spend from (hemogen, The Force's Force points, later a magic mod's mana), declared in XML so another
// system needs a def rather than code: where a pawn's amount is read, where an ability's cost is read, and the floor
// autocast leaves unspent by default. The player sets a per-pawn floor on the limits gizmo (Gizmo_PsycastLimits);
// AbilityAIDriver.PsycastWithinLimits holds a player pawn's cast that would go below it. Psyfocus and neural heat stay
// apart: they are psycast-only, priced per target, and sit beside vanilla's own psyfocus gizmo.
namespace AbilityAI
{
    /// <summary>A pool abilities spend from. Name exactly one value reader (comp, geneResource or need) and at least
    /// one cost reader (costStat, costComp, costExtensionField); the cost readers are tried in that order and the first
    /// non-zero answer is the cost. A def whose requiredMod is not active, or whose types or members can't be found,
    /// takes no part (the second with one warning).</summary>
    public class AIResourceDef : Def
    {
        /// <summary>The row's label on the limits gizmo ("Min hemogen").</summary>
        [MustTranslate] public string limitLabel;
        public Color barColor = new Color(0.4f, 0.4f, 0.4f);
        /// <summary>The floor a pawn has until the player moves it, as a fraction of the pawn's maximum.</summary>
        public float defaultFloor;
        /// <summary>A packageId; the def is ignored while that mod (or DLC) is not active.</summary>
        public string requiredMod;

        /// <summary>A ThingComp on the pawn, with the members holding the current and maximum amount (fields or
        /// properties, float or int).</summary>
        public AIResourceMembers comp;
        /// <summary>A Gene_Resource subclass, full name (RimWorld.Gene_Hemogen): its Value and Max.</summary>
        public string geneResource;
        /// <summary>A NeedDef's defName: its CurLevel and MaxLevel.</summary>
        public string need;

        /// <summary>A StatDef's defName on the AbilityDef's statBases (GetStatValueAbstract); vanilla AbilityDefs.</summary>
        public string costStat;
        /// <summary>A field on one of a vanilla AbilityDef's comp properties.</summary>
        public AIResourceField costComp;
        /// <summary>A float field of this name on any of the def's mod extensions, any def type (VPE Hemosage's
        /// hemogenCost on VEF abilities). Summed when several extensions have it.</summary>
        public string costExtensionField;

        internal int resourceIndex = -1;
        private Func<Pawn, object> holderOf;
        private Func<object, float> currentOf, maxOf;
        private StatDef costStatDef;
        private Type costCompType;
        private Func<object, float> costCompOf;
        private Texture2D barTex, barHighlightTex;
        private string cachedTip;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            int readers = (comp != null ? 1 : 0) + (geneResource.NullOrEmpty() ? 0 : 1) + (need.NullOrEmpty() ? 0 : 1);
            if (readers != 1) yield return "needs exactly one of comp, geneResource and need";
            if (comp != null && (comp.type.NullOrEmpty() || comp.current.NullOrEmpty() || comp.max.NullOrEmpty()))
                yield return "comp needs type, current and max";
            if (costStat.NullOrEmpty() && costComp == null && costExtensionField.NullOrEmpty())
                yield return "needs at least one of costStat, costComp and costExtensionField";
            if (costComp != null && (costComp.type.NullOrEmpty() || costComp.field.NullOrEmpty()))
                yield return "costComp needs type and field";
            if (limitLabel.NullOrEmpty()) yield return "has no limitLabel";
            if (defaultFloor < 0f || defaultFloor > 1f) yield return "defaultFloor must be from 0 to 1";
        }

        /// <summary>The pawn's current and maximum amount; false when it has none of this resource (no comp, gene or
        /// need, or a maximum of 0: The Force puts its comp on every humanlike).</summary>
        public bool TryRead(Pawn pawn, out float value, out float max)
        {
            value = max = 0f;
            object holder = holderOf?.Invoke(pawn);
            if (holder == null) return false;
            max = maxOf(holder);
            if (max <= 0f) return false;
            value = currentOf(holder);
            return true;
        }

        internal float ReadCost(Def def)
        {
            if (def is AbilityDef ad)
            {
                if (costStatDef != null && ad.statBases != null)
                    for (int i = 0; i < ad.statBases.Count; i++)
                        if (ad.statBases[i].stat == costStatDef)
                        {
                            float cost = ad.GetStatValueAbstract(costStatDef);
                            if (cost > 0f) return cost;
                            break;
                        }
                if (costCompType != null && ad.comps != null)
                    for (int i = 0; i < ad.comps.Count; i++)
                        if (ad.comps[i] != null && costCompType.IsInstanceOfType(ad.comps[i]))
                        {
                            float cost = costCompOf(ad.comps[i]);
                            if (cost > 0f) return cost;
                        }
            }
            if (!costExtensionField.NullOrEmpty() && def.modExtensions != null)
            {
                float sum = 0f;
                for (int i = 0; i < def.modExtensions.Count; i++)
                {
                    var ext = def.modExtensions[i];
                    if (ext != null && AIResources.FloatField(ext.GetType(), costExtensionField) is FieldInfo f) sum += (float)f.GetValue(ext);
                }
                if (sum > 0f) return sum;
            }
            return 0f;
        }

        // Main thread only (the gizmo draws them): SolidColorMaterials makes a texture
        internal Texture2D BarTex => barTex ?? (barTex = SolidColorMaterials.NewSolidColorTexture(barColor));
        internal Texture2D BarHighlightTex => barHighlightTex ?? (barHighlightTex = SolidColorMaterials.NewSolidColorTexture(
            new Color(Mathf.Min(1f, barColor.r * 1.25f), Mathf.Min(1f, barColor.g * 1.25f), Mathf.Min(1f, barColor.b * 1.25f))));

        internal string LimitTip
        {
            get
            {
                if (cachedTip == null)
                {
                    cachedTip = "AAI_Limits_Resource_Tip".Translate(label, defaultFloor <= 0f
                        ? (string)"AAI_Limits_NoFloor".Translate() : defaultFloor.ToStringPercent()).Resolve();
                    if (!description.NullOrEmpty()) cachedTip += "\n\n" + description;
                }
                return cachedTip;
            }
        }

        // False with the reason when a type or member is missing; the caller warns and drops the def
        internal bool Resolve(out string problem)
        {
            problem = null;
            if (comp != null)
            {
                Type type = GenTypes.GetTypeInAnyAssembly(comp.type);
                if (type == null || !typeof(ThingComp).IsAssignableFrom(type)) { problem = $"no ThingComp type {comp.type}"; return false; }
                currentOf = AIResources.Getter(type, comp.current);
                maxOf = AIResources.Getter(type, comp.max);
                if (currentOf == null || maxOf == null) { problem = $"{comp.type} has no number member {(currentOf == null ? comp.current : comp.max)}"; return false; }
                holderOf = pawn => AIResources.CompOf(pawn, type);
            }
            else if (!geneResource.NullOrEmpty())
            {
                Type type = GenTypes.GetTypeInAnyAssembly(geneResource);
                if (type == null || !typeof(Gene_Resource).IsAssignableFrom(type)) { problem = $"no Gene_Resource type {geneResource}"; return false; }
                holderOf = pawn => AIResources.GeneOf(pawn, type);
                currentOf = g => ((Gene_Resource)g).Value;
                maxOf = g => ((Gene_Resource)g).Max;
            }
            else
            {
                NeedDef needDef = DefDatabase<NeedDef>.GetNamedSilentFail(need);
                if (needDef == null) { problem = $"no NeedDef {need}"; return false; }
                holderOf = pawn => pawn.needs?.TryGetNeed(needDef);
                currentOf = n => ((Need)n).CurLevel;
                maxOf = n => ((Need)n).MaxLevel;
            }
            if (!costStat.NullOrEmpty())
            {
                costStatDef = DefDatabase<StatDef>.GetNamedSilentFail(costStat);
                if (costStatDef == null) { problem = $"no StatDef {costStat}"; return false; }
            }
            if (costComp != null)
            {
                costCompType = GenTypes.GetTypeInAnyAssembly(costComp.type);
                if (costCompType == null || !typeof(AbilityCompProperties).IsAssignableFrom(costCompType)) { problem = $"no AbilityCompProperties type {costComp.type}"; return false; }
                costCompOf = AIResources.Getter(costCompType, costComp.field);
                if (costCompOf == null) { problem = $"{costComp.type} has no number member {costComp.field}"; return false; }
            }
            return true;
        }
    }

    public class AIResourceMembers
    {
        public string type;
        public string current;
        public string max;
    }

    public class AIResourceField
    {
        public string type;
        public string field;
    }

    public static class AIResources
    {
        private static List<AIResourceDef> active;
        // Per def, its cost in each active resource, by AIResourceDef.resourceIndex; read once (Entad registers defs late, so
        // not at startup)
        private static readonly Dictionary<Def, float[]> costs = new Dictionary<Def, float[]>();
        private static readonly Dictionary<(Type, string), FieldInfo> floatFields = new Dictionary<(Type, string), FieldInfo>();
        private static AIResourceDef hemogen;

        /// <summary>The resources in play this session, in def order.</summary>
        public static List<AIResourceDef> Active
        {
            get
            {
                if (active == null) Init();
                return active;
            }
        }

        /// <summary>Biotech's hemogen, or null without Biotech. Kept for the hemogen-only limits API.</summary>
        public static AIResourceDef Hemogen
        {
            get
            {
                if (active == null) Init();
                return hemogen;
            }
        }

        internal static void Init()
        {
            if (active != null) return;
            active = new List<AIResourceDef>();
            foreach (var def in DefDatabase<AIResourceDef>.AllDefsListForReading)
            {
                if (!def.requiredMod.NullOrEmpty() && !ModsConfig.IsActive(def.requiredMod)) continue;
                bool ok;
                string problem;
                try { ok = def.Resolve(out problem); }
                catch (Exception e) { ok = false; problem = e.Message; }
                if (!ok)
                {
                    Log.Warning($"[Ability AI] Resource {def.defName} is off: {problem}.");
                    continue;
                }
                def.resourceIndex = active.Count;
                active.Add(def);
            }
            hemogen = DefDatabase<AIResourceDef>.GetNamedSilentFail("AAI_Hemogen");
            if (hemogen != null && hemogen.resourceIndex < 0) hemogen = null;
        }

        /// <summary>What a cast of this def spends of the resource; 0 for a resource not in play.</summary>
        public static float Cost(Def def, AIResourceDef resource)
        {
            if (def == null || resource == null || resource.resourceIndex < 0) return 0f;
            var list = Active;
            if (!costs.TryGetValue(def, out float[] byResource))
            {
                byResource = new float[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    try { byResource[i] = list[i].ReadCost(def); }
                    catch (Exception e) { Log.ErrorOnce($"[Ability AI] Reading {def.defName}'s {list[i].defName} cost failed; counted as free: {e}", def.shortHash ^ list[i].shortHash); }
                }
                costs[def] = byResource;
            }
            return byResource[resource.resourceIndex];
        }

        internal static Func<object, float> Getter(Type type, string member)
        {
            var obj = Expression.Parameter(typeof(object));
            MemberExpression access;
            try { access = Expression.PropertyOrField(Expression.Convert(obj, type), member); }
            catch (ArgumentException) { return null; }
            Type t = access.Type;
            if (t != typeof(float) && t != typeof(int) && t != typeof(double)) return null;
            return Expression.Lambda<Func<object, float>>(Expression.Convert(access, typeof(float)), obj).Compile();
        }

        internal static FieldInfo FloatField(Type type, string name)
        {
            if (!floatFields.TryGetValue((type, name), out var field))
            {
                field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType != typeof(float)) field = null;
                floatFields[(type, name)] = field;
            }
            return field;
        }

        internal static object CompOf(Pawn pawn, Type type)
        {
            var comps = pawn.AllComps;
            for (int i = 0; i < comps.Count; i++)
                if (type.IsInstanceOfType(comps[i])) return comps[i];
            return null;
        }

        internal static object GeneOf(Pawn pawn, Type type)
        {
            var genes = pawn.genes?.GenesListForReading;
            if (genes == null) return null;
            for (int i = 0; i < genes.Count; i++)
                if (genes[i].Active && type.IsInstanceOfType(genes[i])) return genes[i];
            return null;
        }
    }
}
