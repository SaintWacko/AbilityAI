using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using LudeonTK;
using Verse.AI;

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
            for (int k = 0; ApparelAIAbility.Ruled(apparel, k) is ApparelAIAbility a; k++)
                if (AbilityAIDriver.Handles(wearer, a)) return true;
            return false;
        }

        // Only verbs with rules: an item's other verbs keep their plain gizmo
        internal static ApparelAIAbility Of(Command_VerbOwner command) => ApparelAIAbility.For(command.verb);

        private static readonly AccessTools.FieldRef<Command_VerbTarget, List<Verb>> groupedVerbs =
            AccessTools.FieldRefAccess<Command_VerbTarget, List<Verb>>("groupedVerbs");

        // A def with mergeVerbGizmos draws one button for every selected wearer, so the menu sets the mode for all
        internal static void OpenMenu(Command_VerbOwner command, ApparelAIAbility clicked)
        {
            var pawns = new List<Pawn> { clicked.Pawn };
            var grouped = groupedVerbs(command);
            if (grouped != null)
                for (int i = 0; i < grouped.Count; i++)
                    if (ApparelAIAbility.For(grouped[i]) is ApparelAIAbility other && other.Pawn is Pawn p && !pawns.Contains(p)
                        && AbilityAIAutocast.Offers(other)) pawns.Add(p);
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

        // One entry per item with a rule, worn by the clicked pawn (whatever it replaces is dropped), with full charges
        [DebugAction("Ability AI", "Give gear with rules", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> GiveGear()
        {
            var nodes = new List<DebugActionNode>();
            var defs = new List<ThingDef>(ApparelAIAbility.RuleDefs);
            defs.SortBy(d => d.label);
            foreach (ThingDef def in defs)
            {
                ThingDef d = def;
                nodes.Add(new DebugActionNode(d.LabelCap, DebugActionType.ToolMapForPawns, pawnAction: p =>
                {
                    if (p.apparel == null || !ApparelUtility.HasPartsToWear(p, d))
                    {
                        Messages.Message($"{p.LabelShort} can't wear {d.label}", p, MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    var apparel = (Apparel)ThingMaker.MakeThing(d, GenStuff.DefaultStuffFor(d));
                    p.apparel.Wear(apparel, dropReplacedApparel: true);
                }));
            }
            return nodes;
        }
    }

    // The attack a jump interrupted runs again on landing (AbilityAIDriver.TakeAfterLanding), for any jump: gear, vanilla
    // abilities and VEF's. The flyer captures the pawn's queue here and restores it when it lands; the taken jobs go in
    // front of anything the player had queued, where QueueAfterCast put them.
    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.MakeFlyer))]
    internal static class Patch_PawnFlyer_ResumeAfterJump
    {
        private static readonly AccessTools.FieldRef<PawnFlyer, JobQueue> flyerQueue =
            AccessTools.FieldRefAccess<PawnFlyer, JobQueue>("jobQueue");

        // A throw here would stop the jump after the jump pack's charge is spent
        private static void Prefix(ThingDef flyingDef, Pawn pawn, out List<Job> __state)
        {
            __state = null;
            if (pawn == null) return;
            try { __state = AbilityAIDriver.TakeAfterLanding(pawn, flyingDef); }
            catch (System.Exception e) { Log.ErrorOnce($"[Ability AI] resuming after a jump: {e}", 0x4A41A1); }
        }

        private static void Postfix(PawnFlyer __result, List<Job> __state)
        {
            if (__state == null || __result == null) return;
            JobQueue queue = flyerQueue(__result);
            if (queue == null) flyerQueue(__result) = queue = new JobQueue();
            for (int i = __state.Count - 1; i >= 0; i--) queue.EnqueueFirst(__state[i]);
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
            // The driver never casts for a burning pawn (CanActNow), so a burning wearer is left to vanilla's trigger,
            // which pops the firefoam pack for one (CompFirefoamPack.ChanceToUse)
            if (wearer.IsBurning()) return true;
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
            string mode = AbilityAIAutocast.Label(AbilityAIAutocast.ModeFor(a.Pawn, a.Def), a.Def, false, a.Pawn);
            __result += "\n\n" + "AAI_Autocast_Hint".Translate(mode).Colorize(ColoredText.SubtleGrayColor);
            if (a.SingleUse) __result += "\n" + "AAI_SingleUse_Hint".Translate().Colorize(ColoredText.SubtleGrayColor);
        }
    }
}
