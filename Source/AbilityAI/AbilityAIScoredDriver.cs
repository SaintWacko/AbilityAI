using System.Collections.Generic;
using Verse;
using Verse.AI;

// The scored driver (.circinus/handoffs/scored-driver.md step 3): with Dynamic priority on, the driver's own casts pick
// the best-scored candidate (AbilityAIScore: priority plus a bonus from the value its worker reports for the target it
// found) instead of the first by priority that finds a target. A candidate is searched only while it could still win:
// its priority plus the bonus its worker's ValueBound allows must beat the best option found so far, so a rule
// without a value below the best is skipped unsearched, as the first-fit loop never reached it either. Job owners'
// offers (step 4) keep the top offerMax instead of the best, so the bar is the offerMax-th best option. Off, and for
// heat vents, TryCandidates runs the first-fit loop unchanged.
namespace AbilityAI
{
    public static partial class AbilityAIDriver
    {
        // A candidate that found a target and passed Valid and the limits, with what its search left behind that a
        // later search would overwrite (CastAtPrey, the barrier statics)
        private struct Option
        {
            public int index;
            public LocalTargetInfo target;
            public LocalTargetInfo dest;
            public bool castAtPrey;
            public int barrierCount;
            public float barrierRadius;
            public float value;
            public float score;
        }

        private static readonly List<Option> options = new List<Option>();
        // Dev log only: the chosen cast's score, for CommitCast's line; set just before the cast is made and cleared
        // after, as noLinkWhy is
        private static string scoreNote;
        // Per candidate of the list being scored: its worker has run (or it was turned away for good), so a later round
        // after a refused winner only looks at the ones a bound skipped
        private static readonly List<bool> searched = new List<bool>();

        private static Job TryCandidatesScored(List<Candidate> list, Pawn pawn, PawnState state, int tick, bool player, AbilityAIRole role,
            ref bool waitingForFight)
        {
            options.Clear();
            searched.Clear();
            for (int i = 0; i < list.Count; i++) searched.Add(false);
            ctx.WantsValue = true;
            try
            {
                if (Offering)
                {
                    SearchScored(list, pawn, player, offerMax, ref waitingForFight);
                    OfferScored(list, pawn, state, tick, player, role);
                    return null;
                }
                while (true)
                {
                    SearchScored(list, pawn, player, 1, ref waitingForFight);
                    int best = BestOption();
                    if (best < 0) return null;
                    Option o = options[best];
                    options.RemoveAt(best);
                    AIAbility a = list[o.index].ability;
                    var ext = list[o.index].ext;
                    // The link step asks Valid again, which must see the winner's exclusivity as its search did
                    exclusiveDef = ext.Worker.Exclusive ? a.Def : null;
                    affectedCounted = ext.Worker.CountsAffectedAroundCaster;
                    if (AbilityAISettings.DebugLog && Prefs.DevMode) scoreNote = ScoreNote(ext, o.score, o.value) + Overtook(list, o);
                    Job job = CastChosen(pawn, state, tick, player, role, a, ext, o.target, o.dest, o.castAtPrey,
                        o.barrierCount, o.barrierRadius, null);
                    scoreNote = null;
                    if (job != null) return job;
                    // Refused at the link step or when its job was made: the next best option, and the candidates a
                    // bound skipped against this one get their search now that the bar is lower
                }
            }
            finally
            {
                ctx.WantsValue = false;
                options.Clear();
                searched.Clear();
                scoreNote = null;
            }
        }

        // "priority 2 -> 3.5, worth 5.8, needs 2": the value is the worker's weighted count, so a line can be checked
        // against the step and cap in the settings
        private static string ScoreNote(AbilityAIExtension ext, float score, float value)
        {
            var worker = ext.Worker;
            string s = score != ext.priority ? $"priority {ext.priority} -> {score:0.##}" : $"priority {ext.priority}";
            if (score != ext.priority && score - ext.priority >= AbilityAIScore.Cap(ext) - 0.001f) s += " (cap)";
            // A help worker's value is urgency points from 0 (ValueBase 0), so "needs 0" would say nothing
            if (value >= 0f && worker.ReportsValue)
                s += worker.ValueBase > 0f ? $", worth {value:0.##}, needs {worker.ValueBase:0.##}" : $", worth {value:0.##}";
            return s;
        }

        // The highest-priority option the winner overtook, if any, so a test fight's log shows a big-group cast beating
        // a single-target one. Options a bound skipped are not here, but those could never have won.
        private static string Overtook(List<Candidate> list, Option winner)
        {
            int priority = list[winner.index].ext.priority;
            int beaten = -1;
            for (int i = 0; i < options.Count; i++)
            {
                if (list[options[i].index].ext.priority <= priority) continue;
                if (beaten < 0 || options[i].index < options[beaten].index) beaten = i;
            }
            if (beaten < 0) return "";
            Option o = options[beaten];
            var ext = list[o.index].ext;
            string score = o.score != ext.priority ? $"priority {ext.priority} -> {o.score:0.##}" : $"priority {ext.priority}";
            AIAbility a = list[o.index].ability;
            // Two rules of one ability (a single-target rule above an area one) are told apart by their workers
            string rule = a.Def == list[winner.index].ability.Def ? $"{ext.Worker.GetType().Name}, " : "";
            return $"; ahead of {a.Label} ({rule}{score})";
        }

        // Searches every candidate not yet searched that could still make the top keep options, in base priority order.
        // While offering, a Word a pawn linked to the caster mustn't take is refused here rather than after the search
        // (where the driver's own cast refuses it), so the options are all things that can be offered.
        private static void SearchScored(List<Candidate> list, Pawn pawn, bool player, int keep, ref bool waitingForFight)
        {
            int bar = Bar(keep);
            for (int i = 0; i < list.Count; i++)
            {
                if (searched[i]) continue;
                AIAbility a = list[i].ability;
                var ext = list[i].ext;
                var worker = ext.Worker;
                if (!CombatAllows(worker))
                {
                    searched[i] = true;
                    if (worker.Combat != AbilityAICombat.InCombat) waitingForFight = false;
                    WhyNot(a, ext, worker.Combat == AbilityAICombat.InCombat ? "not in combat" : "in combat");
                    continue;
                }
                waitingForFight = false;
                if (bar >= 0)
                {
                    // The cap first, which costs nothing; the bound only for a rule the cap leaves in the running
                    float cap = worker.ReportsValue ? AbilityAIScore.Cap(ext) : 0f;
                    if (Beaten(ext.priority + (cap > 0f ? cap : 0f), i, bar)) continue;
                    if (cap > 0f && Beaten(ext.priority + AbilityAIScore.Bonus(worker, worker.ValueBound(ctx, a)), i, bar)) continue;
                }
                searched[i] = true;
                exclusiveDef = worker.Exclusive ? a.Def : null;
                affectedCounted = worker.CountsAffectedAroundCaster;
                ctx.CastAtPrey = false;
                ctx.TargetValue = -1f;
                ctx.NoTargetWhy = null;
                // Valid below must still see the rule's exclusivity and its own count of an area cast at the caster, as
                // its search did, or an AoE hostile self rule is judged by its comps against the caster and refused
                bool found = worker.TryFindTarget(ctx, a, out LocalTargetInfo target, out LocalTargetInfo dest);
                string refusal = null;
                if (!found) refusal = ctx.NoTargetWhy ?? "no target";
                else
                {
                    if (!dest.IsValid) dest = target;
                    if (!Valid(a, target))
                        refusal = whyNot != null ? $"the ability refused {TargetLabel(target)}" : "";
                    else if (player && !PsycastWithinLimits(a, target))
                        refusal = whyNot != null ? $"over this pawn's autocast limits for {TargetLabel(target)}" : "";
                    else if (Offering && AbilityAIGroupLink.Registered && AbilityAIGroupLink.IsWord(a.Def)
                        && !AbilityAIGroupLink.LinkedMayTake(ctx, pawn, a, ext, out Pawn refused))
                        refusal = whyNot != null ? $"{refused.LabelShort} is linked to it and mustn't be reached" : "";
                }
                exclusiveDef = null;
                affectedCounted = false;
                if (refusal != null)
                {
                    if (whyNot != null) WhyNot(a, ext, refusal);
                    continue;
                }
                bool barrier = worker is AbilityAIWorker_Barrier;
                float value = worker.ReportsValue ? ctx.TargetValue : -1f;
                options.Add(new Option
                {
                    index = i, target = target, dest = dest, castAtPrey = ctx.CastAtPrey,
                    barrierCount = barrier ? AbilityAIWorker_Barrier.LastCount : 0,
                    barrierRadius = barrier ? AbilityAIWorker_Barrier.LastRadius : 0f,
                    value = value, score = AbilityAIScore.Score(worker, value),
                });
                bar = Bar(keep);
            }
            ctx.CastAtPrey = false;
        }

        // The option a candidate must beat to make the top keep: the keep-th best, or -1 while there are fewer options
        // than that and anything found is kept. Options are one per candidate searched, a handful, so the quadratic
        // count for keep > 1 is cheaper than keeping them sorted.
        private static int Bar(int keep)
        {
            if (options.Count < keep) return -1;
            if (keep == 1) return BestOption();
            for (int i = 0; i < options.Count; i++)
            {
                int ahead = 0;
                for (int j = 0; j < options.Count; j++)
                    if (j != i && Better(j, i)) ahead++;
                if (ahead == keep - 1) return i;
            }
            return -1;
        }

        private static readonly System.Comparison<Option> BestFirst = (x, y) =>
            x.score != y.score ? y.score.CompareTo(x.score) : x.index.CompareTo(y.index);

        // The options as a job owner's candidates, best first, as the first-fit offer does: a Word that would take the
        // pawn's Group link first is offered as LinkFirst with the Word alone after it, even past offerMax. Both carry
        // the Word's score and value.
        private static void OfferScored(List<Candidate> list, Pawn pawn, PawnState state, int tick, bool player, AbilityAIRole role)
        {
            options.Sort(BestFirst);
            for (int k = 0; k < options.Count && !OfferFull; k++)
            {
                Option o = options[k];
                AIAbility a = list[o.index].ability;
                var ext = list[o.index].ext;
                // FindLinkFirst asks Valid again, which must see the rule's exclusivity as its search did
                exclusiveDef = ext.Worker.Exclusive ? a.Def : null;
                affectedCounted = ext.Worker.CountsAffectedAroundCaster;
                if (AbilityAIGroupLink.Registered && AbilityAIGroupLink.IsWord(a.Def) && AbilityAIGroupLink.LinkOf(pawn) == null
                    && FindLinkFirst(pawn, state, tick, player, role, a, ext, o.target, out AIAbility linker, out int linkCount))
                {
                    Offer(state, new AbilityAICandidate
                    {
                        kind = AbilityAICandidateKind.LinkFirst, ability = a, rule = ext, target = o.target, dest = o.dest,
                        priority = ext.priority, linker = linker, expected = linkCount, castAtPrey = o.castAtPrey,
                        score = o.score, value = o.value, scored = true,
                    });
                }
                Offer(state, new AbilityAICandidate
                {
                    kind = AbilityAICandidateKind.Cast, ability = a, rule = ext, target = o.target, dest = o.dest,
                    priority = ext.priority, castAtPrey = o.castAtPrey,
                    barrierCount = o.barrierCount, barrierRadius = o.barrierRadius,
                    score = o.score, value = o.value, scored = true,
                });
                noLinkWhy = null;
                exclusiveDef = null;
                affectedCounted = false;
            }
        }

        // Ties go to the higher base priority, then the list's order; the list is in base priority order, so the
        // lower index wins a tie
        private static bool Better(int x, int y)
        {
            Option a = options[x], b = options[y];
            return a.score > b.score || (a.score == b.score && a.index < b.index);
        }

        // Whether candidate index, at the most it could score, still loses to option best
        private static bool Beaten(float ceiling, int index, int best)
        {
            Option b = options[best];
            return ceiling < b.score || (ceiling == b.score && b.index < index);
        }

        private static int BestOption()
        {
            int best = -1;
            for (int i = 0; i < options.Count; i++)
                if (best < 0 || Better(i, best)) best = i;
            return best;
        }

        // The cast for a target the driver chose: for a Word of psycast the group link step (through a link only if
        // everyone linked may take it; without one, perhaps the pawn's Group link first), then the ability's own job.
        // Null when refused, with the dev log's reason written; the caller goes on to its next choice. exclusiveDef
        // and affectedCounted are the chosen rule's on the way in and cleared on the way out.
        private static Job CastChosen(Pawn pawn, PawnState state, int tick, bool player, AbilityAIRole role, AIAbility a,
            AbilityAIExtension ext, LocalTargetInfo target, LocalTargetInfo dest, bool castAtPrey, int barrierCount, float barrierRadius,
            AIAbility ventFor)
        {
            if (AbilityAIGroupLink.Registered && AbilityAIGroupLink.IsWord(a.Def))
            {
                if (AbilityAIGroupLink.LinkOf(pawn) != null)
                {
                    if (!AbilityAIGroupLink.LinkedMayTake(ctx, pawn, a, ext, out Pawn refused))
                    {
                        exclusiveDef = null;
                        affectedCounted = false;
                        if (whyNot != null) WhyNot(a, ext, $"{refused.LabelShort} is linked to it and mustn't be reached");
                        return null;
                    }
                }
                else if (FindLinkFirst(pawn, state, tick, player, role, a, ext, target, out AIAbility linker, out int linkCount))
                {
                    Job link = StartLinkFirst(pawn, state, tick, player, linker, a, ext, linkCount, castAtPrey);
                    if (link != null)
                    {
                        exclusiveDef = null;
                        affectedCounted = false;
                        return link;
                    }
                }
            }
            // Made before anything is recorded: another system's ability may still refuse at this point
            // (VEF runs its extensions' own Valid and PreCast here)
            Job cast = a.MakeCastJob(target, dest);
            if (cast == null)
            {
                exclusiveDef = null;
                affectedCounted = false;
                noLinkWhy = null;
                if (whyNot != null) WhyNot(a, ext, $"refused when its cast job was made, at {TargetLabel(target)}");
                return null;
            }
            exclusiveDef = null;
            affectedCounted = false;
            CommitCast(pawn, state, tick, player, a, ext, target, dest, cast, castAtPrey, barrierCount, barrierRadius, ventFor);
            return cast;
        }
    }
}
