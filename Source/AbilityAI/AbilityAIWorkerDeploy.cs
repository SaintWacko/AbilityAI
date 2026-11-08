using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AbilityAI
{
    /// <summary>
    /// Deploy: throw something that fights on its own (Anomaly's turret pack, a modded drone or mech pack) to a cell
    /// from which it sees the most enemies. Candidate cells lie on rings around the caster out to the ability's range,
    /// within 60 degrees either side of the nearest enemy's direction. A cell counts the enemies within reach (radius,
    /// else the deployed turret's gun range, else 20) that it has a line of sight to; it needs minTargets of them, no
    /// enemy within minGapDistance, and no enemy as near the cell as the caster is (the caster's side of the fight).
    /// Not while AbilityAISettings.MaxDeployedNear (gear) or MaxSummonedNear (an ability: Alpha Animals' summons) of the
    /// caster's faction's deployed things, downed pawns left out, stand within reach of the caster or are being cast
    /// there by faction-mates; a cell within Spacing of one of those is left out, so two mates
    /// never pick one cell. Most enemies seen first, then the nearest the caster. A self-cast deployer (the thing
    /// appears at the caster) only needs minTargets enemies in sight within reach of the caster, under the same limit.
    /// What is deployed is the rule's deployedThing, else what the effects reader found.
    /// Cost: up to 36 cells, each a standability check, k distance checks and a check against the few deployed or
    /// incoming cells, line of sight only for enemies in reach; then up to 5 validations.
    /// </summary>
    public class AbilityAIWorker_Deploy : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Offensive;

        public override bool TargetsEnemies => true;

        // value: the weighted count (ctx.Weight) of the enemies seen, only while ctx.WantsValue
        private struct Spot { public IntVec3 cell; public int count; public float distSq; public float value; }
        private static readonly List<Spot> spots = new List<Spot>();
        private const int Rings = 4, PerRing = 9, MaxTries = 5;
        private const float FallbackReach = 20f, Spacing = 4f;
        private static readonly List<IntVec3> taken = new List<IntVec3>();

        /// <summary>Some gear's rule uses this worker (ApparelAIAbility.Note): shows the turret packs' limit.</summary>
        internal static bool UsedByGear;
        /// <summary>Some ability's rule uses this worker (AbilityAINpcGates.Note): shows the summons' limit.</summary>
        internal static bool UsedByAbilities;

        public override bool ReportsValue => true;

        // A cell lies on a ring no further than range (or the innermost ring's distance, when range is shorter) and sees
        // within reach of itself
        public override float ValueBound(AbilityAIContext ctx, AIAbility ability)
        {
            float reach = Reach(ext.deployedThing ?? ability.Effects.spawns);
            float range = AbilityAIUtility.IsSelfCast(ability) ? 0f
                : Mathf.Max(Mathf.Abs(AbilityAIUtility.Range(ability, ext)), Mathf.Max(2f, ext.minRange));
            return AbilityAIUtility.WeightWithin(ctx, ext, ctx.pawn.Position, range + reach + 1f, false);
        }

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            Map map = ctx.map;
            var enemies = ctx.Enemies;
            if (enemies.Count < ext.minTargets) return false;
            float range = AbilityAIUtility.Range(ability, ext);
            ThingDef deployed = ext.deployedThing ?? ability.Effects.spawns;
            float reach = Reach(deployed);
            taken.Clear();
            // Counted within reach of the caster; the cells kept clear go out as far as a candidate can lie
            float spread = range + Spacing;
            int standing = deployed != null ? OwnStandingNear(ctx, deployed, reach, spread) : 0;
            // The cells are picked from the same enemies, so two mates side by side pick the same one (run 12: two
            // raiders' turret packs at one cell, the second failing its reservation): casts under way count as standing
            int from = taken.Count;
            AbilityAIDriver.MateCastCellsNear(ability.Def, pawn.Position, Mathf.Max(reach, spread), taken);
            int incoming = CountWithin(pawn.Position, reach, from);
            int max = ability is ApparelAIAbility ? AbilityAISettings.MaxDeployedNear : AbilityAISettings.MaxSummonedNear;
            if (standing + incoming >= max)
            {
                if (AbilityAISettings.DebugLog)
                    ctx.NoTargetWhy = $"{standing} of its side's {deployed?.label ?? "deployed things"} stand within {reach:0.#} and {incoming} are on the way (at most {max})";
                taken.Clear();
                return false;
            }
            // Made at the caster's feet (Apex's satellite, VFE Pirates' wardrones, DMS's drone packs): the caster's own
            // cell is the only candidate, so it needs the enemies in sight from there
            if (AbilityAIUtility.IsSelfCast(ability))
            {
                taken.Clear();
                int seenHere = 0;
                float valueHere = 0f;
                float reachSq = reach * reach;
                for (int i = 0; i < enemies.Count; i++)
                    if ((enemies[i].Position - pawn.Position).LengthHorizontalSquared <= reachSq
                        && GenSight.LineOfSight(pawn.Position, enemies[i].Position, map, true))
                    {
                        seenHere++;
                        if (ctx.WantsValue) valueHere += ctx.Weight(enemies[i]);
                    }
                if (seenHere < ext.minTargets)
                {
                    if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = $"{seenHere} enemies in sight within {reach:0.#} (needs {ext.minTargets})";
                    return false;
                }
                if (!AbilityAIDriver.Valid(ability, pawn)) return false;
                target = pawn;
                if (ctx.WantsValue) ctx.TargetValue = valueHere;
                return true;
            }
            float spacing2 = Spacing * Spacing;

            Pawn nearest = null;
            float nearestSq = float.MaxValue;
            for (int i = 0; i < enemies.Count; i++)
            {
                float d = (enemies[i].Position - pawn.Position).LengthHorizontalSquared;
                if (d < nearestSq) { nearestSq = d; nearest = enemies[i]; }
            }
            if (nearest == null) return false;
            float facing = (nearest.Position - pawn.Position).AngleFlat;
            float reach2 = reach * reach, gap2 = ext.minGapDistance * ext.minGapDistance;

            spots.Clear();
            bool wants = ctx.WantsValue;
            float minR = Mathf.Max(2f, ext.minRange);
            for (int r = 1; r <= Rings; r++)
            {
                float dist = Mathf.Lerp(minR, range, r / (float)Rings);
                for (int s = 0; s < PerRing; s++)
                {
                    float angle = facing + Mathf.Lerp(-60f, 60f, s / (float)(PerRing - 1));
                    // HorizontalVectorFromAngle shares AngleFlat's convention (clockwise from north); FromAngleFlat doesn't
                    IntVec3 c = pawn.Position + (Vector3Utility.HorizontalVectorFromAngle(angle) * dist).ToIntVec3();
                    if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map) || NearTaken(c, spacing2)) continue;
                    float own = (c - pawn.Position).LengthHorizontalSquared;
                    int seen = 0;
                    float value = 0f;
                    bool tooClose = false;
                    for (int i = 0; i < enemies.Count && !tooClose; i++)
                    {
                        float d = (enemies[i].Position - c).LengthHorizontalSquared;
                        if (d < gap2 || d <= own) tooClose = true;
                        else if (d <= reach2 && GenSight.LineOfSight(c, enemies[i].Position, map, true))
                        {
                            seen++;
                            if (wants) value += ctx.Weight(enemies[i]);
                        }
                    }
                    if (tooClose || seen < ext.minTargets) continue;
                    spots.Add(new Spot { cell = c, count = seen, distSq = own, value = value });
                }
            }
            spots.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
            for (int i = 0; i < spots.Count && i < MaxTries; i++)
                if (AbilityAIDriver.Valid(ability, spots[i].cell))
                {
                    target = spots[i].cell;
                    if (wants) ctx.TargetValue = spots[i].value;
                    break;
                }
            if (!target.IsValid && spots.Count == 0 && AbilityAISettings.DebugLog)
                ctx.NoTargetWhy = $"no cell on its side of the fight sees {ext.minTargets} enemies";
            spots.Clear();
            taken.Clear();
            return target.IsValid;
        }

        // The rule's radius, else the deployed turret's gun range
        private float Reach(ThingDef deployed)
        {
            if (ext.radius > 0f) return ext.radius;
            var verbs = deployed?.building?.turretGunDef?.Verbs;
            if (verbs != null && verbs.Count > 0 && verbs[0].range > 0f) return verbs[0].range;
            return FallbackReach;
        }

        // How many of def the caster's faction has within radius; the cells of those within spread go into taken
        private static int OwnStandingNear(AbilityAIContext ctx, ThingDef def, float radius, float spread)
        {
            var list = ctx.map.listerThings.ThingsOfDef(def);
            Faction f = ctx.pawn.Faction;
            IntVec3 at = ctx.pawn.Position;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Faction != f || (list[i] is Pawn p && p.Downed)) continue;
                IntVec3 c = list[i].Position;
                if (c.InHorDistOf(at, spread)) taken.Add(c);
                if (c.InHorDistOf(at, radius)) n++;
            }
            return n;
        }

        // Of the cells of taken from index from on, how many lie within radius of at
        private static int CountWithin(IntVec3 at, float radius, int from)
        {
            int n = 0;
            for (int i = from; i < taken.Count; i++)
                if (taken[i].InHorDistOf(at, radius)) n++;
            return n;
        }

        private static bool NearTaken(IntVec3 c, float spacing2)
        {
            for (int i = 0; i < taken.Count; i++)
                if ((taken[i] - c).LengthHorizontalSquared < spacing2) return true;
            return false;
        }
    }
}
