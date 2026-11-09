using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Casting at cast markers as work, the way recasting is (AbilityAIRecastWork.cs): a Basic work giver, so the Work tab
// orders it against everything else a colonist does and it never runs drafted. Each colonist takes the nearest due
// marker nobody else has claimed, for the abilities it has on autocast; in range it casts at once, otherwise it walks to
// a cell in range with a line of sight first (JobDriver_CastSpotWalk), which then queues the cast.
namespace AbilityAI
{
    public class WorkGiver_CastSpot : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !AbilityAISpotWork.MayWork(pawn);

        public override Job NonScanJob(Pawn pawn) => AbilityAISpotWork.TryGiveJob(pawn);
    }

    public class JobDriver_CastSpotWalk : JobDriver
    {
        // targetA: the marker's cell; targetB: the cell to cast from
        private CastSpot Spot() => CastSpots.Of(pawn.Map)?.ClaimFor(pawn, job.targetA.Cell, job.loadID);

        public override bool TryMakePreToilReservations(bool errorOnFailed) => Spot() != null;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() =>
            {
                var s = Spot();
                return s == null || !s.due || s.Dormant;
            });
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            Toil cast = ToilMaker.MakeToil("CastSpotCast");
            cast.initAction = () =>
            {
                var s = Spot();
                Job castJob = s == null ? null : AbilityAISpotWork.MakeCastJob(pawn, s);
                if (castJob == null)
                {
                    s?.Wait(pawn, Find.TickManager.TicksGame + AbilityAISpotWork.RetryTicks);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // The claim follows the cast, so nobody else heads for the marker during the warmup
                pawn.jobs.jobQueue.EnqueueFirst(castJob);
                AbilityAIDriver.NoteQueuedCast(pawn, castJob);
                s.Claim(pawn, castJob.loadID);
            };
            cast.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return cast;
        }
    }

    internal static class AbilityAISpotWork
    {
        internal const int RetryTicks = 600;
        // How many due markers one look for work tries, nearest first, before giving up for now
        private const int MaxSpotsTried = 3;

        private struct Ready
        {
            public AIAbility ability;
            public AbilityAIExtension rule;
        }

        private static readonly List<AIAbility> collected = new List<AIAbility>();
        private static readonly List<Ready> ready = new List<Ready>();
        // Ready but for neural heat (AbilityAIDriver.HeatBlocked), only filled while some rule is a heat vent
        private static readonly List<Ready> heatBlocked = new List<Ready>();
        private static readonly List<CastSpot> candidates = new List<CastSpot>();
        private static IntVec3 sortFrom;
        private static readonly System.Comparison<CastSpot> ByDistance =
            (a, b) => (a.anchor - sortFrom).LengthHorizontalSquared.CompareTo((b.anchor - sortFrom).LengthHorizontalSquared);

        // JobGiver_Work asks this for every colonist looking for work: a map with nothing due stops at one lookup
        internal static bool MayWork(Pawn pawn)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast || !AbilityAISettings.UndraftedAutocast) return false;
            if (!AbilityAISpots.Any || pawn.Drafted || pawn.Faction == null || !pawn.Faction.IsPlayer || pawn.InMentalState) return false;
            if (AbilityAIPerspectiveShift.Controlled(pawn)) return false;
            var spots = CastSpots.Of(pawn.Map);
            if (spots == null || spots.dueCount <= 0) return false;
            return AbilityAIAutocast.HasAny(pawn) && AIAbilitySources.HasAny(pawn);
        }

        internal static Job TryGiveJob(Pawn pawn)
        {
            var spots = CastSpots.Of(pawn.Map);
            if (spots == null) return null;
            try
            {
                CollectReady(pawn);
                if (ready.Count == 0 && heatBlocked.Count == 0) return null;
                int tick = Find.TickManager.TicksGame;
                var list = spots.Spots;
                for (int i = 0; i < list.Count && ready.Count > 0; i++)
                {
                    var s = list[i];
                    // A marker this pawn is already walking to or casting at isn't handed out again: a second walk would
                    // take over the claim, and the think tree would drop it as a repeat of the running one
                    if (!s.due || s.Waiting(pawn, tick) || s.claimant != null && s.ClaimHolds() || s.Dormant || ReadyIndex(s.CastDef) < 0) continue;
                    candidates.Add(s);
                }
                sortFrom = pawn.Position;
                candidates.Sort(ByDistance);
                for (int i = 0; i < candidates.Count && i < MaxSpotsTried; i++)
                {
                    var s = candidates[i];
                    Ready r = ready[ReadyIndex(s.CastDef)];
                    AIAbility a = r.ability;
                    if (!CastableAt(pawn, a, r.rule, s.anchor))
                    {
                        s.Wait(pawn, tick + RetryTicks);
                        continue;
                    }
                    if (a.ValidInRange(s.anchor))
                    {
                        Job cast = a.MakeCastJob(s.anchor, s.anchor);
                        if (cast == null)
                        {
                            s.Wait(pawn, tick + RetryTicks);
                            continue;
                        }
                        s.Claim(pawn, cast.loadID);
                        // A cast the verb refuses on start ends at once and drops the claim: without the wait the next
                        // work check would hand out the same cast in the same tick. One that lands leaves the ground
                        // unready (spawners, then crops or trees), so the wait costs nothing then.
                        s.Wait(pawn, tick + RetryTicks);
                        if (AbilityAISettings.DebugLog && Prefs.DevMode)
                            Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} at the marker at {s.anchor} (marker work)");
                        return cast;
                    }
                    if (AbilityAIRecastWork.TryFindStandCell(pawn, a, s.anchor, out IntVec3 stand))
                    {
                        Job walk = JobMaker.MakeJob(AbilityAISpotDefOf.AAI_CastSpotWalk, s.anchor, stand);
                        // Claimed now, so the walk finds this marker and not another psycast's at the same cell
                        s.Claim(pawn, walk.loadID);
                        if (AbilityAISettings.DebugLog && Prefs.DevMode)
                            Log.Message($"[Ability AI] {pawn.LabelShort} walks to {stand} to cast {a.Label} at the marker at {s.anchor} (marker work)");
                        return walk;
                    }
                    s.Wait(pawn, tick + RetryTicks);
                }
                return TryVent(pawn, list, tick);
            }
            finally
            {
                // The wrappers hold their pawns
                collected.Clear();
                ready.Clear();
                heatBlocked.Clear();
                candidates.Clear();
            }
        }

        // Nothing else to cast, and heat alone holds back a psycast with a due, unclaimed marker: a heat vent
        // (AbilityAIHeat), if the pawn has one on autocast
        private static Job TryVent(Pawn pawn, List<CastSpot> list, int tick)
        {
            if (heatBlocked.Count == 0 || AbilityAIHeat.FindWorkVent(pawn, collected) == null) return null;
            for (int r = 0; r < heatBlocked.Count; r++)
            {
                Def def = heatBlocked[r].ability.Def;
                int tried = 0;
                for (int i = 0; i < list.Count && tried < MaxSpotsTried; i++)
                {
                    var s = list[i];
                    if (!s.due || s.Dormant || s.CastDef != def || s.Waiting(pawn, tick) || (s.claimant != null && s.ClaimHolds())) continue;
                    tried++;
                    // A marker these psycasts serve alone never reaches the main loop's waits
                    if (!CastableAt(pawn, heatBlocked[r].ability, heatBlocked[r].rule, s.anchor, true))
                    {
                        s.Wait(pawn, tick + RetryTicks);
                        continue;
                    }
                    // As for a cast: a vent refused on start would otherwise be handed out again in the same tick
                    s.Wait(pawn, tick + RetryTicks);
                    return AbilityAIHeat.TryWorkVentJob(pawn, collected, heatBlocked[r].ability, "marker work");
                }
            }
            return null;
        }

        /// <summary>The cast at the end of a walk, or null when the pawn can't cast at the marker from where it stands
        /// now, or the ground is no longer ready.</summary>
        internal static Job MakeCastJob(Pawn pawn, CastSpot spot)
        {
            try
            {
                CollectReady(pawn);
                int r = ReadyIndex(spot.CastDef);
                if (r < 0) return null;
                AIAbility a = ready[r].ability;
                if (!CastableAt(pawn, a, ready[r].rule, spot.anchor) || !a.ValidInRange(spot.anchor)) return null;
                Job cast = a.MakeCastJob(spot.anchor, spot.anchor);
                if (cast != null && AbilityAISettings.DebugLog && Prefs.DevMode)
                    Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} at the marker at {spot.anchor} (marker work)");
                return cast;
            }
            finally
            {
                collected.Clear();
                ready.Clear();
                heatBlocked.Clear();
            }
        }

        // The ground looked at again with this caster's own radius (Tree Sprout's grows with sensitivity): the marker's
        // state is up to 250 ticks old, and a cast that landed since leaves spawners. Crops count out to the marker's
        // radius whatever the caster's. Then nobody already casting there, and the ability's own rules and the limits.
        // forVent: for a psycast heat holds back (TryVent), so the limits are left out and the rules asked ignoring heat.
        private static bool CastableAt(Pawn pawn, AIAbility a, AbilityAIExtension rule, IntVec3 cell, bool forVent = false)
        {
            float radius = a.DefaultRadius > 0f ? a.DefaultRadius : rule.spotRadius;
            var state = AbilityAISpots.Evaluate(pawn.Map, cell, rule.spotKind, Mathf.Max(rule.spotRadius, radius), radius,
                AbilityAISpots.MinCells(rule.spotKind), out int open);
            if (state != AbilityAISpotState.Due)
            {
                if (AbilityAISettings.DebugLog && Prefs.DevMode && state == AbilityAISpotState.TooFewCells)
                    Log.Message($"[Ability AI] {pawn.LabelShort} leaves the {a.Label} marker at {cell}: {open} open cells in its radius {radius:0.#} (marker work)");
                return false;
            }
            if (OtherCastingNear(pawn, a.Def, cell, rule.spotRadius)) return false;
            if (!forVent) return a.ValidIgnoringRange(cell) && AbilityAIDriver.PsycastWithinLimits(a, cell);
            AIAbility.IgnoringHeat = true;
            try { return a.ValidIgnoringRange(cell); }
            finally { AIAbility.IgnoringHeat = false; }
        }

        // A colonist casting this psycast near the marker by hand, or one whose claim was lost to a load: until it lands
        // the ground still looks ready
        private static bool OtherCastingNear(Pawn pawn, Def def, IntVec3 cell, float radius)
        {
            var pawns = pawn.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            float r2 = radius * radius;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == pawn || p.CurJob == null || !p.CurJob.targetA.IsValid) continue;
                if ((p.CurJob.targetA.Cell - cell).LengthHorizontalSquared > r2) continue;
                if (AIAbilitySources.TryGetRunningCast(p, out AIAbility running, out _) && running.Def == def) return true;
            }
            return false;
        }

        private static int ReadyIndex(Def def)
        {
            for (int i = 0; i < ready.Count; i++)
                if (ready[i].ability.Def == def) return i;
            return -1;
        }

        // The pawn's abilities with a Spot rule that it has on autocast and could cast now
        private static void CollectReady(Pawn pawn)
        {
            ready.Clear();
            heatBlocked.Clear();
            collected.Clear();
            AIAbilitySources.Collect(pawn, collected);
            for (int i = 0; i < collected.Count; i++)
            {
                AIAbility a = collected[i];
                var rule = AbilityAISpots.RuleOf(a.Def);
                if (rule == null || !a.Drivable || a.PlayerAutocastElsewhere) continue;
                if (AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off) continue;
                if (!AbilityAIHooks.NotVetoed(pawn, a)) continue;
                bool castable = a.CanCastNow && AbilityAIDriver.PsycastWithinLimits(a, LocalTargetInfo.Invalid);
                if (!castable && !(AbilityAIHeat.Any && AbilityAIDriver.HeatBlocked(a, true))) continue;
                (castable ? ready : heatBlocked).Add(new Ready { ability = a, rule = rule });
            }
        }
    }
}
