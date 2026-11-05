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
        // targetA: the thing to cast at (for a heap cast, one corpse of the heap); targetB: the cell to cast from;
        // targetC: the pawn itself for a heap cast (Consume Bodies), so the walk ends in the cast it was for and not in
        // another psycast that takes the same corpse (Enthrall)
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
                bool heap = job.targetC.Thing == pawn;
                // A walk to link several prisoners: the link if it still reaches enough of them from here, else the Word
                // on its target alone
                Job castJob = zones == null ? null
                    : (job.def == AbilityAIZoneDefOf.AAI_ZoneLinkWalk ? AbilityAIZoneWork.MakeLinkCastJob(pawn, target) : null)
                        ?? AbilityAIZoneWork.MakeCastJob(pawn, target, heap);
                if (castJob == null)
                {
                    // A heap cast's search is gated per psycast, not per corpse
                    if (heap) AbilityAIZoneWork.WaitHeap(pawn, zones);
                    else zones?.Wait(pawn, target, Find.TickManager.TicksGame + AbilityAIZoneWork.RetryTicks);
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
        // Ready but for neural heat (AbilityAIDriver.HeatBlocked), only filled while some rule is a heat vent
        private static readonly List<Ready> heatBlocked = new List<Ready>();
        private static readonly List<Thing> candidates = new List<Thing>();
        private static IntVec3 sortFrom;
        private static readonly System.Comparison<Thing> ByDistance =
            (a, b) => (a.Position - sortFrom).LengthHorizontalSquared.CompareTo((b.Position - sortFrom).LengthHorizontalSquared);

        // Consume Bodies: of the cells with enough corpses in reach, how many one look for work checks for corpses that
        // would block them
        private const int MaxConsumeCellsTried = 6;
        private static readonly Dictionary<IntVec3, int> cellCounts = new Dictionary<IntVec3, int>();
        private static readonly List<IntVec3> consumeCells = new List<IntVec3>();
        private static readonly List<Thing> consumed = new List<Thing>();
        private static readonly List<Thing> alsoTaken = new List<Thing>();
        private static readonly List<IntVec3> blockers = new List<IntVec3>();
        private static readonly System.Comparison<IntVec3> ByCorpsesThenDistance = (a, b) =>
        {
            int byCount = cellCounts[b].CompareTo(cellCounts[a]);
            return byCount != 0 ? byCount : (a - sortFrom).LengthHorizontalSquared.CompareTo((b - sortFrom).LengthHorizontalSquared);
        };

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
                    if (kind == AbilityAIZoneTarget.ConsumableCorpses)
                    {
                        Job consume = TryGiveConsumeJob(pawn, a, zones, targets, tick);
                        if (consume != null) return consume;
                        continue;
                    }
                    candidates.Clear();
                    for (int i = 0; i < targets.Count; i++)
                    {
                        Thing t = targets[i];
                        if (t.Map != pawn.Map || zones.Waiting(pawn, t, tick) || zones.ClaimedByOther(t, pawn)) continue;
                        candidates.Add(t);
                    }
                    sortFrom = pawn.Position;
                    candidates.Sort(ByDistance);
                    // Word of Trust (AbilityAIGroupLink): through a link up already only if everyone linked may take it;
                    // without one, perhaps the pawn's Group link first, where it reaches several prisoners
                    if (AbilityAIGroupLink.Registered && AbilityAIGroupLink.IsWord(a.Def))
                    {
                        if (AbilityAIGroupLink.LinkOf(pawn) != null)
                        {
                            if (!AbilityAIGroupLink.LinkedMayTake(null, pawn, a, ready[r].rule, out _)) continue;
                        }
                        else if (ready[r].rule.linkFirst)
                        {
                            Job link = TryGiveLinkJob(pawn, a, ready[r].rule, zones, tick);
                            if (link != null) return link;
                        }
                    }
                    for (int i = 0; i < candidates.Count && i < MaxTargetsTried; i++)
                    {
                        Thing t = candidates[i];
                        if (!AbilityAIZones.Wants(kind, t)) continue;
                        if (!Available(pawn, a, t, kind))
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
                return TryVent(pawn, zones, tick);
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

        // Nothing else to cast, and heat alone holds back a psycast with an unclaimed target waiting in its zone: a heat
        // vent (AbilityAIHeat), if the pawn has one on autocast
        private static Job TryVent(Pawn pawn, PsycastZones zones, int tick)
        {
            if (heatBlocked.Count == 0 || AbilityAIHeat.FindWorkVent(pawn, collected) == null) return null;
            for (int r = 0; r < heatBlocked.Count; r++)
            {
                AIAbility a = heatBlocked[r].ability;
                AbilityAIZoneTarget kind = heatBlocked[r].rule.zoneTarget;
                var targets = zones.TargetsFor(a.Def, kind, tick);
                // Consume Bodies is cast at the caster: worth it with the setting's corpses, and not while this pawn waits
                // out a failed heap search
                if (kind == AbilityAIZoneTarget.ConsumableCorpses)
                {
                    if (targets.Count < AbilityAISettings.ZoneConsumeMinCorpses || zones.WaitingZone(pawn, a.Def, tick)) continue;
                    if (!ValidIgnoringHeat(a, pawn)) continue;
                    return AbilityAIHeat.TryWorkVentJob(pawn, collected, a, "zone work");
                }
                int tried = 0;
                for (int i = 0; i < targets.Count && tried < MaxTargetsTried; i++)
                {
                    Thing t = targets[i];
                    if (t.Map != pawn.Map || zones.Waiting(pawn, t, tick) || zones.ClaimedByOther(t, pawn) || !AbilityAIZones.Wants(kind, t)) continue;
                    tried++;
                    if (!Free(pawn, a, t, kind) || !ValidIgnoringHeat(a, t)) continue;
                    // Which vent qualifies depends on the pawn alone, so a miss here is a miss for every psycast
                    return AbilityAIHeat.TryWorkVentJob(pawn, collected, a, "zone work");
                }
            }
            return null;
        }

        private static bool ValidIgnoringHeat(AIAbility a, LocalTargetInfo target)
        {
            AIAbility.IgnoringHeat = true;
            try { return a.ValidIgnoringRange(target); }
            finally { AIAbility.IgnoringHeat = false; }
        }

        /// <summary>The cast at the end of a walk, or null when nothing the pawn has can be cast at the target from where
        /// it stands now.</summary>
        internal static Job MakeCastJob(Pawn pawn, Thing target, bool heap)
        {
            var zones = PsycastZones.Of(pawn.Map);
            if (zones == null || target == null || !target.Spawned) return null;
            try
            {
                CollectReady(pawn, zones);
                for (int r = 0; r < ready.Count; r++)
                {
                    AIAbility a = ready[r].ability;
                    if ((ready[r].rule.zoneTarget == AbilityAIZoneTarget.ConsumableCorpses) != heap) continue;
                    if (heap)
                    {
                        // The walk's target is one corpse of the heap; the cast takes the heap around where the pawn
                        // stands now
                        if (!AbilityAIZones.Wants(AbilityAIZoneTarget.ConsumableCorpses, target)) continue;
                        float radius = a.DefaultRadius;
                        if (!ConsumeReady(pawn, a) || !CorpsesAt(pawn, a.Def, zones, pawn.Position, radius, AbilityAISettings.ZoneConsumeMinCorpses))
                            continue;
                        Job consume = a.MakeCastJob(pawn, pawn);
                        if (consume == null) continue;
                        ClaimConsumed(zones, pawn, consume.loadID);
                        if (AbilityAISettings.DebugLog && Prefs.DevMode)
                            Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} at {pawn.Position} on {consumed.Count} corpses (zone work)");
                        return consume;
                    }
                    if (!AbilityAIZones.Wants(ready[r].rule.zoneTarget, target) || !Available(pawn, a, target, ready[r].rule.zoneTarget) || !a.ValidInRange(target)) continue;
                    if (!AbilityAIGroupLink.LinkedMayTake(null, pawn, a, ready[r].rule, out _)) continue;
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
                heatBlocked.Clear();
                consumed.Clear();
                alsoTaken.Clear();
            }
        }

        // Of a Word's nearest targets, how many a search for a cell to link from starts from
        private const int MaxLinkCandidates = 12;
        private static readonly List<Pawn> linkable = new List<Pawn>();
        private static readonly List<Pawn> linkTargets = new List<Pawn>();

        // A Word with zone work (Word of Trust) and several of its targets close together: the cell from which the
        // pawn's Group link reaches the most of them, at least GroupLinkMinTargets, and nobody the Word mustn't reach
        // (AbilityAIGroupLink.Survey). Found as Consume Bodies finds its cell: each target marks every cell within the
        // link's radius of it, and the best few cells are checked. The pawn casts the link there, or walks there first
        // (AAI_ZoneLinkWalk), and the Word follows from the driver once the link is up. Starts from candidates, the Word's
        // targets nobody else has claimed, nearest first. A failed search waits RetryTicks.
        private static Job TryGiveLinkJob(Pawn pawn, AIAbility word, AbilityAIExtension rule, PsycastZones zones, int tick)
        {
            AIAbility linker = AbilityAIGroupLink.FindLinker(pawn, collected, true, AbilityAIRole.Player);
            if (linker == null || zones.WaitingZone(pawn, linker.Def, tick)) return null;
            float radius = linker.DefaultRadius;
            if (radius <= 0f || radius >= GenRadial.MaxRadialPatternRadius) return null;
            int need = AbilityAIGroupLink.MinTargets;
            Map map = pawn.Map;
            try
            {
                for (int i = 0; i < candidates.Count && linkable.Count < MaxLinkCandidates; i++)
                    if (candidates[i] is Pawn p && AbilityAIZones.Wants(rule.zoneTarget, p) && Available(pawn, word, p, rule.zoneTarget))
                        linkable.Add(p);
                if (linkable.Count < need) return null;
                int reach = GenRadial.NumCellsInRadius(radius);
                for (int i = 0; i < linkable.Count; i++)
                    for (int k = 0; k < reach; k++)
                    {
                        IntVec3 c = linkable[i].Position + GenRadial.RadialPattern[k];
                        cellCounts[c] = (cellCounts.TryGetValue(c, out int n) ? n : 0) + 1;
                    }
                foreach (var kv in cellCounts)
                    if (kv.Value >= need) consumeCells.Add(kv.Key);
                sortFrom = pawn.Position;
                consumeCells.Sort(ByCorpsesThenDistance);
                int tried = 0;
                for (int i = 0; i < consumeCells.Count && tried < MaxConsumeCellsTried; i++)
                {
                    IntVec3 c = consumeCells[i];
                    if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) continue;
                    tried++;
                    if (c != pawn.Position && !pawn.CanReach(c, PathEndMode.OnCell, Danger.Some)) continue;
                    if (!LinkTargetsAt(pawn, word, rule, zones, c, radius, out Pawn nearest)) continue;
                    if (!AbilityAIGroupLink.Worth(pawn, linker, word, nearest, linkTargets.Count, true)) break;
                    if (c == pawn.Position)
                    {
                        Job cast = linker.MakeCastJob(pawn, pawn);
                        if (cast == null) break;
                        AbilityAIDriver.PlanLink(pawn, word.Def, rule, cast);
                        ClaimLinked(zones, pawn, cast.loadID);
                        if (AbilityAISettings.DebugLog && Prefs.DevMode)
                            Log.Message($"[Ability AI] {pawn.LabelShort} casts {linker.Label} first, to reach {linkTargets.Count} targets of {word.Label} (zone work)");
                        return cast;
                    }
                    Job walk = JobMaker.MakeJob(AbilityAIZoneDefOf.AAI_ZoneLinkWalk, nearest, c);
                    ClaimLinked(zones, pawn, walk.loadID);
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} walks to {c} to link {linkTargets.Count} targets of {word.Label} (zone work)");
                    return walk;
                }
                zones.WaitZone(pawn, linker.Def, tick + RetryTicks);
                return null;
            }
            finally
            {
                linkable.Clear();
                linkTargets.Clear();
                cellCounts.Clear();
                consumeCells.Clear();
            }
        }

        /// <summary>The cast at the end of a link walk: the pawn's Group link, if from where it stands now it still reaches
        /// enough targets of a Word it has ready for this target and nobody that Word mustn't reach; else null, and the
        /// walk casts the Word at its target alone.</summary>
        internal static Job MakeLinkCastJob(Pawn pawn, Thing target)
        {
            var zones = PsycastZones.Of(pawn.Map);
            if (zones == null || !(target is Pawn) || !target.Spawned || !AbilityAIGroupLink.Any || AbilityAIGroupLink.LinkOf(pawn) != null) return null;
            try
            {
                CollectReady(pawn, zones);
                AIAbility linker = AbilityAIGroupLink.FindLinker(pawn, collected, true, AbilityAIRole.Player);
                if (linker == null) return null;
                float radius = linker.DefaultRadius;
                for (int r = 0; r < ready.Count; r++)
                {
                    AIAbility word = ready[r].ability;
                    var rule = ready[r].rule;
                    if (!rule.linkFirst || !AbilityAIGroupLink.IsWord(word.Def) || !AbilityAIZones.Wants(rule.zoneTarget, target)) continue;
                    if (!LinkTargetsAt(pawn, word, rule, zones, pawn.Position, radius, out Pawn nearest)) continue;
                    if (!AbilityAIGroupLink.Worth(pawn, linker, word, nearest, linkTargets.Count, true)) continue;
                    Job cast = linker.MakeCastJob(pawn, pawn);
                    if (cast == null) return null;
                    AbilityAIDriver.PlanLink(pawn, word.Def, rule, cast);
                    ClaimLinked(zones, pawn, cast.loadID);
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} casts {linker.Label} first, to reach {linkTargets.Count} targets of {word.Label} (zone work)");
                    return cast;
                }
                return null;
            }
            finally
            {
                collected.Clear();
                ready.Clear();
                heatBlocked.Clear();
                linkTargets.Clear();
            }
        }

        // The Word's targets a link at c would reach, into linkTargets, leaving out those another colonist has claimed;
        // false when that is under GroupLinkMinTargets or someone in reach mustn't take the Word. nearest: the target
        // nearest c, which the Word is cast at once the link is up.
        private static bool LinkTargetsAt(Pawn pawn, AIAbility word, AbilityAIExtension rule, PsycastZones zones, IntVec3 c, float radius, out Pawn nearest)
        {
            nearest = null;
            if (!AbilityAIGroupLink.Survey(null, pawn, c, radius, word, rule, linkTargets, out _)) return false;
            float best = float.MaxValue;
            for (int i = linkTargets.Count - 1; i >= 0; i--)
            {
                Pawn p = linkTargets[i];
                if (zones.ClaimedByOther(p, pawn))
                {
                    linkTargets.RemoveAt(i);
                    continue;
                }
                float d = (p.Position - c).LengthHorizontalSquared;
                if (d < best) { best = d; nearest = p; }
            }
            return linkTargets.Count >= AbilityAIGroupLink.MinTargets;
        }

        private static void ClaimLinked(PsycastZones zones, Pawn pawn, int jobId)
        {
            for (int i = 0; i < linkTargets.Count; i++) zones.Claim(linkTargets[i], pawn, jobId);
        }

        // Consume Bodies: the cell with the most of the zone's corpses within the radius, at least the setting's
        // minimum. VEF takes every humanlike corpse in the radius around the caster, through walls, whoever's it is,
        // and destroys them, so a cell is refused when any corpse in reach isn't one the zone offers (the colony's dead,
        // a friendly faction's, one outside the zone or forbidden, one another colonist is about to cast at). Each
        // corpse marks every cell within the radius of it, which counts each cell's corpses in one pass; only the best
        // few cells are checked for the corpses that would block them.
        private static Job TryGiveConsumeJob(Pawn pawn, AIAbility a, PsycastZones zones, List<Thing> targets, int tick)
        {
            Def def = a.Def;
            int need = AbilityAISettings.ZoneConsumeMinCorpses;
            float radius = a.DefaultRadius;
            if (targets.Count < need || zones.WaitingZone(pawn, def, tick) || radius <= 0f || radius >= GenRadial.MaxRadialPatternRadius) return null;
            if (!ConsumeReady(pawn, a)) return null;
            Map map = pawn.Map;
            try
            {
                int reach = GenRadial.NumCellsInRadius(radius);
                for (int i = 0; i < targets.Count; i++)
                {
                    Thing t = targets[i];
                    if (t.Map != map || !Counts(pawn, zones, t)) continue;
                    // From 1: VEF leaves a corpse on the caster's own cell
                    for (int k = 1; k < reach; k++)
                    {
                        IntVec3 c = t.Position + GenRadial.RadialPattern[k];
                        cellCounts[c] = (cellCounts.TryGetValue(c, out int n) ? n : 0) + 1;
                    }
                }
                foreach (var kv in cellCounts)
                    if (kv.Value >= need) consumeCells.Add(kv.Key);
                sortFrom = pawn.Position;
                consumeCells.Sort(ByCorpsesThenDistance);
                int tried = 0;
                float r2 = radius * radius;
                for (int i = 0; i < consumeCells.Count && tried < MaxConsumeCellsTried; i++)
                {
                    IntVec3 c = consumeCells[i];
                    if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map) || NearBlocker(c, r2)) continue;
                    tried++;
                    if (!CorpsesAt(pawn, def, zones, c, radius, need, out Thing blocker))
                    {
                        // The best cells crowd round the densest heap: one of the colony's dead in it would otherwise
                        // use up every try on cells it blocks too
                        if (blocker != null) blockers.Add(blocker.Position);
                        continue;
                    }
                    if (c != pawn.Position && !pawn.CanReach(c, PathEndMode.OnCell, Danger.Some)) continue;
                    if (c == pawn.Position)
                    {
                        Job cast = a.MakeCastJob(pawn, pawn);
                        if (cast == null) break;
                        ClaimConsumed(zones, pawn, cast.loadID);
                        zones.WaitZone(pawn, def, tick + CastWaitTicks);
                        if (AbilityAISettings.DebugLog && Prefs.DevMode)
                            Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} at {c} on {consumed.Count} corpses (zone work)");
                        return cast;
                    }
                    Job walk = JobMaker.MakeJob(AbilityAIZoneDefOf.AAI_ZoneCastWalk, consumed[0], c, pawn);
                    ClaimConsumed(zones, pawn, walk.loadID);
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} walks to {c} to cast {a.Label} on {consumed.Count} corpses (zone work)");
                    return walk;
                }
                zones.WaitZone(pawn, def, tick + RetryTicks);
                return null;
            }
            finally
            {
                cellCounts.Clear();
                consumeCells.Clear();
                consumed.Clear();
                alsoTaken.Clear();
                blockers.Clear();
            }
        }

        private static bool NearBlocker(IntVec3 c, float r2)
        {
            for (int i = 0; i < blockers.Count; i++)
                if (blockers[i] != c && (blockers[i] - c).LengthHorizontalSquared <= r2) return true;
            return false;
        }

        internal static void WaitHeap(Pawn pawn, PsycastZones zones)
        {
            if (zones == null) return;
            int until = Find.TickManager.TicksGame + RetryTicks;
            try
            {
                AIAbilitySources.Collect(pawn, collected);
                for (int i = 0; i < collected.Count; i++)
                    if (AbilityAIZones.RuleOf(collected[i].Def)?.zoneTarget == AbilityAIZoneTarget.ConsumableCorpses)
                        zones.WaitZone(pawn, collected[i].Def, until);
            }
            finally { collected.Clear(); }
        }

        private static bool ConsumeReady(Pawn pawn, AIAbility a) =>
            a.ValidIgnoringRange(pawn) && AbilityAIDriver.PsycastWithinLimits(a, pawn);

        // One of the zone's corpses this pawn may count: not forbidden, not in someone else's hands, nobody else casting
        // at it
        private static bool Counts(Pawn pawn, PsycastZones zones, Thing t) =>
            AbilityAIZones.Wants(AbilityAIZoneTarget.ConsumableCorpses, t) && !t.IsForbidden(pawn) && !zones.ClaimedByOther(t, pawn)
            && pawn.CanReserve(t);

        // What a cast at c would take, into consumed, and whether that is enough and nothing else is in reach (else the
        // corpse in the way, as blocker). A corpse a hauler has reserved isn't counted but doesn't block either: it is one
        // the zone offers. The cast takes it all the same if it is still there, so it is claimed with the rest
        // (alsoTaken), or a second colonist's heap could hold it too and VPE would destroy it twice.
        private static bool CorpsesAt(Pawn pawn, Def def, PsycastZones zones, IntVec3 c, float radius, int need) =>
            CorpsesAt(pawn, def, zones, c, radius, need, out _);

        private static bool CorpsesAt(Pawn pawn, Def def, PsycastZones zones, IntVec3 c, float radius, int need, out Thing blocker)
        {
            consumed.Clear();
            alsoTaken.Clear();
            blocker = null;
            Map map = pawn.Map;
            var zone = zones.ZoneFor(def, false);
            if (zone == null || radius <= 0f) return false;
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(c, map, radius, true))
            {
                if (!(t is Corpse corpse) || corpse.InnerPawn == null || !corpse.InnerPawn.RaceProps.Humanlike || t.Position == c) continue;
                bool offered = zone[t.Position] && AbilityAIZones.Wants(AbilityAIZoneTarget.ConsumableCorpses, t) && !t.IsForbidden(pawn)
                    && !zones.ClaimedByOther(t, pawn) && !AbilityAIZones.Busy(t, def);
                if (!offered)
                {
                    consumed.Clear();
                    alsoTaken.Clear();
                    blocker = t;
                    return false;
                }
                if (pawn.CanReserve(t)) consumed.Add(t);
                else alsoTaken.Add(t);
            }
            return consumed.Count >= need;
        }

        // The claim on every corpse of the heap follows the walk, then the cast, so no other colonist counts them
        private static void ClaimConsumed(PsycastZones zones, Pawn pawn, int jobId)
        {
            for (int i = 0; i < consumed.Count; i++) zones.Claim(consumed[i], pawn, jobId);
            for (int i = 0; i < alsoTaken.Count; i++) zones.Claim(alsoTaken[i], pawn, jobId);
        }

        // Everything about this target short of reach and range: not forbidden, not in someone else's hands (a warden
        // talking to the prisoner, a hauler carrying the corpse off), nobody already casting at it, and the ability's
        // own rules and the pawn's psycast limits. An unfinished thing is reserved by whoever is making it, and finishing
        // that one is the point of Craft Timeskip (VPE's cast doesn't reserve it either).
        private static bool Available(Pawn pawn, AIAbility a, Thing t, AbilityAIZoneTarget kind) =>
            Free(pawn, a, t, kind) && a.ValidIgnoringRange(t) && AbilityAIDriver.PsycastWithinLimits(a, t);

        // Available's checks that don't depend on what the caster can pay, so TryVent can ask them of a psycast heat
        // holds back
        private static bool Free(Pawn pawn, AIAbility a, Thing t, AbilityAIZoneTarget kind)
        {
            if (!(t is Pawn) && t.IsForbidden(pawn)) return false;
            if (kind != AbilityAIZoneTarget.Unfinished && !pawn.CanReserve(t)) return false;
            return !AbilityAIZones.Busy(t, a.Def) && !OtherCasting(pawn, a.Def, t);
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
            heatBlocked.Clear();
            collected.Clear();
            AIAbilitySources.Collect(pawn, collected);
            for (int i = 0; i < collected.Count; i++)
            {
                AIAbility a = collected[i];
                var rule = AbilityAIZones.RuleOf(a.Def);
                if (rule == null || !a.Drivable || a.PlayerAutocastElsewhere) continue;
                if (AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off) continue;
                if (AbilityAIZones.NeedsZone(rule.zoneTarget) && !zones.HasCells(a.Def)) continue;
                if (!AbilityAIHooks.NotVetoed(pawn, a)) continue;
                bool castable = a.CanCastNow && AbilityAIDriver.PsycastWithinLimits(a, LocalTargetInfo.Invalid);
                if (!castable && !(AbilityAIHeat.Any && AbilityAIDriver.HeatBlocked(a, true))) continue;
                if (!AbilityAIZones.CasterMay(rule, pawn)) continue;
                (castable ? ready : heatBlocked).Add(new Ready { ability = a, rule = rule });
            }
        }
    }
}
