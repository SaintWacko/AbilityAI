using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

// Job owners: a mod with its own per-pawn arbiter (one thing that decides every job a pawn starts) takes the pawns it
// owns away from the driver's constant-tree postfix and asks for candidates instead (GetCandidates), starting the one
// it picks through Start. Nothing is held between the offer and the start: the claims mates respect are read from
// what pawns are running (BuildClaims), so offering five candidates blocks nobody, and a candidate the owner passes
// over needs no call. Start does all the bookkeeping the driver does when it casts on its own.
//
// Cost: an owned pawn pays one interface call per constant-tree check (Owns, which owners must answer from a field or
// a cached lookup). GetCandidates is the driver's own evaluation with its intervals, so asking every tick costs what
// the postfix costs today; it goes on past the first cast found, so each extra candidate asked for is one more
// worker target search.
namespace AbilityAI
{
    /// <summary>A mod that decides every job its pawns start. Registered with AbilityAIHooks.RegisterJobOwner.</summary>
    public interface IAbilityAIJobOwner
    {
        /// <summary>Asked on every constant-tree check of a pawn with abilities: answer from a field or a cached lookup,
        /// never a search. True = the driver starts nothing for this pawn; the owner asks AbilityAIDriver.GetCandidates
        /// and starts what it picks with AbilityAIDriver.Start.</summary>
        bool Owns(Pawn pawn);
    }

    /// <summary>What a rule does in a fight, for an arbiter scoring candidates. Each worker has a default; a rule's
    /// category field overrides it.</summary>
    public enum AbilityAICategory : byte
    {
        /// <summary>In a rule: the worker's own. Never on a candidate.</summary>
        Default,
        /// <summary>Breaks an enemy's aim or cast (Interrupt).</summary>
        Interrupt,
        /// <summary>Gets an ally out of danger (Rescue, Skip's Rescue).</summary>
        Rescue,
        /// <summary>Heals or restores (Recovery, Bloodfeed).</summary>
        Recovery,
        /// <summary>Hurts or disables enemies (the hostile area, cone, beam and target workers, Deploy, Skip's Repel).</summary>
        Offensive,
        /// <summary>Protects the caster or its side where it stands (Cover, Defensive, Barrier).</summary>
        Defensive,
        /// <summary>Strengthens the caster or allies (SelfBuff, AoE friendly, Target friendly).</summary>
        Buff,
        /// <summary>Moves the caster (Movement, Skip's Escape).</summary>
        Movement,
        /// <summary>Anything else (Calm, Firefighting, Shed heat, group links, colonist work).</summary>
        Utility,
    }

    public enum AbilityAICandidateKind : byte
    {
        /// <summary>Cast ability at target / dest.</summary>
        Cast,
        /// <summary>Cast the pawn's Group link first, so the Word in ability (at target) reaches everyone linked on the
        /// pawn's next check. The Word alone always follows as its own Cast candidate, even past max.</summary>
        LinkFirst,
        /// <summary>The Word a Group link was cast for, at target through the link. Offered alone: while it is due
        /// nothing else is looked at, and the link comes down if no Word is made in time.</summary>
        LinkWord,
        /// <summary>Walk to dest (a cell) to cast ability, at target when it names a thing. Offered only when no cast
        /// is. Its category is Movement; the rule's own is rule.Worker.Category.</summary>
        Approach,
        /// <summary>A player pawn's walk back after a Full autocast walk. ability is null; dest is where it stood or the
        /// cell already chosen, though Start may pick a covered cell nearby instead (ReturnToCover). With no walk
        /// possible, Start returns the attack the pawn was on before the walk, or null.</summary>
        Return,
    }

    /// <summary>One thing the driver would do for a pawn now, from AbilityAIDriver.GetCandidates. Hand it to
    /// AbilityAIDriver.Start in the same tick to get its job; it holds nothing, so one not started needs no call.</summary>
    public struct AbilityAICandidate
    {
        public AbilityAICandidateKind kind;
        public AIAbility ability;
        public AbilityAIExtension rule;
        public LocalTargetInfo target;
        public LocalTargetInfo dest;
        /// <summary>The rule's priority (higher first). Return: int.MinValue.</summary>
        public int priority;
        public AbilityAICategory category;

        internal int offer;
        internal int tick;
        internal AIAbility linker;
        internal bool castAtPrey;
        internal int barrierCount;
        internal float barrierRadius;
        internal int expected;
        internal AIAbility ventFor;
        internal string at;
    }

    public static partial class AbilityAIDriver
    {
        // Set while GetCandidates runs Evaluate: the paths that would start a job add a candidate here instead
        private static List<AbilityAICandidate> offerInto;
        private static int offerMax;
        private static int offerId;

        private static bool Offering => offerInto != null;
        private static bool Offered => offerInto != null && offerInto.Count > 0;
        private static bool OfferFull => offerInto.Count >= offerMax;

        private static void Offer(PawnState state, AbilityAICandidate c)
        {
            c.offer = state.offer;
            c.tick = Find.TickManager.TicksGame;
            if (c.category == AbilityAICategory.Default)
                c.category = c.rule?.Worker != null ? c.rule.Worker.Category : AbilityAICategory.Movement;
            offerInto.Add(c);
        }

        private static Job ReturnOrOffer(Pawn pawn, PawnState state)
        {
            if (!Offering) return ReturnJob(pawn, state);
            Offer(state, new AbilityAICandidate
            {
                kind = AbilityAICandidateKind.Return, priority = int.MinValue, category = AbilityAICategory.Movement,
                dest = state.returnCell.IsValid ? state.returnCell : state.home,
            });
            return null;
        }

        /// <summary>For a job owner (IAbilityAIJobOwner): clears into, then fills it with up to max things the driver
        /// would do for this pawn now, best first, under the same rules, settings, autocast modes and intervals as its
        /// own casts (a pawn checked less than 30 ticks ago, or within the gap after a cast, gets nothing). Casts are
        /// sorted by rule priority; a walk into range or back is offered only when no cast is. Starts nothing, makes no
        /// job and holds nothing: pass the one picked to Start in the same tick. Ask at most once per pawn per check:
        /// an ask that offers something makes the batch before it stale, and passing over a whole batch still costs the
        /// pawn its next 30 ticks, as a check that cast nothing does.</summary>
        public static void GetCandidates(Pawn pawn, List<AbilityAICandidate> into, int max = 3)
        {
            if (into == null) return;
            into.Clear();
            if (pawn == null || max <= 0 || evaluating || !AbilityAISettings.Enabled) return;
            if (!AIAbilitySources.HasAny(pawn)) return;
            var control = ControlOf(pawn);
            if (control == Control.None || !AIAbilitySources.AnyWithExtension(pawn)) return;
            var state = states.GetValue(pawn, _ => new PawnState());
            // A new id only once something is offered: vanilla can run the constant tree twice in a tick, and a second
            // ask that stops at the interval check must not make the first batch stale
            int previous = state.offer;
            state.offer = offerId + 1;
            offerInto = into;
            offerMax = max;
            try
            {
                Evaluate(pawn, control);
                if (into.Count > 0) offerId++;
                else state.offer = previous;
                if (into.Count == 0 && whyNot != null && whyNot.Length > 0) LogWhyNot(pawn, control);
            }
            catch (Exception e)
            {
                into.Clear();
                state.offer = previous;
                Log.ErrorOnce($"[Ability AI] GetCandidates threw for {pawn.def.defName}: {e}", pawn.def.shortHash ^ 0x7A13);
            }
            finally
            {
                offerInto = null;
                whyNot = null;
                noLinkWhy = null;
            }
        }

        /// <summary>For a job owner: the job for a candidate from GetCandidates this tick, with the bookkeeping the
        /// driver does for its own casts (the gap before the next cast, the rule's minGapSeconds, barrier and group
        /// link tracking, IAbilityAIHook.Notify_AICast; for the player's pawns, the fight job queued to resume after
        /// the cast and a Full walk's way home). Start the returned job at once, from any think node. Null when the
        /// candidate is stale (another tick, or another candidate of the batch already started) or its ability
        /// refused when the job was made: try the next.</summary>
        public static Job Start(Pawn pawn, AbilityAICandidate candidate)
        {
            if (pawn == null || evaluating || !states.TryGetValue(pawn, out PawnState state)) return null;
            if (candidate.offer != state.offer || candidate.tick != Find.TickManager.TicksGame) return null;
            Job job;
            try { job = StartCandidate(pawn, state, candidate); }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Ability AI] Start threw for {pawn.def.defName}: {e}", pawn.def.shortHash ^ 0x7A14);
                return null;
            }
            finally
            {
                // A refused Group link leaves its reason for the Word's dev log line, which may never come
                noLinkWhy = null;
            }
            if (job != null)
            {
                state.offer = 0;
                state.ownerJobId = job.loadID;
            }
            return job;
        }

        /// <summary>The pawn's current job is a cast the driver or a job owner's Start made, still running;
        /// warmupTicksLeft is -1 before the warmup (walking into range), else the ticks left.</summary>
        public static bool IsAICast(Pawn pawn, out int warmupTicksLeft)
        {
            warmupTicksLeft = -1;
            return pawn != null && IsOwnCast(pawn, out warmupTicksLeft);
        }

        private static Job StartCandidate(Pawn pawn, PawnState state, AbilityAICandidate c)
        {
            int tick = Find.TickManager.TicksGame;
            bool player = ControlOf(pawn) == Control.Player;
            switch (c.kind)
            {
                case AbilityAICandidateKind.Cast:
                {
                    Job cast = c.ability.MakeCastJob(c.target, c.dest);
                    if (cast == null) return null;
                    CommitCast(pawn, state, tick, player, c.ability, c.rule, c.target, c.dest, cast, c.castAtPrey,
                        c.barrierCount, c.barrierRadius, c.ventFor);
                    return cast;
                }
                case AbilityAICandidateKind.LinkFirst:
                    return StartLinkFirst(pawn, state, tick, player, c.linker, c.ability, c.rule, c.expected, c.castAtPrey);
                case AbilityAICandidateKind.LinkWord:
                    return StartLinkWord(pawn, state, tick, player, c.ability, c.rule, (Pawn)c.target.Thing);
                case AbilityAICandidateKind.Approach:
                    return StartApproach(pawn, state, player, c.ability, c.rule, c.dest.Cell, c.target, c.expected, c.at);
                case AbilityAICandidateKind.Return:
                    return ReturnJob(pawn, state);
            }
            return null;
        }

        // What TryCandidates does once a cast job is made, for the driver's own casts and Start alike
        private static void CommitCast(Pawn pawn, PawnState state, int tick, bool player, AIAbility a, AbilityAIExtension ext,
            LocalTargetInfo target, LocalTargetInfo dest, Job cast, bool castAtPrey, int barrierCount, float barrierRadius, AIAbility ventFor)
        {
            state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
            if (ext.minGapSeconds > 0f)
            {
                if (state.abilityNext == null) state.abilityNext = new Dictionary<Def, int>();
                state.abilityNext[a.Def] = tick + Mathf.RoundToInt(ext.minGapSeconds * GenTicks.TicksPerRealSecond);
            }
            if (ext.Worker is AbilityAIWorker_Barrier)
            {
                TrackBarrier(state, ext.hediff, barrierCount, barrierRadius, tick);
                state.barrierJobId = cast.loadID;
            }
            WatchWarmup(state, a, ext, target, cast);
            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} ({ext.Worker.GetType().Name}) on {TargetLabel(target)}"
                    + (ventFor != null ? $" to vent heat: {ventFor.Label} had a target but heat held it back" : "")
                    + (noLinkWhy != null ? $", no group link first: {noLinkWhy}" : "")
                    + (Offering || !evaluating ? ", started by its job owner" : ""));
            noLinkWhy = null;
            AbilityAIHooks.NotifyCast(pawn, a, target, dest);
            AbilityAICombatAI.HoldForCast(pawn, a, target, cast);
            if (player) QueueAfterCast(pawn, state, castAtPrey, cast);
            else if (castAtPrey) ResumeHunt(pawn);
        }
    }
}
