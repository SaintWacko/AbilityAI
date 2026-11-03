using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

// Zone casting as work, the way recasting is (AbilityAIRecastWork.cs): a Basic work giver, so the Work tab orders it
// against everything else a colonist does and it never runs drafted. Each colonist takes the nearest target nobody else
// has claimed, for each ability it has on autocast with a zone rule. A walk-up ability's own cast job walks to the
// target; a ranged one (Enthrall) first walks to a cell in range with a line of sight (JobDriver_ZoneCastWalk), which
// then queues the cast.
namespace AbilityAI
{
    public class WorkGiver_PsycastZone : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !AbilityAIZoneWork.MayWork(pawn);

        public override Job NonScanJob(Pawn pawn) => AbilityAIZoneWork.TryGiveJob(pawn);
    }

    public class JobDriver_ZoneCastWalk : JobDriver
    {
        // targetA: the thing to cast at; targetB: the cell to cast from
        private bool Claimed() => PsycastZones.Of(pawn.Map)?.ClaimFor(pawn, job.targetA.Thing, job.loadID) == true;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => Claimed();

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !Claimed());
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            Toil cast = ToilMaker.MakeToil("ZoneCast");
            cast.initAction = () =>
            {
                Thing target = job.targetA.Thing;
                var zones = PsycastZones.Of(pawn.Map);
                Job castJob = zones == null ? null : AbilityAIZoneWork.MakeCastJob(pawn, target);
                if (castJob == null)
                {
                    zones?.Wait(pawn, target, Find.TickManager.TicksGame + AbilityAIZoneWork.RetryTicks);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // The claim follows the cast, so nobody else heads for the target during the warmup
                pawn.jobs.jobQueue.EnqueueFirst(castJob);
                zones.Claim(target, pawn, castJob.loadID);
            };
            cast.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return cast;
        }
    }

    internal static class AbilityAIZoneWork
    {
        internal const int RetryTicks = 600;
        // After a cast is handed out: a cast the ability refuses on start ends at once and drops the claim, and without
        // the wait the next work check would hand out the same cast in the same tick. Short, since a target may want
        // another cast (a wall Mend only half restored).
        private const int CastWaitTicks = 120;
        // Of each ability's targets, how many one look for work tries, nearest first, before giving up for now
        private const int MaxTargetsTried = 4;

        private struct Ready
        {
            public AIAbility ability;
            public AbilityAIExtension rule;
        }

        private static readonly List<AIAbility> collected = new List<AIAbility>();
        private static readonly List<Ready> ready = new List<Ready>();
        private static readonly List<Thing> candidates = new List<Thing>();
        private static IntVec3 sortFrom;
        private static readonly System.Comparison<Thing> ByDistance =
            (a, b) => (a.Position - sortFrom).LengthHorizontalSquared.CompareTo((b.Position - sortFrom).LengthHorizontalSquared);

        /// <summary>The pawn's job with this id is its current or a queued one.</summary>
        internal static bool JobPending(Pawn pawn, int jobId)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.jobs == null) return false;
            if (pawn.CurJob != null && pawn.CurJob.loadID == jobId) return true;
            var queue = pawn.jobs.jobQueue;
            for (int i = 0; i < queue.Count; i++)
                if (queue[i].job.loadID == jobId) return true;
            return false;
        }

        internal static bool MayWork(Pawn pawn)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast || !AbilityAISettings.UndraftedAutocast) return false;
            if (!AbilityAIZones.Any || pawn.Drafted || pawn.Faction == null || !pawn.Faction.IsPlayer || pawn.InMentalState) return false;
            if (!AbilityAIAutocast.HasAny(pawn)) return false;
            var zones = PsycastZones.Of(pawn.Map);
            return zones != null && zones.AnyDue(Find.TickManager.TicksGame) && AIAbilitySources.HasAny(pawn);
        }

        internal static Job TryGiveJob(Pawn pawn)
        {
            var zones = PsycastZones.Of(pawn.Map);
            if (zones == null) return null;
            try
            {
                CollectReady(pawn, zones);
                int tick = Find.TickManager.TicksGame;
                for (int r = 0; r < ready.Count; r++)
                {
                    AIAbility a = ready[r].ability;
                    AbilityAIZoneTarget kind = ready[r].rule.zoneTarget;
                    var targets = zones.TargetsFor(a.Def, kind, tick);
                    if (targets.Count == 0) continue;
                    candidates.Clear();
                    for (int i = 0; i < targets.Count; i++)
                    {
                        Thing t = targets[i];
                        if (t.Map != pawn.Map || zones.Waiting(pawn, t, tick) || zones.ClaimedByOther(t, pawn)) continue;
                        candidates.Add(t);
                    }
                    sortFrom = pawn.Position;
                    candidates.Sort(ByDistance);
                    for (int i = 0; i < candidates.Count && i < MaxTargetsTried; i++)
                    {
                        Thing t = candidates[i];
                        if (!AbilityAIZones.Wants(kind, t)) continue;
                        if (!Available(pawn, a, t))
                        {
                            zones.Wait(pawn, t, tick + RetryTicks);
                            continue;
                        }
                        // In reach for a walk-up ability (its cast job walks there), in range and sight for a ranged one
                        if (a.ValidInRange(t))
                        {
                            Job cast = a.MakeCastJob(t, t);
                            if (cast == null)
                            {
                                zones.Wait(pawn, t, tick + RetryTicks);
                                continue;
                            }
                            zones.Claim(t, pawn, cast.loadID);
                            zones.Wait(pawn, t, tick + CastWaitTicks);
                            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                                Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} on {t.LabelShort} at {t.Position} (zone work)");
                            return cast;
                        }
                        if (!a.IsTouch && a.Range > 0f && AbilityAIRecastWork.TryFindStandCell(pawn, a, t.Position, out IntVec3 stand))
                        {
                            Job walk = JobMaker.MakeJob(AbilityAIZoneDefOf.AAI_ZoneCastWalk, t, stand);
                            zones.Claim(t, pawn, walk.loadID);
                            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                                Log.Message($"[Ability AI] {pawn.LabelShort} walks to {stand} to cast {a.Label} on {t.LabelShort} (zone work)");
                            return walk;
                        }
                        zones.Wait(pawn, t, tick + RetryTicks);
                    }
                }
                return null;
            }
            finally
            {
                // The wrappers hold their pawns
                collected.Clear();
                ready.Clear();
                candidates.Clear();
            }
        }

        /// <summary>The cast at the end of a walk, or null when nothing the pawn has can be cast at the target from where
        /// it stands now.</summary>
        internal static Job MakeCastJob(Pawn pawn, Thing target)
        {
            var zones = PsycastZones.Of(pawn.Map);
            if (zones == null || target == null || !target.Spawned) return null;
            try
            {
                CollectReady(pawn, zones);
                for (int r = 0; r < ready.Count; r++)
                {
                    AIAbility a = ready[r].ability;
                    if (!AbilityAIZones.Wants(ready[r].rule.zoneTarget, target) || !Available(pawn, a, target) || !a.ValidInRange(target)) continue;
                    Job cast = a.MakeCastJob(target, target);
                    if (cast == null) continue;
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} on {target.LabelShort} at {target.Position} (zone work)");
                    return cast;
                }
                return null;
            }
            finally
            {
                collected.Clear();
                ready.Clear();
            }
        }

        // Everything about this target short of reach and range: not forbidden, not in someone else's hands (a warden
        // talking to the prisoner, a hauler carrying the corpse off), nobody already casting at it, and the ability's
        // own rules and the pawn's psycast limits
        private static bool Available(Pawn pawn, AIAbility a, Thing t)
        {
            if (!(t is Pawn) && t.IsForbidden(pawn)) return false;
            if (!pawn.CanReserve(t)) return false;
            if (AbilityAIZones.Busy(t, a.Def) || OtherCasting(pawn, a.Def, t)) return false;
            return a.ValidIgnoringRange(t) && AbilityAIDriver.PsycastWithinLimits(a, t);
        }

        // A colonist casting this psycast at the target by hand, or one whose claim was lost to a load
        private static bool OtherCasting(Pawn pawn, Def def, Thing t)
        {
            var pawns = pawn.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == pawn || p.CurJob == null || p.CurJob.targetA.Thing != t) continue;
                if (AIAbilitySources.TryGetRunningCast(p, out AIAbility running, out _) && running.Def == def) return true;
            }
            return false;
        }

        // The pawn's abilities with a zone rule that it has on autocast, that have a zone to work in (or need none), and
        // that it could cast now
        private static void CollectReady(Pawn pawn, PsycastZones zones)
        {
            ready.Clear();
            collected.Clear();
            AIAbilitySources.Collect(pawn, collected);
            for (int i = 0; i < collected.Count; i++)
            {
                AIAbility a = collected[i];
                var rule = AbilityAIZones.RuleOf(a.Def);
                if (rule == null || !a.Drivable || a.PlayerAutocastElsewhere) continue;
                if (AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off) continue;
                if (AbilityAIZones.NeedsZone(rule.zoneTarget) && !zones.HasCells(a.Def)) continue;
                if (!AbilityAIHooks.NotVetoed(pawn, a) || !a.CanCastNow || !AbilityAIDriver.PsycastWithinLimits(a, LocalTargetInfo.Invalid)) continue;
                if (!AbilityAIZones.CasterMay(rule, pawn)) continue;
                ready.Add(new Ready { ability = a, rule = rule });
            }
        }
    }
}
