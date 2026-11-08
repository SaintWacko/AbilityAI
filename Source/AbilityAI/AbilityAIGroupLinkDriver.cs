using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

// The driver's side of group links (AbilityAIGroupLink.cs). VEF records the ability being cast on the caster's comp when
// its cast job is made, so the Word's job can't be made while the link's waits in the queue: the plan is kept on the
// pawn's state and the Word's job is made the moment the link's job ends, from the constant think tree, which vanilla
// asks before anything else each time a pawn starts a job. A link this AI cast comes off once the Word has gone off (or
// never will): the Word's effects stay on each pawn for their own time, and a link left up would carry the next Word
// anyone casts to everyone still linked.
namespace AbilityAI
{
    public static partial class AbilityAIDriver
    {
        // How long after the link's job ends its Word may still be started before the link is taken down
        private const int LinkWaitTicks = 60;
        // After a link that came to nothing (never landed, or its Word couldn't follow): the pawn doesn't link again for
        // this long, or a target the survey counted but the Word refuses would have it pay for a link every check
        private const int LinkRetryTicks = 600;

        /// <summary>Plans this AI is following. Only a fast-path gate for LinkCheck, as BarriersUp is.</summary>
        internal static int LinksUp;

        private static readonly List<Pawn> linkTargets = new List<Pawn>();

        // Why TryLinkFirst cast no link for the Word about to be cast alone, for its dev log line; set only with the dev
        // log on, and only when the pawn has a Group link
        private static string noLinkWhy;

        /// <summary>Records that pawn's job linkJob casts a group link for word, by rule (the version for the caster's
        /// role). Zone work calls it for Word of Trust; the driver for every other Word.</summary>
        internal static void PlanLink(Pawn pawn, Def word, AbilityAIExtension rule, Def linker, Job linkJob)
        {
            var state = states.GetValue(pawn, _ => new PawnState());
            if (state.linkWord == null) LinksUp++;
            state.linkWord = word;
            state.linkerDef = linker;
            state.linkRule = rule;
            state.linkJobId = linkJob.loadID;
            state.linkWordJobId = -1;
            state.linkHediff = null;
            state.linkDeadline = -1;
        }

        private static void ClearLink(PawnState state)
        {
            if (state.linkWord == null) return;
            state.linkWord = null;
            state.linkerDef = null;
            state.linkRule = null;
            state.linkHediff = null;
            state.linkJobId = state.linkWordJobId = state.linkDeadline = -1;
            if (LinksUp > 0) LinksUp--;
        }

        // Ends the plan and takes the link down, unless the Word went off and the player keeps links up
        private static void EndLink(Pawn pawn, PawnState state, string why, bool wordOut)
        {
            Hediff link = state.linkHediff ?? (AbilityAIZoneWork.JobPending(pawn, state.linkJobId) ? null : AbilityAIGroupLink.LinkOf(pawn));
            if (!wordOut) WaitToLink(pawn, state);
            ClearLink(state);
            if (link == null || (wordOut && AbilityAISettings.GroupLinkKeep)) return;
            var hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs == null || !hediffs.Contains(link)) return;
            pawn.health.RemoveHediff(link);
            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                Log.Message($"[Ability AI] {pawn.LabelShort} drops {link.LabelBase}: {why}");
        }

        private static void WaitToLink(Pawn pawn, PawnState state)
        {
            if (state.linkerDef == null) return;
            int until = Find.TickManager.TicksGame + LinkRetryTicks;
            if (state.abilityNext == null) state.abilityNext = new Dictionary<Def, int>();
            state.abilityNext[state.linkerDef] = until;
            // Zone work waits per pawn and psycast on its own map component
            if (state.linkRule?.Worker is AbilityAIWorker_Zone && pawn.Spawned) PsycastZones.Of(pawn.Map)?.WaitZone(pawn, state.linkerDef, until);
        }

        /// <summary>Called from the constant-tree postfix for every pawn while any plan is followed, ahead of every
        /// reason the driver might not evaluate the pawn, so a link this AI cast always comes down.</summary>
        internal static void LinkCheck(Pawn pawn)
        {
            if (evaluating || !states.TryGetValue(pawn, out PawnState state) || state.linkWord == null) return;
            if (pawn.Dead || !pawn.Spawned)
            {
                ClearLink(state);
                return;
            }
            if (AbilityAIZoneWork.JobPending(pawn, state.linkJobId)) return;
            if (state.linkHediff == null) state.linkHediff = AbilityAIGroupLink.LinkOf(pawn);
            if (state.linkWordJobId >= 0)
            {
                if (!AbilityAIZoneWork.JobPending(pawn, state.linkWordJobId)) EndLink(pawn, state, "its Word has gone off", true);
                return;
            }
            int tick = Find.TickManager.TicksGame;
            if (state.linkDeadline < 0) state.linkDeadline = tick + LinkWaitTicks;
            else if (tick >= state.linkDeadline) EndLink(pawn, state, "its Word was never cast", false);
        }

        // The link's job is over and its Word hasn't been made yet
        private static bool LinkWordDue(Pawn pawn, PawnState state) =>
            state.linkWord != null && state.linkWordJobId < 0 && !AbilityAIZoneWork.JobPending(pawn, state.linkJobId);

        // The Word the link was cast for, on the nearest linked target, once everyone linked may take it. Anything
        // else (the link never landed, the Word can't be cast, someone walked in the Word mustn't reach) ends the plan,
        // takes the link down and keeps the pawn from linking again for LinkRetryTicks; everything else waits for the
        // pawn's next check.
        private static Job LinkFollowUp(Pawn pawn, PawnState state, List<AIAbility> abilities, int tick, bool player)
        {
            Hediff link = AbilityAIGroupLink.LinkOf(pawn);
            if (link == null)
            {
                if (AbilityAISettings.DebugLog && Prefs.DevMode) Log.Message($"[Ability AI] {pawn.LabelShort}'s group link never landed");
                WaitToLink(pawn, state);
                ClearLink(state);
                return null;
            }
            state.linkHediff = link;
            AbilityAIExtension rule = state.linkRule;
            AIAbility word = null;
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i].Def == state.linkWord) { word = abilities[i]; break; }
            string why = null;
            if (word == null || rule?.Worker == null) why = "its Word is gone";
            else if (!word.CanCastNow || (player ? !PlayerAllowed(pawn, word) || !PsycastWithinLimits(word, LocalTargetInfo.Invalid) : !AbilityAIHooks.Allowed(pawn, word)))
                why = $"{word.Label} can't be cast now";
            else if (!AbilityAIGroupLink.LinkedMayTake(ctx, pawn, word, rule, out Pawn refused))
                why = $"{refused.LabelShort} is linked, and {word.Label} mustn't reach them";
            var linked = AbilityAIGroupLink.Linked(link);
            Pawn target = null;
            Job cast = null;
            if (why == null && linked == null) why = "the link holds nobody";
            if (why == null)
            {
                target = AbilityAIGroupLink.NearestLinkedTarget(ctx, pawn, word, rule, linked);
                if (target == null) why = $"no linked target for {word.Label}";
                else if (player && !PsycastWithinLimits(word, target)) why = $"over this pawn's autocast limits for {target.LabelShort}";
                else if (Offering)
                {
                    // Made at Start; a job owner that passes it over leaves the link to come down at linkDeadline
                    Offer(state, new AbilityAICandidate
                    {
                        kind = AbilityAICandidateKind.LinkWord, ability = word, rule = rule, target = target, dest = target,
                        priority = rule.priority,
                    });
                    return null;
                }
                else if ((cast = word.MakeCastJob(target, target)) == null) why = $"{word.Label} was refused at {target.LabelShort}";
            }
            if (cast == null)
            {
                EndLink(pawn, state, why, false);
                return null;
            }
            CommitLinkWord(pawn, state, tick, player, word, rule, target, linked, cast);
            return cast;
        }

        private static Job StartLinkWord(Pawn pawn, PawnState state, int tick, bool player, AIAbility word, AbilityAIExtension rule, Pawn target)
        {
            Hediff link = AbilityAIGroupLink.LinkOf(pawn);
            var linked = link != null ? AbilityAIGroupLink.Linked(link) : null;
            if (linked == null || target == null) return null;
            Job cast = word.MakeCastJob(target, target);
            if (cast == null)
            {
                EndLink(pawn, state, $"{word.Label} was refused at {target.LabelShort}", false);
                return null;
            }
            CommitLinkWord(pawn, state, tick, player, word, rule, target, linked, cast);
            return cast;
        }

        private static void CommitLinkWord(Pawn pawn, PawnState state, int tick, bool player, AIAbility word, AbilityAIExtension rule,
            Pawn target, List<Pawn> linked, Job cast)
        {
            state.linkWordJobId = cast.loadID;
            state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
            if (rule.minGapSeconds > 0f)
            {
                if (state.abilityNext == null) state.abilityNext = new Dictionary<Def, int>();
                state.abilityNext[word.Def] = tick + Mathf.RoundToInt(rule.minGapSeconds * GenTicks.TicksPerRealSecond);
            }
            // A Word from zone work: no other colonist heads for a pawn this cast reaches that its zone work wants
            if (rule.Worker is AbilityAIWorker_Zone && PsycastZones.Of(pawn.Map) is PsycastZones zones)
                for (int i = 0; i < linked.Count; i++)
                    if (linked[i] != null && AbilityAIZones.Wants(rule, linked[i])) zones.Claim(linked[i], pawn, cast.loadID);
            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                Log.Message($"[Ability AI] {pawn.LabelShort} casts {word.Label} on {target.LabelShort} through its group link ({linked.Count} linked)");
            AbilityAIHooks.NotifyCast(pawn, word, target, target);
            AbilityAICombatAI.HoldForCast(pawn, word, target, cast);
            if (player) QueueAfterCast(pawn, state, false);
        }

        // Before a Word the driver chose: with a link up, everyone linked must be able to take it (null, refused set);
        // without one, the pawn's Group link first when the link reaches enough of the Word's targets where it stands.
        // True with the linker and how many targets it would link; false to go on with the Word alone.
        private static bool FindLinkFirst(Pawn pawn, PawnState state, int tick, bool player, AbilityAIRole role, AIAbility word,
            AbilityAIExtension ext, LocalTargetInfo target, out AIAbility linker, out int count)
        {
            noLinkWhy = null;
            count = 0;
            linker = null;
            if (!ext.linkFirst || !AbilityAIGroupLink.Any) return false;
            bool log = AbilityAISettings.DebugLog && Prefs.DevMode;
            linker = AbilityAIGroupLink.FindLinker(pawn, collected, player, role, out bool held);
            if (linker == null)
            {
                if (log && held) noLinkWhy = "its Group link can't be cast now";
                return false;
            }
            if (state.abilityNext != null && state.abilityNext.TryGetValue(linker.Def, out int next) && tick < next)
            {
                if (log) noLinkWhy = $"waiting {next - tick} ticks after a link that came to nothing";
                return false;
            }
            try
            {
                float radius = linker.DefaultRadius;
                if (radius <= 0f) return false;
                if (!AbilityAIGroupLink.Survey(ctx, pawn, pawn.Position, radius, word, ext, linkTargets, out Pawn refused))
                {
                    if (log) noLinkWhy = $"{refused.LabelShort} would be linked";
                    return false;
                }
                AbilityAIGroupLink.KeepInReach(linkTargets, pawn.Position, radius, word);
                count = linkTargets.Count;
                if (count < AbilityAIGroupLink.MinTargets)
                {
                    if (log) noLinkWhy = $"{count} target{(count == 1 ? "" : "s")} in reach, under {AbilityAIGroupLink.MinTargets}";
                    return false;
                }
                string notWorth = AbilityAIGroupLink.WhyNotWorth(pawn, linker, word, target, count, player);
                if (notWorth != null)
                {
                    if (log) noLinkWhy = $"{count} targets, but {notWorth}";
                    return false;
                }
                return true;
            }
            finally { linkTargets.Clear(); }
        }

        // The link's cast job for a FindLinkFirst that said yes, or null (noLinkWhy set) to go on with the Word alone
        private static Job StartLinkFirst(Pawn pawn, PawnState state, int tick, bool player, AIAbility linker, AIAbility word,
            AbilityAIExtension ext, int count, bool castAtPrey)
        {
            Job job = linker.MakeCastJob(pawn, pawn);
            if (job == null)
            {
                if (AbilityAISettings.DebugLog && Prefs.DevMode) noLinkWhy = "Group link refused when its job was made";
                return null;
            }
            PlanLink(pawn, word.Def, ext, linker.Def, job);
            state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                Log.Message($"[Ability AI] {pawn.LabelShort} casts {linker.Label} first, to reach {count} targets of {word.Label}");
            AbilityAIHooks.NotifyCast(pawn, linker, pawn, pawn);
            AbilityAICombatAI.HoldForCast(pawn, linker, pawn, job);
            if (player) QueueAfterCast(pawn, state, castAtPrey);
            return job;
        }
    }
}
