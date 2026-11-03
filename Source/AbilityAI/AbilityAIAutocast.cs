using System;
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
//
// Another ability system's gizmos (VEF's) are hooked by its compat assembly, which calls Offers, OpenMenu and
// Patch_CommandAbility_Autocast.DrawBadge here so both systems get the same menu and badge.
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

        // A mode for another ability system's def (VEF's). Saved by the def's database type and name, since the core
        // can't name those types: once that system is gone the type doesn't resolve and the entry is dropped on load.
        private class OtherEntry : IExposable
        {
            public Pawn pawn;
            public string defType;
            public string defName;
            public AbilityAIAutocastMode mode;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref defType, "defType");
                Scribe_Values.Look(ref defName, "defName");
                Scribe_Values.Look(ref mode, "mode");
            }
        }

        // A pawn's psycast limits; only pawns whose limits differ from the defaults are stored
        private class LimitsEntry : IExposable
        {
            public Pawn pawn;
            public float heatCap = 1f;
            public float focusFloor;
            public float hemogenFloor = DefaultHemogenFloor;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref heatCap, "heatCap", 1f);
                Scribe_Values.Look(ref focusFloor, "focusFloor", 0f);
                Scribe_Values.Look(ref hemogenFloor, "hemogenFloor", DefaultHemogenFloor);
            }
        }

        private struct Limits
        {
            public float heatCap, focusFloor, hemogenFloor;
        }

        /// <summary>Default hemogen floor, as a fraction of the pawn's maximum: a sanguophage needs its hemogen to
        /// live, so autocast leaves it a reserve unless the player lowers it.</summary>
        public const float DefaultHemogenFloor = 0.3f;

        /// <summary>Default heat cap: no limit beyond vanilla's own (its entropy-limit toggle decides overheating).</summary>
        public const float NoHeatCap = 1f;
        public const float NoFocusFloor = 0f;

        private static AbilityAIAutocast instance;
        // Only pawns with at least one mode set are in here, so "does this pawn autocast anything" is one lookup
        // Keyed by Def so another ability system's defs can share it; only AbilityDef entries are saved here
        private readonly Dictionary<Pawn, Dictionary<Def, AbilityAIAutocastMode>> modes = new Dictionary<Pawn, Dictionary<Def, AbilityAIAutocastMode>>();
        private readonly Dictionary<Pawn, Limits> limits = new Dictionary<Pawn, Limits>();
        private List<Entry> saved;
        private List<OtherEntry> savedOther;
        private List<LimitsEntry> savedLimits;
        // A def's runtime type to the type whose DefDatabase holds it (a def with a Class attribute is a subclass)
        private static readonly Dictionary<Type, Type> databaseTypes = new Dictionary<Type, Type>();

        /// <summary>Run once per loaded or new game, on its first tick: a compat assembly converts its system's own
        /// autocast flags into modes here (VEF's).</summary>
        internal static event Action WorldReady;
        private bool readyPending, ready;

        /// <summary>This world's first tick has run WorldReady. Before it a compat assembly leaves its system's flags as
        /// saved, so a WorldReady handler sees them unconverted.</summary>
        internal static bool Ready => Instance?.ready ?? false;

        /// <summary>The player switched AbilityAISettings.BetterAutocastOwns. Raised from the settings page, in a game
        /// or at the main menu.</summary>
        internal static event Action OwnerChanged;

        // BetterAutocastOwns as this world last ran with it, so a switch made at the main menu is carried over when
        // the game loads (the VPE compat reads and sets it)
        private bool betterAutocastOwned;

        internal static bool WorldBetterAutocastOwned
        {
            get => Instance?.betterAutocastOwned ?? false;
            set { if (Instance is AbilityAIAutocast inst) inst.betterAutocastOwned = value; }
        }

        internal static void NotifyOwnerChanged()
        {
            Version++;
            AutocastMarker.ClearCache();
            try { OwnerChanged?.Invoke(); }
            catch (Exception e) { Log.Error($"[Ability AI] Carrying autocast choices over to the new owner failed: {e}"); }
        }

        /// <summary>Goes up on every mode change, so a cached answer built from the modes knows it is stale.</summary>
        internal static int Version { get; private set; }

        public AbilityAIAutocast(World world) : base(world)
        {
            instance = this;
            // A new world: let go of the last game's pawn held by the gizmo's cache, and the last game's abilities
            Gizmo_PsycastLimits.ClearCache();
            AutocastMarker.ClearCache();
        }

        private static AbilityAIAutocast Instance => instance != null && instance.world == Find.World ? instance : null;

        public static bool HasAny(Pawn pawn)
        {
            var inst = Instance;
            return inst != null && inst.modes.ContainsKey(pawn);
        }

        // The AbilityDef overloads keep assemblies built before modes were keyed by Def working
        public static AbilityAIAutocastMode ModeFor(Pawn pawn, AbilityDef def) => ModeFor(pawn, (Def)def);

        public static void SetMode(Pawn pawn, AbilityDef def, AbilityAIAutocastMode mode) => SetMode(pawn, (Def)def, mode);

        public static AbilityAIAutocastMode ModeFor(Pawn pawn, Def def)
        {
            var inst = Instance;
            if (inst == null || !inst.modes.TryGetValue(pawn, out var byDef)) return AbilityAIAutocastMode.Off;
            return byDef.TryGetValue(def, out var m) ? m : AbilityAIAutocastMode.Off;
        }

        public static void SetMode(Pawn pawn, Def def, AbilityAIAutocastMode mode)
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
            if (!inst.modes.TryGetValue(pawn, out var d)) inst.modes[pawn] = d = new Dictionary<Def, AbilityAIAutocastMode>();
            d[def] = mode;
        }

        /// <summary>The pawn's psycast autocast limits, or false when it has none (the defaults).</summary>
        public static bool TryGetLimits(Pawn pawn, out float heatCap, out float focusFloor) =>
            TryGetLimits(pawn, out heatCap, out focusFloor, out _);

        /// <summary>The pawn's autocast limits, or false when it has none (the defaults, which are still written out).</summary>
        public static bool TryGetLimits(Pawn pawn, out float heatCap, out float focusFloor, out float hemogenFloor)
        {
            var inst = Instance;
            if (inst != null && inst.limits.TryGetValue(pawn, out var l))
            {
                heatCap = l.heatCap;
                focusFloor = l.focusFloor;
                hemogenFloor = l.hemogenFloor;
                return true;
            }
            heatCap = NoHeatCap;
            focusFloor = NoFocusFloor;
            hemogenFloor = DefaultHemogenFloor;
            return false;
        }

        /// <summary>Sets the pawn's psycast limits, keeping its hemogen floor; the defaults remove its entry.</summary>
        public static void SetLimits(Pawn pawn, float heatCap, float focusFloor)
        {
            TryGetLimits(pawn, out _, out _, out float hemogenFloor);
            SetLimits(pawn, heatCap, focusFloor, hemogenFloor);
        }

        public static void SetLimits(Pawn pawn, float heatCap, float focusFloor, float hemogenFloor)
        {
            var inst = Instance ?? Find.World?.GetComponent<AbilityAIAutocast>();
            if (inst == null || pawn == null) return;
            heatCap = Mathf.Clamp01(heatCap);
            focusFloor = Mathf.Clamp01(focusFloor);
            hemogenFloor = Mathf.Clamp01(hemogenFloor);
            if (heatCap >= NoHeatCap && focusFloor <= NoFocusFloor && Mathf.Approximately(hemogenFloor, DefaultHemogenFloor)) inst.limits.Remove(pawn);
            else inst.limits[pawn] = new Limits { heatCap = heatCap, focusFloor = focusFloor, hemogenFloor = hemogenFloor };
        }

        // World.FinalizeInit runs before the loader resolves references and before the maps load, so the saved
        // pawns aren't there yet: the hooks wait for the first tick
        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            readyPending = true;
        }

        public override void WorldComponentTick()
        {
            if (!readyPending) return;
            readyPending = false;
            ready = true;
            // Without the bridge (Better Autocasting removed, or VPE) Ability AI owns everything, so a later return of
            // Better Autocasting is a switch to carry over
            if (!AbilityAISettings.BetterAutocastActive) betterAutocastOwned = false;
            var handlers = WorldReady;
            if (handlers == null) return;
            foreach (Action a in handlers.GetInvocationList())
            {
                try { a(); }
                catch (Exception e) { Log.Error($"[Ability AI] {a.Method.DeclaringType?.FullName}.{a.Method.Name} threw on world load: {e}"); }
            }
        }

        private static Type DatabaseType(Def def)
        {
            Type type = def.GetType();
            if (databaseTypes.TryGetValue(type, out Type db)) return db;
            for (Type t = type; t != null && t != typeof(Def); t = t.BaseType)
                if (GenDefDatabase.GetDefSilentFail(t, def.defName, false) == def) { db = t; break; }
            databaseTypes[type] = db;
            return db;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                saved = new List<Entry>();
                savedOther = new List<OtherEntry>();
                foreach (var kv in modes)
                {
                    // A discarded pawn is gone for good; a dead one may still be resurrected
                    if (kv.Key == null || kv.Key.Discarded) continue;
                    foreach (var m in kv.Value)
                    {
                        if (m.Value == AbilityAIAutocastMode.Off) continue;
                        if (m.Key is AbilityDef def) saved.Add(new Entry { pawn = kv.Key, def = def, mode = m.Value });
                        else if (DatabaseType(m.Key) is Type db)
                            savedOther.Add(new OtherEntry { pawn = kv.Key, defType = db.FullName, defName = m.Key.defName, mode = m.Value });
                    }
                }
            }
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                savedLimits = new List<LimitsEntry>();
                foreach (var kv in limits)
                    if (kv.Key != null && !kv.Key.Discarded)
                        savedLimits.Add(new LimitsEntry { pawn = kv.Key, heatCap = kv.Value.heatCap, focusFloor = kv.Value.focusFloor, hemogenFloor = kv.Value.hemogenFloor });
            }
            Scribe_Collections.Look(ref saved, "abilityAutocast", LookMode.Deep);
            Scribe_Collections.Look(ref savedOther, "otherAutocast", LookMode.Deep);
            Scribe_Collections.Look(ref savedLimits, "psycastLimits", LookMode.Deep);
            Scribe_Values.Look(ref betterAutocastOwned, "betterAutocastOwned", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                modes.Clear();
                if (saved != null)
                    foreach (var e in saved)
                        if (e?.pawn != null && e.def != null) SetMode(e.pawn, e.def, e.mode);
                if (savedOther != null)
                    foreach (var e in savedOther)
                    {
                        if (e?.pawn == null || e.defType.NullOrEmpty() || e.defName.NullOrEmpty()) continue;
                        Type type = GenTypes.GetTypeInAnyAssembly(e.defType);
                        if (type == null || !typeof(Def).IsAssignableFrom(type)) continue;
                        Def def = GenDefDatabase.GetDefSilentFail(type, e.defName, false);
                        if (def != null) SetMode(e.pawn, def, e.mode);
                    }
                limits.Clear();
                if (savedLimits != null)
                    foreach (var e in savedLimits)
                        if (e?.pawn != null) SetLimits(e.pawn, e.heatCap, e.focusFloor, e.hemogenFloor);
            }
            if (Scribe.mode == LoadSaveMode.Saving || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                saved = null;
                savedOther = null;
                savedLimits = null;
            }
        }

        private static bool CanApproach(Def def)
        {
            var exts = def.modExtensions;
            if (exts == null) return false;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.Worker != null && ext.approachDistance > 0f) return true;
            return false;
        }

        private static readonly AbilityAIAutocastMode[] Modes =
            { AbilityAIAutocastMode.Off, AbilityAIAutocastMode.Stationary, AbilityAIAutocastMode.Full };

        /// <summary>A touch ability (the Word psycasts: no range of its own, the cast job walks the caster up to the
        /// target in every mode). "Stationary" would be the wrong word for it, so its in-place mode is plain
        /// "Autocast"; it stores the same Stationary mode and has the same badge.</summary>
        internal static bool IsTouch(AbilityDef def)
        {
            var v = def?.verbProperties;
            // targetable: a targetRequired, untargetable verb is a self-cast (AbilityAIUtility.IsSelfCast)
            return v != null && v.range <= 0f && v.targetable && def.targetRequired;
        }

        // A Movement rule (Power Leap, Shadowskip, Ghostwalk) or a Skip Escape rule (Royalty's Skip) moves the caster in
        // the in-place mode too: drafted autocast runs every worker while holding position, gap-close and escape
        // included. "Stationary" would promise the pawn stays put, so these get the plain "Autocast" label that touch
        // abilities have. Skip with only Rescue and Repel (VPE's) moves other pawns and keeps "Stationary". A Recast
        // rule walks the caster into range of the spot, so it is plain "Autocast" too, and so is a Zone rule, whose work
        // walks the caster to the target.
        private static bool MovesCaster(Def def)
        {
            var exts = def.modExtensions;
            if (exts == null) return false;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && (ext.Worker is AbilityAIWorker_Movement
                    || ext.Worker is AbilityAIWorker_Skip && ext.skipMode == AbilityAISkipMode.Escape
                    || ext.Worker is AbilityAIWorker_Recast || ext.Worker is AbilityAIWorker_Zone || ext.Worker is AbilityAIWorker_Spot)) return true;
            return false;
        }

        internal static string Label(AbilityAIAutocastMode mode, AbilityDef def) => Label(mode, def, IsTouch(def));

        internal static string Label(AbilityAIAutocastMode mode, Def def, bool touch)
        {
            switch (mode)
            {
                case AbilityAIAutocastMode.Stationary:
                    return touch || MovesCaster(def) ? "AAI_Mode_Touch".Translate() : "AAI_Mode_Stationary".Translate();
                case AbilityAIAutocastMode.Full: return "AAI_Mode_Full".Translate();
                default: return "AAI_Mode_Off".Translate();
            }
        }

        /// <summary>Whether this gizmo gets the autocast menu: the player's own pawn, an ability the AI knows how to
        /// use, and no hook vetoes it (an Entad trait not identified yet, an item bound to someone else).</summary>
        internal static bool Offers(Ability ability) =>
            ability != null && Offers(ability.pawn, ability.def) && AbilityAIHooks.NotVetoed(ability.pawn, ability);

        /// <summary>Offers for an ability of any system. For VEF's this also says whether Ability AI owns the
        /// ability, which hides VEF's own autocast toggle.</summary>
        internal static bool Offers(AIAbility ability) =>
            ability != null && ability.Drivable && !ability.PlayerAutocastElsewhere && Offers(ability.Pawn, ability.Def)
            && AbilityAIHooks.NotVetoed(ability.Pawn, ability);

        private static bool Offers(Pawn p, Def def)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) return false;
            if (p == null || def == null || p.Faction == null || !p.Faction.IsPlayer || p.IsPrisoner) return false;
            // A pawn that can't be drafted (most animals) only ever autocasts undrafted, where it uses every worker
            // while fighting back (AbilityAIDriver.FightsUndrafted). Without undrafted autocast it never would.
            if (p.drafter == null && !AbilityAISettings.UndraftedAutocast) return false;
            return AbilityAIDriver.HasWorker(def, AbilityAIDriver.PlayerSet.All);
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
            OpenMenu(clicked.pawn, def, IsTouch(def), pawns, title);
        }

        /// <summary>The menu for any system's ability. pawns: everyone the clicked button stands for, the clicked
        /// pawn first; the chosen mode is set for all of them.</summary>
        internal static void OpenMenu(Pawn clicked, Def def, bool touch, List<Pawn> pawns, string title)
        {
            AbilityAIAutocastMode current = ModeFor(clicked, def);

            var options = new List<FloatMenuOption>();
            // Full only adds the walk into range: drafted pawns, abilities set up with an approachDistance. Elsewhere
            // it would act as Stationary, so it isn't offered (a Full set earlier stays, and shows as current; picking
            // it again on a pawn that can't be drafted stores Stationary, which is what it was doing anyway).
            bool full = clicked.drafter != null && CanApproach(def);
            foreach (AbilityAIAutocastMode mode in Modes)
            {
                if (mode == AbilityAIAutocastMode.Full && !full && current != AbilityAIAutocastMode.Full) continue;
                string label = Label(mode, def, touch);
                if (mode == current) label = "AAI_Mode_Current".Translate(label);
                AbilityAIAutocastMode m = mode;
                options.Add(new FloatMenuOption(label, () =>
                {
                    // A pawn in the group that can't be drafted never walks: it gets in-place instead of Full
                    for (int i = 0; i < pawns.Count; i++)
                        SetMode(pawns[i], def, m == AbilityAIAutocastMode.Full && pawns[i].drafter == null ? AbilityAIAutocastMode.Stationary : m);
                }));
            }
            // Markers to be cast at, from here rather than the Architect menu so it doesn't get a button per thing
            ThingDef recastThing = AbilityAIRecast.RuleOf(def)?.recastThing;
            if (recastThing != null && RecastSpots.Of(clicked.MapHeld) != null)
                options.Add(new FloatMenuOption("AAI_RecastPlace".Translate(recastThing.label) + "...",
                    () => Find.DesignatorManager.Select(new Designator_RecastMarker(recastThing, def))));
            if (AbilityAISpots.RuleOf(def) != null && CastSpots.Of(clicked.MapHeld) != null)
                options.Add(new FloatMenuOption("AAI_SpotPlace".Translate(def.label) + "...",
                    () => Find.DesignatorManager.Select(new Designator_CastSpot(def))));
            // The zone is shared by every caster on the map, so any caster's menu edits it
            var zoneRule = AbilityAIZones.RuleOf(def);
            var zones = PsycastZones.Of(clicked.MapHeld);
            if (zoneRule != null && AbilityAIZones.NeedsZone(zoneRule.zoneTarget) && zones != null)
            {
                options.Add(new FloatMenuOption("AAI_ZoneExpand".Translate(def.label) + "...",
                    () => Find.DesignatorManager.Select(new Designator_PsycastZone(def, true))));
                if (zones.HasCells(def))
                    options.Add(new FloatMenuOption("AAI_ZoneShrink".Translate(def.label) + "...",
                        () => Find.DesignatorManager.Select(new Designator_PsycastZone(def, false))));
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
            var mode = AbilityAIAutocast.HasAny(ability.pawn)
                ? AbilityAIAutocast.ModeFor(ability.pawn, ability.def) : AbilityAIAutocastMode.Off;
            if (mode == AbilityAIAutocastMode.Off)
            {
                // Not set yet, but the right-click menu is there: a grey ring in the badge's spot
                if (AbilityAISettings.ShowMarker && AutocastMarker.Offers(ability)) AutocastMarker.Draw(button, shrunk);
                return;
            }
            // A mode left on an ability a hook now vetoes (an item since bound to someone else) doesn't cast: no badge
            if (!AbilityAIHooks.NotVetoed(ability.pawn, ability)) return;
            DrawBadge(button, mode, shrunk, false);
        }

        /// <summary>The mode badge. Left edge by default. Full size, vanilla's top text line holds the hotkey (left),
        /// cooldown countdown (centre) and charges (right), and the label runs along the bottom, so the badge goes on
        /// the second line. Shrunk there is no label or countdown, so bottom left, clear of the hotkey; rightEdge puts
        /// it bottom right instead, for gizmos where another mod draws in the bottom-left corner.</summary>
        internal static void DrawBadge(Rect button, AbilityAIAutocastMode mode, bool shrunk, bool rightEdge)
        {
            string text = mode == AbilityAIAutocastMode.Full ? "AAI_Badge_Full".Translate() : "AAI_Badge_Stationary".Translate();
            Text.Font = GameFont.Tiny;
            Vector2 size = Text.CalcSize(text);
            float y = shrunk ? button.yMax - size.y - 1f : button.y + 3f + Text.LineHeight;
            // A long translation stays inside the button rather than spilling onto the next gizmo
            float width = Mathf.Min(size.x + 4f, button.width - 6f);
            var r = new Rect(shrunk && rightEdge ? button.xMax - 3f - width : button.x + 3f, y, width, size.y);
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
