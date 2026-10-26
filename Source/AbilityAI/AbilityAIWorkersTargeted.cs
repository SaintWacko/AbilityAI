using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// The phase 2 workers: one target at a time (an enemy, an ally, a downed ally) plus firefighting. Same rules as
// AbilityAIWorkers.cs: stateless, shared between pawns. k = hostile pawns on the map, a = the
// caster's standing allies.
namespace AbilityAI
{
    internal static class TargetingUtil
    {
        public const int MaxTries = 5;

        /// <summary>The verb's range, or for a touch ability (the Word of psycasts, range 0 or less) maxRange or 20
        /// tiles: the cast job walks there, so this is how far the AI looks.</summary>
        public static float Reach(AIAbility ability, AbilityAIExtension ext)
        {
            float r = AbilityAIUtility.Range(ability, ext);
            if (r > 0f) return r;
            return ext.maxRange >= 0f ? ext.maxRange : 20f;
        }

        public static bool AnyWithin(List<Pawn> pawns, IntVec3 c, float r2)
        {
            for (int i = 0; i < pawns.Count; i++)
                if ((pawns[i].Position - c).LengthHorizontalSquared <= r2) return true;
            return false;
        }

        /// <summary>Fighting right now: has an enemy target, an attack job, or was hurt or attacked recently.</summary>
        public static bool Fighting(AbilityAIContext ctx, Pawn p)
        {
            if (p == ctx.pawn) return ctx.InCombat;
            var mind = p.mindState;
            if (mind == null) return false;
            if (mind.enemyTarget != null) return true;
            var job = p.CurJob;
            if (job != null && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic)) return true;
            return ctx.tick - mind.lastHarmTick < AbilityAIContext.RecentTicks
                || (mind.lastRangedHarmTick > 0 && ctx.tick - mind.lastRangedHarmTick < AbilityAIContext.RecentTicks);
        }

        public struct Cand { public Pawn pawn; public float score; }

        public static void SortBest(List<Cand> list) => list.Sort((a, b) => b.score.CompareTo(a.score));

        /// <summary>The first of the best few the ability accepts as a pawn target.</summary>
        public static bool FirstValid(List<Cand> list, AIAbility ability, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            SortBest(list);
            for (int i = 0; i < list.Count && i < MaxTries; i++)
                if (AbilityAIDriver.Valid(ability, list[i].pawn)) { target = list[i].pawn; break; }
            list.Clear();
            return target.IsValid;
        }
    }

    /// <summary>
    /// Target hostile: one enemy (Stun, Burden, Berserk, Chaos Skip). Enemies within [minRange, range] the ability
    /// would affect (WouldAffect), scored by: the caster's current target +1, aiming at the caster +1, times its
    /// kind's combat power (0.5-3 x of 100), divided by distance over range. meleeOnly keeps only enemies next to
    /// the caster or an ally (one in a hostile break included: AbilityAIUtility.InMeleeWithSide). With minTargets above 1 the enemy needs that many of its side (itself included) within
    /// radius (default 5); preferGrouped multiplies the score by them, and avoidAllies skips enemies with anyone on
    /// the caster's side (Friendlies, of allyKinds) within that radius (a berserk enemy attacks whoever is nearest). An ally in a
    /// hostile break is skipped, and avoided like a friendly, unless MayHitBrokenAllies. The best five are validated.
    /// Approach: the same out to range + approachDistance.
    /// Cost: k WouldAffect checks, k² distance checks only with grouping, k×a with meleeOnly or avoidAllies.
    /// </summary>
    public class AbilityAIWorker_TargetHostile : AbilityAIWorker
    {
        public override bool TargetsEnemies => true;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            Score(ctx, ability, TargetingUtil.Reach(ability, ext));
            return TargetingUtil.FirstValid(cands, ability, out target);
        }

        public override bool TryFindApproachTarget(AbilityAIContext ctx, AIAbility ability, float reach, out Thing goal)
        {
            goal = null;
            Score(ctx, ability, reach);
            TargetingUtil.SortBest(cands);
            for (int i = 0; i < cands.Count && i < TargetingUtil.MaxTries; i++)
                if (AbilityAIDriver.ValidIgnoringRange(ability, cands[i].pawn)) { goal = cands[i].pawn; break; }
            cands.Clear();
            return goal != null;
        }

        private void Score(AbilityAIContext ctx, AIAbility ability, float range)
        {
            cands.Clear();
            var enemies = ctx.Enemies;
            float maxSq = range * range, minSq = ext.minRange * ext.minRange;
            float near = ext.radius >= 0f ? ext.radius : 5f;
            float near2 = near * near;
            bool grouping = ext.preferGrouped || ext.minTargets > 1;
            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            Thing current = ctx.EnemyTarget;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                float d = (e.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq || d < minSq) continue;
                if (spareBroken && ctx.IsBrokenAlly(e)) continue;
                if (!AbilityAIUtility.WouldAffect(ability, ext, e)) continue;
                if (ext.meleeOnly && !AbilityAIUtility.InMeleeWithSide(ctx, e)) continue;
                float score = 1f;
                if (e == current) score += 1f;
                if (AbilityAIUtility.AimsAt(e, ctx.pawn)) score += 1f;
                score *= Mathf.Clamp(e.kindDef.combatPower / 100f, 0.5f, 3f);
                if (grouping)
                {
                    int group = 1;
                    for (int j = 0; j < enemies.Count; j++)
                        if (j != i && (enemies[j].Position - e.Position).LengthHorizontalSquared <= near2
                            && (!spareBroken || !ctx.IsBrokenAlly(enemies[j]))) group++;
                    if (group < ext.minTargets) continue;
                    if (ext.preferGrouped) score *= group;
                }
                if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, e.Position, near2, spareBroken, false)) continue;
                score /= 1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f);
                cands.Add(new TargetingUtil.Cand { pawn = e, score = score });
            }
        }
    }

    /// <summary>
    /// Target friendly: a buff on one ally or the caster (Focus, Painblock on someone hurting). Allies in range
    /// without the ability's hediff; with minPain only those in at least that much pain, otherwise only allies
    /// fighting now. Scored by (1 + pain), the caster x1.5, divided by distance over range. Cost: a checks.
    /// </summary>
    public class AbilityAIWorker_TargetFriendly : AbilityAIWorker
    {
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            cands.Clear();
            float range = TargetingUtil.Reach(ability, ext);
            float maxSq = range * range;
            bool self = AbilityAIUtility.CanTargetSelf(ability);
            bool onlySelf = AbilityAIUtility.IsSelfCast(ability);
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            var allies = ctx.SupportAllies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn a = allies[i];
                bool isSelf = a == ctx.pawn;
                if (isSelf ? !self : onlySelf) continue;
                float d = (a.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq || AbilityAIUtility.Has(a, hediff) || !AbilityAIUtility.Fits(ext, a)) continue;
                float pain = a.health?.hediffSet?.PainTotal ?? 0f;
                if (ext.minPain > 0f ? pain < ext.minPain : !TargetingUtil.Fighting(ctx, a)) continue;
                float score = (1f + pain) * (isSelf ? 1.5f : 1f) / (1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f));
                cands.Add(new TargetingUtil.Cand { pawn = a, score = score });
            }
            return TargetingUtil.FirstValid(cands, ability, out target);
        }
    }

    /// <summary>
    /// Defensive: protect the caster or an ally who is in trouble (Invisibility). In trouble = health under
    /// healthThreshold with an enemy within 15 tiles, or hurt in melee in the last 10 seconds, or at least minThreats
    /// ranged enemies aiming at them (being shot recently counts as one); with rangedOnly only the last of those.
    /// Skips anyone with the ability's hediff.
    /// The caster first (x1.5), then whoever is worst hurt. Cost: a × k distance checks at worst (only for allies
    /// under the health threshold), plus one pass over the enemies' targets.
    /// </summary>
    public class AbilityAIWorker_Defensive : AbilityAIWorker
    {
        private const float DangerRange = 15f;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            cands.Clear();
            bool self = AbilityAIUtility.CanTargetSelf(ability);
            bool onlySelf = AbilityAIUtility.IsSelfCast(ability);
            float range = TargetingUtil.Reach(ability, ext);
            float maxSq = range * range;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            var allies = ctx.SupportAllies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn a = allies[i];
                bool isSelf = a == ctx.pawn;
                if (isSelf ? !self : onlySelf) continue;
                if ((a.Position - ctx.pawn.Position).LengthHorizontalSquared > maxSq || AbilityAIUtility.Has(a, hediff)
                    || !AbilityAIUtility.Fits(ext, a)) continue;
                float health = a.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
                if (!InTrouble(ctx, a, health)) continue;
                cands.Add(new TargetingUtil.Cand { pawn = a, score = (2f - health) * (isSelf ? 1.5f : 1f) });
            }
            return TargetingUtil.FirstValid(cands, ability, out target);
        }

        private bool InTrouble(AbilityAIContext ctx, Pawn p, float health)
        {
            if (!ext.rangedOnly && AbilityAIUtility.InMelee(ctx, p)) return true;
            // minThreats 0 would count every ally in range as in trouble: treat it as "threats don't count"
            if (ext.minThreats > 0 && ctx.AimedAtCount(p) + (ctx.RecentlyShot(p) ? 1 : 0) >= ext.minThreats) return true;
            if (ext.rangedOnly || health >= ext.healthThreshold) return false;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].Position.InHorDistOf(p.Position, DangerRange)) return true;
            return false;
        }
    }

    /// <summary>
    /// Rescue: help a downed ally (Painblock gets a pawn downed by pain alone back on its feet). Downed allies in
    /// range without the ability's hediff, painShockOnly limiting it to pain shock and minBleedRate to those
    /// bleeding that fast (cached on the hediff set); nearest first. Any time, in combat or not. Cost: one pass over
    /// downed allies.
    /// </summary>
    public class AbilityAIWorker_Rescue : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            cands.Clear();
            if (AbilityAIUtility.IsSelfCast(ability)) { target = LocalTargetInfo.Invalid; return false; }
            float range = TargetingUtil.Reach(ability, ext);
            float maxSq = range * range;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            var downed = ctx.DownedAllies;
            for (int i = 0; i < downed.Count; i++)
            {
                Pawn p = downed[i];
                float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq || AbilityAIUtility.Has(p, hediff) || !AbilityAIUtility.Fits(ext, p)) continue;
                if (ext.painShockOnly && p.health?.InPainShock != true) continue;
                if (ext.minBleedRate > 0f && (p.health?.hediffSet?.BleedRateTotal ?? 0f) < ext.minBleedRate) continue;
                cands.Add(new TargetingUtil.Cand { pawn = p, score = -d });
            }
            return TargetingUtil.FirstValid(cands, ability, out target);
        }
    }

    /// <summary>
    /// Calm: end an ally's mental break (Word of Serenity). Allies in an aggressive mental state (berserk, often
    /// from an enemy psycast), or any mental state with anyMentalState, that won't end by itself within
    /// minBreakSeconds (a 15-second psycast berserk isn't worth a 4-minute coma); nearest first. Touch abilities look
    /// within maxRange (default 20) and the cast job walks there. Any time. Cost: a checks.
    /// </summary>
    public class AbilityAIWorker_Calm : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            cands.Clear();
            bool player = AbilityAIDriver.EvaluatingPlayer;
            float range = TargetingUtil.Reach(ability, ext);
            // An undrafted pawn of the player's walks across the base to calm a friend; a drafted one keeps the
            // extension's reach so it doesn't wander far from where it was put
            if (player && !ctx.pawn.Drafted && AbilityAIUtility.Range(ability, ext) <= 0f) range = AbilityAISettings.CalmRange;
            float maxSq = range * range;
            var allies = ctx.Allies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn p = allies[i];
                if (p == ctx.pawn || !Wants(p, player) || !AbilityAIUtility.Fits(ext, p)) continue;
                float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq) continue;
                if (ext.minBreakSeconds > 0f && TicksLeft(p.MentalState) < ext.minBreakSeconds * GenTicks.TicksPerRealSecond) continue;
                cands.Add(new TargetingUtil.Cand { pawn = p, score = -d });
            }
            // A catatonic breakdown is a hediff that downs the pawn for days, not a mental state, but vanilla's
            // StopMentalState cures it and prices it as extreme. The player's pawns only, when extreme breaks count.
            if (player && AbilityAISettings.CalmBreaks != AbilityAICalmBreaks.Aggressive && HediffDefOf.CatatonicBreakdown != null)
            {
                var downed = ctx.DownedAllies;
                for (int i = 0; i < downed.Count; i++)
                {
                    Pawn p = downed[i];
                    float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
                    if (d > maxSq || p.health?.hediffSet?.HasHediff(HediffDefOf.CatatonicBreakdown) != true || !AbilityAIUtility.Fits(ext, p)) continue;
                    cands.Add(new TargetingUtil.Cand { pawn = p, score = -d });
                }
            }
            return TargetingUtil.FirstValid(cands, ability, out target);
        }

        // Aggressive breaks always; anyMentalState on the extension, any break. The player's pawns go by the
        // CalmBreaks setting on top of that; NPCs keep the extension's rule.
        private bool Wants(Pawn p, bool player)
        {
            if (!p.InMentalState) return false;
            if (ext.anyMentalState || p.InAggroMentalState) return true;
            if (!player) return false;
            switch (AbilityAISettings.CalmBreaks)
            {
                case AbilityAICalmBreaks.Any: return true;
                case AbilityAICalmBreaks.MajorAndExtreme: return Intensity(p.MentalStateDef) >= MentalBreakIntensity.Major;
                default: return false;
            }
        }

        // A mental state's break intensity, as vanilla's StopMentalState works it out (the MentalBreakDef that
        // starts it; None for states no break starts, such as a psycast's berserk, which vanilla prices as minor).
        // Built once by MentalStateDef index: one array read per check.
        private static MentalBreakIntensity[] intensities;

        private static MentalBreakIntensity Intensity(MentalStateDef def)
        {
            if (def == null) return MentalBreakIntensity.None;
            if (intensities == null)
            {
                var arr = new MentalBreakIntensity[DefDatabase<MentalStateDef>.DefCount];
                var breaks = DefDatabase<MentalBreakDef>.AllDefsListForReading;
                for (int i = 0; i < breaks.Count; i++)
                {
                    var s = breaks[i].mentalState;
                    if (s != null && s.index < arr.Length && arr[s.index] == MentalBreakIntensity.None)
                        arr[s.index] = breaks[i].intensity;
                }
                intensities = arr;
            }
            return def.index < intensities.Length ? intensities[def.index] : MentalBreakIntensity.None;
        }

        // The most the break can still last: a forced end (psycasts set one) or the def's own longest duration.
        // Breaks with neither are taken as long.
        private static float TicksLeft(MentalState s)
        {
            if (s == null) return 0f;
            float left = float.MaxValue;
            if (s.forceRecoverAfterTicks > 0) left = s.forceRecoverAfterTicks - s.Age;
            if (s.def != null && s.def.maxTicksBeforeRecovery > 0 && s.def.maxTicksBeforeRecovery < 99999999)
                left = Mathf.Min(left, s.def.maxTicksBeforeRecovery - s.Age);
            return left;
        }
    }

    /// <summary>
    /// Interrupt: hit an enemy who is aiming something dangerous: a hostile ability or psycast, or a weapon with an
    /// explosive projectile (grenades, launchers), or with minAimSeconds any aim with that long left. Only when more
    /// of the enemy's aim is left than this ability's own warmup. A stun pauses the enemy's aim for its length (it
    /// doesn't cancel it); a mental state or a teleport breaks it. Area abilities centre on the enemy. Nearest first.
    /// Abilities of other systems (VEF's) have no warmup stance: their cast job's own warmup counts, through the
    /// ability sources. An ally in a hostile break is skipped unless MayHitBrokenAllies.
    /// Cost: k stance reads and k current-job checks, then up to 10 validations.
    /// </summary>
    public class AbilityAIWorker_Interrupt : AbilityAIWorker
    {
        public override bool TargetsEnemies => true;
        // One stun stops the cast; a second caster's is wasted
        protected override bool DefaultExclusive => true;
        private const int MarginTicks = 6;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            cands.Clear();
            float range = TargetingUtil.Reach(ability, ext);
            float maxSq = range * range;
            int ourWarmup = Mathf.CeilToInt(ability.WarmupSeconds * GenTicks.TicksPerRealSecond) + MarginTicks;
            int minAim = Mathf.RoundToInt(ext.minAimSeconds * GenTicks.TicksPerRealSecond);
            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                int left;
                bool danger;
                if (e.stances?.curStance is Stance_Warmup w && w.verb != null)
                {
                    left = w.ticksLeft;
                    // A non-hostile ability (painblock, focus on its own side) isn't worth a stun
                    danger = (w.verb is Verb_CastAbility cast && cast.ability?.def != null && cast.ability.def.hostile)
                        || Explosive(w.verb) || (minAim > 0 && left >= minAim);
                }
                else if (AIAbilitySources.TryGetRunningCast(e, out AIAbility running, out left))
                    danger = running.Hostile || (minAim > 0 && left >= minAim);
                else continue;
                if (!danger || left <= ourWarmup) continue;
                if (spareBroken && ctx.IsBrokenAlly(e)) continue;
                float d = (e.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq || !AbilityAIUtility.WouldAffect(ability, ext, e)) continue;
                cands.Add(new TargetingUtil.Cand { pawn = e, score = -d });
            }
            TargetingUtil.SortBest(cands);
            for (int i = 0; i < cands.Count && i < TargetingUtil.MaxTries; i++)
            {
                target = AbilityAIUtility.PawnOrCell(ability, cands[i].pawn);
                if (target.IsValid) break;
            }
            cands.Clear();
            return target.IsValid;
        }

        private static bool Explosive(Verb verb)
        {
            ThingDef proj = verb.GetProjectile();
            return proj?.projectile != null && proj.projectile.explosionRadius > 0f;
        }
    }

    /// <summary>
    /// Firefighting: put out fire on or next to the caster's side (Waterskip). Burning allies (weight 3, downed 4) and fires
    /// within 2.5 tiles of an ally (weight 1, at most 60 fires looked at, fires on other pawns left out), within reach
    /// of the caster; the spot whose effect radius covers the most weight wins, best five validated. Water breaks
    /// invisibility, so a spot that would reach an invisible ally is skipped unless someone there is burning. A
    /// self-cast ability (an area around the caster) only looks within its radius, and casts when what it covers
    /// weighs at least minTargets. Any time.
    /// Cost: nothing while the map has no fire; otherwise at most 60 × a distance checks, then a few validations.
    /// </summary>
    public class AbilityAIWorker_Firefighting : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;
        private const float NearAlly = 2.5f;
        private const int MaxFires = 60;
        private struct Spot { public IntVec3 cell; public float weight; public float score; }
        private static readonly List<Spot> spots = new List<Spot>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            var fires = ctx.map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            if (fires.Count == 0) return false;
            bool self = AbilityAIUtility.IsSelfCast(ability);
            // Reach gives a rangeless ability 20 tiles to walk; a self-cast one only covers what is around it
            float range = self ? 0f : TargetingUtil.Reach(ability, ext);
            float radius = Mathf.Max(AbilityAIUtility.Radius(ability, ext), 0.9f);
            float reachSq = (range + radius) * (range + radius);
            IntVec3 from = ctx.pawn.Position;
            var allies = ctx.Allies;

            spots.Clear();
            for (int i = 0; i < allies.Count; i++)
                if (allies[i] != ctx.pawn && allies[i].IsBurning() && (allies[i].Position - from).LengthHorizontalSquared <= reachSq)
                    spots.Add(new Spot { cell = allies[i].Position, weight = 3f });
            // A downed ally on fire can't put itself out: the case that needs the water most
            var downed = ctx.DownedAllies;
            for (int i = 0; i < downed.Count; i++)
                if (downed[i].IsBurning() && (downed[i].Position - from).LengthHorizontalSquared <= reachSq)
                    spots.Add(new Spot { cell = downed[i].Position, weight = 4f });
            int looked = 0;
            float near2 = NearAlly * NearAlly;
            for (int i = 0; i < fires.Count && looked < MaxFires; i++)
            {
                if (!(fires[i] is Fire f) || f.parent is Pawn || !f.Spawned) continue;
                if ((f.Position - from).LengthHorizontalSquared > reachSq) continue;
                looked++;
                if (TargetingUtil.AnyWithin(allies, f.Position, near2) || TargetingUtil.AnyWithin(downed, f.Position, near2))
                    spots.Add(new Spot { cell = f.Position, weight = 1f });
            }
            if (spots.Count == 0) return false;

            float r2 = radius * radius;
            if (self)
            {
                float covered = 0f;
                for (int i = 0; i < spots.Count; i++) covered += spots[i].weight;
                spots.Clear();
                if (covered < ext.minTargets) return false;
                if (InvisibleAllyNear(allies, from, r2) && !BurningAllyNear(allies, ctx.pawn, from, r2)
                    && !BurningAllyNear(downed, ctx.pawn, from, r2)) return false;
                target = ctx.pawn;
                return true;
            }
            for (int i = 0; i < spots.Count; i++)
            {
                float s = 0f;
                for (int j = 0; j < spots.Count; j++)
                    if ((spots[j].cell - spots[i].cell).LengthHorizontalSquared <= r2) s += spots[j].weight;
                var sp = spots[i];
                sp.score = s;
                spots[i] = sp;
            }
            spots.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < spots.Count && i < TargetingUtil.MaxTries; i++)
            {
                if (InvisibleAllyNear(allies, spots[i].cell, r2) && !BurningAllyNear(allies, ctx.pawn, spots[i].cell, r2)
                    && !BurningAllyNear(downed, ctx.pawn, spots[i].cell, r2)) continue;
                if (AbilityAIDriver.Valid(ability, spots[i].cell)) { target = spots[i].cell; break; }
            }
            spots.Clear();
            return target.IsValid;
        }

        private static bool BurningAllyNear(List<Pawn> allies, Pawn caster, IntVec3 c, float r2)
        {
            for (int i = 0; i < allies.Count; i++)
                if (allies[i] != caster && (allies[i].Position - c).LengthHorizontalSquared <= r2 && allies[i].IsBurning()) return true;
            return false;
        }

        private static bool InvisibleAllyNear(List<Pawn> allies, IntVec3 c, float r2)
        {
            for (int i = 0; i < allies.Count; i++)
                if ((allies[i].Position - c).LengthHorizontalSquared <= r2 && allies[i].IsPsychologicallyInvisible()) return true;
            return false;
        }
    }
}
