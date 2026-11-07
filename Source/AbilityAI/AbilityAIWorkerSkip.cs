using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// The one worker that picks a second target: where the ability sends the first. Same rules as AbilityAIWorkers.cs:
// stateless, shared between pawns. k = hostile pawns on the map, a = the caster's allies.
namespace AbilityAI
{
    /// <summary>
    /// Skip: an ability that moves its target to a cell the caster picks (AIAbility.TryGetDestRule: Royalty's Skip,
    /// VPE's Skip). One rule per use, chosen by skipMode, each behind its own setting:
    /// - Repel: an enemy standing next to someone of the caster's side who can't hold it off (downed, under
    ///   healthThreshold, or a ranged fighter; a healthy brawler is left to it) goes far from that side and toward its
    ///   own, and never into the player's home area when the caster isn't hostile to the player. An enemy carrying
    ///   someone is left alone, since the carried pawn would go with it, and so is a prisoner breaking out. An ally in a
    ///   hostile break only if it is of the caster's own faction and MayHitBrokenAllies; for a player caster never a
    ///   pawn whose home faction isn't hostile to the player (Skip costs 15 goodwill with it). Worst threat first: next to
    ///   a downed ally x3, a hurt one x2.
    /// - Rescue: a pawn of the caster's faction other than the caster, downed with an enemy within 10 tiles, or under
    ///   healthThreshold and hurt in melee or shot at. It goes near the caster, away from the enemies, behind cover
    ///   from the nearest one. A drafted pawn still on its feet stays where the player put it.
    /// - Escape: the caster itself, when Movement's escape applies (under healthThreshold, fleeing, or a ranged pawn
    ///   in melee, with an enemy within 12 tiles), to the cell furthest from every enemy.
    /// Nobody in a player-ordered job is moved. The destination rule is applied here, since vanilla only checks it in
    /// its targeting UI: within range of the rule's origin (vanilla: the TARGET; VEF: the caster), in sight of it when
    /// the rule asks, standable, not fogged, walkable by the pawn moved, not behind a door it can't pass, and whatever
    /// the system's own ValidDest adds. Candidate cells lie on rings around that origin. The move must be worth it:
    /// Repel lands at least 10 tiles from the side, the others gain 4 tiles on the nearest enemy (Rescue also needs 6
    /// clear).
    /// Cost: Repel a pass over the enemies with up to a adjacency checks each; Rescue a pass over the allies; then for
    /// each of the best three targets 48 cells (Repel), 32 (Escape) or about 110 (Rescue) times k distance checks,
    /// a cover check for Rescue's best 12, and line of sight for the best six cells.
    /// </summary>
    public class AbilityAIWorker_Skip : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory =>
            ext.skipMode == AbilityAISkipMode.Rescue ? AbilityAICategory.Rescue
            : ext.skipMode == AbilityAISkipMode.Escape ? AbilityAICategory.Movement : AbilityAICategory.Offensive;

        private const float DownedDanger = 10f;
        private const float EscapeDangerRange = 12f;
        private const float RepelMinDistance = 10f;
        private const float RescueMinDistance = 6f;
        private const float MinGain = 4f;
        private const float RescueDisc = 5f;
        private const float ScoreCap = 30f;
        private const int MaxTargets = 3;
        private const int MaxCells = 6;
        private const int CoverChecks = 12;

        private static readonly Vector3[] Dirs = MakeDirs(16);
        private static readonly float[] RepelRings = { 1f, 0.7f, 0.45f };
        private static readonly float[] Rings = { 1f, 0.6f };

        private struct Spot { public IntVec3 cell; public float score; }
        private static readonly List<Spot> spots = new List<Spot>();
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();
        private static readonly List<Pawn> side = new List<Pawn>();

        protected override AbilityAICombat DefaultCombat =>
            ext.skipMode == AbilityAISkipMode.Rescue ? AbilityAICombat.Any : AbilityAICombat.InCombat;

        protected internal override bool ForFleeing => ext.skipMode == AbilityAISkipMode.Escape;

        public override bool TargetsEnemies => ext.skipMode == AbilityAISkipMode.Repel;

        // Two casters skipping one pawn waste the second cast, whichever use it is
        protected override bool DefaultExclusive => true;

        private static Vector3[] MakeDirs(int n)
        {
            var dirs = new Vector3[n];
            for (int i = 0; i < n; i++) dirs[i] = Quaternion.AngleAxis(i * (360f / n), Vector3.up) * Vector3.forward;
            return dirs;
        }

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (!AbilityAISettings.SkipAllowed(ext.skipMode)) return false;
            if (!ability.TryGetDestRule(out AIDestRule rule)) return false;
            float destRange = rule.range > 0f ? rule.range : AbilityAIUtility.Range(ability, ext);
            if (destRange < 2f) return false;
            var dr = new DestCtx { ability = ability, rule = rule, range = destRange };

            Pawn moved;
            IntVec3 cell;
            bool found;
            try
            {
                switch (ext.skipMode)
                {
                    case AbilityAISkipMode.Repel: found = TryRepel(ctx, dr, out moved, out cell); break;
                    case AbilityAISkipMode.Rescue: found = TryRescue(ctx, dr, out moved, out cell); break;
                    default: found = TryEscape(ctx, dr, out moved, out cell); break;
                }
            }
            finally
            {
                cands.Clear();
                spots.Clear();
                side.Clear();
            }
            if (!found) return false;
            target = moved;
            dest = cell;
            return true;
        }

        // What every destination check needs, passed down as one
        private struct DestCtx
        {
            public AIAbility ability;
            public AIDestRule rule;
            public float range;
            public IntVec3 Origin(AbilityAIContext ctx, Pawn moved) => rule.fromCaster ? ctx.pawn.Position : moved.Position;
        }

        private static bool PlayerOrdered(Pawn p) => p.CurJob != null && p.CurJob.playerForced;

        // CompAbilityEffect.Apply's goodwill rule for a player caster, short of the lodger and quest-helper exemptions
        private static bool CostsGoodwill(Pawn p)
        {
            if (p.IsSlaveOfColony) return false;
            Faction home = p.HomeFaction;
            return home != null && home != Faction.OfPlayer && !home.HostileTo(Faction.OfPlayer);
        }

        private bool TryRepel(AbilityAIContext ctx, DestCtx dr, out Pawn moved, out IntVec3 cell)
        {
            AIAbility ability = dr.ability;
            moved = null;
            cell = IntVec3.Invalid;
            float range = AbilityAIUtility.Range(ability, ext);
            float maxSq = range * range;
            bool mayHitBroken = AbilityAIUtility.MayHitBrokenAllies(ext);
            bool playerCaster = ctx.pawn.Faction == Faction.OfPlayer;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                float d = (e.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq) continue;
                if (ctx.IsBrokenAlly(e) && (!mayHitBroken || e.Faction != ctx.pawn.Faction)) continue;
                if (e.carryTracker?.CarriedThing is Pawn) continue;
                // A prisoner breaking out is hostile without a mental state; sending it far from the colonists is
                // sending it on its way
                if (e.IsPrisoner) continue;
                if (playerCaster && CostsGoodwill(e)) continue;
                if (!AbilityAIUtility.WouldAffect(ability, ext, e)) continue;
                float threat = ThreatTo(ctx, e);
                if (threat <= 0f) continue;
                float score = threat * Mathf.Clamp(e.kindDef.combatPower / 100f, 0.5f, 3f) / (1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f));
                cands.Add(new TargetingUtil.Cand { pawn = e, score = score });
            }
            if (cands.Count == 0) return false;
            TargetingUtil.SortBest(cands);

            side.AddRange(ctx.Friendlies);
            side.AddRange(ctx.DownedAllies);
            Faction faction = ctx.pawn.Faction;
            bool avoidHome = faction != null && !faction.HostileTo(Faction.OfPlayer);
            for (int i = 0; i < cands.Count && i < MaxTargets; i++)
            {
                Pawn e = cands[i].pawn;
                if (!AbilityAIDriver.Valid(ability, e)) continue;
                if (TryRepelCell(ctx, dr, e, avoidHome, out cell)) { moved = e; return true; }
            }
            return false;
        }

        // How badly the enemy is needed elsewhere: 3 next to a downed ally, 2 next to a hurt one, 1 next to a ranged
        // one, 0 when everyone it stands beside can fight it in melee
        private float ThreatTo(AbilityAIContext ctx, Pawn e)
        {
            IntVec3 at = e.Position;
            var downed = ctx.DownedAllies;
            for (int i = 0; i < downed.Count; i++)
                if (downed[i].Position.AdjacentTo8WayOrInside(at)) return 3f;
            float best = 0f;
            var friendlies = ctx.Friendlies;
            for (int i = 0; i < friendlies.Count; i++)
            {
                Pawn p = friendlies[i];
                if (!p.Position.AdjacentTo8WayOrInside(at)) continue;
                if ((p.health?.summaryHealth?.SummaryHealthPercent ?? 1f) < ext.healthThreshold) return 2f;
                if (best < 1f && AbilityAIContext.FightsAtRange(p)) best = 1f;
            }
            return best;
        }

        private static bool TryRepelCell(AbilityAIContext ctx, DestCtx dr, Pawn e, bool avoidHome, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            IntVec3 origin = dr.Origin(ctx, e);
            float destRange = dr.range;
            var home = avoidHome ? ctx.map.areaManager.Home : null;
            var enemies = ctx.Enemies;
            spots.Clear();
            for (int i = 0; i < Dirs.Length; i++)
                for (int s = 0; s < RepelRings.Length; s++)
                {
                    IntVec3 c = Ring(origin, Dirs[i], destRange * RepelRings[s] - 0.5f);
                    if (c == e.Position || !CellOk(ctx, origin, destRange, c)) continue;
                    if (home != null && home[c]) continue;
                    float fromSide = MinDist(side, c, null);
                    if (fromSide < RepelMinDistance) continue;
                    // Back toward its own side, so it doesn't land alone somewhere behind ours
                    float fromOwn = MinDist(enemies, c, e);
                    spots.Add(new Spot { cell = c, score = Mathf.Min(fromSide, ScoreCap) - 0.3f * Mathf.Min(fromOwn, ScoreCap) });
                }
            return BestDest(ctx, dr, e, origin, out result);
        }

        private bool TryRescue(AbilityAIContext ctx, DestCtx dr, out Pawn moved, out IntVec3 cell)
        {
            AIAbility ability = dr.ability;
            moved = null;
            cell = IntVec3.Invalid;
            var enemies = ctx.Enemies;
            if (enemies.Count == 0) return false;
            float range = AbilityAIUtility.Range(ability, ext);
            float maxSq = range * range;
            var downed = ctx.DownedAllies;
            for (int i = 0; i < downed.Count; i++)
            {
                Pawn p = downed[i];
                if ((p.Position - ctx.pawn.Position).LengthHorizontalSquared > maxSq || !AbilityAIUtility.Fits(ext, p)) continue;
                float near = MinDist(enemies, p.Position, null);
                if (near > DownedDanger) continue;
                cands.Add(new TargetingUtil.Cand { pawn = p, score = 3f + 1f - near / DownedDanger });
            }
            var allies = ctx.SupportAllies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn p = allies[i];
                if (p == ctx.pawn || p.Drafted || PlayerOrdered(p)) continue;
                if ((p.Position - ctx.pawn.Position).LengthHorizontalSquared > maxSq || !AbilityAIUtility.Fits(ext, p)) continue;
                float health = p.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
                if (health >= ext.healthThreshold) continue;
                if (!AbilityAIUtility.InMelee(ctx, p) && ctx.AimedAtCount(p) == 0 && !ctx.RecentlyShot(p)) continue;
                cands.Add(new TargetingUtil.Cand { pawn = p, score = 2f - health });
            }
            if (cands.Count == 0) return false;
            TargetingUtil.SortBest(cands);
            for (int i = 0; i < cands.Count && i < MaxTargets; i++)
            {
                Pawn p = cands[i].pawn;
                if (!AbilityAIDriver.Valid(ability, p)) continue;
                if (TryRescueCell(ctx, dr, p, out cell)) { moved = p; return true; }
            }
            return false;
        }

        // Near the caster first (it is presumably out of the worst of it, and can watch over the pawn), then rings
        // around the rule's origin (the pawn, for vanilla) for a caster that is itself in the thick of it
        private static bool TryRescueCell(AbilityAIContext ctx, DestCtx dr, Pawn p, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            IntVec3 origin = dr.Origin(ctx, p);
            float destRange = dr.range;
            var enemies = ctx.Enemies;
            float need = Mathf.Max(RescueMinDistance, MinDist(enemies, p.Position, null) + MinGain);
            IntVec3 casterAt = ctx.pawn.Position;
            spots.Clear();
            int n = GenRadial.NumCellsInRadius(RescueDisc);
            for (int i = 0; i < n; i++)
                AddRescueSpot(ctx, p, origin, destRange, casterAt + GenRadial.RadialPattern[i], need);
            for (int i = 0; i < Dirs.Length; i++)
                for (int s = 0; s < Rings.Length; s++)
                    AddRescueSpot(ctx, p, origin, destRange, Ring(origin, Dirs[i], destRange * Rings[s] - 0.5f), need);
            // Cover reads the cells around each spot, so only the best few by distance pay for it
            spots.Sort((a, b) => b.score.CompareTo(a.score));
            if (spots.Count > CoverChecks) spots.RemoveRange(CoverChecks, spots.Count - CoverChecks);
            for (int i = 0; i < spots.Count; i++)
            {
                var s = spots[i];
                Pawn nearest = NearestOf(enemies, s.cell, out _);
                s.score += 8f * CoverUtility.CalculateOverallBlockChance(s.cell, nearest.Position, ctx.map);
                spots[i] = s;
            }
            return BestDest(ctx, dr, p, origin, out result);
        }

        private static void AddRescueSpot(AbilityAIContext ctx, Pawn p, IntVec3 origin, float destRange, IntVec3 c, float need)
        {
            if (c == p.Position || !CellOk(ctx, origin, destRange, c)) return;
            float clear = MinDist(ctx.Enemies, c, null);
            if (clear < need) return;
            float score = Mathf.Min(clear, ScoreCap) - 0.5f * (c - ctx.pawn.Position).LengthHorizontal;
            spots.Add(new Spot { cell = c, score = score });
        }

        private bool TryEscape(AbilityAIContext ctx, DestCtx dr, out Pawn moved, out IntVec3 cell)
        {
            AIAbility ability = dr.ability;
            float destRange = dr.range;
            moved = null;
            cell = IntVec3.Invalid;
            Pawn pawn = ctx.pawn;
            if (PlayerOrdered(pawn)) return false;
            bool reason = ctx.HealthFraction < ext.healthThreshold || AbilityAIUtility.IsFleeing(ctx)
                || ((ctx.UsesRanged || ext.escapeInMelee) && AbilityAIUtility.InMelee(ctx));
            if (!reason || ctx.NearestEnemy(out float d) == null || d > EscapeDangerRange * EscapeDangerRange) return false;
            if (!AbilityAIDriver.Valid(ability, pawn)) return false;

            IntVec3 origin = pawn.Position;
            var enemies = ctx.Enemies;
            float need = MinDist(enemies, origin, null) + MinGain;
            spots.Clear();
            for (int i = 0; i < Dirs.Length; i++)
                for (int s = 0; s < Rings.Length; s++)
                {
                    IntVec3 c = Ring(origin, Dirs[i], destRange * Rings[s] - 0.5f);
                    if (!CellOk(ctx, origin, destRange, c)) continue;
                    float clear = MinDist(enemies, c, null);
                    if (clear < need) continue;
                    spots.Add(new Spot { cell = c, score = clear });
                }
            if (!BestDest(ctx, dr, pawn, origin, out cell)) return false;
            moved = pawn;
            return true;
        }

        private static IntVec3 Ring(IntVec3 origin, Vector3 dir, float dist) =>
            origin + new IntVec3(Mathf.RoundToInt(dir.x * dist), 0, Mathf.RoundToInt(dir.z * dist));

        // The checks that need no raycast; BestDest does the rest for the best few
        private static bool CellOk(AbilityAIContext ctx, IntVec3 origin, float destRange, IntVec3 c) =>
            c.InBounds(ctx.map) && (c - origin).LengthHorizontalSquared <= destRange * destRange
            && c.Standable(ctx.map) && !c.Fogged(ctx.map);

        // Vanilla's CompAbilityEffect_WithDest.CanHitTarget and CanPlaceSelectedTargetAt read the targeting UI's own
        // selection and so can't be asked; the system's ValidDest last, as the dearest
        private static bool BestDest(AbilityAIContext ctx, DestCtx dr, Pawn moved, IntVec3 origin, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            spots.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < spots.Count && i < MaxCells; i++)
            {
                IntVec3 c = spots[i].cell;
                if (dr.rule.lineOfSight && !GenSight.LineOfSight(origin, c, ctx.map)) continue;
                var door = c.GetDoor(ctx.map);
                if (door != null && !door.CanPhysicallyPass(moved)) continue;
                if (!c.WalkableBy(ctx.map, moved)) continue;
                if (!dr.ability.ValidDest(moved, c)) continue;
                result = c;
                break;
            }
            spots.Clear();
            return result.IsValid;
        }

        private static float MinDist(List<Pawn> pawns, IntVec3 c, Pawn skip)
        {
            NearestOf(pawns, c, out float d, skip);
            return d;
        }

        private static Pawn NearestOf(List<Pawn> pawns, IntVec3 c, out float dist, Pawn skip = null)
        {
            Pawn best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == skip) continue;
                float d = (p.Position - c).LengthHorizontalSquared;
                if (d < bestSq) { bestSq = d; best = p; }
            }
            dist = best != null ? Mathf.Sqrt(bestSq) : float.MaxValue;
            return best;
        }
    }
}
