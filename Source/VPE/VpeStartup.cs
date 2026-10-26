using System;
using System.Collections.Generic;
using RimWorld;
using VanillaPsycastsExpanded;
using VanillaPsycastsExpanded.Skipmaster;
using VanillaPsycastsExpanded.Technomancer;
using Verse;
using VefAbility = VEF.Abilities.Ability;
using VefAbilityDef = VEF.Abilities.AbilityDef;

// Vanilla Psycasts Expanded's side of VEF abilities: what a psycast costs and what it does that VEF's own extensions
// don't say. VPE marks its psycasts with AbilityExtension_Psycast and charges psyfocus and neural heat from it on the
// vanilla psychic entropy tracker. Psycasts² postfixes both cost methods, so they are always called rather than the
// extension's fields read.
namespace AbilityAI.Vpe
{
    [StaticConstructorOnStartup]
    internal static class VpeStartup
    {
        static VpeStartup()
        {
            Vef.VefAIAbility.RegisterCosts(new VpeCosts());
            Vef.VefAIAbility.RegisterEffectsReader(ReadEffects);
            BetterAutocastBridge.Init();
        }

        private static void ReadEffects(VefAbilityDef def, AIAbilityEffects e)
        {
            var psycast = def.GetModExtension<AbilityExtension_Psycast>();
            // VPE's ValidateTarget then refuses a target without psychic sensitivity; WouldAffect leaves those out
            // before the AI gets that far
            if (psycast != null && psycast.psychic) e.psychic = true;
            // The Cover worker keeps walls off pawns and off the caster's own cell
            var wallraise = def.GetModExtension<AbilityExtension_Wallraise>();
            if (wallraise?.pattern != null)
            {
                e.wall = true;
                e.wallPattern = wallraise.pattern;
            }
            else if (def.abilityClass != null && typeof(Ability_IceBlock).IsAssignableFrom(def.abilityClass))
            {
                e.wall = true;
                e.wallPattern = IceBlockPattern;
            }
            // VPE's smokepop makes its smoke in code, with no extension saying so: the Cover worker then doesn't cast
            // it again while the caster already stands in smoke
            if (def.abilityClass != null && typeof(Ability_Smokepop).IsAssignableFrom(def.abilityClass)) e.smoke = true;
            // Haywire marks a turret in VPE's own set rather than with a hediff, so skipAffected asks the set
            if (def.abilityClass != null && typeof(Ability_AffectMechs).IsAssignableFrom(def.abilityClass))
                e.thingAffected = IsHaywire;
        }

        private static readonly Func<Thing, bool> IsHaywire = t => HaywireManager.HaywireThings.Contains(t);

        // Ability_IceBlock.Cast: 20 random cells of the 5x5 square centred on the target. Any of the 25 may get ice.
        private static readonly List<IntVec2> IceBlockPattern = Square(2);

        private static List<IntVec2> Square(int half)
        {
            var cells = new List<IntVec2>();
            for (int x = -half; x <= half; x++)
                for (int z = -half; z <= half; z++) cells.Add(new IntVec2(x, z));
            return cells;
        }
    }

    internal sealed class VpeCosts : Vef.IVefAbilityCosts
    {
        public bool Covers(VefAbilityDef def) => def.GetModExtension<AbilityExtension_Psycast>() != null;

        // AbilityExtension_Psycast.IsEnabledForPawn's refusals that build a translated reason, without the reason. The
        // path lock it also checks hands back a stored string, so that one is left to it.
        public bool Affordable(VefAbility ability)
        {
            var psycast = ability.def.GetModExtension<AbilityExtension_Psycast>();
            if (psycast == null) return false;
            Pawn pawn = ability.pawn;
            var pe = pawn.psychicEntropy;
            if (pe == null || pe.PsychicSensitivity < float.Epsilon || pawn.Downed) return false;
            var psycasts = pawn.Psycasts();
            if (psycasts == null || psycasts.CurrentlyChanneling != null) return false;
            // Strictly more, as Hediff_PsycastAbilities.SufficientPsyfocusPresent
            if (!(pe.CurrentPsyfocus > psycast.GetPsyfocusUsedByPawn(pawn))) return false;
            return !pe.WouldOverflowEntropy(psycast.GetEntropyUsedByPawn(pawn));
        }

        // Word of Serenity charges by the target's break on top of the base cost, unscaled, as its Cast does
        public float PsyfocusCost(VefAbility ability, LocalTargetInfo target)
        {
            var psycast = ability.def.GetModExtension<AbilityExtension_Psycast>();
            if (psycast == null) return 0f;
            float cost = psycast.GetPsyfocusUsedByPawn(ability.pawn);
            if (psycast is AbilityExtension_PsycastWordOfSerenity serenity)
            {
                Map map = ability.pawn.Map;
                cost += target.IsValid && map != null
                    ? serenity.PsyfocusCostForTarget(target.ToGlobalTargetInfo(map))
                    : LeastBreakCost(serenity);
            }
            return cost;
        }

        public float HeatGain(VefAbility ability) =>
            ability.def.GetModExtension<AbilityExtension_Psycast>()?.GetEntropyUsedByPawn(ability.pawn) ?? 0f;

        // The three per-intensity costs default to -1 (unset); unset ones are left out
        private static float LeastBreakCost(AbilityExtension_PsycastWordOfSerenity s)
        {
            float least = float.MaxValue;
            if (s.psyfocusCostForMinor >= 0f) least = Math.Min(least, s.psyfocusCostForMinor);
            if (s.psyfocusCostForMajor >= 0f) least = Math.Min(least, s.psyfocusCostForMajor);
            if (s.psyfocusCostForExtreme >= 0f) least = Math.Min(least, s.psyfocusCostForExtreme);
            return least == float.MaxValue ? 0f : least;
        }
    }
}
