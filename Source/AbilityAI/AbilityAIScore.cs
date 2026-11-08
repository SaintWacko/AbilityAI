using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

// Dynamic priority (the scored driver, .circinus/handoffs/scored-driver.md): a rule's effective score is its XML
// priority plus a bonus from what its worker reports the chosen target is worth (AbilityAIWorker.ReportsValue,
// AbilityAIContext.TargetValue). A rule or worker that knows nothing of this, a third-party subclass included, reports
// no value and scores at its priority, exactly as before.
namespace AbilityAI
{
    /// <summary>How much one target is worth to a caster, for the scored driver's values: registered with
    /// AbilityAIHooks.RegisterTargetWeight, so a mod with its own threat model (a job owner) and Ability AI value the
    /// same raider the same way.</summary>
    public interface IAbilityAITargetWeight
    {
        /// <summary>1 = an ordinary enemy; Ability AI clamps the answer to [1, 3], so a weak pawn still counts as a whole
        /// target. Asked on the main thread, at most once per target per evaluation of a caster: answer from a cache.
        /// target is a pawn or an enemy turret.</summary>
        float WeightOf(Pawn caster, Thing target);
    }

    public static class AbilityAIScore
    {
        public const float MinWeight = 1f, MaxWeight = 3f;
        private static readonly List<IAbilityAITargetWeight> providers = new List<IAbilityAITargetWeight>();

        internal static void Register(IAbilityAITargetWeight provider)
        {
            if (provider != null && !providers.Contains(provider)) providers.Add(provider);
        }

        /// <summary>The first provider's answer, else the pawn's kind's combat power / 100; clamped to [1, 3]. Uncached:
        /// workers ask AbilityAIContext.Weight, which keeps the answer for the evaluation.</summary>
        internal static float WeightOf(Pawn caster, Thing t)
        {
            while (providers.Count > 0)
            {
                float w;
                try { w = providers[0].WeightOf(caster, t); }
                catch (Exception e)
                {
                    // Dropped rather than retried: a provider that throws once would throw for every target of every
                    // caster, every evaluation
                    Log.ErrorOnce($"[Ability AI] target weight {providers[0].GetType().FullName}.WeightOf threw, so it is dropped for this session: {e}",
                        providers[0].GetType().GetHashCode() ^ 0x5A6);
                    providers.RemoveAt(0);
                    continue;
                }
                return float.IsNaN(w) ? MinWeight : Mathf.Clamp(w, MinWeight, MaxWeight);
            }
            // Turrets and anything else without a kind count as one ordinary target
            return t is Pawn p && p.kindDef != null ? Mathf.Clamp(p.kindDef.combatPower / 100f, MinWeight, MaxWeight) : MinWeight;
        }

        /// <summary>Priority gained per point of value past the worker's ValueBase: the rule's priorityPerValue, else the
        /// setting.</summary>
        public static float Step(AbilityAIExtension ext) => ext.priorityPerValue >= 0f ? ext.priorityPerValue : AbilityAISettings.PriorityPerValue;

        /// <summary>The most priority a value can add: the rule's maxPriorityBonus, else the setting.</summary>
        public static float Cap(AbilityAIExtension ext) => ext.maxPriorityBonus >= 0f ? ext.maxPriorityBonus : AbilityAISettings.MaxPriorityBonus;

        /// <summary>What a value adds to the rule's priority; 0 for no value (negative) or a worker that reports none.</summary>
        public static float Bonus(AbilityAIWorker worker, float value)
        {
            if (value < 0f || !worker.ReportsValue) return 0f;
            float cap = Cap(worker.ext);
            if (cap <= 0f) return 0f;
            return Mathf.Min(cap, Mathf.Max(0f, value - worker.ValueBase) * Step(worker.ext));
        }

        /// <summary>The rule's effective score for a value: its priority plus Bonus.</summary>
        public static float Score(AbilityAIWorker worker, float value) => worker.ext.priority + Bonus(worker, value);
    }
}
