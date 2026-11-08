using System.Collections.Generic;
using RimWorld;
using Verse;

// Barriers: a channelled shield around the caster that stops projectiles fired from outside it (VPE's Guardian
// Skipbarrier: radius 9, destroys any projectile whose origin is outside the radius, +1 heat every 10 ticks, ended by
// VPE at the heat maximum or by the player's cancel gizmo). VPE lets the caster cast no other psycast while it is up,
// so it is raised only against a real volley and dropped once the volley has thinned (AbilityAIDriver.BarrierUpkeep).
// The player's pawns raise it drafted only; other factions too. Settings: BarrierMinShooters, BarrierDropFraction,
// BarrierDropTicks (General tab, shown with a rule).
namespace AbilityAI
{
    /// <summary>
    /// Barrier: self-cast when at least BarrierMinShooters ranged enemies (and enemy turrets) outside the radius aim
    /// at pawns or buildings of the caster's side inside it. Not while the caster or an ally within the radius has the
    /// rule's hediff (the barrier) up, nor while a faction-mate within the radius is casting it. The player's pawns
    /// only while drafted. The driver drops it later (BarrierUpkeep).
    /// Needs hediff (the barrier's). Cost: k distance checks and aim reads, then one pass over the allies.
    /// </summary>
    public class AbilityAIWorker_Barrier : AbilityAIWorker
    {
        protected internal override AbilityAICategory DefaultCategory => AbilityAICategory.Defensive;

        /// <summary>The count and radius of the last target found, read by the driver right after a cast.</summary>
        internal static int LastCount;
        internal static float LastRadius;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            if (AbilityAIDriver.EvaluatingPlayer && !pawn.Drafted)
            {
                if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = "the player's pawns raise it drafted only";
                return false;
            }
            if (AbilityAIUtility.Has(pawn, ext.hediff)) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            if (radius <= 0f) return false;
            int count = AbilityAIBarrier.Threats(ctx, radius);
            if (count < AbilityAISettings.BarrierMinShooters)
            {
                if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = $"{count} shooters outside aim inside, needs {AbilityAISettings.BarrierMinShooters}";
                return false;
            }
            float r2 = radius * radius;
            var allies = ctx.Allies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn p = allies[i];
                if (p != pawn && (p.Position - pawn.Position).LengthHorizontalSquared <= r2 && AbilityAIUtility.Has(p, ext.hediff))
                {
                    if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = $"{p.LabelShort} already has one up nearby";
                    return false;
                }
            }
            if (AbilityAIDriver.MateCastingNear(ability.Def, pawn.Position, radius))
            {
                if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = "a mate nearby is already raising one";
                return false;
            }
            LastCount = count;
            LastRadius = radius;
            target = pawn;
            return true;
        }
    }

    public static class AbilityAIBarrier
    {
        private static readonly HashSet<Def> noted = new HashSet<Def>();

        /// <summary>Some ability def has a Barrier rule: the settings section is shown.</summary>
        public static bool Any => noted.Count > 0;

        /// <summary>Called at startup for every ability def of every system, beside AbilityAIZones.Note.</summary>
        public static void Note(Def abilityDef)
        {
            var exts = abilityDef?.modExtensions;
            if (exts == null) return;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension rule && rule.Worker is AbilityAIWorker_Barrier)
                {
                    noted.Add(abilityDef);
                    return;
                }
        }

        /// <summary>The tooltip line for an ability with a Barrier rule, or null.</summary>
        internal static string Hint(Def abilityDef) =>
            abilityDef != null && noted.Contains(abilityDef)
                ? (string)"AAI_Barrier_Hint".Translate(AbilityAISettings.BarrierMinShooters, (AbilityAISettings.BarrierDropTicks / 60f).ToString("0.#"),
                    (AbilityAISettings.BarrierMinShooters * AbilityAISettings.BarrierDropFraction).ToString("0.#"))
                : null;

        /// <summary>Ranged enemies and enemy turrets outside radius of the caster aiming at something of its side inside
        /// it: the caster, a pawn or building of its faction or of one not hostile to it.</summary>
        internal static int Threats(AbilityAIContext ctx, float radius)
        {
            Pawn pawn = ctx.pawn;
            IntVec3 c = pawn.Position;
            float r2 = radius * radius;
            int n = 0;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                if ((e.Position - c).LengthHorizontalSquared <= r2 || !AbilityAIContext.FightsAtRange(e)) continue;
                if (Inside(pawn, AbilityAIUtility.AimTarget(e), c, r2)) n++;
            }
            var turrets = ctx.EnemyTurrets;
            for (int i = 0; i < turrets.Count; i++)
            {
                if (!(turrets[i] is Building_Turret t) || (t.Position - c).LengthHorizontalSquared <= r2) continue;
                if (Inside(pawn, t.CurrentTarget.Thing, c, r2)) n++;
            }
            return n;
        }

        private static bool Inside(Pawn caster, Thing t, IntVec3 c, float r2)
        {
            if (t == null || !t.Spawned || (t.Position - c).LengthHorizontalSquared > r2) return false;
            return t == caster || (t.Faction != null && !t.HostileTo(caster));
        }

        /// <summary>The player's Max heat limit is set and the pawn is at it: autocast spends no more heat, and a barrier
        /// it raised keeps adding heat.</summary>
        internal static bool AtHeatLimit(Pawn pawn)
        {
            var pe = pawn.psychicEntropy;
            if (pe == null || !AbilityAIAutocast.TryGetLimits(pawn, out float heatCap, out _, out _)) return false;
            return heatCap < AbilityAIAutocast.NoHeatCap && pe.EntropyValue >= heatCap * pe.MaxEntropy;
        }
    }
}
