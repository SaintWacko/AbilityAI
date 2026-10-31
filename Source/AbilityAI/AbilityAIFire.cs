using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AbilityAI
{
    /// <summary>
    /// Fire danger for Target friendly's fireDanger (Hydro shield): how close a pawn is to burning. 3 = burning now,
    /// 2 = a fire within 2.5 tiles of it, 1 = an enemy that can set fires has it within reach (plus 3 tiles), 0 = none.
    /// An enemy can set fires with an incendiary or flame weapon (projectile ai_IsIncendiary or Flame damage, a Flame
    /// beam) or an ability whose rule says startsFires (vanilla's fire spew and fire burst count without one).
    /// Cost: one IsBurning read; nothing more for fires while the map has none, else 21 cell lookups; one distance
    /// check per enemy, whose reach is worked out at most every 250 ticks.
    /// </summary>
    public static class AbilityAIFire
    {
        private const float NearFire = 2.5f;
        private const float ThreatMargin = 3f;
        private const int ReachTicks = 250;
        private const int PruneTicks = 2500;

        private struct Reach { public int tick; public float reach; }
        private static readonly Dictionary<Pawn, Reach> reaches = new Dictionary<Pawn, Reach>();
        private static readonly Dictionary<Def, AbilityAIExtension> fireRules = new Dictionary<Def, AbilityAIExtension>();
        private static readonly List<AIAbility> abilities = new List<AIAbility>();
        private static int lastPrune = -PruneTicks;
        private static readonly int NearFireCells = GenRadial.NumCellsInRadius(NearFire);

        public static float Danger(AbilityAIContext ctx, Pawn p)
        {
            if (p.IsBurning()) return 3f;
            if (FireNear(ctx.map, p.Position)) return 2f;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                float r = ReachOf(enemies[i]);
                if (r <= 0f) continue;
                r += ThreatMargin;
                if ((enemies[i].Position - p.Position).LengthHorizontalSquared <= r * r) return 1f;
            }
            return 0f;
        }

        private static bool FireNear(Map map, IntVec3 c)
        {
            if (map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count == 0) return false;
            for (int i = 0; i < NearFireCells; i++)
            {
                IntVec3 cell = c + GenRadial.RadialPattern[i];
                if (cell.InBounds(map) && cell.ContainsStaticFire(map)) return true;
            }
            return false;
        }

        // How far from where it stands the pawn can start a fire, 0 when it can't
        private static float ReachOf(Pawn e)
        {
            int now = Find.TickManager.TicksGame;
            if (now < lastPrune || now - lastPrune >= PruneTicks)
            {
                // Pawns that left the map or died are dropped here rather than tracked; an earlier tick means another
                // game was loaded, whose pawns must not be kept
                reaches.Clear();
                lastPrune = now;
            }
            if (reaches.TryGetValue(e, out Reach cached) && now - cached.tick < ReachTicks) return cached.reach;

            float reach = 0f;
            var verbs = e.equipment?.PrimaryEq?.AllVerbs;
            if (verbs != null)
                for (int i = 0; i < verbs.Count; i++)
                    reach = Mathf.Max(reach, VerbReach(verbs[i].verbProps));
            if (AIAbilitySources.AnyWithExtension(e))
            {
                abilities.Clear();
                AIAbilitySources.Collect(e, abilities);
                for (int i = 0; i < abilities.Count; i++)
                {
                    var rule = FireRule(abilities[i].Def);
                    if (rule == null) continue;
                    reach = Mathf.Max(reach, Mathf.Max(abilities[i].Range, 0f)
                        + Mathf.Max(AbilityAIUtility.Radius(abilities[i], rule), 0f));
                }
                abilities.Clear();
            }
            reaches[e] = new Reach { tick = now, reach = reach };
            return reach;
        }

        private static float VerbReach(VerbProperties v)
        {
            if (v == null || v.IsMeleeAttack) return 0f;
            var proj = v.defaultProjectile?.projectile;
            bool fire = v.beamDamageDef == DamageDefOf.Flame
                || (proj != null && (proj.ai_IsIncendiary || proj.damageDef == DamageDefOf.Flame));
            if (!fire) return 0f;
            return v.range + Mathf.Max(proj?.explosionRadius ?? 0f, 0f);
        }

        // The rule that says the ability starts fires (its radius is the fire's spread), or null. Cached per def.
        private static AbilityAIExtension FireRule(Def def)
        {
            if (fireRules.TryGetValue(def, out var rule)) return rule;
            AbilityAIExtension first = null;
            var exts = def.modExtensions;
            if (exts != null)
                for (int i = 0; i < exts.Count; i++)
                {
                    if (!(exts[i] is AbilityAIExtension ext)) continue;
                    if (first == null) first = ext;
                    if (ext.startsFires) { rule = ext; break; }
                }
            if (rule == null && first != null && def is AbilityDef ad && ad.comps != null)
                for (int i = 0; i < ad.comps.Count; i++)
                    if (ad.comps[i] is CompProperties_AbilityFireSpew || ad.comps[i] is CompProperties_AbilityFireBurst)
                    {
                        rule = first;
                        break;
                    }
            fireRules[def] = rule;
            return rule;
        }
    }
}
