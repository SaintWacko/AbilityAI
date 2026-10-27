using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AbilityAI
{
    /// <summary>CAI 5000 (Krkr.rule56 and its forks), by reflection: no reference to its assembly, nothing patched.
    ///
    /// CAI's comp reacts to threats every scan by starting a job of its own with InterruptForced (take cover, duck,
    /// retreat, reposition). It holds off while the pawn's gun is about to fire, but a psycast's warmup isn't on the
    /// gun, so a raider's cast was cut off and started again at the next check: run 27 showed 31% of NPC casts
    /// repeating the same ability on the same target, against 8% for the player's pawns CAI leaves alone. CAI also
    /// holds off while its own "interrupted recently" stamp is fresh, and sets it in the future itself to pause a
    /// pawn, so a cast here pushes that stamp to the end of the warmup.
    ///
    /// Its cover jobs also make a raider look idle: crouched behind a wall on a combat wait it neither targets nor is
    /// hurt, which InCombat read as out of a fight. IsCoverJob names those jobs.</summary>
    [StaticConstructorOnStartup]
    internal static class AbilityAICombatAI
    {
        private static readonly Type compType;
        private static readonly FieldInfo dataField;
        private static readonly PropertyInfo lastInterrupted;
        private static readonly JobDef gotoCover, gotoDuck, gotoRetreat;

        // CAI's reaction holds while now - LastInterrupted <= 45 x body size, its search-and-destroy while <= 30 x
        // body size: the stamp is set so the shorter one still covers the cast
        private const int ShortestHold = 30;
        // The ability's own cast after the warmup, and the scan that happens to land on that tick
        private const int MarginTicks = 30;
        // The stamp stays put if the cast is cut short (target dead, caster staggered), leaving the raider without
        // CAI's cover or retreat until it runs out: no longer for a walk than CAI's own longest pause
        private const int MaxWalkTicks = 240;

        static AbilityAICombatAI()
        {
            gotoCover = DefDatabase<JobDef>.GetNamedSilentFail("CombatAI_Goto_Cover");
            gotoDuck = DefDatabase<JobDef>.GetNamedSilentFail("CombatAI_Goto_Duck");
            gotoRetreat = DefDatabase<JobDef>.GetNamedSilentFail("CombatAI_Goto_Retreat");
            Type type = GenTypes.GetTypeInAnyAssembly("CombatAI.Comps.ThingComp_CombatAI", "CombatAI.Comps");
            if (type == null) return;
            var field = AccessTools.Field(type, "data");
            var prop = field == null ? null : AccessTools.Property(field.FieldType, "LastInterrupted");
            if (prop?.GetGetMethod() == null || prop.GetSetMethod() == null || prop.PropertyType != typeof(int))
            {
                Log.Warning("[Ability AI] CAI 5000 is loaded but its interrupt stamp wasn't found; CAI may cut off NPC casts.");
                return;
            }
            dataField = field;
            lastInterrupted = prop;
            compType = type;
        }

        /// <summary>CAI's cover, duck and retreat walks, which it starts for pawns under fire.</summary>
        internal static bool IsCoverJob(JobDef def) => def != null && (def == gotoCover || def == gotoDuck || def == gotoRetreat);

        /// <summary>Keeps CAI's reactions off this NPC until the cast it is starting has gone off. Never shortens
        /// a hold CAI set itself. Once per cast, so the reflection costs nothing that matters.</summary>
        internal static void HoldForCast(Pawn pawn, AIAbility ability, LocalTargetInfo target)
        {
            if (compType == null) return;
            ThingComp comp = null;
            var comps = pawn.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (compType.IsInstanceOfType(comps[i])) { comp = comps[i]; break; }
            }
            if (comp == null) return;
            object data = dataField.GetValue(comp);
            if (data == null) return;

            int ticks = Mathf.CeilToInt(ability.WarmupSeconds * GenTicks.TicksPerRealSecond) + MarginTicks;
            // A walk-up ability walks to its target before the warmup starts
            if (ability.IsTouch && target.IsValid)
                ticks += Mathf.Min(MaxWalkTicks, Mathf.CeilToInt(pawn.Position.DistanceTo(target.Cell) * pawn.TicksPerMoveCardinal));
            int stamp = GenTicks.TicksGame + ticks - Mathf.CeilToInt(ShortestHold * pawn.RaceProps.baseBodySize);
            if ((int)lastInterrupted.GetValue(data) < stamp) lastInterrupted.SetValue(data, stamp);
        }
    }
}
