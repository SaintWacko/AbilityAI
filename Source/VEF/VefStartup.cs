using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
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
            var harmony = new Harmony("saintwacko.abilityai.vef");
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(VefStartup).Assembly))
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.Error($"[Ability AI] Patch {type.FullName} failed; that one feature is off, the rest still works: {e}"); }
            }
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
            if (AbilityAISettings.VanillaStandDown && AbilityAIDriver.Handles(__instance.pawn, VefAIAbility.Of(__instance))) __result = false;
        }
    }
}
