using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Workers decide WHETHER an ability is worth casting now and AT WHAT. Each is a category, not an ability: an
// AbilityDef picks one with AbilityAIExtension.workerClass and tunes it with the extension's fields. Workers are
// shared between pawns and must keep no per-pawn state. A host mod's own comp states its area, hediff and stun
// through AbilityAIInfo (AbilityAIInfo.cs).
//
// Cost: a worker only runs for a pawn that holds an ability with an extension, that ability is ready (CanCast,
// reserve, gap) and a host hook claims it (or, for the player's pawns, the player set it to autocast; undrafted ones
// only try the workers their situation allows: AbilityAIDriver.PlayerSet), at most once per 30 ticks per pawn and then not again for the
// global gap after a cast. Each worker notes its own cost below; k = hostile pawns on the map.
namespace AbilityAI
{
    public abstract class AbilityAIWorker
    {
        public AbilityAIExtension ext;

        protected virtual AbilityAICombat DefaultCombat => AbilityAICombat.InCombat;

        public AbilityAICombat Combat => ext.combat == AbilityAICombat.Default ? DefaultCombat : ext.combat;

        /// <summary>Whether the player's pawns may autocast it while undrafted: any worker that isn't combat-only
        /// (Recovery, Rescue, Calm, Firefighting by default). A def can opt a combat worker in with combat Any.</summary>
        public bool WorksUndrafted => Combat != AbilityAICombat.InCombat;

        /// <summary>Whether an undrafted player pawn that is fleeing may autocast it: the undrafted set plus workers
        /// that help a pawn get away or keep going (Movement, Recovery).</summary>
        public bool WorksFleeing => WorksUndrafted || ForFleeing;

        /// <summary>True for workers that are worth using while running away. A fleeing pawn may not count as in
        /// combat (nobody has hit it yet), so these also skip the combat-mode check while it flees.</summary>
        protected internal virtual bool ForFleeing => false;

        /// <summary>A target worth casting at now, or false. The driver runs AbilityAIDriver.Valid on the result
        /// once more, so a worker only needs to call it where it is choosing between candidates.</summary>
        public abstract bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest);

        /// <summary>Only asked when TryFindTarget found nothing for any ability and the extension sets
        /// approachDistance: the thing the pawn should walk toward so that it could cast, looking as far as reach
        /// (range + approachDistance). Targets are checked with AbilityAIDriver.ValidIgnoringRange, since range and
        /// line of sight are what the walk is for. A targeted ability casts at the goal from a cast position the
        /// driver finds; a self-cast one walks to the goal.</summary>
        public virtual bool TryFindApproachTarget(AbilityAIContext ctx, Ability ability, float reach, out Thing goal)
        {
            goal = null;
            return false;
        }

        /// <summary>True for a worker that picks its own approach cell (TryFindApproachCell) for self-cast
        /// abilities; the driver then never asks TryFindApproachTarget for them.</summary>
        public virtual bool UsesApproachCell => false;

        /// <summary>Self-cast abilities, only when UsesApproachCell: a cell to walk to and stand on, which the worker
        /// must have checked the pawn can reach, and how many targets the cast would reach from there. While the walk
        /// lasts, ctx.ApproachExpected(ability) returns that number, so the worker can wait for the spot it chose
        /// instead of casting at the first cell that meets minTargets. False = retried in 2 s.</summary>
        public virtual bool TryFindApproachCell(AbilityAIContext ctx, Ability ability, out IntVec3 cell, out int expected)
        {
            cell = IntVec3.Invalid;
            expected = 0;
            return false;
        }
    }

    public static class AbilityAIUtility
    {
        /// <summary>The extension's radius, else the first effect comp that states one, else the ability's own
        /// Ability_EffectRadius stat (the area psycasts: blinding pulse, berserk pulse).</summary>
        public static float Radius(Ability ability, AbilityAIExtension ext)
        {
            if (ext.radius >= 0f) return ext.radius;
            var comps = ability.def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    if (AbilityAIInfo.Of(comps[i]) is IAbilityAIInfo info) return info.AIRadius;
                    if (comps[i] is CompProperties_AbilitySmokepop smoke) return smoke.smokeRadius;
                }
            return ability.def.HasAreaOfEffect ? ability.def.EffectRadius : 0f;
        }

        /// <summary>The extension's hediff, else the first effect comp that gives one.</summary>
        public static HediffDef Hediff(Ability ability, AbilityAIExtension ext)
        {
            if (ext.hediff != null) return ext.hediff;
            var comps = ability.def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    if (AbilityAIInfo.Of(comps[i]) is IAbilityAIInfo info && info.AIHediff != null) return info.AIHediff;
                    if (comps[i] is CompProperties_AbilityGiveHediff give && give.hediffDef != null) return give.hediffDef;
                }
            return null;
        }

        /// <summary>How far the ability reaches: its verb's range, capped by the extension's maxRange.</summary>
        public static float Range(Ability ability, AbilityAIExtension ext)
        {
            float r = ability.verb.EffectiveRange;
            return ext.maxRange >= 0f ? Mathf.Min(r, ext.maxRange) : r;
        }

        /// <summary>Cast on the caster only: no target needed, or an untargetable verb that may target itself.
        /// (Range 0 alone isn't: the Word of psycasts have no range because the caster walks up and touches.)</summary>
        public static bool IsSelfCast(Ability ability)
        {
            var vp = ability.def.verbProperties;
            if (vp == null || !ability.def.targetRequired) return true;
            return !vp.targetable && vp.targetParams?.canTargetSelf == true;
        }

        /// <summary>Whether the verb accepts the caster as its target (Painblock, Invisibility, Focus).</summary>
        public static bool CanTargetSelf(Ability ability) =>
            IsSelfCast(ability) || ability.def.verbProperties?.targetParams?.canTargetSelf == true;

        // What an ability does to the pawns it hits, read once per AbilityDef from its comps
        private class Effects
        {
            public bool stuns, mentalState, psychic, notOnMechs;
            public HediffDef hediff;
        }
        private static readonly Dictionary<AbilityDef, Effects> effects = new Dictionary<AbilityDef, Effects>();

        /// <summary>Forget what was read from the comps: AbilityAIInfo.Register calls it, since a new reader can
        /// change the answer.</summary>
        internal static void ClearEffectsCache() => effects.Clear();

        private static Effects EffectsOf(AbilityDef def)
        {
            if (effects.TryGetValue(def, out var e)) return e;
            e = new Effects();
            var comps = def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (AbilityAIInfo.Of(c) is IAbilityAIInfo info)
                    {
                        e.stuns |= info.AIStuns;
                        if (e.hediff == null) e.hediff = info.AIHediff;
                    }
                    if (c.compClass != null && typeof(CompAbilityEffect_Stun).IsAssignableFrom(c.compClass)) e.stuns = true;
                    // As CompAbilityEffect_GiveHediff.Apply decides who gets it
                    if (c is CompProperties_AbilityGiveHediff give && give.applyToTarget && !give.onlyApplyToSelf && e.hediff == null) e.hediff = give.hediffDef;
                    if (c is CompProperties_AbilityGiveMentalState) e.mentalState = true;
                    if (c is CompProperties_AbilityEffect ae)
                    {
                        if (ae.psychic) e.psychic = true;
                        // Psycast.CanApplyPsycastTo: once any comp is psychic, any comp not for mechs rules them out
                        if (!ae.applicableToMechs) e.notOnMechs = true;
                    }
                }
            effects[def] = e;
            return e;
        }

        /// <summary>Whether casting this at a hostile pawn would do anything: a psycast can't touch a pawn without
        /// psychic sensitivity (or a mech, for some), and with skipAffected a pawn already stunned by a stunning
        /// ability, already carrying its hediff, or already in a mental state from a mental-state one is skipped.
        /// Cost: a few field reads, plus a cached stat read for psycasts.</summary>
        public static bool WouldAffect(Ability ability, AbilityAIExtension ext, Pawn p)
        {
            var e = EffectsOf(ability.def);
            // Psycast.CanApplyPsycastTo, without the uncached stat read; plain Ability copies of psycasts skip it
            // in vanilla too
            if (e.psychic && ability is Psycast)
            {
                if (p.GetStatValue(StatDefOf.PsychicSensitivity, true, 250) < float.Epsilon) return false;
                if (e.notOnMechs && p.Faction != null && p.Faction == Faction.OfMechanoids) return false;
            }
            if (!ext.skipAffected) return true;
            if (e.stuns && p.stances?.stunner != null && p.stances.stunner.Stunned) return false;
            HediffDef h = ext.hediff ?? e.hediff;
            if (h != null && Has(p, h)) return false;
            if (e.mentalState && p.InMentalState) return false;
            return true;
        }

        /// <summary>A ranged enemy (mechs and animals by their own verbs) whose fight target or current attack job
        /// is this pawn.</summary>
        public static bool AimsAt(Pawn e, Pawn pawn) => AbilityAIContext.FightsAtRange(e) && AimTarget(e) == pawn;

        /// <summary>Who a pawn is attacking: its fight target, else the target of an attack job. Other jobs
        /// (kidnapping, stripping) don't count.</summary>
        public static Thing AimTarget(Pawn e)
        {
            Thing t = e.mindState?.enemyTarget;
            if (t != null) return t;
            var job = e.CurJob;
            if (job != null && (job.def == JobDefOf.AttackStatic || job.def == JobDefOf.AttackMelee)) return job.targetA.Thing;
            return null;
        }

        /// <summary>Standing next to the caster or anyone on its side (Friendlies, and allies in a hostile break), so
        /// presumably fighting them in melee.</summary>
        public static bool InMeleeWithSide(AbilityAIContext ctx, Pawn e)
        {
            var side = ctx.Friendlies;
            for (int i = 0; i < side.Count; i++)
                if (side[i].Position.AdjacentTo8WayOrInside(e.Position)) return true;
            var broken = ctx.BrokenAllies;
            for (int i = 0; i < broken.Count; i++)
                if (broken[i] != e && broken[i].Position.AdjacentTo8WayOrInside(e.Position)) return true;
            return false;
        }

        /// <summary>Whether a hostile worker using this extension may target an ally in a hostile mental break
        /// (AbilityAIContext.IsBrokenAlly), by the BrokenAllies setting.</summary>
        public static bool MayHitBrokenAllies(AbilityAIExtension ext)
        {
            switch (AbilityAISettings.BrokenAllies)
            {
                case AbilityAIBrokenAllies.Any: return true;
                case AbilityAIBrokenAllies.HarmlessOnly: return ext.harmless;
                default: return false;
            }
        }

        public static bool Has(Pawn pawn, HediffDef hediff) => hediff != null && pawn.health?.hediffSet?.HasHediff(hediff) == true;

        /// <summary>The pawn itself if the ability accepts it, else the cell it stands on.</summary>
        public static LocalTargetInfo PawnOrCell(Ability ability, Pawn target)
        {
            var pawnTarget = new LocalTargetInfo(target);
            if (AbilityAIDriver.Valid(ability, pawnTarget)) return pawnTarget;
            var cellTarget = new LocalTargetInfo(target.Position);
            return AbilityAIDriver.Valid(ability, cellTarget) ? cellTarget : LocalTargetInfo.Invalid;
        }

        /// <summary>Running away: panicking, on a flee job, or leaving the map while fighting.</summary>
        public static bool IsFleeing(AbilityAIContext ctx)
        {
            Pawn pawn = ctx.pawn;
            if (pawn.MentalStateDef == MentalStateDefOf.PanicFlee) return true;
            var job = pawn.CurJob;
            if (job == null) return false;
            if (job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower) return true;
            return job.exitMapOnArrival && ctx.InCombat;
        }

        /// <summary>The caster was hurt in melee in the last 10 seconds by someone still standing next to it.</summary>
        public static bool InMelee(AbilityAIContext ctx) => InMelee(ctx, ctx.pawn);

        /// <summary>Hurt in melee in the last 10 seconds by someone still standing next to it.</summary>
        public static bool InMelee(AbilityAIContext ctx, Pawn p)
        {
            Pawn threat = p.mindState?.meleeThreat;
            if (threat == null || !threat.Spawned || threat.Dead || threat.Downed || threat.Map != ctx.map) return false;
            return ctx.tick - p.mindState.lastMeleeThreatHarmTick < AbilityAIContext.RecentTicks
                && threat.Position.InHorDistOf(p.Position, 1.5f);
        }
    }

    /// <summary>
    /// AoE hostile: an area ability at the spot that catches the most enemies (Flashbang, Artillery, blinding and
    /// berserk pulses). Each enemy in range is a candidate centre, scored by the enemies within the radius of it that
    /// the ability would affect (WouldAffect: a second flashbang skips the group the first one stunned); at least
    /// minTargets, between minRange and the ability's range, no ally inside with avoidAllies. An ally in a hostile
    /// break counts as an ally (never counted, never a centre, avoided with avoidAllies) unless MayHitBrokenAllies. The best five are checked
    /// against the verb (line of sight). Approach: the same scoring out to range + approachDistance, without the verb.
    /// Cost: k WouldAffect checks and k² distance checks, then up to 10 target validations.
    /// </summary>
    public class AbilityAIWorker_AoEHostile : AbilityAIWorker
    {
        private struct Centre { public Pawn pawn; public int count; public float distSq; }
        private static readonly List<Centre> centres = new List<Centre>();
        private static readonly List<bool> affected = new List<bool>();
        private const int MaxTries = 5;

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (!Score(ctx, ability, AbilityAIUtility.Range(ability, ext))) return false;
            for (int i = 0; i < centres.Count && i < MaxTries; i++)
            {
                target = AbilityAIUtility.PawnOrCell(ability, centres[i].pawn);
                if (target.IsValid) break;
            }
            centres.Clear();
            return target.IsValid;
        }

        public override bool TryFindApproachTarget(AbilityAIContext ctx, Ability ability, float reach, out Thing goal)
        {
            goal = null;
            if (!Score(ctx, ability, reach)) return false;
            for (int i = 0; i < centres.Count && i < MaxTries; i++)
            {
                Pawn p = centres[i].pawn;
                if (AbilityAIDriver.ValidIgnoringRange(ability, p) || AbilityAIDriver.ValidIgnoringRange(ability, p.Position)) { goal = p; break; }
            }
            centres.Clear();
            return goal != null;
        }

        // Fills centres, best first; false when there are none
        private bool Score(AbilityAIContext ctx, Ability ability, float range)
        {
            centres.Clear();
            var enemies = ctx.Enemies;
            if (enemies.Count < ext.minTargets) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            float maxSq = range * range, minSq = ext.minRange * ext.minRange;
            IntVec3 from = ctx.pawn.Position;

            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            affected.Clear();
            int affectable = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                bool a = (!spareBroken || !ctx.IsBrokenAlly(enemies[i])) && AbilityAIUtility.WouldAffect(ability, ext, enemies[i]);
                affected.Add(a);
                if (a) affectable++;
            }
            if (affectable < ext.minTargets) return false;

            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                float d = (e.Position - from).LengthHorizontalSquared;
                if (d > maxSq || d < minSq) continue;
                if (spareBroken && !affected[i] && ctx.IsBrokenAlly(e)) continue;
                int count = 0;
                for (int j = 0; j < enemies.Count; j++)
                    if (affected[j] && (j == i || (enemies[j].Position - e.Position).LengthHorizontalSquared <= r2)) count++;
                if (count < ext.minTargets) continue;
                if (ext.avoidAllies && radius > 0f && AnyAllyNear(ctx, e.Position, r2, spareBroken)) continue;
                centres.Add(new Centre { pawn = e, count = count, distSq = d });
            }
            // Most enemies caught first, then the nearest (more likely to be in sight)
            centres.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
            return centres.Count > 0;
        }

        // Anyone on the caster's side, colonists included when the caster is a friendly visitor, and the ones in a
        // hostile break when they are to be spared
        private static bool AnyAllyNear(AbilityAIContext ctx, IntVec3 c, float r2, bool spareBroken)
        {
            if (TargetingUtil.AnyWithin(ctx.Friendlies, c, r2)) return true;
            return spareBroken && TargetingUtil.AnyWithin(ctx.BrokenAllies, c, r2);
        }
    }

    /// <summary>
    /// AoE friendly: an area buff over allies (Battle Cry). Self-cast abilities centre on the caster; targeted ones
    /// on whichever ally gathers the most. Counts allies (the caster included) within the radius that don't yet have
    /// the ability's hediff; casts when at least minTargets would gain it. Approach (self-cast only): candidate cells
    /// are the allies within approachDistance + radius that lack the hediff, and the midpoint of every pair of them
    /// within two radii of each other; the standable cell within approachDistance that would cover the most (the
    /// caster counted once, at the new cell), nearest on a tie, and more than the caster covers where it stands.
    /// During the walk the pawn only shouts early once its current cell covers as many as the chosen one. Only ally
    /// cells and pair midpoints are tried, so three allies only their centroid covers count as two. Cost: with m
    /// nearby allies, m²/2 candidate cells × m distance checks (about 500 for 10) plus up to 4 reachability checks;
    /// once per 10 s after a walk starts, once per 2 s while no spot is found. Targeted cast: k² distance checks,
    /// then up to 5 target validations, best ally first.
    /// </summary>
    public class AbilityAIWorker_AoEFriendly : AbilityAIWorker
    {
        private struct Spot { public IntVec3 cell; public int count; public float dist; }
        private static readonly List<Pawn> near = new List<Pawn>();
        private static readonly List<Spot> spots = new List<Spot>();

        public override bool UsesApproachCell => true;

        public override bool TryFindApproachCell(AbilityAIContext ctx, Ability ability, out IntVec3 cell, out int expected)
        {
            cell = IntVec3.Invalid;
            expected = 0;
            if (!AbilityAIUtility.IsSelfCast(ability)) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            Pawn pawn = ctx.pawn;
            Map map = ctx.map;
            IntVec3 from = pawn.Position;
            float maxSq = ext.approachDistance * ext.approachDistance;
            float lookSq = (ext.approachDistance + radius) * (ext.approachDistance + radius);
            bool selfCounts = !AbilityAIUtility.Has(pawn, hediff);

            // Allies (not the caster) that would gain the buff and could be covered from a reachable cell
            near.Clear();
            var allies = ctx.SupportAllies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn a = allies[i];
                if (a == pawn || AbilityAIUtility.Has(a, hediff)) continue;
                if ((a.Position - from).LengthHorizontalSquared <= lookSq) near.Add(a);
            }
            int here = CountNear(from, r2) + (selfCounts ? 1 : 0);
            int floor = Mathf.Max(ext.minTargets, here + 1);
            if (near.Count + (selfCounts ? 1 : 0) < floor) { near.Clear(); return false; }

            spots.Clear();
            float pair2 = 4f * r2;
            for (int i = 0; i < near.Count; i++)
            {
                IntVec3 a = near[i].Position;
                AddSpot(ctx, a, from, maxSq, r2, selfCounts, floor);
                for (int j = i + 1; j < near.Count; j++)
                {
                    IntVec3 b = near[j].Position;
                    if ((a - b).LengthHorizontalSquared > pair2) continue;
                    AddSpot(ctx, new IntVec3((a.x + b.x) / 2, 0, (a.z + b.z) / 2), from, maxSq, r2, selfCounts, floor);
                }
            }
            near.Clear();
            if (spots.Count == 0) return false;
            spots.Sort(BetterFirst);
            // A midpoint across a wall or river can be the best spot all fight: take the best one the pawn can reach
            for (int i = 0; i < spots.Count && i < MaxReachTries; i++)
            {
                if (!pawn.CanReach(spots[i].cell, PathEndMode.OnCell, Danger.Some)) continue;
                cell = spots[i].cell;
                expected = spots[i].count;
                break;
            }
            spots.Clear();
            return cell.IsValid;
        }

        private const int MaxReachTries = 4;
        private static readonly System.Comparison<Spot> BetterFirst =
            (x, y) => x.count != y.count ? y.count.CompareTo(x.count) : x.dist.CompareTo(y.dist);

        private void AddSpot(AbilityAIContext ctx, IntVec3 c, IntVec3 from, float maxSq, float r2, bool selfCounts, int floor)
        {
            float d = (c - from).LengthHorizontalSquared;
            if (d > maxSq || c == from || !c.InBounds(ctx.map) || !c.Standable(ctx.map)) return;
            int n = CountNear(c, r2) + (selfCounts ? 1 : 0);
            if (n >= floor) spots.Add(new Spot { cell = c, count = n, dist = d });
        }

        private static int CountNear(IntVec3 c, float r2)
        {
            int n = 0;
            for (int i = 0; i < near.Count; i++)
                if ((near[i].Position - c).LengthHorizontalSquared <= r2) n++;
            return n;
        }

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            var allies = ctx.SupportAllies;

            if (AbilityAIUtility.IsSelfCast(ability))
            {
                int n = Count(allies, ctx.pawn.Position, r2, hediff);
                if (n < ext.minTargets) return false;
                // Walking to a better spot: don't shout on the way unless this cell is already as good
                if (ctx.ApproachExpected(ability) > n) return false;
                target = ctx.pawn;
                return true;
            }

            float range = AbilityAIUtility.Range(ability, ext);
            float maxSq = range * range;
            // Every ally that would gather enough, best first: the best one can fail validation (out of sight) while
            // the next would pass
            centres.Clear();
            for (int i = 0; i < allies.Count; i++)
            {
                float d = (allies[i].Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq) continue;
                int n = Count(allies, allies[i].Position, r2, hediff);
                if (n >= ext.minTargets) centres.Add(new Centre { pawn = allies[i], count = n, distSq = d });
            }
            centres.Sort(MostThenNearest);
            for (int i = 0; i < centres.Count && i < MaxTries; i++)
            {
                target = AbilityAIUtility.PawnOrCell(ability, centres[i].pawn);
                if (target.IsValid) break;
            }
            centres.Clear();
            return target.IsValid;
        }

        private struct Centre { public Pawn pawn; public int count; public float distSq; }
        private static readonly List<Centre> centres = new List<Centre>();
        private const int MaxTries = 5;
        private static readonly System.Comparison<Centre> MostThenNearest =
            (a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq);

        private static int Count(List<Pawn> allies, IntVec3 c, float r2, HediffDef hediff)
        {
            int n = 0;
            for (int i = 0; i < allies.Count; i++)
                if ((allies[i].Position - c).LengthHorizontalSquared <= r2 && !AbilityAIUtility.Has(allies[i], hediff)) n++;
            return n;
        }
    }

    /// <summary>
    /// Self buff: cast on the caster while an enemy is within maxRange (default 30) and the caster doesn't already
    /// have the ability's hediff. Cost: k distance checks.
    /// </summary>
    public class AbilityAIWorker_SelfBuff : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (AbilityAIUtility.Has(ctx.pawn, AbilityAIUtility.Hediff(ability, ext))) return false;
            float reach = ext.maxRange >= 0f ? ext.maxRange : 30f;
            if (ctx.NearestEnemy(out float d) == null || d > reach * reach) return false;
            target = ctx.pawn;
            return true;
        }
    }

    /// <summary>
    /// Movement: close on the enemy or get away from it. Self-cast abilities (Sprint) are cast when either applies;
    /// targeted ones (Blink) pick a cell.
    /// Gap-close (melee pawns only): the current enemy target is further than minGapDistance (default 6) and within
    /// maxRange (default 40; a targeted ability also needs it in reach); a targeted ability lands next to it.
    /// Escape: health under healthThreshold, fleeing, or a ranged pawn caught in melee, with an enemy within 12 tiles;
    /// a targeted ability tries 16 directions at full and 60% range for the cell furthest from every enemy, and only
    /// goes if that gains at least 4 tiles. Cost: escape is 32 × k distance checks, then up to 6 validations;
    /// gap-close with a targeted ability up to 8 validations, only while the enemy is within its reach.
    /// </summary>
    public class AbilityAIWorker_Movement : AbilityAIWorker
    {
        protected internal override bool ForFleeing => true;

        private const float EscapeDangerRange = 12f;
        private const float EscapeMinGain = 4f;
        private const int Directions = 16;
        private const int MaxTries = 6;

        private struct Spot { public IntVec3 cell; public float score; }
        private static readonly List<Spot> spots = new List<Spot>();

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            bool self = AbilityAIUtility.IsSelfCast(ability);
            if (self && AbilityAIUtility.Has(ctx.pawn, AbilityAIUtility.Hediff(ability, ext))) return false;

            if ((ext.modes & AbilityAIMoveModes.Escape) != 0 && WantsEscape(ctx))
            {
                if (self) { target = ctx.pawn; return true; }
                if (TryEscapeCell(ctx, ability, out IntVec3 cell)) { target = cell; return true; }
            }
            if ((ext.modes & AbilityAIMoveModes.GapClose) != 0 && !ctx.UsesRanged && ctx.EnemyTarget is Pawn enemy)
            {
                float d = (enemy.Position - ctx.pawn.Position).LengthHorizontal;
                float reach = ext.maxRange >= 0f ? ext.maxRange : 40f;
                if (d < ext.minGapDistance || d > reach) return false;
                if (self) { target = ctx.pawn; return true; }
                // Even the cell next to the enemy nearest the caster is out of reach: skip the eight validations
                if (d > AbilityAIUtility.Range(ability, ext) + 1.5f) return false;
                if (TryCellNear(ctx, ability, enemy, out IntVec3 cell)) { target = cell; return true; }
            }
            return false;
        }

        private bool WantsEscape(AbilityAIContext ctx)
        {
            bool reason = ctx.HealthFraction < ext.healthThreshold || AbilityAIUtility.IsFleeing(ctx)
                || (ctx.UsesRanged && AbilityAIUtility.InMelee(ctx));
            if (!reason) return false;
            return ctx.NearestEnemy(out float d) != null && d <= EscapeDangerRange * EscapeDangerRange;
        }

        // The standable cell next to the enemy nearest the caster that the ability accepts
        private static bool TryCellNear(AbilityAIContext ctx, Ability ability, Pawn enemy, out IntVec3 best)
        {
            best = IntVec3.Invalid;
            float bestD = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 c = enemy.Position + GenAdj.AdjacentCells[i];
                if (!c.InBounds(ctx.map)) continue;
                float d = (c - ctx.pawn.Position).LengthHorizontalSquared;
                if (d >= bestD || !AbilityAIDriver.Valid(ability, c)) continue;
                bestD = d;
                best = c;
            }
            return best.IsValid;
        }

        private bool TryEscapeCell(AbilityAIContext ctx, Ability ability, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            var enemies = ctx.Enemies;
            if (enemies.Count == 0) return false;
            float range = AbilityAIUtility.Range(ability, ext);
            if (range < 2f) return false;
            float now = MinEnemyDist(enemies, ctx.pawn.Position);

            spots.Clear();
            for (int i = 0; i < Directions; i++)
            {
                float angle = i * (360f / Directions);
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                for (int s = 0; s < 2; s++)
                {
                    float dist = s == 0 ? range - 0.5f : range * 0.6f;
                    IntVec3 c = ctx.pawn.Position + new IntVec3(Mathf.RoundToInt(dir.x * dist), 0, Mathf.RoundToInt(dir.z * dist));
                    if (!c.InBounds(ctx.map)) continue;
                    float score = MinEnemyDist(enemies, c);
                    if (score - now < EscapeMinGain) continue;
                    spots.Add(new Spot { cell = c, score = score });
                }
            }
            spots.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < spots.Count && i < MaxTries; i++)
                if (AbilityAIDriver.Valid(ability, spots[i].cell)) { result = spots[i].cell; break; }
            spots.Clear();
            return result.IsValid;
        }

        private static float MinEnemyDist(List<Pawn> enemies, IntVec3 c)
        {
            float best = float.MaxValue;
            for (int i = 0; i < enemies.Count; i++)
            {
                float d = (enemies[i].Position - c).LengthHorizontalSquared;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }
    }

    /// <summary>
    /// Cover: when the caster is under fire, put something between it and the shooters (Smokescreen, Smokepop,
    /// Wallraise, Skipshield). Under fire = at least minThreats ranged enemies aiming at the caster, counting being
    /// shot in the last 10 seconds as one. Placed on the caster (offsetTowardThreat 0) or that many tiles toward the
    /// nearest threat. Not cast when it would add nothing: smoke while the caster stands in smoke, a spawned thing
    /// (the skipshield) while one already stands within the ability's radius of the caster, anything else (a wall)
    /// while the caster already has half cover from the nearest threat. A wall is never raised onto a pawn.
    /// Cost: k checks of each enemy's target, then up to 3 validations.
    /// </summary>
    public class AbilityAIWorker_Cover : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            bool smoke = false;
            ThingDef spawns = null;
            CompProperties_AbilityWallraise wall = null;
            var comps = ability.def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    if (comps[i] is CompProperties_AbilitySmokepop) smoke = true;
                    else if (comps[i] is CompProperties_AbilitySpawn sp && sp.thingDef != null) spawns = sp.thingDef;
                    else if (comps[i] is CompProperties_AbilityWallraise w) wall = w;
                }
            if (smoke && pawn.Position.AnyGas(ctx.map, GasType.BlindSmoke)) return false;
            if (spawns != null && SpawnedNear(ctx, spawns, Mathf.Max(AbilityAIUtility.Radius(ability, ext), 1.5f))) return false;

            int threats = ctx.RecentlyShot(pawn) ? 1 : 0;
            Pawn nearest = null;
            float nearestD = float.MaxValue;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                if (!AbilityAIUtility.AimsAt(e, pawn)) continue;
                threats++;
                float d = (e.Position - pawn.Position).LengthHorizontalSquared;
                if (d < nearestD) { nearestD = d; nearest = e; }
            }
            if (threats < ext.minThreats) return false;
            if (!smoke && spawns == null && nearest != null && CoverUtility.CalculateOverallBlockChance(pawn, nearest.Position, ctx.map) >= 0.5f) return false;

            if (ext.offsetTowardThreat <= 0f || nearest == null)
            {
                if (wall != null) return false;   // a wall on the caster's own cell would bury it
                target = pawn.Position;
                return true;
            }
            Vector3 dir = (nearest.Position - pawn.Position).ToVector3().normalized;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            // Straight toward the threat first, then a tile to either side
            for (int s = 0; s < 3; s++)
            {
                Vector3 off = dir * ext.offsetTowardThreat + side * (s == 0 ? 0f : s == 1 ? 1f : -1f);
                IntVec3 c = pawn.Position + new IntVec3(Mathf.RoundToInt(off.x), 0, Mathf.RoundToInt(off.z));
                if (!c.InBounds(ctx.map) || (wall != null && PawnInPattern(ctx.map, c, wall))) continue;
                if (AbilityAIDriver.Valid(ability, c)) { target = c; return true; }
            }
            return false;
        }

        private static bool SpawnedNear(AbilityAIContext ctx, ThingDef def, float radius)
        {
            var list = ctx.map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < list.Count; i++)
                if (list[i].Position.InHorDistOf(ctx.pawn.Position, radius)) return true;
            return false;
        }

        private static bool PawnInPattern(Map map, IntVec3 c, CompProperties_AbilityWallraise wall)
        {
            if (wall.pattern == null) return c.GetFirstPawn(map) != null;
            for (int i = 0; i < wall.pattern.Count; i++)
            {
                IntVec3 cell = c + new IntVec3(wall.pattern[i].x, 0, wall.pattern[i].z);
                if (cell.InBounds(map) && cell.GetFirstPawn(map) != null) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Recovery: out of combat, cast on the caster when a need is low (Second Wind). need (default rest) under
    /// needThreshold (default 30%). Cost: one need lookup.
    /// </summary>
    public class AbilityAIWorker_Recovery : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.OutOfCombat;
        protected internal override bool ForFleeing => true;

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Need need = ctx.pawn.needs?.TryGetNeed(ext.need ?? NeedDefOf.Rest);
            if (need == null || need.CurLevelPercentage >= ext.needThreshold) return false;
            target = ctx.pawn;
            return true;
        }
    }
}
