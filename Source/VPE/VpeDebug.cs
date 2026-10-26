using System.Linq;
using LudeonTK;
using RimWorld;
using VanillaPsycastsExpanded;
using VEF.Abilities;
using Verse;
using VefAbilityDef = VEF.Abilities.AbilityDef;

namespace AbilityAI.Vpe
{
    // VPE's Psycasts tab, and its dev mode, only show on player pawns, so there is no way in the game to hand a raider
    // the psycasts that have Ability AI rules. This does it on any humanlike, and leaves the pawn in its lord.
    internal static class VpeDebug
    {
        private static readonly string[] RuledPsycasts =
        {
            "VPE_Stun", "VPE_Bolt", "VPE_Overshield", "VPE_PowerLeap", "VPE_Stabilize", "VPE_WordofSerenity",
        };

        // Level 6 for the heat room: VPE's heat limit grows by 5 a level, and Overshield alone is 50 heat
        private const int TestLevel = 6;

        [DebugAction("Ability AI", "Give VPE test psycasts", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void GiveTestPsycasts(Pawn pawn)
        {
            var comp = pawn.GetComp<CompAbilities>();
            if (comp == null || !pawn.RaceProps.Humanlike)
            {
                Messages.Message($"{pawn.LabelShort} can't hold VPE psycasts", pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }
            // Adding a psylink is what makes VPE add its own psycast hediff (a postfix on Hediff_Psylink.PostAdd)
            if (pawn.Psycasts() == null)
                pawn.health.AddHediff(HediffDefOf.PsychicAmplifier, pawn.health.hediffSet.GetBrain());
            var psycasts = pawn.Psycasts();
            if (psycasts == null)
            {
                Messages.Message($"VPE gave {pawn.LabelShort} no psycast hediff", pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (psycasts.level < TestLevel) psycasts.ChangeLevel(TestLevel - psycasts.level, sendLetter: false);

            var given = RuledPsycasts
                .Select(n => DefDatabase<VefAbilityDef>.GetNamedSilentFail(n))
                .Where(d => d != null)
                .ToList();
            foreach (var def in given) comp.GiveAbility(def);
            pawn.psychicEntropy?.OffsetPsyfocusDirectly(1f);

            Messages.Message($"{pawn.LabelShort}: psylink {psycasts.level}, {given.Count} psycasts, full psyfocus",
                pawn, MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
