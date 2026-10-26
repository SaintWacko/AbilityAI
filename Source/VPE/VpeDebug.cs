using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using VanillaPsycastsExpanded;
using VEF.Abilities;
using Verse;
using VefAbilityDef = VEF.Abilities.AbilityDef;

namespace AbilityAI.Vpe
{
    // VPE's Psycasts tab, and its dev mode, only show on player pawns, so there is no way in the game to hand a raider
    // the psycasts that have Ability AI rules, or to see which of them VPE would refuse it. These do both on any
    // humanlike, and leave the pawn in its lord.
    internal static class VpeDebug
    {
        private static readonly string[] RuledPsycasts =
        {
            "VPE_Stun", "VPE_Bolt", "VPE_Overshield", "VPE_PowerLeap", "VPE_Stabilize", "VPE_WordofSerenity",
        };

        // Level 6 for the heat room: VPE's heat limit grows by 5 a level, and Overshield alone is 50 heat
        private const int TestLevel = 6;

        [DebugAction("Ability AI", "Give VPE test psycasts", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void GiveTestPsycasts(Pawn pawn) =>
            Give(pawn, RuledPsycasts.Select(n => DefDatabase<VefAbilityDef>.GetNamedSilentFail(n)).Where(d => d != null));

        // One entry per VPE path, giving every psycast of it that has an Ability AI rule, to try a path's rules on
        // raiders without a psycaster of that path turning up
        [DebugAction("Ability AI", "Give VPE path psycasts", allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static List<DebugActionNode> GivePathPsycasts()
        {
            var nodes = new List<DebugActionNode>();
            var ruled = DefDatabase<VefAbilityDef>.AllDefsListForReading
                .Where(d => d.HasModExtension<AbilityAIExtension>() && d.GetModExtension<AbilityExtension_Psycast>()?.path != null)
                .GroupBy(d => d.GetModExtension<AbilityExtension_Psycast>().path)
                .OrderBy(g => g.Key.label);
            foreach (var group in ruled)
            {
                var defs = group.ToList();
                nodes.Add(new DebugActionNode($"{group.Key.LabelCap} ({defs.Count})", DebugActionType.ToolMapForPawns,
                    pawnAction: p => Give(p, defs)));
            }
            return nodes;
        }

        private static void Give(Pawn pawn, IEnumerable<VefAbilityDef> defs)
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

            var given = defs.ToList();
            foreach (var def in given) comp.GiveAbility(def);
            pawn.psychicEntropy?.OffsetPsyfocusDirectly(1f);

            Messages.Message($"{pawn.LabelShort}: psylink {psycasts.level}, {given.Count} psycasts, full psyfocus",
                pawn, MessageTypeDefOf.TaskCompletion, false);
            // GiveAbility adds a psycast whose path the pawn can't unlock (backstory, meme, gene) all the same
            ShowStatus(pawn);
        }

        [DebugAction("Ability AI", "Show VPE psycast status", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void ShowStatus(Pawn pawn)
        {
            var learned = pawn.GetComp<CompAbilities>()?.LearnedAbilities;
            if (learned == null) return;
            var sb = new StringBuilder();
            var pe = pawn.psychicEntropy;
            sb.Append(pawn.LabelShort);
            if (pe != null) sb.Append($": heat {pe.EntropyValue:F0} / {pe.MaxEntropy:F0}, psyfocus {pe.CurrentPsyfocus:P0}");
            sb.AppendLine();
            foreach (var ability in learned)
            {
                if (!ability.def.HasModExtension<AbilityAIExtension>()) continue;
                var psycast = ability.def.GetModExtension<AbilityExtension_Psycast>();
                string cost = psycast == null ? ""
                    : $" ({psycast.GetPsyfocusUsedByPawn(pawn):P0} psyfocus, {psycast.GetEntropyUsedByPawn(pawn):F0} heat)";
                string state = ability.IsEnabledForPawn(out string reason) ? "ready" : reason.NullOrEmpty() ? "refused" : reason;
                sb.AppendLine($"{ability.def.LabelCap}{cost}: {state}");
            }
            Find.WindowStack.Add(new Dialog_MessageBox(sb.ToString().TrimEnd()));
        }
    }
}
