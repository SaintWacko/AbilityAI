using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Heat vents: psycasts that clear neural heat (VPE's Aggressive Heat Dump, Neural Heat Detonation and Heat Focus).
// Cast whenever they could, they would spend psyfocus clearing heat nothing needed cleared, so a rule marked heatVent
// is only tried when heat is in the way of something else: the driver tries its vents after nothing was cast, and only
// if one of the pawn's psycasts that heat alone held back (AbilityAIDriver.HeatBlocked) found a target in this
// situation; zone, marker and recast work cast an out-of-combat vent when heat alone holds back a psycast that has
// work waiting there. Operator's choice (zone-psycasts.md, phase 3); a third "vent above" mark on the limits gizmo is
// the fallback if this plays badly.
namespace AbilityAI
{
    /// <summary>
    /// Shed heat: a self-cast that turns neural heat into psyfocus (VPE's Heat Focus). Cast only when it would remove
    /// at least minHeatShed: heat above the heatFloorStat value, capped by the psyfocus room left (focusPerHeat psyfocus
    /// per point). Out of combat by default. Meant for heatVent rules only. Cost: two stat reads.
    /// </summary>
    public class AbilityAIWorker_ShedHeat : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.OutOfCombat;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (!AbilityAIHeat.Sheds(ctx.pawn, ext))
            {
                if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = "it would shed too little heat (psyfocus nearly full, or heat near its floor)";
                return false;
            }
            target = ctx.pawn;
            return true;
        }
    }

    public static class AbilityAIHeat
    {
        private static readonly HashSet<Def> noted = new HashSet<Def>();

        /// <summary>Some ability def has a heatVent rule: without one the driver keeps no list of abilities that
        /// weren't ready, and the work givers don't ask about heat.</summary>
        public static bool Any => noted.Count > 0;

        /// <summary>Called at startup for every ability def of every system, beside AbilityAIZones.Note.</summary>
        public static void Note(Def abilityDef)
        {
            var exts = abilityDef?.modExtensions;
            if (exts == null) return;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension rule && (rule.heatVent || rule.For(AbilityAIRole.Npc).heatVent
                    || rule.For(AbilityAIRole.FriendlyNpc).heatVent))
                {
                    noted.Add(abilityDef);
                    return;
                }
        }

        private static bool IsVent(Def def) => def != null && noted.Contains(def);

        /// <summary>The tooltip line for an ability with a heatVent rule, or null.</summary>
        internal static string Hint(Def abilityDef) => IsVent(abilityDef) ? (string)"AAI_HeatVent_Hint".Translate() : null;

        /// <summary>Whether a Shed heat cast would take at least minHeatShed off the pawn's heat now.</summary>
        internal static bool Sheds(Pawn pawn, AbilityAIExtension ext)
        {
            var pe = pawn.psychicEntropy;
            if (pe == null) return false;
            float floor = ext.heatFloorStat != null ? pawn.GetStatValue(ext.heatFloorStat, true, 250) : 0f;
            float above = pe.EntropyValue - floor;
            if (above <= 0f) return false;
            float shed = ext.focusPerHeat > 0f ? Mathf.Min(1f - pe.CurrentPsyfocus, above * ext.focusPerHeat) / ext.focusPerHeat : above;
            return shed >= ext.minHeatShed;
        }

        /// <summary>For the work givers: blocked (heat alone holds it back) has work waiting, so cast one of the pawn's
        /// out-of-combat Shed heat vents instead (FindWorkVent). abilities: the pawn's collected abilities. Null when
        /// there is none.</summary>
        internal static Job TryWorkVentJob(Pawn pawn, List<AIAbility> abilities, AIAbility blocked, string work)
        {
            AIAbility a = FindWorkVent(pawn, abilities);
            if (a == null) return null;
            Job job = a.MakeCastJob(pawn, pawn);
            if (job != null && AbilityAISettings.DebugLog && Prefs.DevMode)
                Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} to vent heat: {blocked.Label} has work but heat holds it back ({work})");
            return job;
        }

        /// <summary>The pawn's out-of-combat Shed heat vent that is on autocast, ready and would shed enough now, or
        /// null. Depends on the pawn alone, so the work givers ask it before looking for work a held-back psycast has.</summary>
        internal static AIAbility FindWorkVent(Pawn pawn, List<AIAbility> abilities)
        {
            for (int i = 0; i < abilities.Count; i++)
            {
                AIAbility a = abilities[i];
                if (!IsVent(a.Def) || !a.Drivable || a.PlayerAutocastElsewhere) continue;
                if (AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off) continue;
                var exts = a.Def.modExtensions;
                for (int j = 0; j < exts.Count; j++)
                {
                    if (!(exts[j] is AbilityAIExtension rule) || !rule.heatVent || !(rule.Worker is AbilityAIWorker_ShedHeat worker)) continue;
                    if (worker.Combat == AbilityAICombat.InCombat || !Sheds(pawn, rule)) continue;
                    if (!AbilityAIHooks.NotVetoed(pawn, a) || !a.CanCastNow || !AbilityAIDriver.PsycastWithinLimits(a, pawn)) break;
                    if (!a.Valid(pawn)) break;
                    return a;
                }
            }
            return null;
        }
    }
}
