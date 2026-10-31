using System;
using HarmonyLib;
using RimWorld;
using VanillaPsycastsExpanded.Wildspeaker;
using Verse;

// VPE's plant spawners (Bramble Maze, the wild plant and tree psycasts) are vanilla GroundSpawners, the insect-tunnel
// spawner, so each one starts its own looping Tunnel sound. Bramble Maze makes one per cell of its radius 6, about
// 112 at once, and every new world sustainer makes the game re-sort all the live ones by camera distance. In a fight
// where several Wildspeakers cast it the casts took 70 to 750 ms each and grew with every cast (run 2 of build 0177).
// Tunnel plays one voice at a time anyway, so silencing the spawners loses only that rumble.
namespace AbilityAI.Vpe
{
    internal static class VpeQuietSpawners
    {
        internal static void Install()
        {
            try
            {
                var getter = AccessTools.PropertyGetter(typeof(GroundSpawner), "SustainerSound");
                if (getter == null)
                {
                    Log.Warning("[Ability AI] GroundSpawner.SustainerSound was not found; VPE's plant spawners keep their sound.");
                    return;
                }
                new Harmony("saintwacko.abilityai.vpe").Patch(getter, postfix: new HarmonyMethod(typeof(VpeQuietSpawners), nameof(Postfix)));
                AbilityAISettings.VpeSpawnerPatchApplied = true;
            }
            catch (Exception e) { Log.Warning($"[Ability AI] Could not quiet VPE's plant spawners: {e.Message}"); }
        }

        // Read once, in the spawner's SpawnSetup, so the setting takes effect for spawners made after it changes
        private static void Postfix(GroundSpawner __instance, ref SoundDef __result)
        {
            if (AbilityAISettings.VpeQuietSpawners && __instance is PlantSpawner) __result = null;
        }
    }
}
