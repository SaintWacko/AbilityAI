using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Recasting as work. A Basic work giver, so the Work tab orders it against everything else a colonist does (a doctor
// set to Doctor above Basic treats patients first) and it never runs drafted. Each colonist takes the due spot nearest
// to itself that no one else has claimed, for the abilities it has on autocast; in range it casts at once, otherwise
// it walks to a cell in range with a line of sight first (JobDriver_RecastWalk), which then queues the cast.
namespace AbilityAI
{
    public class WorkGiver_Recast : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !AbilityAIRecastWork.MayWork(pawn);

        public override Job NonScanJob(Pawn pawn) => AbilityAIRecastWork.TryGiveJob(pawn);
    }

    public class JobDriver_RecastWalk : JobDriver
    {
        // targetA: the spot's anchor; targetB: the cell to cast from
        private RecastSpot Spot() => RecastSpots.Of(pawn.Map)?.ClaimFor(pawn, job.targetA.Cell, job.loadID);

        public override bool TryMakePreToilReservations(bool errorOnFailed) => Spot() != null;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() =>
            {
                var s = Spot();
                return s == null || !s.due || s.Dormant;
            });
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            Toil cast = ToilMaker.MakeToil("RecastCast");
            cast.initAction = () =>
            {
                var s = Spot();
                Job castJob = s == null ? null : AbilityAIRecastWork.MakeCastJob(pawn, s);
                if (castJob == null)
                {
                    s?.Wait(pawn, Find.TickManager.TicksGame + AbilityAIRecastWork.RetryTicks);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                // The claim follows the cast, so nobody else heads for the spot during the warmup
                pawn.jobs.jobQueue.EnqueueFirst(castJob);
                s.Claim(pawn, castJob.loadID);
            };
            cast.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return cast;
        }
    }

    internal static class AbilityAIRecastWork
    {
        internal const int RetryTicks = 600;
        // How many due spots one look for work tries, nearest first, before giving up for now
        private const int MaxSpotsTried = 3;
        // Of the cells in range of the cast cell nearest the pawn, how many reachability checks one search makes
        private const int MaxReachChecks = 6;

        private struct Ready
        {
            public AIAbility ability;
            public ThingDef thing;
        }

        private static readonly List<AIAbility> collected = new List<AIAbility>();
        private static readonly List<Ready> ready = new List<Ready>();
        private static readonly List<RecastSpot> candidates = new List<RecastSpot>();
        private static readonly List<IntVec3> standCells = new List<IntVec3>();
        private static IntVec3 sortFrom;
        private static readonly System.Comparison<RecastSpot> ByDistance =
            (a, b) => (a.anchor - sortFrom).LengthHorizontalSquared.CompareTo((b.anchor - sortFrom).LengthHorizontalSquared);
        private static readonly System.Comparison<IntVec3> CellByDistance =
            (a, b) => (a - sortFrom).LengthHorizontalSquared.CompareTo((b - sortFrom).LengthHorizontalSquared);

        // JobGiver_Work asks this for every colonist looking for work: a map with nothing due stops at one lookup
        internal static bool MayWork(Pawn pawn)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast || !AbilityAISettings.UndraftedAutocast) return false;
            if (pawn.Drafted || pawn.Faction == null || !pawn.Faction.IsPlayer || pawn.InMentalState) return false;
            var spots = RecastSpots.Of(pawn.Map);
            if (spots == null || spots.dueCount <= 0) return false;
            return AbilityAIAutocast.HasAny(pawn) && AIAbilitySources.HasAny(pawn);
        }

        internal static Job TryGiveJob(Pawn pawn)
        {
            var spots = RecastSpots.Of(pawn.Map);
            if (spots == null) return null;
            try
            {
                CollectReady(pawn);
                if (ready.Count == 0) return null;
                int tick = Find.TickManager.TicksGame;
                var list = spots.Spots;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    // A spot this pawn is already walking to or casting at isn't handed out again: a second walk would
                    // take over the claim, and the think tree would drop it as a repeat of the running one
                    if (!s.due || s.Waiting(pawn, tick) || s.claimant != null && s.ClaimHolds() || s.Dormant || !Serves(s.thingDef)) continue;
                    candidates.Add(s);
                }
                sortFrom = pawn.Position;
                candidates.Sort(ByDistance);
                for (int i = 0; i < candidates.Count && i < MaxSpotsTried; i++)
                {
                    var s = candidates[i];
                    if (!pawn.CanReach(s.anchor, PathEndMode.Touch, Danger.Some))
                    {
                        s.Wait(pawn, tick + RetryTicks);
                        continue;
                    }
                    for (int j = 0; j < ready.Count; j++)
                    {
                        if (ready[j].thing != s.thingDef) continue;
                        AIAbility a = ready[j].ability;
                        if (!TryFindCastCell(a, s, spots, out IntVec3 cell) || !AbilityAIDriver.PsycastWithinLimits(a, cell)) continue;
                        // Standing on the cell would spawn a building under the caster: the stand-cell search
                        // below moves it off first
                        bool onCell = cell == pawn.Position && s.thingDef.category == ThingCategory.Building;
                        if (!onCell && a.ValidInRange(cell))
                        {
                            Job cast = a.MakeCastJob(cell, cell);
                            if (cast == null) continue;
                            s.Claim(pawn, cast.loadID);
                            // A cast the verb refuses on start ends at once and drops the claim: without the wait the
                            // next work check would hand out the same cast in the same tick. A cast that lands makes
                            // the spot no longer due, so the wait costs nothing then.
                            s.Wait(pawn, tick + RetryTicks);
                            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                                Log.Message($"[Ability AI] {pawn.LabelShort} recasts {a.Label} at {cell}");
                            return cast;
                        }
                        if (TryFindStandCell(pawn, a, cell, out IntVec3 stand))
                        {
                            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                                Log.Message($"[Ability AI] {pawn.LabelShort} walks to {stand} to recast {a.Label} at {cell}");
                            Job walk = JobMaker.MakeJob(AbilityAIRecastDefOf.AAI_RecastWalk, s.anchor, stand);
                            // Claimed now, so the walk finds this spot and not another thing's at the same cell
                            s.Claim(pawn, walk.loadID);
                            return walk;
                        }
                    }
                    s.Wait(pawn, tick + RetryTicks);
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

        /// <summary>The cast at the end of a walk, or null when nothing the pawn has can be cast at the spot from
        /// where it stands now.</summary>
        internal static Job MakeCastJob(Pawn pawn, RecastSpot spot)
        {
            var spots = RecastSpots.Of(pawn.Map);
            if (spots == null) return null;
            try
            {
                CollectReady(pawn);
                for (int j = 0; j < ready.Count; j++)
                {
                    if (ready[j].thing != spot.thingDef) continue;
                    AIAbility a = ready[j].ability;
                    if (!TryFindCastCell(a, spot, spots, out IntVec3 cell) || !a.ValidInRange(cell)
                        || !AbilityAIDriver.PsycastWithinLimits(a, cell)) continue;
                    Job cast = a.MakeCastJob(cell, cell);
                    if (cast == null) continue;
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} recasts {a.Label} at {cell}");
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

        // The pawn's abilities with a Recast rule that it has on autocast and could cast now
        private static void CollectReady(Pawn pawn)
        {
            ready.Clear();
            collected.Clear();
            AIAbilitySources.Collect(pawn, collected);
            for (int i = 0; i < collected.Count; i++)
            {
                AIAbility a = collected[i];
                var rule = AbilityAIRecast.RuleOf(a.Def);
                if (rule == null || !a.Drivable || a.PlayerAutocastElsewhere) continue;
                if (AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off) continue;
                if (!AbilityAIHooks.NotVetoed(pawn, a) || !a.CanCastNow || !AbilityAIDriver.PsycastWithinLimits(a, LocalTargetInfo.Invalid)) continue;
                ready.Add(new Ready { ability = a, thing = rule.recastThing });
            }
        }

        private static bool Serves(ThingDef def)
        {
            for (int i = 0; i < ready.Count; i++)
                if (ready[i].thing == def) return true;
            return false;
        }

        /// <summary>The spot's own cell when the ability takes it (a pinhole, recast in place: the new one wipes the
        /// old), else the free cell beside it nearest the anchor (an ice crystal, whose old one still stands there;
        /// the next recast goes back to the anchor once it is gone, so the crystal swaps between two cells rather
        /// than wandering off). Cells other spots of the same thing start from are left to them.</summary>
        private static bool TryFindCastCell(AIAbility a, RecastSpot spot, RecastSpots spots, out IntVec3 cell)
        {
            Map map = a.Pawn.Map;
            cell = spot.anchor;
            if (CastCellOk(a, spot, cell, map)) return true;
            for (int i = 0; i < 8; i++)
            {
                cell = spot.anchor + (i < 4 ? GenAdj.CardinalDirections[i] : GenAdj.DiagonalDirections[i - 4]);
                if (!OtherAnchor(spots, spot, cell) && CastCellOk(a, spot, cell, map)) return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        private static bool OtherAnchor(RecastSpots spots, RecastSpot spot, IntVec3 cell)
        {
            var list = spots.Spots;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != spot && list[i].anchor == cell && list[i].thingDef == spot.thingDef) return true;
            return false;
        }

        // VEF's building spawn refuses a cell with any building on it, which its ValidIgnoringRange doesn't check
        // (that is the verb's CanHitTarget); a building also has to stand on ground a pawn could stand on
        private static bool CastCellOk(AIAbility a, RecastSpot spot, IntVec3 c, Map map)
        {
            if (!c.InBounds(map) || c.Fogged(map) || c.Filled(map)) return false;
            if (spot.thingDef.category == ThingCategory.Building && (c.GetFirstBuilding(map) != null || !c.Standable(map))) return false;
            return a.ValidIgnoringRange(c);
        }

        // A cell within range of the target with a line of sight to it, nearest the pawn first. A margin off the
        // range keeps the verb's own check (from the exact cell, inclusive or not by system) from failing on arrival.
        // A touch-range ability (Runesmith's etch, range 1.7) can't afford the full margin, which would leave no
        // cell at all, so it takes the cells next to the target, whose distances (1 and 1.41) sit clear of 1.7.
        // Never the target cell itself: the thing would spawn under the caster.
        private static bool TryFindStandCell(Pawn pawn, AIAbility a, IntVec3 target, out IntVec3 stand)
        {
            stand = IntVec3.Invalid;
            Map map = pawn.Map;
            float range = a.Range >= 3f ? a.Range - 1.5f : a.Range - 0.25f;
            range = Mathf.Min(range, GenRadial.MaxRadialPatternRadius - 1f);
            if (range < 1f) return false;
            int count = GenRadial.NumCellsInRadius(range);
            standCells.Clear();
            for (int i = 1; i < count; i++)
            {
                IntVec3 c = target + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && c.Standable(map) && !c.Fogged(map)) standCells.Add(c);
            }
            sortFrom = pawn.Position;
            standCells.Sort(CellByDistance);
            int reachChecks = 0;
            for (int i = 0; i < standCells.Count && reachChecks < MaxReachChecks; i++)
            {
                IntVec3 c = standCells[i];
                if (!GenSight.LineOfSight(c, target, map, true)) continue;
                reachChecks++;
                if (!pawn.CanReach(c, PathEndMode.OnCell, Danger.Some)) continue;
                stand = c;
                standCells.Clear();
                return true;
            }
            standCells.Clear();
            return false;
        }
    }
}
