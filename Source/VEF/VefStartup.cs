using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VEF.Abilities;
using Verse;
using VefAbility = VEF.Abilities.Ability;

// The VPE compat's Better Autocasting bridge asks VefAutocast whether we own an ability and hooks ConvertAll
[assembly: InternalsVisibleTo("AbilityAI.VPE")]

namespace AbilityAI.Vef
{
    /// <summary>Registers VEF's abilities with the AI and applies this assembly's patches, one class at a time, as the
    /// core assembly does.</summary>
    [StaticConstructorOnStartup]
    internal static class VefStartup
    {
        static VefStartup()
        {
            VefAIAbility.Init();
            AIAbilitySources.Register(VefAIAbility.Source);
            AbilityAIAutocast.WorldReady += VefAutocast.ConvertAll;
            if (AbilityAISettings.DebugLog) VefCastTimer.Install();
            AbilityAISettings.DebugLogSwitchedOn += VefCastTimer.Install;
            var harmony = new Harmony("saintwacko.abilityai.vef");
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(VefStartup).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.Error($"[Ability AI] Patch {type.FullName} failed; that one feature is off, the rest still works: {e}"); }
            }
            // After the patches, as in the core assembly
            try
            {
                var defs = DefDatabase<VEF.Abilities.AbilityDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    AbilityAIRecast.Note(defs[i]);
                    AbilityAIGather.Note(defs[i]);
                    AbilityAIZones.Note(defs[i]);
                    AbilityAISpots.Note(defs[i]);
                    AbilityAIHeat.Note(defs[i]);
                    AbilityAIBarrier.Note(defs[i]);
                    AbilityAIGroupLink.Note(defs[i]);
                }
                AbilityAIRecast.RegisterLifetime(SpawnedLifetime);
                AbilityAIZones.RegisterBusy(InRunningCast);
            }
            catch (Exception e) { Log.Error($"[Ability AI] Setting up recasting for VEF abilities failed; they are not recast: {e}"); }
            try
            {
                var defs = DefDatabase<VEF.Abilities.AbilityDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++) AbilityAINpcGates.Note(defs[i]);
            }
            catch (Exception e) { Log.Error($"[Ability AI] Setting up the NPC gates for VEF abilities failed; their settings may not show: {e}"); }
        }

        // A colonist's cast of this ability already holds the thing. VEF fixes the targets when the job is made and an
        // area cast's effect may not check them again (VPE's Consume Bodies destroys each), so a second cast over the
        // same thing would act on it twice. Covers what the zone claims can't: casts ordered by hand, and casts under
        // way when the game was loaded (the claims aren't saved, VEF's targets are).
        private static bool InRunningCast(Thing thing, Def def)
        {
            if (!(def is VEF.Abilities.AbilityDef)) return false;
            Map map = thing.MapHeld;
            if (map == null) return false;
            var pawns = map.mapPawns.SpawnedPawnsInFaction(RimWorld.Faction.OfPlayer);
            for (int i = 0; i < pawns.Count; i++)
            {
                if (!(pawns[i].jobs?.curDriver is JobDriver_CastAbilityOnce)) continue;
                var comp = pawns[i].GetComp<CompAbilities>();
                if (comp?.currentlyCasting?.def != def || comp.currentlyCastingTargets == null) continue;
                var targets = comp.currentlyCastingTargets;
                for (int j = 0; j < targets.Length; j++)
                    if (targets[j].Thing == thing) return true;
            }
            return false;
        }

        // When a thing VEF's spawn abilities left runs out, for recasting. Ability_SpawnBuilding sets finalTick after the
        // spawn (so this is read later, never from the spawn itself), and a building it gave a damage per tick (its
        // power) wears down first if its hit points run out sooner. Ability_Spawn counts down CompDuration instead.
        private static int SpawnedLifetime(Thing thing)
        {
            var building = thing.TryGetComp<CompSpawnedBuilding>();
            if (building != null)
            {
                int end = building.finalTick > 0 ? building.finalTick : -1;
                if (building.damagePerTick > 0)
                {
                    int worn = building.lastDamageTick + thing.HitPoints / building.damagePerTick;
                    end = end < 0 ? worn : Math.Min(end, worn);
                }
                return end;
            }
            var duration = thing.TryGetComp<CompDuration>();
            return duration != null ? Find.TickManager.TicksGame + duration.durationTicksLeft : -1;
        }
    }

    /// <summary>
    /// VEF casts NPCs' abilities through its postfix on Pawn.TryGetAttackVerb, which swaps in a random ability verb
    /// whose AutoCast is true (Better Autocasting for VPE replaces that postfix with its own, filtering on AutoCast the
    /// same way). Where Ability AI decides for this pawn and ability (Handles), AutoCast reads false, so neither picks
    /// it and the two AIs never both cast it. Neither patch of theirs is touched or reordered.
    ///
    /// For the player's pawns Handles is true once the player has set an autocast mode. A colonist's AutoCast is VEF's
    /// raw autoCast flag, so a true answer for one whose ability we own is a flag set before we took the ability over:
    /// it is converted to our mode here (VefAutocast.Convert) the first time VEF or the gizmo reads it. No other VEF,
    /// VPE, Better Autocasting or Psycasts² class overrides AutoCast, so patching the base getter covers every
    /// ability type.
    ///
    /// Cost: one bool read on every call that VEF answered false; the rest is one dictionary lookup, then the
    /// wrapper lookup and the settings checks Handles makes.
    /// </summary>
    [HarmonyPatch(typeof(VEF.Abilities.Ability), nameof(VEF.Abilities.Ability.AutoCast), MethodType.Getter)]
    internal static class Patch_VefAbility_AutoCast_StandDown
    {
        static void Postfix(VefAbility __instance, ref bool __result)
        {
            if (!__result || !VefAIAbility.Supported(__instance.def)) return;
            // Pawns tick before the world component, so on a loaded game's first tick a saved flag on an ability we
            // own is still unconverted (Convert waits for AbilityAIAutocast.Ready): it reads false without converting
            if (__instance.pawn != null && __instance.pawn.IsColonist && !AbilityAIAutocast.Ready && VefAutocast.Owns(__instance))
            {
                __result = false;
                return;
            }
            if (VefAutocast.Convert(__instance))
            {
                __result = false;
                return;
            }
            if (AbilityAIDriver.Handles(__instance.pawn, VefAIAbility.Of(__instance))) __result = false;
        }
    }
}
