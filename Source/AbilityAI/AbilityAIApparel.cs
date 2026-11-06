using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// Vanilla's own triggers for worn gear stand down where this mod decides for the wearer, and the gear's gizmos get
// the autocast menu, badge and hint (ApparelAIAbility).
namespace AbilityAI
{
    internal static class AbilityAIApparel
    {
        internal static bool Handled(Apparel apparel)
        {
            Pawn wearer = apparel?.Wearer;
            if (wearer == null || !ApparelAIAbility.Any) return false;
            var a = ApparelAIAbility.For(apparel);
            return a != null && AbilityAIDriver.Handles(wearer, a);
        }

        // Only the verb the rules are for (verbIndex): an item's other verbs keep their plain gizmo
        internal static ApparelAIAbility Of(Command_VerbOwner command)
        {
            if (!(command.verb?.DirectOwner is CompApparelVerbOwner comp) || !(comp.parent is Apparel apparel)) return null;
            var a = ApparelAIAbility.For(apparel);
            return a != null && a.verb == command.verb ? a : null;
        }

        private static readonly AccessTools.FieldRef<Command_VerbTarget, List<Verb>> groupedVerbs =
            AccessTools.FieldRefAccess<Command_VerbTarget, List<Verb>>("groupedVerbs");

        // A def with mergeVerbGizmos draws one button for every selected wearer, so the menu sets the mode for all
        internal static void OpenMenu(Command_VerbOwner command, ApparelAIAbility clicked)
        {
            var pawns = new List<Pawn> { clicked.Pawn };
            var grouped = groupedVerbs(command);
            if (grouped != null)
                for (int i = 0; i < grouped.Count; i++)
                    if (grouped[i].DirectOwner is CompApparelVerbOwner comp && comp.Wearer is Pawn p && !pawns.Contains(p)
                        && AbilityAIAutocast.Offers(ApparelAIAbility.Of(grouped[i], comp))) pawns.Add(p);
            AbilityAIAutocast.OpenMenu(clicked.Pawn, clicked.Def, false, pawns, command.LabelCap);
        }

        internal static void Badge(ApparelAIAbility a, Rect button, bool shrunk)
        {
            Pawn p = a.Pawn;
            if (p == null || !AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) return;
            var mode = AbilityAIAutocast.HasAny(p) ? AbilityAIAutocast.ModeFor(p, a.Def) : AbilityAIAutocastMode.Off;
            if (mode == AbilityAIAutocastMode.Off)
            {
                if (AbilityAISettings.ShowMarker && AutocastMarker.Offers(a)) AutocastMarker.Draw(button, shrunk);
                return;
            }
            if (!AbilityAIHooks.NotVetoed(p, a)) return;
            Patch_CommandAbility_Autocast.DrawBadge(button, mode, shrunk, false);
        }
    }

    /// <summary>The smokepop belt pops for a non-colonist when a turret's bullet lands near it. Where this mod decides
    /// for the wearer its Cover rule does instead, so a belt with few charges isn't spent by both. Thing's and
    /// Apparel's versions of the method are empty, so skipping the override skips nothing else: the skipping bool
    /// prefix (against the prefix-returns-void lint) is the point here, as for the two patches below.</summary>
    [HarmonyPatch(typeof(SmokepopBelt), nameof(SmokepopBelt.Notify_BulletImpactNearby))]
    internal static class Patch_SmokepopBelt_StandDown
    {
        static bool Prefix(SmokepopBelt __instance) => !AbilityAIApparel.Handled(__instance);
    }

    /// <summary>The low-shield pack deploys for a non-colonist when a bullet it would stop lands nearby; stood down as
    /// the smokepop belt is.</summary>
    [HarmonyPatch(typeof(BroadshieldPack), nameof(BroadshieldPack.Notify_BulletImpactNearby))]
    internal static class Patch_BroadshieldPack_StandDown
    {
        static bool Prefix(BroadshieldPack __instance) => !AbilityAIApparel.Handled(__instance);
    }

    /// <summary>The tox, firefoam and disruptor flare packs try themselves on a timer for wearers the player doesn't
    /// control (CompAIUsablePack); the comp's tick does nothing else. Per worn pack per tick: one hash-interval test
    /// in vanilla, here a set lookup before it for packs with no rule.</summary>
    [HarmonyPatch(typeof(CompAIUsablePack), nameof(CompAIUsablePack.CompTick))]
    internal static class Patch_CompAIUsablePack_StandDown
    {
        static bool Prefix(CompAIUsablePack __instance)
        {
            if (!ApparelAIAbility.HasRules(__instance.parent.def)) return true;
            var apparel = __instance.parent as Apparel;
            // Only on the check ticks vanilla would act on, so Handles isn't asked every tick
            Pawn wearer = apparel?.Wearer;
            int interval = (__instance.props as CompProperties_AIUSablePack)?.checkInterval ?? 60;
            if (wearer == null || !wearer.IsHashIntervalTick(interval)) return true;
            return !AbilityAIApparel.Handled(apparel);
        }
    }

    /// <summary>The right-click menu and the badge on gear gizmos (Command_VerbOwner), full size and shrunk: both draw
    /// through Command.GizmoOnGUIInt, which Command_VerbOwner doesn't override. Caught before the button sees the
    /// click, since the gizmo is disabled while the item has no charges. Every command drawn goes through here: one
    /// event-type check, then a type check.</summary>
    [HarmonyPatch(typeof(Command), "GizmoOnGUIInt")]
    internal static class Patch_CommandVerbOwner_Autocast
    {
        static void Prefix(Command __instance, Rect butRect)
        {
            Event ev = Event.current;
            if (ev.type != EventType.MouseDown || ev.button != 1 || !(__instance is Command_VerbOwner command) || !Mouse.IsOver(butRect)) return;
            var a = AbilityAIApparel.Of(command);
            if (a == null || !AbilityAIAutocast.Offers(a)) return;
            ev.Use();
            AbilityAIApparel.OpenMenu(command, a);
        }

        static void Postfix(Command __instance, Rect butRect, GizmoRenderParms parms)
        {
            if (Event.current.type != EventType.Repaint || !(__instance is Command_VerbOwner command)) return;
            var a = AbilityAIApparel.Of(command);
            if (a != null) AbilityAIApparel.Badge(a, butRect, parms.shrunk);
        }
    }

    /// <summary>"Right-click to set autocast (now: ...)" on gear gizmos. Only built while a gizmo is hovered.</summary>
    [HarmonyPatch(typeof(Command), nameof(Command.DescPostfix), MethodType.Getter)]
    internal static class Patch_Command_ApparelAutocastHint
    {
        static void Postfix(Command __instance, ref string __result)
        {
            if (!(__instance is Command_VerbOwner command)) return;
            var a = AbilityAIApparel.Of(command);
            if (a?.Pawn == null || !AutocastMarker.Offers(a)) return;
            string mode = AbilityAIAutocast.Label(AbilityAIAutocast.ModeFor(a.Pawn, a.Def), a.Def, false);
            __result += "\n\n" + "AAI_Autocast_Hint".Translate(mode).Colorize(ColoredText.SubtleGrayColor);
            if (a.SingleUse) __result += "\n" + "AAI_SingleUse_Hint".Translate().Colorize(ColoredText.SubtleGrayColor);
        }
    }
}
