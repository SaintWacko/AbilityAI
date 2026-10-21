using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AbilityAI
{
    /// <summary>Saves AbilityAISettings. The values live in static fields so the hot paths never go through an
    /// instance; this only carries them to and from disk.</summary>
    public class AbilityAIModSettings : ModSettings
    {
        public override void ExposeData()
        {
            base.ExposeData();
            AbilityAISettings.ExposeData();
        }
    }

    public class AbilityAIMod : Mod
    {
        private Vector2 scroll;
        private float viewHeight = 600f;

        public AbilityAIMod(ModContentPack content) : base(content)
        {
            // Settings only: no defs are loaded yet, and patching waits for AbilityAIStartup
            GetSettings<AbilityAIModSettings>();
        }

        public override string SettingsCategory() => "Ability AI";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // The bottom 40 px are kept for the reset button
            var outer = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 40f);
            var view = new Rect(0f, 0f, outer.width - 16f, viewHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            // maxOneColumn: without it an overrun wraps into a second column and CurHeight stops growing
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(view);
            AbilityAISettings.DoSettings(list);
            viewHeight = list.CurHeight + 20f;
            list.End();
            Widgets.EndScrollView();

            var reset = new Rect(inRect.x, inRect.yMax - 32f, 200f, 32f);
            if (Widgets.ButtonText(reset, "AAI_Reset".Translate())) AbilityAISettings.Reset();
        }
    }

    /// <summary>Applies the Harmony patches once defs are loaded, one class at a time, so a patch that fails to
    /// bind turns off only its own feature.</summary>
    [StaticConstructorOnStartup]
    internal static class AbilityAIStartup
    {
        static AbilityAIStartup()
        {
            var harmony = new Harmony("saintwacko.abilityai");
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(AbilityAIStartup).Assembly))
            {
                // Only classes carrying [HarmonyPatch] themselves: the class processor would otherwise treat any
                // static Prepare method as a lifecycle hook
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Log.Error($"[Ability AI] Patch {type.FullName} failed; that one feature is off, the rest still works: {e}"); }
            }
        }
    }

    /// <summary>
    /// Vanilla's fight AI (JobGiver_AIFightEnemy.GetAbilityJob, through Pawn_AbilityTracker.AICastableAbilities) and
    /// its non-combat cast job givers ask Ability.AICanTargetNow, which is true only for abilities with aiCanUse. A
    /// host that leaves aiCanUse on as its fallback for when this mod is missing would otherwise have both AIs cast
    /// the same ability. When this mod is the one deciding for this pawn and ability, vanilla is told no, and so it
    /// is for the player's own pawns with any ability this mod has rules for, even with autocast off: vanilla's fight
    /// AI does run for colony mechs and tame animals, and the player's choice in the autocast menu should be the only
    /// way those cast (AbilityAIDriver.OthersStandDown).
    ///
    /// That includes a player pawn the driver isn't casting for right now (mode Off, undrafted with a combat-only
    /// ability, in a lord), so a host can't count on its aiCanUse fallback for the player's pawns while this mod and
    /// its player autocast are on. A player pawn in a mental break, and NPCs, stand vanilla down only where this mod
    /// decides (Handles): an NPC whose kind is switched off in the settings, or an unclaimed ability with Unclaimed
    /// off, keeps vanilla's behaviour.
    ///
    /// Cost: one bool read on every call, and only calls vanilla answered true (abilities with aiCanUse that are
    /// ready) go on to the extension lookup and the hooks' vote. Our own driver never calls this method, so it
    /// doesn't stand itself down.
    /// </summary>
    [HarmonyPatch(typeof(Ability), nameof(Ability.AICanTargetNow))]
    internal static class Patch_Ability_AICanTargetNow_StandDown
    {
        static void Postfix(Ability __instance, ref bool __result)
        {
            if (!__result || !AbilityAISettings.VanillaStandDown) return;
            if (AbilityAIDriver.OthersStandDown(__instance.pawn, __instance)) __result = false;
        }
    }
}
