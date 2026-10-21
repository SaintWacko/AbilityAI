using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

// Player autocast: the player sets, per pawn and per ability, whether the ability AI may cast it for them.
//
// Modes are set from a right-click menu on the ability's gizmo. Vanilla's Command turns a right-click into "open the
// float menu", and Command_Ability passes that on, but a disabled gizmo (an ability on cooldown) swallows the click
// and posts "disabled" instead, which made VEF's autocast toggle unusable while an ability recharged. So the click is
// caught here before vanilla's button sees it, on any Command_Ability (Command_Psycast doesn't override GizmoOnGUI).
//
// Only abilities with an AbilityAIExtension get the menu: the extension is what tells the AI how to use them.
namespace AbilityAI
{
    public enum AbilityAIAutocastMode
    {
        /// <summary>The player casts it (default).</summary>
        Off,
        /// <summary>Cast when the rules say so, but never move to do it.</summary>
        Stationary,
        /// <summary>Also walk into range, then go back.</summary>
        Full,
    }

    /// <summary>The player's autocast choices, saved with the world. Keyed by pawn and AbilityDef rather than by the
    /// Ability object, so a mode survives the ability being removed and granted again (taking gear off and back on).</summary>
    public class AbilityAIAutocast : WorldComponent
    {
        private class Entry : IExposable
        {
            public Pawn pawn;
            public AbilityDef def;
            public AbilityAIAutocastMode mode;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Defs.Look(ref def, "def");
                Scribe_Values.Look(ref mode, "mode");
            }
        }

        // A pawn's psycast limits; only pawns whose limits differ from the defaults are stored
        private class LimitsEntry : IExposable
        {
            public Pawn pawn;
            public float heatCap = 1f;
            public float focusFloor;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref heatCap, "heatCap", 1f);
                Scribe_Values.Look(ref focusFloor, "focusFloor", 0f);
            }
        }

        private struct Limits
        {
            public float heatCap, focusFloor;
        }

        /// <summary>Default heat cap: no limit beyond vanilla's own (its entropy-limit toggle decides overheating).</summary>
        public const float NoHeatCap = 1f;
        public const float NoFocusFloor = 0f;

        private static AbilityAIAutocast instance;
        // Only pawns with at least one mode set are in here, so "does this pawn autocast anything" is one lookup
        private readonly Dictionary<Pawn, Dictionary<AbilityDef, AbilityAIAutocastMode>> modes = new Dictionary<Pawn, Dictionary<AbilityDef, AbilityAIAutocastMode>>();
        private readonly Dictionary<Pawn, Limits> limits = new Dictionary<Pawn, Limits>();
        private List<Entry> saved;
        private List<LimitsEntry> savedLimits;

        /// <summary>Goes up on every mode change, so a cached answer built from the modes knows it is stale.</summary>
        internal static int Version { get; private set; }

        public AbilityAIAutocast(World world) : base(world)
        {
            instance = this;
            // A new world: let go of the last game's pawn held by the gizmo's cache
            Gizmo_PsycastLimits.ClearCache();
        }

        private static AbilityAIAutocast Instance => instance != null && instance.world == Find.World ? instance : null;

        public static bool HasAny(Pawn pawn)
        {
            var inst = Instance;
            return inst != null && inst.modes.ContainsKey(pawn);
        }

        public static AbilityAIAutocastMode ModeFor(Pawn pawn, AbilityDef def)
        {
            var inst = Instance;
            if (inst == null || !inst.modes.TryGetValue(pawn, out var byDef)) return AbilityAIAutocastMode.Off;
            return byDef.TryGetValue(def, out var m) ? m : AbilityAIAutocastMode.Off;
        }

        public static void SetMode(Pawn pawn, AbilityDef def, AbilityAIAutocastMode mode)
        {
            var inst = Instance ?? Find.World?.GetComponent<AbilityAIAutocast>();
            if (inst == null || pawn == null || def == null) return;
            Version++;
            if (mode == AbilityAIAutocastMode.Off)
            {
                if (inst.modes.TryGetValue(pawn, out var byDef))
                {
                    byDef.Remove(def);
                    if (byDef.Count == 0) inst.modes.Remove(pawn);
                }
                return;
            }
            if (!inst.modes.TryGetValue(pawn, out var d)) inst.modes[pawn] = d = new Dictionary<AbilityDef, AbilityAIAutocastMode>();
            d[def] = mode;
        }

        /// <summary>The pawn's psycast autocast limits, or false when it has none (the defaults).</summary>
        public static bool TryGetLimits(Pawn pawn, out float heatCap, out float focusFloor)
        {
            var inst = Instance;
            if (inst != null && inst.limits.TryGetValue(pawn, out var l))
            {
                heatCap = l.heatCap;
                focusFloor = l.focusFloor;
                return true;
            }
            heatCap = NoHeatCap;
            focusFloor = NoFocusFloor;
            return false;
        }

        /// <summary>Sets the pawn's psycast limits; the defaults remove its entry.</summary>
        public static void SetLimits(Pawn pawn, float heatCap, float focusFloor)
        {
            var inst = Instance ?? Find.World?.GetComponent<AbilityAIAutocast>();
            if (inst == null || pawn == null) return;
            heatCap = Mathf.Clamp01(heatCap);
            focusFloor = Mathf.Clamp01(focusFloor);
            if (heatCap >= NoHeatCap && focusFloor <= NoFocusFloor) inst.limits.Remove(pawn);
            else inst.limits[pawn] = new Limits { heatCap = heatCap, focusFloor = focusFloor };
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                saved = new List<Entry>();
                foreach (var kv in modes)
                {
                    // A discarded pawn is gone for good; a dead one may still be resurrected
                    if (kv.Key == null || kv.Key.Discarded) continue;
                    foreach (var m in kv.Value)
                        if (m.Key != null && m.Value != AbilityAIAutocastMode.Off)
                            saved.Add(new Entry { pawn = kv.Key, def = m.Key, mode = m.Value });
                }
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                savedLimits = new List<LimitsEntry>();
                foreach (var kv in limits)
                    if (kv.Key != null && !kv.Key.Discarded)
                        savedLimits.Add(new LimitsEntry { pawn = kv.Key, heatCap = kv.Value.heatCap, focusFloor = kv.Value.focusFloor });
            }
            Scribe_Collections.Look(ref saved, "abilityAutocast", LookMode.Deep);
            Scribe_Collections.Look(ref savedLimits, "psycastLimits", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                modes.Clear();
                if (saved != null)
                    foreach (var e in saved)
                        if (e?.pawn != null && e.def != null) SetMode(e.pawn, e.def, e.mode);
                limits.Clear();
                if (savedLimits != null)
                    foreach (var e in savedLimits)
                        if (e?.pawn != null) SetLimits(e.pawn, e.heatCap, e.focusFloor);
            }
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                saved = null;
                savedLimits = null;
            }
        }

        private static bool CanApproach(AbilityDef def)
        {
            var exts = def.modExtensions;
            if (exts == null) return false;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.Worker != null && ext.approachDistance > 0f) return true;
            return false;
        }

        private static readonly AbilityAIAutocastMode[] Modes =
            { AbilityAIAutocastMode.Off, AbilityAIAutocastMode.Stationary, AbilityAIAutocastMode.Full };

        internal static string Label(AbilityAIAutocastMode mode)
        {
            switch (mode)
            {
                case AbilityAIAutocastMode.Stationary: return "AAI_Mode_Stationary".Translate();
                case AbilityAIAutocastMode.Full: return "AAI_Mode_Full".Translate();
                default: return "AAI_Mode_Off".Translate();
            }
        }

        /// <summary>Whether this gizmo gets the autocast menu: the player's own pawn, an ability the AI knows how to
        /// use, and no hook vetoes it (an Entad trait not identified yet, an item bound to someone else).</summary>
        internal static bool Offers(Ability ability)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) return false;
            Pawn p = ability?.pawn;
            if (p == null || p.Faction == null || !p.Faction.IsPlayer || p.IsPrisoner) return false;
            // A pawn that can't be drafted (most animals) only ever autocasts undrafted, where it uses every worker
            // while fighting back (AbilityAIDriver.FightsUndrafted). Without undrafted autocast it never would.
            if (p.drafter == null && !AbilityAISettings.UndraftedAutocast) return false;
            return AbilityAIDriver.HasWorker(ability.def, AbilityAIDriver.PlayerSet.All) && AbilityAIHooks.NotVetoed(p, ability);
        }

        // A def with groupAbility draws one button for every selected pawn whose gizmo is shown (drafted, or the def
        // shows it undrafted), so the menu sets the mode for all of those. Otherwise each pawn has its own button
        // ("Ability (Name)" when several are selected) and the menu is for that pawn alone.
        // title: the gizmo's own label, so a mod that relabels ability gizmos keeps its name in the menu
        internal static void OpenMenu(Ability clicked, string title)
        {
            AbilityDef def = clicked.def;
            var pawns = new List<Pawn> { clicked.pawn };
            if (def.groupAbility)
                foreach (object o in Find.Selector.SelectedObjectsListForReading)
                    // The same checks vanilla uses to show the gizmo; includeTemporary for implant and gear abilities
                    if (o is Pawn p && p != clicked.pawn && (p.Drafted ? def.showWhenDrafted : def.displayGizmoWhileUndrafted)
                        && p.abilities?.GetAbility(def, true) is Ability a && a.GizmosVisible() && Offers(a)) pawns.Add(p);
            AbilityAIAutocastMode current = ModeFor(clicked.pawn, def);

            var options = new List<FloatMenuOption>();
            // Full only adds the walk into range: drafted pawns, abilities set up with an approachDistance. Elsewhere
            // it would act as Stationary, so it isn't offered (a Full set earlier stays, and shows as current; picking
            // it again on a pawn that can't be drafted stores Stationary, which is what it was doing anyway).
            bool full = clicked.pawn.drafter != null && CanApproach(def);
            foreach (AbilityAIAutocastMode mode in Modes)
            {
                if (mode == AbilityAIAutocastMode.Full && !full && current != AbilityAIAutocastMode.Full) continue;
                string label = Label(mode);
                if (mode == current) label = "AAI_Mode_Current".Translate(label);
                AbilityAIAutocastMode m = mode;
                options.Add(new FloatMenuOption(label, () =>
                {
                    // A pawn in the group that can't be drafted never walks: it gets in-place instead of Full
                    for (int i = 0; i < pawns.Count; i++)
                        SetMode(pawns[i], def, m == AbilityAIAutocastMode.Full && pawns[i].drafter == null ? AbilityAIAutocastMode.Stationary : m);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options, "AAI_Mode_Title".Translate(title.NullOrEmpty() ? (string)def.LabelCap : title)));
        }
    }

    /// <summary>The right-click menu and the mode badge on ability gizmos. Runs every frame per ability gizmo shown:
    /// one event-type check, and for abilities with an AI extension one or two dictionary lookups.</summary>
    [HarmonyPatch(typeof(Command_Ability), nameof(Command_Ability.GizmoOnGUI))]
    internal static class Patch_CommandAbility_Autocast
    {
        private static readonly Color BadgeColor = new Color(0.55f, 0.85f, 1f);

        static void Prefix(Command_Ability __instance, Vector2 topLeft, float maxWidth)
        {
            if (Event.current.type != EventType.MouseDown) return;
            RightClick(__instance, new Rect(topLeft.x, topLeft.y, __instance.GetWidth(maxWidth), 75f));
        }

        static void Postfix(Command_Ability __instance, Vector2 topLeft, float maxWidth)
        {
            if (Event.current.type != EventType.Repaint) return;
            Badge(__instance, new Rect(topLeft.x, topLeft.y, __instance.GetWidth(maxWidth), 75f), false);
        }

        internal static void RightClick(Command_Ability command, Rect rect)
        {
            Event ev = Event.current;
            if (ev.type != EventType.MouseDown || ev.button != 1) return;
            // The rect first: every visible gizmo sees every right-click, and Offers asks the hooks
            if (!Mouse.IsOver(rect)) return;
            Ability ability = command.Ability;
            if (ability == null || !AbilityAIAutocast.Offers(ability)) return;
            // Used here, vanilla's button never sees the press: no cast on an enabled gizmo, no "disabled" message
            // on one that is cooling down
            ev.Use();
            // The label the gizmo last drew with (a shrunk gizmo doesn't refresh it, so it may lack the pawn's name)
            AbilityAIAutocast.OpenMenu(ability, command.LabelCap);
        }

        internal static void Badge(Command_Ability command, Rect button, bool shrunk)
        {
            Ability ability = command.Ability;
            if (ability?.pawn == null || !AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) return;
            if (!AbilityAIAutocast.HasAny(ability.pawn)) return;
            var mode = AbilityAIAutocast.ModeFor(ability.pawn, ability.def);
            if (mode == AbilityAIAutocastMode.Off) return;
            // A mode left on an ability a hook now vetoes (an item since bound to someone else) doesn't cast: no badge
            if (!AbilityAIHooks.NotVetoed(ability.pawn, ability)) return;
            // Left edge. Full size, vanilla's top text line holds the hotkey (left), cooldown countdown (centre) and
            // charges (right), and the label runs along the bottom, so the badge goes on the second line. Shrunk there
            // is no label or countdown, so bottom left, clear of the hotkey.
            string text = mode == AbilityAIAutocastMode.Full ? "AAI_Badge_Full".Translate() : "AAI_Badge_Stationary".Translate();
            Text.Font = GameFont.Tiny;
            Vector2 size = Text.CalcSize(text);
            float y = shrunk ? button.yMax - size.y - 1f : button.y + 3f + Text.LineHeight;
            // A long translation stays inside the button rather than spilling onto the next gizmo
            var r = new Rect(button.x + 3f, y, Mathf.Min(size.x + 4f, button.width - 6f), size.y);
            GUI.DrawTexture(r, TexUI.GrayTextBG);
            GUI.color = BadgeColor;
            Widgets.Label(new Rect(r.x + 2f, r.y, r.width - 2f, size.y), text.Truncate(r.width - 2f));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }
    }

    /// <summary>The same for ability gizmos drawn shrunk (a pawn with more abilities than fit in one row): the gizmo
    /// grid calls Command.GizmoOnGUIShrunk, which goes straight to GizmoOnGUIInt and never reaches
    /// Command_Ability.GizmoOnGUI. Runs for every shrunk command; anything not an ability gizmo leaves at one type
    /// check.</summary>
    [HarmonyPatch(typeof(Command), nameof(Command.GizmoOnGUIShrunk))]
    internal static class Patch_Command_ShrunkAutocast
    {
        static void Prefix(Command __instance, Vector2 topLeft, float size)
        {
            if (Event.current.type != EventType.MouseDown || !(__instance is Command_Ability command)) return;
            Patch_CommandAbility_Autocast.RightClick(command, new Rect(topLeft.x, topLeft.y, size, size));
        }

        static void Postfix(Command __instance, Vector2 topLeft, float size)
        {
            if (Event.current.type != EventType.Repaint || !(__instance is Command_Ability command)) return;
            Patch_CommandAbility_Autocast.Badge(command, new Rect(topLeft.x, topLeft.y, size, size), true);
        }
    }
}
