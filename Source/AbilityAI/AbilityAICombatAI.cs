using System;
using System.Collections.Generic;
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
    /// hurt, which InCombat read as out of a fight. IsCoverJob names those jobs.
    ///
    /// The stamp is put back when the cast job ends. CAI tells a player's order on a drafted colonist it auto-controls
    /// from its own jobs by comparing the job's start tick with that stamp, so a stamp left in the future would make
    /// the player's next order look like CAI's, and CAI would override it.</summary>
    [StaticConstructorOnStartup]
    internal static class AbilityAICombatAI
    {
        private static readonly Type compType;
        private static readonly FieldInfo dataField;
        private static readonly PropertyInfo lastInterrupted;
        private static readonly MethodInfo autoControlled;
        private static readonly JobDef gotoCover, gotoDuck, gotoRetreat;

        // CAI's reaction holds while now - LastInterrupted <= 45 x body size (its 30 x check is search-and-destroy,
        // which only drafted colonists on auto-control do)
        private const int ReactionHold = 45;
        // The ability's own cast after the warmup, and the scan that happens to land on that tick
        private const int MarginTicks = 30;
        // CheckHold puts the stamp back when the job ends, but a pawn whose constant tree isn't checked (despawned
        // into a transporter, say) keeps it: no longer for a walk than CAI's own longest pause
        private const int MaxWalkTicks = 240;
        // The same for a channel (below), long enough for a Frost Ray at high psychic sensitivity
        private const int MaxChannelTicks = 600;

        private struct Hold
        {
            public object data;
            public int jobId, previous, stamp;
            public bool afterCast;
            public Stance channel;
        }

        // Only while a cast is running: empty almost all the time
        private static readonly Dictionary<Pawn, Hold> holds = new Dictionary<Pawn, Hold>();
        private static readonly List<Pawn> tmpGone = new List<Pawn>();
        // Leaving a game doesn't despawn its pawns, so an entry from the last game would otherwise never go
        private static Game holdsGame;

        internal static bool Holding => holds.Count > 0;

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
            // Missing in a fork: the player's pawns are then never held, as before auto-control existed
            autoControlled = AccessTools.PropertyGetter(type, "IsAIAutoControlled");
            if (autoControlled?.ReturnType != typeof(bool)) autoControlled = null;
            compType = type;
        }

        /// <summary>CAI's cover, duck and retreat walks, which it starts for pawns under fire.</summary>
        internal static bool IsCoverJob(JobDef def) => def != null && (def == gotoCover || def == gotoDuck || def == gotoRetreat);

        /// <summary>Keeps CAI's reactions off this pawn until the cast job it is starting has gone off: an NPC, or a
        /// drafted colonist CAI auto-controls (CAI leaves other colonists alone). Never shortens a hold CAI set
        /// itself. Once per cast, so the reflection costs nothing that matters.</summary>
        internal static void HoldForCast(Pawn pawn, AIAbility ability, LocalTargetInfo target, Job cast)
        {
            if (compType == null) return;
            ThingComp comp = null;
            var comps = pawn.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (compType.IsInstanceOfType(comps[i])) { comp = comps[i]; break; }
            }
            if (comp == null) return;
            if (pawn.Faction != null && pawn.Faction.IsPlayer && (autoControlled == null || !(bool)autoControlled.Invoke(comp, null))) return;
            object data = dataField.GetValue(comp);
            if (data == null) return;

            int ticks = Mathf.CeilToInt(ability.WarmupSeconds * GenTicks.TicksPerRealSecond) + MarginTicks;
            // A walk-up ability walks to its target before the warmup starts
            if (ability.IsTouch && target.IsValid)
                ticks += Mathf.Min(MaxWalkTicks, Mathf.CeilToInt(pawn.Position.DistanceTo(target.Cell) * pawn.TicksPerMoveCardinal));
            int stamp = GenTicks.TicksGame + ticks - (int)(ReactionHold * pawn.RaceProps.baseBodySize);
            SameGame();
            int current = (int)lastInterrupted.GetValue(data);
            // A cast started while our hold for the last one is still in place: what goes back is still the value
            // from before the first
            bool held = holds.TryGetValue(pawn, out var old) && current == old.stamp;
            int previous = held ? old.previous : current;
            if (current < stamp) lastInterrupted.SetValue(data, stamp);
            else if (held) stamp = current;
            else return;

            // A pawn that died or left mid-cast never comes back to CheckHold
            foreach (var p in holds.Keys)
                if (p.Destroyed || !p.Spawned) tmpGone.Add(p);
            for (int i = 0; i < tmpGone.Count; i++) holds.Remove(tmpGone[i]);
            tmpGone.Clear();
            holds[pawn] = new Hold { data = data, jobId = cast.loadID, previous = previous, stamp = stamp };
        }

        /// <summary>Puts CAI's stamp back once the cast job is no longer running (cast, cut short, or replaced by
        /// a player's order). Called at every constant-tree check, so at most 30 ticks late, and at once when the
        /// job ends on its own. A stamp CAI has moved since is CAI's and stays.</summary>
        internal static void CheckHold(Pawn pawn)
        {
            SameGame();
            if (!holds.TryGetValue(pawn, out var hold)) return;
            Job cur = pawn.CurJob;
            if (cur != null && cur.loadID == hold.jobId) return;
            // A channel: an ability that keeps its caster standing for its effect once the cast job is over (VPE's
            // Frost Ray, whose ray stays where it was cast from for 4 s). CAI's cover job cancels busy stances, so the
            // caster walked off and left the ray behind. Taken only on the first check after the job, so a gun's
            // later warmup or cooldown is never mistaken for one
            if (!hold.afterCast)
            {
                hold.afterCast = true;
                if (pawn.stances?.curStance is Stance_Busy busy && !(busy is Stance_Warmup) && !(busy is Stance_Cooldown)
                    && busy.ticksLeft > 0 && (int)lastInterrupted.GetValue(hold.data) == hold.stamp)
                {
                    int stamp = GenTicks.TicksGame + Mathf.Min(busy.ticksLeft, MaxChannelTicks) + MarginTicks
                        - (int)(ReactionHold * pawn.RaceProps.baseBodySize);
                    if (stamp > hold.stamp)
                    {
                        lastInterrupted.SetValue(hold.data, stamp);
                        hold.stamp = stamp;
                    }
                    hold.channel = busy;
                    holds[pawn] = hold;
                    return;
                }
            }
            else if (hold.channel != null && pawn.stances?.curStance == hold.channel) return;
            holds.Remove(pawn);
            if ((int)lastInterrupted.GetValue(hold.data) == hold.stamp) lastInterrupted.SetValue(hold.data, hold.previous);
        }

        private static void SameGame()
        {
            if (holdsGame == Current.Game) return;
            holds.Clear();
            holdsGame = Current.Game;
        }
    }
}
