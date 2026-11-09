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

        /// <summary>Fighting right now: has an enemy target, an attack job, shot at or is aiming at an enemy pawn or
        /// active turret, or was hurt or attacked recently.</summary>
        public static bool Fighting(AbilityAIContext ctx, Pawn p)
        {
            if (p == ctx.pawn) return ctx.InCombat;
            var mind = p.mindState;
            if (mind == null) return false;
            if (mind.enemyTarget != null) return true;
            var job = p.CurJob;
            if (job != null && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic)) return true;
            // A drafted colonist firing on its own (JobDriver_Wait's auto-attack) sets neither of those: it counted only
            // once hit, so Word of Protection saw two of five colonists on a firing line (build 0216 test). Its last shot
            // and its aim say it is fighting. Only at a hostile pawn: the same tick and stance come from a hunter's shot at
            // a deer and an ally's warmup on a friendly cast (review 232). An enemy turret counts too: Target hostile now
            // shoots at turrets, and a colonist trading fire with one alone read as not fighting (review 249).
            if (mind.lastAttackTargetTick > 0 && ctx.tick - mind.lastAttackTargetTick < AbilityAIContext.RecentTicks
                && IsFoe(mind.lastAttackedTarget.Thing, p)) return true;
            if (IsFoe(p.TargetCurrentlyAimingAt.Thing, p)) return true;
            return ctx.tick - mind.lastHarmTick < AbilityAIContext.RecentTicks
                || (mind.lastRangedHarmTick > 0 && ctx.tick - mind.lastRangedHarmTick < AbilityAIContext.RecentTicks);
        }

        // The same turrets Target hostile picks from (ctx.EnemyTurrets): an unpowered or dormant one is no fight
        private static bool IsFoe(Thing t, Pawn p) =>
            (t is Pawn || (t is Building_Turret turret && !turret.ThreatDisabled(p))) && t.HostileTo(p);

        // value: the scored driver's value for this pawn, for workers that report one
        public struct Cand { public Pawn pawn; public float score; public float value; }

        public static void SortBest(List<Cand> list) => list.Sort((a, b) => b.score.CompareTo(a.score));

        /// <summary>The first of the best few the ability accepts as a pawn target.</summary>
        public static bool FirstValid(List<Cand> list, AIAbility ability, out LocalTargetInfo target) => FirstValid(list, ability, out target, out _);

        /// <summary>FirstValid, with the chosen candidate's value (-1 when none is chosen).</summary>
        public static bool FirstValid(List<Cand> list, AIAbility ability, out LocalTargetInfo target, out float value)
        {
            target = LocalTargetInfo.Invalid;
            value = -1f;
            SortBest(list);
            for (int i = 0; i < list.Count && i < MaxTries; i++)
                if (AbilityAIDriver.Valid(ability, list[i].pawn)) { target = list[i].pawn; value = list[i].value; break; }
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
    /// the caster's side (Friendlies, of allyKinds) within that radius (a berserk enemy attacks whoever is nearest),
    /// the caster too unless casterLeaves. An ally in a
    /// hostile break is skipped, and avoided like a friendly, unless MayHitBrokenAllies. The best five are validated.
    /// Approach: the same out to range + approachDistance. whileHunting: the prey of the caster's hunt is a candidate
    /// too, scored as its current target. armedOnly: only enemies holding a weapon. avoidBuildings: not at an enemy with a
    /// friendly building within the radius (default 5), for a fire or blast that spreads past the target (Ignite).
    /// Cost: k WouldAffect checks, k² distance checks only with grouping, k×a with meleeOnly or avoidAllies; with
    /// avoidBuildings a pass over the cells in the radius for each enemy that passes the rest (about 30 at radius 3).
    /// Turrets: with Turret in targetKinds the enemy turrets that are a threat now are candidates too, scored as a
    /// pawn of combat power 100 (+1 when it aims at the caster), grouped and avoided the same way; meleeOnly and
    /// inMentalState leave them out. inMentalState: only enemies in a mental state (Animal calm's manhunters).
    /// </summary>
    public class AbilityAIWorker_TargetHostile : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Offensive;

        public override bool TargetsEnemies => true;
        // value: the weighted group (the target and its side near it), only while ReportsValue and ctx.WantsValue
        private struct Cand { public Thing thing; public float score; public float value; }
        private static readonly List<Cand> cands = new List<Cand>();

        // Only a group rule has a count past its minimum; a single target's worth is already in its own score
        public override bool ReportsValue => ext.minTargets > 1;

        // A target within reach and its group within Near of it: all of them within reach + Near of the caster. The prey
        // is a candidate even when it isn't in Enemies (Score), so its weight is added on top
        public override float ValueBound(AbilityAIContext ctx, AIAbility ability)
        {
            if (!ReportsValue) return -1f;
            float bound = AbilityAIUtility.WeightWithin(ctx, ext, ctx.pawn.Position, TargetingUtil.Reach(ability, ext) + Near + 1f, false);
            return ext.whileHunting && ctx.Prey != null ? bound + ctx.Weight(ctx.Prey) : bound;
        }

        private static void SortBest() => cands.Sort((a, b) => b.score.CompareTo(a.score));

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Score(ctx, ability, TargetingUtil.Reach(ability, ext));
            SortBest();
            // An ability that only takes a location (VPE's Frost Ray) is aimed at the enemy's cell
            for (int i = 0; i < cands.Count && i < TargetingUtil.MaxTries; i++)
            {
                target = AbilityAIUtility.PawnOrCell(ability, cands[i].thing);
                if (!target.IsValid) continue;
                ctx.CastAtPrey = ext.whileHunting && cands[i].thing == ctx.Prey;
                if (ctx.WantsValue && ReportsValue) ctx.TargetValue = cands[i].value;
                break;
            }
            cands.Clear();
            return target.IsValid;
        }

        public override bool TryFindApproachTarget(AbilityAIContext ctx, AIAbility ability, float reach, out Thing goal)
        {
            goal = null;
            Score(ctx, ability, reach);
            SortBest();
            for (int i = 0; i < cands.Count && i < TargetingUtil.MaxTries; i++)
            {
                Thing t = cands[i].thing;
                if (AbilityAIDriver.ValidIgnoringRange(ability, t) || AbilityAIDriver.ValidIgnoringRange(ability, t.Position)) { goal = t; break; }
            }
            cands.Clear();
            return goal != null;
        }

        // Grouping, avoidAllies and avoidBuildings: how far around the target they look
        private float Near => ext.radius >= 0f ? ext.radius : 5f;

        public override bool StillClear(AbilityAIContext ctx, AIAbility ability, LocalTargetInfo target)
        {
            if (!ext.avoidAllies && !ext.avoidBuildings) return true;
            IntVec3 at = AimCell(ctx, target);
            if (!at.IsValid) return true;
            float near = Near;
            if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, at, near * near, !AbilityAIUtility.MayHitBrokenAllies(ext), ext.casterLeaves)) return false;
            // Buildings don't move: only a pawn target can carry the area onto one
            return !ext.avoidBuildings || !(target.Thing is Pawn) || !AbilityAIUtility.FriendlyBuildingWithin(ctx, at, near);
        }

        // The per-enemy tests of Consider; grouping, avoidAllies and avoidBuildings are about where the area lands,
        // which a linked Word has none of
        protected internal override bool IsLinkTarget(AbilityAIContext ctx, AIAbility ability, Pawn p)
        {
            if (p.Downed || !p.HostileTo(ctx.pawn) || !AbilityAIUtility.WouldAffect(ability, ext, p)) return false;
            if (ctx.IsBrokenAlly(p) && !AbilityAIUtility.MayHitBrokenAllies(ext)) return false;
            if (ext.armedOnly && p.equipment?.Primary == null) return false;
            if (ext.inMentalState && !p.InMentalState) return false;
            if (ext.permanentEnemiesOnly && CostsGoodwill(p)) return false;
            return !ext.meleeOnly || AbilityAIUtility.InMeleeWithSide(ctx, p);
        }

        private void Score(AbilityAIContext ctx, AIAbility ability, float range)
        {
            cands.Clear();
            var enemies = ctx.Enemies;
            float maxSq = range * range, minSq = ext.minRange * ext.minRange;
            float near = Near;
            float near2 = near * near;
            bool grouping = ext.preferGrouped || ext.minTargets > 1;
            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            Thing current = ctx.EnemyTarget;
            Pawn prey = ext.whileHunting ? ctx.Prey : null;
            bool preySeen = false;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                // A prey of a faction is hostile to its hunter, so it can be here too
                if (e == prey) preySeen = true;
                if (spareBroken && ctx.IsBrokenAlly(e)) continue;
                Consider(ctx, ability, e, e == current || e == prey, range, maxSq, minSq, near2, grouping, spareBroken);
            }
            if (prey != null && !preySeen)
                Consider(ctx, ability, prey, true, range, maxSq, minSq, near2, grouping, spareBroken);
            if (AbilityAIUtility.WantsTurrets(ext) && !ext.meleeOnly && !ext.inMentalState)
            {
                var turrets = ctx.EnemyTurrets;
                for (int i = 0; i < turrets.Count; i++)
                    ConsiderTurret(ctx, ability, turrets[i], turrets[i] == current, range, maxSq, minSq, near2, grouping, spareBroken);
            }
        }

        private void ConsiderTurret(AbilityAIContext ctx, AIAbility ability, Thing t, bool isCurrent, float range, float maxSq, float minSq,
            float near2, bool grouping, bool spareBroken)
        {
            float d = (t.Position - ctx.pawn.Position).LengthHorizontalSquared;
            if (d > maxSq || d < minSq) return;
            if (!AbilityAIUtility.WouldAffect(ability, ext, t)) return;
            float score = 1f;
            if (isCurrent) score += 1f;
            if (t is Building_Turret turret && turret.CurrentTarget.Thing == ctx.pawn) score += 1f;
            float value = -1f;
            if (grouping)
            {
                int group = 1 + CountNear(ctx, t, near2, spareBroken, ref value);
                if (group < ext.minTargets) return;
                if (ext.preferGrouped) score *= group;
            }
            if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, t.Position, near2, spareBroken, ext.casterLeaves)) return;
            if (ext.avoidBuildings && AbilityAIUtility.FriendlyBuildingWithin(ctx, t.Position, Near)) return;
            score /= 1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f);
            cands.Add(new Cand { thing = t, score = score, value = value });
        }

        // As CompAbilityEffect_PsychicSlaughter.Apply decides when a kill costs the player goodwill
        private static bool CostsGoodwill(Pawn p)
        {
            Faction home = p.HomeFaction;
            return p.Faction != null && home != null && home != Faction.OfPlayer && home.def.humanlikeFaction
                && !p.Faction.def.PermanentlyHostileTo(FactionDefOf.PlayerColony);
        }

        // The rest of the target's side within near2 of it: enemy pawns, and enemy turrets when the rule names them.
        // value: set to the weighted group, the target included, while ReportsValue and ctx.WantsValue; else left as it is
        private int CountNear(AbilityAIContext ctx, Thing t, float near2, bool spareBroken, ref float value)
        {
            bool weigh = ctx.WantsValue && ReportsValue;
            float w = weigh ? ctx.Weight(t) : 0f;
            int n = 0;
            var enemies = ctx.Enemies;
            for (int j = 0; j < enemies.Count; j++)
                if (enemies[j] != t && (enemies[j].Position - t.Position).LengthHorizontalSquared <= near2
                    && (!spareBroken || !ctx.IsBrokenAlly(enemies[j])))
                {
                    n++;
                    if (weigh) w += ctx.Weight(enemies[j]);
                }
            if (AbilityAIUtility.WantsTurrets(ext))
            {
                var turrets = ctx.EnemyTurrets;
                for (int j = 0; j < turrets.Count; j++)
                    if (turrets[j] != t && (turrets[j].Position - t.Position).LengthHorizontalSquared <= near2)
                    {
                        n++;
                        if (weigh) w += ctx.Weight(turrets[j]);
                    }
            }
            if (weigh) value = w;
            return n;
        }

        private void Consider(AbilityAIContext ctx, AIAbility ability, Pawn e, bool isCurrent, float range, float maxSq, float minSq,
            float near2, bool grouping, bool spareBroken)
        {
            float d = (e.Position - ctx.pawn.Position).LengthHorizontalSquared;
            if (d > maxSq || d < minSq) return;
            if (!AbilityAIUtility.WouldAffect(ability, ext, e)) return;
            if (ext.armedOnly && e.equipment?.Primary == null) return;
            if (ext.inMentalState && !e.InMentalState) return;
            if (ext.permanentEnemiesOnly && CostsGoodwill(e)) return;
            if (ext.meleeOnly && !AbilityAIUtility.InMeleeWithSide(ctx, e)) return;
            float score = 1f;
            if (isCurrent) score += 1f;
            if (AbilityAIUtility.AimsAt(e, ctx.pawn)) score += 1f;
            score *= Mathf.Clamp(e.kindDef.combatPower / 100f, 0.5f, 3f);
            float value = -1f;
            if (grouping)
            {
                int group = 1 + CountNear(ctx, e, near2, spareBroken, ref value);
                if (group < ext.minTargets) return;
                if (ext.preferGrouped) score *= group;
            }
            if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, e.Position, near2, spareBroken, ext.casterLeaves)) return;
            // Here rather than on the best few: when those all stand by a wall, a clear enemy further down still counts
            if (ext.avoidBuildings && AbilityAIUtility.FriendlyBuildingWithin(ctx, e.Position, Near)) return;
            score /= 1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f);
            cands.Add(new Cand { thing = e, score = score, value = value });
        }
    }

    /// <summary>
    /// Target friendly: a buff on one ally or the caster (Focus, Painblock on someone hurting). Allies in range
    /// without the ability's hediff; with fireDanger only those in fire danger (AbilityAIFire), with minPain only
    /// those in at least that much pain, otherwise only allies fighting now; with avoidFireDanger none in fire danger;
    /// with auraKinds, only allies whose aura
    /// would reach enough enemies (AbilityAIUtility.AuraWorthIt). Scored by (1 + pain + fire danger), the caster
    /// x1.5, divided by distance over range; with valueFromThreats also x (1 + threats on the ally). Cost: a checks,
    /// and for an aura, fireDanger or avoidFireDanger rule a pass over the enemies per ally that gets that far; with
    /// Dynamic priority a minPain rule also reads each candidate's pain shock threshold stat; valueFromThreats one
    /// pass over the enemies' aims per evaluation (shared with Defensive and Cover), then a lookup per ally.
    /// </summary>
    public class AbilityAIWorker_TargetFriendly : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Buff;

        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        // value: UrgencyPoints x the chosen ally's urgency, for the triggers that measure one: fireDanger (AbilityAIFire
        // danger / 3, burning = 1) and minPain (pain from minPain up to the ally's pain shock threshold). On the
        // fighting trigger only with valueFromThreats, as the threats on the ally: whether a fight makes a buff more
        // urgent depends on the buff (Word of protection yes, Focus no), which only the rule can say. An aura rule is
        // already gated on its count and keeps its priority. ValueBase 0: an ally firing with nobody firing back is
        // fighting but under no threat, and stays at the rule's priority
        public override bool ReportsValue => ext.auraKinds == AbilityAITargetKinds.None && (ext.fireDanger || ext.minPain > 0f || ext.valueFromThreats);

        // ConfigErrors refuse valueFromThreats beside the other triggers; if one slips through, the trigger wins
        private bool ByThreats => ext.valueFromThreats && !ext.fireDanger && ext.minPain <= 0f && ext.auraKinds == AbilityAITargetKinds.None;

        public override float ValueBase => 0f;

        // Threats: no ally has more aims on it than there are enemies, plus one for a recent shot and melee's points
        public override float ValueBound(AbilityAIContext ctx, AIAbility ability) =>
            !ReportsValue ? -1f
            : ByThreats ? ctx.Enemies.Count + 1f + AbilityAIWorker_Defensive.MeleePoints
            : AbilityAIScore.UrgencyPoints;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            bool weigh = ctx.WantsValue && ReportsValue;
            cands.Clear();
            float range = TargetingUtil.Reach(ability, ext);
            float maxSq = range * range;
            bool self = AbilityAIUtility.CanTargetSelf(ability);
            bool onlySelf = AbilityAIUtility.IsSelfCast(ability);
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            bool byThreats = ByThreats;
            var allies = ctx.SupportAllies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn a = allies[i];
                bool isSelf = a == ctx.pawn;
                if (isSelf ? !self : onlySelf) continue;
                float d = (a.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq || !Wants(ctx, ability, a, hediff, out float pain, out float fire)) continue;
                float score = (1f + pain + fire) * (isSelf ? 1.5f : 1f) / (1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f));
                float value = -1f;
                if (byThreats)
                {
                    // The target choice too, with the setting off as well: measured only on the ally the pain and
                    // distance score picks, the nearest ally with nothing on it would win over one under four guns a
                    // few tiles further, and the cast would score low besides
                    float threats = Threats(ctx, a);
                    score *= 1f + threats;
                    if (weigh) value = threats;
                }
                else if (weigh) value = Urgency(a, pain, fire) * AbilityAIScore.UrgencyPoints;
                cands.Add(new TargetingUtil.Cand { pawn = a, score = score, value = value });
            }
            if (!TargetingUtil.FirstValid(cands, ability, out target, out float value)) return false;
            if (weigh) ctx.TargetValue = value;
            return true;
        }

        // Counted as Defensive counts them: each ranged enemy aiming at the ally, a shot in the last 10 seconds, melee
        private static float Threats(AbilityAIContext ctx, Pawn a) =>
            ctx.AimedAtCount(a) + (ctx.RecentlyShot(a) ? 1f : 0f)
            + (AbilityAIUtility.InMelee(ctx, a) ? AbilityAIWorker_Defensive.MeleePoints : 0f);

        private float Urgency(Pawn a, float pain, float fire)
        {
            // A fireDanger rule only targets allies in fire danger, so the fire is why it casts, whatever the pain
            if (ext.fireDanger) return Mathf.Clamp01(fire / AbilityAIFire.Burning);
            float shock = a.GetStatValue(StatDefOf.PainShockThreshold);
            return shock > ext.minPain ? Mathf.Clamp01((pain - ext.minPain) / (shock - ext.minPain)) : 1f;
        }

        private bool Wants(AbilityAIContext ctx, AIAbility ability, Pawn a, HediffDef hediff, out float pain, out float fire)
        {
            pain = fire = 0f;
            if (AbilityAIUtility.Has(a, hediff) || !AbilityAIUtility.Fits(ext, a)) return false;
            pain = a.health?.hediffSet?.PainTotal ?? 0f;
            if (ext.fireDanger)
            {
                fire = AbilityAIFire.Danger(ctx, a);
                if (fire <= 0f) return false;
            }
            else if (ext.minPain > 0f ? pain < ext.minPain : !TargetingUtil.Fighting(ctx, a)) return false;
            if (ext.avoidFireDanger && AbilityAIFire.Danger(ctx, a) > 0f) return false;
            return AbilityAIUtility.AuraWorthIt(ctx, ability, ext, a);
        }

        protected internal override bool IsLinkTarget(AbilityAIContext ctx, AIAbility ability, Pawn p) =>
            p != ctx.pawn && ctx.SupportAllies.Contains(p) && Wants(ctx, ability, p, AbilityAIUtility.Hediff(ability, ext), out _, out _);
    }

    /// <summary>
    /// Defensive: protect the caster or an ally who is in trouble (Invisibility). In trouble = health under
    /// healthThreshold with an enemy within 15 tiles, or hurt in melee in the last 10 seconds, or at least minThreats
    /// ranged enemies aiming at them (being shot recently counts as one); with rangedOnly only the last of those.
    /// Skips anyone with the ability's hediff.
    /// The caster first (x1.5), then whoever is worst hurt. Cost: a × k distance checks at worst (only for allies
    /// under the health threshold; with Dynamic priority for each of those even when melee or threats already
    /// count, to measure the worst reason), plus one pass over the enemies' targets.
    /// </summary>
    public class AbilityAIWorker_Defensive : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Defensive;

        private const float DangerRange = 15f;
        // In melee counts as two shooters past the trigger
        internal const float MeleePoints = 2f;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        // value: the chosen ally's worst trouble: each threat past minThreats a point, in melee MeleePoints, health
        // under healthThreshold near enemies UrgencyPoints x (threshold - health) / threshold. The caster's x1.5 is a
        // target choice (it needs no walk and knows its own state), not more trouble, so it isn't in the value. No
        // bound: health urgency alone can reach the cap, so a bound would never skip the search
        public override bool ReportsValue => true;

        public override float ValueBase => 0f;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            bool weigh = ctx.WantsValue;
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
                float trouble = Trouble(ctx, a, health, weigh);
                if (trouble < 0f) continue;
                cands.Add(new TargetingUtil.Cand { pawn = a, score = (2f - health) * (isSelf ? 1.5f : 1f), value = trouble });
            }
            if (!TargetingUtil.FirstValid(cands, ability, out target, out float value)) return false;
            if (weigh) ctx.TargetValue = value;
            return true;
        }

        // -1 = not in trouble. Without weigh the first reason found answers (0); with it every reason is measured
        // and the largest is the value
        private float Trouble(AbilityAIContext ctx, Pawn p, float health, bool weigh)
        {
            float trouble = -1f;
            if (!ext.rangedOnly && AbilityAIUtility.InMelee(ctx, p))
            {
                if (!weigh) return 0f;
                trouble = MeleePoints;
            }
            // minThreats 0 would count every ally in range as in trouble: treat it as "threats don't count"
            if (ext.minThreats > 0)
            {
                int threats = ctx.AimedAtCount(p) + (ctx.RecentlyShot(p) ? 1 : 0);
                if (threats >= ext.minThreats)
                {
                    if (!weigh) return 0f;
                    trouble = Mathf.Max(trouble, threats - ext.minThreats);
                }
            }
            if (ext.rangedOnly || health >= ext.healthThreshold) return trouble;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].Position.InHorDistOf(p.Position, DangerRange))
                    return Mathf.Max(trouble, AbilityAIScore.UrgencyPoints * (ext.healthThreshold - health) / ext.healthThreshold);
            return trouble;
        }
    }

    /// <summary>
    /// Rescue: help a downed ally (Painblock gets a pawn downed by pain alone back on its feet). Downed allies in
    /// range without the ability's hediff, painShockOnly limiting it to pain shock and minBleedRate to those
    /// bleeding that fast (cached on the hediff set); nearest first. Any time, in combat or not. Cost: one pass over
    /// downed allies, and with Dynamic priority a pass over each one's hediffs for the time left to bleed out.
    /// </summary>
    public class AbilityAIWorker_Rescue : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Rescue;

        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        // value: UrgencyPoints x how close the chosen pawn is to bleeding out (AbilityAIUtility.BleedUrgency), so a
        // rescue of someone with minutes left can pass an Interrupt
        public override bool ReportsValue => true;

        public override float ValueBase => 0f;

        public override float ValueBound(AbilityAIContext ctx, AIAbility ability) => AbilityAIScore.UrgencyPoints;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            dest = LocalTargetInfo.Invalid;
            bool weigh = ctx.WantsValue;
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
                cands.Add(new TargetingUtil.Cand { pawn = p, score = -d, value = weigh ? AbilityAIScore.UrgencyPoints * AbilityAIUtility.BleedUrgency(p) : -1f });
            }
            if (!TargetingUtil.FirstValid(cands, ability, out target, out float value)) return false;
            if (weigh) ctx.TargetValue = value;
            return true;
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
                if (p == ctx.pawn || !Calms(p, player)) continue;
                float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq) continue;
                cands.Add(new TargetingUtil.Cand { pawn = p, score = -d });
            }
            // A catatonic breakdown is a hediff that downs the pawn for days, not a mental state, but vanilla's
            // StopMentalState cures it and prices it as extreme. The player's pawns only, when extreme breaks count.
            if (CalmsCatatonic(player))
            {
                var downed = ctx.DownedAllies;
                for (int i = 0; i < downed.Count; i++)
                {
                    Pawn p = downed[i];
                    float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
                    if (d > maxSq || !Catatonic(p)) continue;
                    cands.Add(new TargetingUtil.Cand { pawn = p, score = -d });
                }
            }
            return TargetingUtil.FirstValid(cands, ability, out target);
        }

        private bool Calms(Pawn p, bool player) =>
            Wants(p, player) && AbilityAIUtility.Fits(ext, p)
            && !(ext.minBreakSeconds > 0f && TicksLeft(p.MentalState) < ext.minBreakSeconds * GenTicks.TicksPerRealSecond);

        private static bool CalmsCatatonic(bool player) =>
            player && AbilityAISettings.CalmBreaks != AbilityAICalmBreaks.Aggressive && HediffDefOf.CatatonicBreakdown != null;

        private bool Catatonic(Pawn p) =>
            p.health?.hediffSet?.HasHediff(HediffDefOf.CatatonicBreakdown) == true && AbilityAIUtility.Fits(ext, p);

        protected internal override bool IsLinkTarget(AbilityAIContext ctx, AIAbility ability, Pawn p)
        {
            if (p == ctx.pawn) return false;
            bool player = AbilityAIDriver.EvaluatingPlayer;
            if (ctx.Allies.Contains(p)) return Calms(p, player);
            return CalmsCatatonic(player) && ctx.DownedAllies.Contains(p) && Catatonic(p);
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
    /// Bloodfeed: a sanguophage's bite (vanilla's Bloodfeed), out of combat. One rule per feedOn, tried by priority:
    /// downed enemies whenever the caster has room for the bite's hemogen, then the colony's prisoners and its own
    /// people only once its hemogen is under the target its owner set (vanilla's ShouldConsumeHemogenNow, the same
    /// point at which vanilla sends it to a prisoner). Never a pawn the bite would kill, counting a quarter day of its
    /// bleeding on top (a downed raider bleeds until someone tends it), one in a mental state, or one with a standing
    /// enemy within 15 tiles. Prisoners go through vanilla's own check (the bloodfeed interaction allowed, secure,
    /// reachable); faction-mates must not be downed, drafted, bleeding or short of blood already, and are left alone
    /// while the caster may take hemogen packs and one is within reach, which vanilla's own job goes for instead.
    /// A touch ability: an undrafted pawn of the player's looks as far as CalmRange, like a calming word; others the
    /// extension's reach. Nearest first.
    /// Cost: nothing while the caster's hemogen is high enough; otherwise one pass over the map's humanlikes, the
    /// colony's prisoners (vanilla's reach check for those in range) or the caster's allies, each with k distance
    /// checks for the enemy rule.
    /// </summary>
    public class AbilityAIWorker_Bloodfeed : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Recovery;

        protected override AbilityAICombat DefaultCombat => AbilityAICombat.OutOfCombat;
        private const float DangerRange = 15f;
        // BleedRateTotal is blood lost per day. Two hours of it: a quarter day refused nearly every untended
        // downed raider (141%/d plus the 0.45 bite is past the 0.6 life-threatening stage), while this still
        // refuses the 200%/d bleeders a bite would push into dying before anyone tends them.
        private const float BleedDays = 2f / 24f;
        private const int MaxPacksChecked = 5;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        // As vanilla's JobGiver_GetHemogen looks for one: carried, in the inventory, or on the map unforbidden. The
        // reach check paths, so only the first few.
        private static bool PackReachable(Pawn pawn)
        {
            ThingDef pack = ThingDefOf.HemogenPack;
            if (pawn.carryTracker?.CarriedThing?.def == pack) return true;
            var inv = pawn.inventory?.innerContainer;
            if (inv != null)
                for (int i = 0; i < inv.Count; i++)
                    if (inv[i].def == pack) return true;
            var packs = pawn.Map.listerThings.ThingsOfDef(pack);
            int checkedCount = 0;
            for (int i = 0; i < packs.Count && checkedCount < MaxPacksChecked; i++)
            {
                if (packs[i].IsForbidden(pawn) || !pawn.CanReserve(packs[i])) continue;
                checkedCount++;
                if (pawn.CanReach(packs[i], PathEndMode.OnCell, Danger.Deadly)) return true;
            }
            return false;
        }

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            var gene = pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>();
            var bite = Bite(ability.Def as AbilityDef);
            if (gene == null || bite == null) return false;
            if (ext.feedOn == AbilityAIFeedOn.DownedEnemies ? gene.Value > gene.Max - bite.hemogenGain : !gene.ShouldConsumeHemogenNow())
                return false;
            float range = TargetingUtil.Reach(ability, ext);
            if (AbilityAIDriver.EvaluatingPlayer && !pawn.Drafted && AbilityAIUtility.Range(ability, ext) <= 0f) range = AbilityAISettings.CalmRange;
            float maxSq = range * range;
            cands.Clear();
            switch (ext.feedOn)
            {
                case AbilityAIFeedOn.DownedEnemies:
                {
                    var list = ctx.map.mapPawns.AllHumanlikeSpawned;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn p = list[i];
                        if (!p.Downed || p.Dead || p.IsPrisonerOfColony || !(p.HostileTo(pawn) || p.IsWildMan())) continue;
                        Consider(ctx, p, bite, maxSq);
                    }
                    break;
                }
                case AbilityAIFeedOn.Prisoners:
                {
                    if (pawn.Faction == null || !pawn.Faction.IsPlayer) break;
                    var list = ctx.map.mapPawns.PrisonersOfColonySpawned;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn p = list[i];
                        // Vanilla's own rule for its hungry-sanguophage job, reach check last since it paths
                        if ((p.Position - pawn.Position).LengthHorizontalSquared > maxSq) continue;
                        if (Consider(ctx, p, bite, maxSq) && !JobGiver_GetHemogen.CanFeedOnPrisoner(pawn, p).Accepted)
                            cands.RemoveAt(cands.Count - 1);
                    }
                    break;
                }
                default:
                {
                    // Vanilla takes a pack only when a whole one fits
                    if (gene.hemogenPacksAllowed && gene.Max - gene.Value >= JobGiver_GetHemogen.HemogenPackHemogenGain
                        && PackReachable(pawn)) break;
                    var list = ctx.SupportAllies;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn p = list[i];
                        if (p == pawn || p.Drafted || !p.RaceProps.Humanlike || p.IsBloodfeeder()) continue;
                        var health = p.health?.hediffSet;
                        if (health == null || health.BleedRateTotal > 0f || health.HasHediff(HediffDefOf.BloodLoss)) continue;
                        Consider(ctx, p, bite, maxSq);
                    }
                    break;
                }
            }
            return TargetingUtil.FirstValid(cands, ability, out target);
        }

        private bool Consider(AbilityAIContext ctx, Pawn p, CompProperties_AbilityBloodfeederBite bite, float maxSq)
        {
            float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
            if (d > maxSq || p.InMentalState || !AbilityAIUtility.Fits(ext, p)) return false;
            if (bite.targetBloodLoss > 0f)
            {
                float bleeding = (p.health?.hediffSet?.BleedRateTotal ?? 0f) * BleedDays;
                if (p.WouldDieFromAdditionalBloodLoss(bite.targetBloodLoss + bleeding)) return false;
            }
            if (TargetingUtil.AnyWithin(ctx.Enemies, p.Position, DangerRange * DangerRange)) return false;
            cands.Add(new TargetingUtil.Cand { pawn = p, score = -d });
            return true;
        }

        private static CompProperties_AbilityBloodfeederBite Bite(AbilityDef def)
        {
            var comps = def?.comps;
            if (comps == null) return null;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is CompProperties_AbilityBloodfeederBite bite) return bite;
            return null;
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
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Interrupt;

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
    /// self-cast ability (an area around the caster) only looks within its radius. Either kind casts only when what it
    /// covers weighs at least minTargets. Any time.
    /// Cost: nothing while the map has no fire; otherwise at most 60 × a distance checks, then a few validations.
    /// </summary>
    public class AbilityAIWorker_Firefighting : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;
        private const float NearAlly = 2.5f;
        private const int MaxFires = 60;
        private struct Spot { public IntVec3 cell; public float weight; public float score; }
        private static readonly List<Spot> spots = new List<Spot>();

        // The value is the fire weight covered, already weighed by who is burning
        public override bool ReportsValue => true;

        // Bounding the weight would take the fire scan itself; with no fire the rule finds nothing anyway
        public override float ValueBound(AbilityAIContext ctx, AIAbility ability) =>
            ctx.map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0 ? -1f : float.MaxValue;

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
                if (ctx.WantsValue) ctx.TargetValue = covered;
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
                if (spots[i].score < ext.minTargets) break;
                if (InvisibleAllyNear(allies, spots[i].cell, r2) && !BurningAllyNear(allies, ctx.pawn, spots[i].cell, r2)
                    && !BurningAllyNear(downed, ctx.pawn, spots[i].cell, r2)) continue;
                if (AbilityAIDriver.Valid(ability, spots[i].cell))
                {
                    target = spots[i].cell;
                    if (ctx.WantsValue) ctx.TargetValue = spots[i].score;
                    break;
                }
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
