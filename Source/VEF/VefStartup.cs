using System;
using HarmonyLib;
using Verse;
using VefAbility = VEF.Abilities.Ability;

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
    /// Handles is false for the player's pawns until the autocast menu covers VEF abilities: their VEF toggle keeps
    /// working as before. No other VEF, VPE, Better Autocasting or Psycasts² class overrides AutoCast, so patching the
    /// base getter covers every ability type.
    ///
    /// Cost: one bool read on every call that VEF answered false; the rest is one dictionary lookup, then the
    /// wrapper lookup and the settings checks Handles makes.
    /// </summary>
    [HarmonyPatch(typeof(VEF.Abilities.Ability), nameof(VEF.Abilities.Ability.AutoCast), MethodType.Getter)]
    internal static class Patch_VefAbility_AutoCast_StandDown
    {
        static void Postfix(VefAbility __instance, ref bool __result)
        {
            if (!__result || !AbilityAISettings.VanillaStandDown || !VefAIAbility.Supported(__instance.def)) return;
            if (AbilityAIDriver.Handles(__instance.pawn, VefAIAbility.Of(__instance))) __result = false;
        }
    }
}
