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
    /// Not while one of the deployed things of the caster's faction stands within reach of the caster, nor while a
    /// faction-mate is casting it at a cell within reach of the caster. Most
    /// enemies seen first, then the nearest the caster.
    /// Cost: up to 36 cells, each a standability check and k distance checks, line of sight only for enemies in reach;
    /// then up to 5 validations.
    /// </summary>
    public class AbilityAIWorker_Deploy : AbilityAIWorker
    {
        public override bool TargetsEnemies => true;

        private struct Spot { public IntVec3 cell; public int count; public float distSq; }
        private static readonly List<Spot> spots = new List<Spot>();
        private const int Rings = 4, PerRing = 9, MaxTries = 5;
        private const float FallbackReach = 20f;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            Map map = ctx.map;
            var enemies = ctx.Enemies;
            if (enemies.Count < ext.minTargets) return false;
            float range = AbilityAIUtility.Range(ability, ext);
            float reach = Reach(ability);
            ThingDef deployed = ability.Effects.spawns;
            if (deployed != null && OwnStandingNear(ctx, deployed, reach))
            {
                if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = $"a {deployed.label} of its side already stands within {reach:0.#}";
                return false;
            }
            // The cells are picked from the same enemies, so two mates side by side pick the same one (run 12: two
            // raiders' turret packs at one cell, the second failing its reservation); one thrown nearby does for both
            if (AbilityAIDriver.MateCastingNear(ability.Def, pawn.Position, reach))
            {
                if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = "a mate nearby is already deploying one";
                return false;
            }

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
            float minR = Mathf.Max(2f, ext.minRange);
            for (int r = 1; r <= Rings; r++)
            {
                float dist = Mathf.Lerp(minR, range, r / (float)Rings);
                for (int s = 0; s < PerRing; s++)
                {
                    float angle = facing + Mathf.Lerp(-60f, 60f, s / (float)(PerRing - 1));
                    // HorizontalVectorFromAngle shares AngleFlat's convention (clockwise from north); FromAngleFlat doesn't
                    IntVec3 c = pawn.Position + (Vector3Utility.HorizontalVectorFromAngle(angle) * dist).ToIntVec3();
                    if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) continue;
                    float own = (c - pawn.Position).LengthHorizontalSquared;
                    int seen = 0;
                    bool tooClose = false;
                    for (int i = 0; i < enemies.Count && !tooClose; i++)
                    {
                        float d = (enemies[i].Position - c).LengthHorizontalSquared;
                        if (d < gap2 || d <= own) tooClose = true;
                        else if (d <= reach2 && GenSight.LineOfSight(c, enemies[i].Position, map, true)) seen++;
                    }
                    if (tooClose || seen < ext.minTargets) continue;
                    spots.Add(new Spot { cell = c, count = seen, distSq = own });
                }
            }
            spots.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
            for (int i = 0; i < spots.Count && i < MaxTries; i++)
                if (AbilityAIDriver.Valid(ability, spots[i].cell)) { target = spots[i].cell; break; }
            if (!target.IsValid && spots.Count == 0 && AbilityAISettings.DebugLog)
                ctx.NoTargetWhy = $"no cell on its side of the fight sees {ext.minTargets} enemies";
            spots.Clear();
            return target.IsValid;
        }

        // The rule's radius, else the deployed turret's gun range
        private float Reach(AIAbility ability)
        {
            if (ext.radius > 0f) return ext.radius;
            var verbs = ability.Effects.spawns?.building?.turretGunDef?.Verbs;
            if (verbs != null && verbs.Count > 0 && verbs[0].range > 0f) return verbs[0].range;
            return FallbackReach;
        }

        private static bool OwnStandingNear(AbilityAIContext ctx, ThingDef def, float radius)
        {
            var list = ctx.map.listerThings.ThingsOfDef(def);
            Faction f = ctx.pawn.Faction;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Faction == f && list[i].Position.InHorDistOf(ctx.pawn.Position, radius)) return true;
            return false;
        }
    }
}
