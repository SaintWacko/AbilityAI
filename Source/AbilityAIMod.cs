using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// The VEF compat assembly hooks VEF's gizmos into the same autocast menu, badge and marker, which stay internal here
[assembly: InternalsVisibleTo("AbilityAI.VEF")]
// The VPE compat's Better Autocasting bridge reads the owner setting and carries modes over when it changes
[assembly: InternalsVisibleTo("AbilityAI.VPE")]

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
        private AbilityAISettings.Tab tab;
        private List<TabRecord> tabs;
        private LoadedLanguage tabsLanguage;

        public AbilityAIMod(ModContentPack content) : base(content)
        {
            // Settings only: no defs are loaded yet, and patching waits for AbilityAIStartup
            GetSettings<AbilityAIModSettings>();
        }

        public override string SettingsCategory() => "Ability AI";

        private TabRecord MakeTab(AbilityAISettings.Tab t, string key) =>
            new TabRecord(key.Translate(), () => { tab = t; scroll = Vector2.zero; }, () => tab == t);

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // The labels are translated when the list is made: made again after a language change
            if (tabs == null || tabsLanguage != LanguageDatabase.activeLanguage)
            {
                tabsLanguage = LanguageDatabase.activeLanguage;
                tabs = new List<TabRecord>
                {
                    MakeTab(AbilityAISettings.Tab.General, "AAI_Tab_General"),
                    MakeTab(AbilityAISettings.Tab.OtherFactions, "AAI_Tab_OtherFactions"),
                    MakeTab(AbilityAISettings.Tab.YourPawns, "AAI_Tab_YourPawns"),
                    MakeTab(AbilityAISettings.Tab.MentalBreaks, "AAI_Tab_MentalBreaks"),
                };
            }
            // TabDrawer draws the tabs above the rect it is given; the bottom 40 px are kept for the reset buttons
            var menu = new Rect(inRect.x, inRect.y + TabDrawer.TabHeight, inRect.width, inRect.height - TabDrawer.TabHeight - 40f);
            Widgets.DrawMenuSection(menu);
            TabDrawer.DrawTabs(menu, tabs);

            var outer = menu.ContractedBy(10f);
            var view = new Rect(0f, 0f, outer.width - 16f, viewHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            // maxOneColumn: without it an overrun wraps into a second column and CurHeight stops growing
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(view);
            AbilityAISettings.DoTab(list, tab);
            viewHeight = list.CurHeight + 20f;
            list.End();
            Widgets.EndScrollView();

            var resetTab = new Rect(inRect.x, inRect.yMax - 32f, 200f, 32f);
            if (Widgets.ButtonText(resetTab, "AAI_ResetTab".Translate())) AbilityAISettings.ResetTab(tab);
            var resetAll = new Rect(inRect.xMax - 200f, inRect.yMax - 32f, 200f, 32f);
            if (Widgets.ButtonText(resetAll, "AAI_Reset".Translate()))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("AAI_Reset_Confirm".Translate(),
                    AbilityAISettings.Reset, destructive: true));
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
            // After the patches, so a def problem here can't keep them from being applied
            try
            {
                var abilityDefs = DefDatabase<AbilityDef>.AllDefsListForReading;
                for (int i = 0; i < abilityDefs.Count; i++)
                {
                    AbilityAIRecast.Note(abilityDefs[i]);
                    AbilityAIGather.Note(abilityDefs[i]);
                }
            }
            catch (Exception e) { Log.Error($"[Ability AI] Setting up recasting failed; nothing is recast: {e}"); }
            try
            {
                var abilityDefs = DefDatabase<AbilityDef>.AllDefsListForReading;
                for (int i = 0; i < abilityDefs.Count; i++) AbilityAINpcGates.Note(abilityDefs[i]);
            }
            catch (Exception e) { Log.Error($"[Ability AI] Setting up the NPC gates failed; their settings may not show: {e}"); }
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
    /// its player autocast are on, except for an ability its hook vetoes for that pawn: a veto hands it back. A player pawn in a mental break, and NPCs, stand vanilla down only where this mod
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

    /// <summary>
    /// Vanilla also casts abilities from think-tree branches that never ask AICanTargetNow: a raider's Longjump
    /// escape, an enemy Tunneler's smokepop, an Apocriton's jump away after a resurrection. Each branch sits under a
    /// ThinkNode_ConditionalHasAbility for its ability, so for a pawn not of the player's faction whose ability this
    /// mod handles (Handles), the node answers no and the branch is skipped. Only the abilities whose vanilla
    /// branches this mod's rules replace: other mods key their own branches on abilities too (Better Traders Guild's
    /// NPC paramedic jumps to a pawn it is rescuing with LongjumpMechLauncher, which this mod has no rescue rule for).
    /// The player's pawns keep vanilla's branches for the same reason.
    /// Cost: one reference compare on each such node vanilla answered yes, then the extension lookup.
    /// </summary>
    [HarmonyPatch(typeof(ThinkNode_ConditionalHasAbility), "Satisfied")]
    internal static class Patch_ConditionalHasAbility_StandDown
    {
        private static readonly string[] ReplacedNames = { "Longjump", "LongjumpMech", "SmokepopMech" };
        private static AbilityDef[] replaced;

        private static bool Replaced(AbilityDef def)
        {
            if (replaced == null)
            {
                var found = new List<AbilityDef>();
                foreach (string name in ReplacedNames)
                    if (DefDatabase<AbilityDef>.GetNamedSilentFail(name) is AbilityDef d) found.Add(d);
                replaced = found.ToArray();
            }
            for (int i = 0; i < replaced.Length; i++)
                if (replaced[i] == def) return true;
            return false;
        }

        static void Postfix(ThinkNode_ConditionalHasAbility __instance, Pawn pawn, ref bool __result)
        {
            if (!__result || !AbilityAISettings.VanillaStandDown || !Replaced(__instance.ability)
                || (pawn.Faction != null && pawn.Faction.IsPlayer)) return;
            Ability ability = pawn.abilities?.GetAbility(__instance.ability);
            if (ability != null && AbilityAIDriver.Handles(pawn, ability)) __result = false;
        }
    }
}
