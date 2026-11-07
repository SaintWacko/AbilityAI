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

        // The cast driver whose timed call is running, so a nested call that ends its job knows it is enclosed
        private static JobDriver activeDriver;
        // Set by the nested call that ended a cast job, read by the enclosing call on the same driver
        private static JobDriver endedDriver;
        private static double endedMs;
        private static string endedNextJob;

        internal struct Timing
        {
            public long start;
            public int toil;
            public bool lastToil;
            public bool enclosed;
            public JobDriver outerActive;
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
            if (__state.lastToil) __state.enclosed = activeDriver == __instance;
            else
            {
                // A cast can start another pawn's cast job inside this call, so the previous one is put back after
                __state.outerActive = activeDriver;
                activeDriver = __instance;
            }
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
                // A busy stance turns the call away before the job ends; DriverTick ends it later, in a call nothing encloses
                if (!__instance.ended) return;
                string next = __instance.pawn?.CurJobDef?.defName ?? "none";
                if (__state.enclosed)
                {
                    endedDriver = __instance;
                    endedMs = ms;
                    endedNextJob = next;
                }
                else if (ms >= SlowMs)
                    Log.Message($"{Head(__instance, __state)}ending the job and choosing the next {ms:F1} ms (next job {next})");
                return;
            }
            activeDriver = __state.outerActive;
            double endPart = 0;
            string nextJob = null;
            if (endedDriver == __instance)
            {
                endPart = endedMs;
                nextJob = endedNextJob;
            }
            endedDriver = null;
            if (ms < SlowMs) return;

            string phase = __instance.GetType() == typeof(JobDriver_CastAbilityOnce)
                ? (__state.toil < 0 ? "warmup start" : "cast")
                : $"toil {__state.toil + 2} of {__instance.GetType().Name}";
            string text = $"{Head(__instance, __state)}{phase} {ms - endPart:F1} ms";
            if (nextJob != null)
                text += $", then ending the job and choosing the next {endPart:F1} ms (next job {nextJob})";
            Log.Message(text);
        }

        private static string Head(JobDriver driver, Timing state)
        {
            Pawn pawn = driver.pawn;
            string who = pawn.Faction == Faction.OfPlayer ? "player"
                : pawn.HostileTo(Faction.OfPlayer) ? "hostile NPC" : "friendly NPC";
            string targets = state.targets == 1 ? $"at {state.firstTarget}"
                : $"{state.targets} targets (first {state.firstTarget ?? "none"})";
            return $"[Ability AI] slow cast: {state.ability.def.label} by {pawn.LabelShort} ({who}), {targets}: ";
        }
    }
}
