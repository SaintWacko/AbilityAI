using System;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using VEF.Abilities;
using Verse;
using Verse.AI;
using VefAbility = VEF.Abilities.Ability;

namespace AbilityAI.Vef
{
    /// <summary>
    /// Dev log timer for VEF casts. Dubs Performance Analyzer follows JobDriver_CastAbilityOnce down to the toil's
    /// Action.Invoke and no further, so it shows that casts are slow but not which ability. Every toil starts in
    /// JobDriver.TryActuallyStartNextToil (from ReadyForNextToil and from DriverTick): the warmup toil runs the
    /// ability's PreWarmupAction there, and the cast toil runs Ability.Cast. The cast toil completes instantly, so the
    /// same call goes on to end the job and pick the caster's next one through the think tree (Ability AI's postfix,
    /// CAI's cover search and the rest) in a nested call on the same driver; that part is measured and reported
    /// apart from the cast.
    ///
    /// Patched only once the dev log has been switched on this session: the target runs for every job driver in
    /// the game, and a game that never turns the log on should not carry a patch there.
    /// </summary>
    internal static class VefCastTimer
    {
        private const double SlowMs = 5.0;
        private static readonly double msPerTimestamp = 1000.0 / Stopwatch.Frequency;
        private static bool installed;

        // Set by the nested call that ended a cast job, read by the enclosing call on the same driver
        private static JobDriver endedDriver;
        private static double endedMs;
        private static string endedNextJob;

        internal struct Timing
        {
            public long start;
            public int toil;
            public bool lastToil;
            public VefAbility ability;
            public int targets;
            public string firstTarget;
        }

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                new Harmony("saintwacko.abilityai.vef.casttimer").Patch(
                    AccessTools.Method(typeof(JobDriver), "TryActuallyStartNextToil"),
                    prefix: new HarmonyMethod(typeof(VefCastTimer), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(VefCastTimer), nameof(Postfix)));
            }
            catch (Exception e) { Log.Warning($"[Ability AI] The cast timer could not be set up; slow casts are not logged: {e.Message}"); }
        }

        private static void Prefix(JobDriver __instance, out Timing __state)
        {
            __state = default;
            if (!(__instance is JobDriver_CastAbilityOnce driver) || !Prefs.DevMode || !AbilityAISettings.DebugLog) return;
            var comp = driver.CompAbilities;
            var ability = comp?.currentlyCasting;
            if (ability == null) return;
            var targets = comp.currentlyCastingTargets;
            __state.ability = ability;
            __state.toil = driver.CurToilIndex;
            __state.lastToil = driver.OnLastToil;
            __state.targets = targets?.Length ?? 0;
            if (__state.targets > 0)
                __state.firstTarget = targets[0].HasThing ? targets[0].Thing.LabelShort : targets[0].Cell.ToString();
            __state.start = Stopwatch.GetTimestamp();
        }

        private static void Postfix(JobDriver __instance, Timing __state)
        {
            if (__state.ability == null) return;
            double ms = (Stopwatch.GetTimestamp() - __state.start) * msPerTimestamp;
            if (__state.lastToil)
            {
                endedDriver = __instance;
                endedMs = ms;
                endedNextJob = __instance.pawn?.CurJobDef?.defName ?? "none";
                return;
            }
            double endPart = 0;
            string nextJob = null;
            if (endedDriver == __instance)
            {
                endPart = endedMs;
                nextJob = endedNextJob;
                endedDriver = null;
            }
            if (ms < SlowMs) return;

            Pawn pawn = __instance.pawn;
            string who = pawn.Faction == Faction.OfPlayer ? "player"
                : pawn.HostileTo(Faction.OfPlayer) ? "hostile NPC" : "friendly NPC";
            string phase = __instance.GetType() == typeof(JobDriver_CastAbilityOnce)
                ? (__state.toil < 0 ? "warmup start" : "cast")
                : $"toil {__state.toil + 1} of {__instance.GetType().Name}";
            string targets = __state.targets == 1 ? $"at {__state.firstTarget}"
                : $"{__state.targets} targets (first {__state.firstTarget ?? "none"})";
            string text = $"[Ability AI] slow cast: {__state.ability.def.label} by {pawn.LabelShort} ({who}), {targets}: "
                + $"{phase} {ms - endPart:F1} ms";
            if (nextJob != null)
                text += $", then ending the job and choosing the next {endPart:F1} ms (next job {nextJob})";
            Log.Message(text);
        }
    }
}
