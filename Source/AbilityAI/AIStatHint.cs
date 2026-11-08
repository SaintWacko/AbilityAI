using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

// A tooltip line for an AbilityDef stat that a cast changes something by, so the player sees what autocasting spends
// on their behalf (The Force: each cast shifts the caster's light or dark side attunement). Declared in XML like the
// resources, so another system needs a def rather than code.
namespace AbilityAI
{
    /// <summary>A line on the gizmo tooltip of an ability whose AbilityDef lists this stat in its statBases with a
    /// non-zero value. The hint holds {0} for the value. A def whose requiredMod is not active, or whose stat can't be
    /// found, takes no part (the second with one warning).</summary>
    public class AIStatHintDef : Def
    {
        /// <summary>A StatDef's defName, looked up only while requiredMod is active.</summary>
        public string stat;
        /// <summary>A packageId; the def is ignored while that mod (or DLC) is not active.</summary>
        public string requiredMod;
        [MustTranslate] public string hint;

        internal StatDef statDef;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            if (stat.NullOrEmpty()) yield return "has no stat";
            if (hint.NullOrEmpty() || !hint.Contains("{0}")) yield return "needs a hint with {0} for the value";
        }
    }

    public static class AIStatHints
    {
        private static List<AIStatHintDef> active;
        // Built once per def from its statBases; null when no hint applies, which is most abilities
        private static readonly Dictionary<AbilityDef, string> hints = new Dictionary<AbilityDef, string>();

        private static void Init()
        {
            active = new List<AIStatHintDef>();
            foreach (var def in DefDatabase<AIStatHintDef>.AllDefsListForReading)
            {
                if (!def.requiredMod.NullOrEmpty() && !ModsConfig.IsActive(def.requiredMod)) continue;
                def.statDef = DefDatabase<StatDef>.GetNamedSilentFail(def.stat);
                if (def.statDef == null)
                {
                    Log.Warning($"[Ability AI] Stat hint {def.defName} is off: no StatDef {def.stat}.");
                    continue;
                }
                active.Add(def);
            }
        }

        /// <summary>The lines for this def's stats, joined; null when none applies. Main thread (tooltips).</summary>
        public static string Hint(AbilityDef def)
        {
            if (def == null) return null;
            if (active == null) Init();
            if (active.Count == 0) return null;
            if (hints.TryGetValue(def, out string hint)) return hint;
            try { hint = Build(def); }
            catch (Exception e)
            {
                hint = null;
                Log.ErrorOnce($"[Ability AI] Reading {def.defName}'s stat hints failed: {e}", def.shortHash ^ 0x5A17);
            }
            hints[def] = hint;
            return hint;
        }

        private static string Build(AbilityDef def)
        {
            if (def.statBases == null) return null;
            string hint = null;
            for (int i = 0; i < active.Count; i++)
                for (int j = 0; j < def.statBases.Count; j++)
                {
                    if (def.statBases[j].stat != active[i].statDef) continue;
                    // Abstract, as The Force reads it when it applies the change
                    float value = def.GetStatValueAbstract(active[i].statDef);
                    if (Math.Abs(value) < 0.001f) break;
                    string line = active[i].hint.Formatted(value.ToString("0.##")).Resolve();
                    hint = hint == null ? line : hint + "\n" + line;
                    break;
                }
            return hint;
        }
    }
}
