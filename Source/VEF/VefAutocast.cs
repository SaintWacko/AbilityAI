using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using VEF.Abilities;
using VefAbility = VEF.Abilities.Ability;
using VefCommand = VEF.Abilities.Command_Ability;

// Player autocast for VEF's abilities: the same right-click menu, badge, marker and tooltip line the vanilla gizmos
// get, on VEF's Command_Ability (VPE's psycast command is a subclass and draws through it).
//
// For every VEF ability Ability AI owns for the player (AbilityAIAutocast.Offers: the player's own pawn, an ability
// the driver can cast, the settings on), VEF's own autocast toggle is hidden by making CanAutoCast read false, so one
// ability never has two toggles. That one getter also drops VEF's check icon and its "right-click to autocast" line.
// A VEF autoCast flag the player had set before is turned into our in-place mode the first time it is seen, then
// cleared, which is also what keeps Better Autocasting's loop (it reads the raw flag) off the ability.
namespace AbilityAI.Vef
{
    internal static class VefAutocast
    {
        /// <summary>Ability AI decides this ability's autocast for the player. Called from CanAutoCast, which VEF reads
        /// for every NPC ability on every attack-verb choice: an NPC leaves at the faction check.</summary>
        internal static bool Owns(VefAbility ability)
        {
            Pawn p = ability.pawn;
            if (p?.Faction == null || !p.Faction.IsPlayer) return false;
            // A break takes the pawn out of the player's hands; VEF's casting during one is left as it was (or to
            // our NPC rules under BreakCasting, through the AutoCast stand-down)
            if (p.InMentalState || !VefAIAbility.Supported(ability.def)) return false;
            return AbilityAIAutocast.Offers(VefAIAbility.Of(ability));
        }

        /// <summary>VEF's own flag on an ability we own becomes our in-place mode (unless a mode is already set), and
        /// the flag is cleared. Colonists only: VEF reads the flag only for them, every other player pawn autocasts by
        /// CanAutoCast.</summary>
        internal static bool Convert(VefAbility ability)
        {
            if (!ability.autoCast || ability.pawn == null || !ability.pawn.IsColonist) return false;
            // Before the first tick ConvertAll hasn't run, and the Better Autocasting bridge's load-time carry (run
            // from it) has to see the flags as saved; a gizmo drawn on load would otherwise clear them first
            if (!AbilityAIAutocast.Ready || !Owns(ability)) return false;
            if (AbilityAIAutocast.ModeFor(ability.pawn, ability.def) == AbilityAIAutocastMode.Off)
                AbilityAIAutocast.SetMode(ability.pawn, ability.def, AbilityAIAutocastMode.Stationary);
            ability.autoCast = false;
            return true;
        }

        /// <summary>Run at the start of ConvertAll, before any flag is converted: the Better Autocasting bridge carries a
        /// switch of owner made at the main menu over here, and it has to see the flags as they were saved.</summary>
        internal static Action BeforeConvertAll;

        // Once per game, so a colonist whose flag was set before this mod (or this ability's rules) arrived starts
        // autocasting without the player having to look at its gizmo first
        internal static void ConvertAll()
        {
            BeforeConvertAll?.Invoke();
            var pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction;
            for (int i = 0; i < pawns.Count; i++)
            {
                var list = pawns[i].GetComp<CompAbilities>()?.LearnedAbilities;
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++) Convert(list[j]);
            }
        }

        // VEF's commands group by def (Command_Ability.GroupsWith), so the clicked button stands for every selected
        // pawn that has the ability and shows its gizmo
        internal static void OpenMenu(VefCommand command, VefAbility clicked)
        {
            var def = clicked.def;
            var pawns = new List<Pawn> { clicked.pawn };
            foreach (object o in Find.Selector.SelectedObjectsListForReading)
            {
                if (!(o is Pawn p) || p == clicked.pawn) continue;
                var other = VefAIAbility.FindOn(p, def);
                if (other != null && other.ShowGizmoOnPawn() && Owns(other)) pawns.Add(p);
            }
            AbilityAIAutocast.OpenMenu(clicked.pawn, def, VefAIAbility.Of(clicked).IsTouch, pawns, command.LabelCap);
        }

        // Shrunk, Psycasts² draws its charge counter in the bottom-left corner where the vanilla gizmos have the
        // badge, so on VEF's gizmos it goes bottom right (VPE's cost label is top right, VEF's check icon too)
        internal static void Badge(VefAbility ability, Rect button, bool shrunk)
        {
            Pawn p = ability?.pawn;
            if (p == null || !AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast || !VefAIAbility.Supported(ability.def)) return;
            if (VefAIAbility.Of(ability).PlayerAutocastElsewhere) return;
            var mode = AbilityAIAutocast.HasAny(p) ? AbilityAIAutocast.ModeFor(p, ability.def) : AbilityAIAutocastMode.Off;
            if (mode == AbilityAIAutocastMode.Off)
            {
                if (AbilityAISettings.ShowMarker && AutocastMarker.Offers(VefAIAbility.Of(ability))) AutocastMarker.Draw(button, shrunk, true);
                return;
            }
            // Modes are kept per def, so a mode set on one item's ability would otherwise badge another item's copy
            // of the def that a host vetoes (Entad: a trait not identified yet, whose badge would give it away)
            if (!AbilityAIHooks.NotVetoed(p, VefAIAbility.Of(ability))) return;
            Patch_CommandAbility_Autocast.DrawBadge(button, mode, shrunk, true);
        }
    }

    /// <summary>VEF's toggle off where we own the ability. After Better Autocasting's postfix, which forces true for
    /// the abilities it handles: while ours owns them, ours is the only toggle. On the base getter and on every
    /// override, since an override doesn't call the base: VEF's Ability_Spawn and Ability_SpawnBuilding read false,
    /// and Better Autocasting postfixes both to force true as well.</summary>
    [HarmonyPatch]
    [HarmonyAfter("dev.tobot.vpe-better-autocast")]
    internal static class Patch_VefAbility_CanAutoCast_Owned
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(VefAbility), nameof(VefAbility.CanAutoCast));
            foreach (Type t in typeof(VefAbility).AllSubclasses())
            {
                // A getter on an open generic type can't be patched, and the throw would take the base getter's
                // patch down with it
                if (t.ContainsGenericParameters) continue;
                MethodInfo getter = AccessTools.DeclaredPropertyGetter(t, nameof(VefAbility.CanAutoCast));
                if (getter != null && !getter.IsAbstract) yield return getter;
            }
        }

        static void Postfix(VefAbility __instance, ref bool __result)
        {
            if (__result && VefAutocast.Owns(__instance)) __result = false;
        }
    }

    /// <summary>The right-click menu and the badge on VEF's ability gizmos, full size and shrunk (both draw through
    /// GizmoOnGUIInt). The right-click is caught before the button sees it: VEF disables the gizmo while the ability
    /// cools down, and a disabled gizmo would swallow the click and post its reason instead. Per gizmo per frame:
    /// one event-type check, and on repaint a def lookup and a dictionary lookup or two.</summary>
    [HarmonyPatch(typeof(VEF.Abilities.Command_Ability), "GizmoOnGUIInt")]
    internal static class Patch_VefCommand_Autocast
    {
        static void Prefix(VefCommand __instance, Rect butRect)
        {
            Event ev = Event.current;
            if (ev.type != EventType.MouseDown || ev.button != 1 || !Mouse.IsOver(butRect)) return;
            VefAbility ability = __instance.ability;
            if (ability == null || !VefAutocast.Owns(ability)) return;
            ev.Use();
            VefAutocast.OpenMenu(__instance, ability);
        }

        static void Postfix(VefCommand __instance, Rect butRect, GizmoRenderParms parms)
        {
            if (Event.current.type != EventType.Repaint) return;
            VefAutocast.Badge(__instance.ability, butRect, parms.shrunk);
        }
    }

    /// <summary>"Right-click to set autocast (now: ...)" on VEF's ability gizmos, as Patch_Command_AutocastHint adds
    /// it to vanilla's. Only built while a gizmo is hovered.</summary>
    [HarmonyPatch(typeof(Command), nameof(Command.DescPostfix), MethodType.Getter)]
    internal static class Patch_Command_VefAutocastHint
    {
        static void Postfix(Command __instance, ref string __result)
        {
            if (!(__instance is VefCommand command)) return;
            VefAbility ability = command.ability;
            if (ability?.pawn == null || !VefAIAbility.Supported(ability.def)) return;
            var a = VefAIAbility.Of(ability);
            if (!AutocastMarker.Offers(a)) return;
            string mode = AbilityAIAutocast.Label(AbilityAIAutocast.ModeFor(ability.pawn, ability.def), ability.def, a.IsTouch);
            __result += "\n\n" + "AAI_Autocast_Hint".Translate(mode).Colorize(ColoredText.SubtleGrayColor);
            string recast = AbilityAIRecast.Hint(ability.def);
            if (recast != null) __result += "\n" + recast.Colorize(ColoredText.SubtleGrayColor);
        }
    }
}
