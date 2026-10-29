using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

// Recasting: abilities that leave something behind for a while (a solar pinhole, VPE's ice crystal) are kept up for
// the player. Each thing a colonist casts with a Recast rule becomes a spot on its map (RecastSpots). Before it runs
// out (AbilityAISettings.RecastAt of its lifetime left) a colonist with one of those abilities on autocast walks over
// and casts it again, as Basic work (WorkGiver_Recast in AbilityAIRecastWork.cs), so the Work tab decides when; and
// once it has run out or been destroyed, a recast marker shows the spot until someone does.
//
// The spot, not the marker, is what is saved: markers are made again from the spots on load (their def isn't
// saveable), so a save never holds a thing of this mod's on the map. The thing an ability leaves gets a small comp
// (CompRecastSpot, added to the rule's thing def at startup) that recognises a cast by a running cast job and gives
// the player a gizmo to stop or start keeping it up.
namespace AbilityAI
{
    /// <summary>Recast: keeps up the thing the ability leaves (recastThing). Not run by the driver: recasting is Basic
    /// work for the player's own colonists, never an NPC's (see AbilityAIRecastWork). The worker exists so the rule
    /// reads like every other one and so the player's autocast menu is offered for the ability.</summary>
    public class AbilityAIWorker_Recast : AbilityAIWorker
    {
        // Any: undrafted is the only time it runs, and HasWorker has to count it for the menu and the stand-down
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;

        public override bool DriverCasts => false;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            return false;
        }
    }

    [DefOf]
    public static class AbilityAIRecastDefOf
    {
        public static ThingDef AAI_RecastMarker;
        public static JobDef AAI_RecastWalk;

        static AbilityAIRecastDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(AbilityAIRecastDefOf));
    }

    public static class AbilityAIRecast
    {
        private static readonly HashSet<ThingDef> things = new HashSet<ThingDef>();
        private static readonly List<Func<Thing, int>> lifetimeReaders = new List<Func<Thing, int>>();

        /// <summary>Another autocast mod decides this ability def for the player (Better Autocasting, set by the VPE
        /// compat's bridge). Spots its casts made keep no marker and aren't recast while it does.</summary>
        internal static Func<Def, bool> Ceded;

        /// <summary>Some ability has a Recast rule: the settings section and the work giver have something to do.</summary>
        public static bool Any => things.Count > 0;

        internal static bool Tracks(ThingDef def) => things.Contains(def);

        /// <summary>The ability def's Recast rule, or null.</summary>
        public static AbilityAIExtension RuleOf(Def def)
        {
            var exts = def?.modExtensions;
            if (exts == null) return null;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.Worker is AbilityAIWorker_Recast && ext.recastThing != null) return ext;
            return null;
        }

        /// <summary>Called at startup for every ability def of every system (the core for vanilla's, a compat
        /// assembly for its own): a Recast rule's thing gets the comp that notices it being cast.</summary>
        public static void Note(Def abilityDef)
        {
            var rule = RuleOf(abilityDef);
            ThingDef thing = rule?.recastThing;
            if (thing == null || things.Contains(thing)) return;
            if (thing.thingClass == null || !typeof(ThingWithComps).IsAssignableFrom(thing.thingClass))
            {
                Log.Warning($"[Ability AI] {abilityDef.defName}'s Recast rule names {thing.defName}, whose class {thing.thingClass} can't hold a comp, so it is never recast.");
                return;
            }
            if (thing.comps == null) thing.comps = new List<CompProperties>();
            thing.comps.Add(new CompProperties(typeof(CompRecastSpot)));
            things.Add(thing);
        }

        /// <summary>How another ability system's things run out: the tick a thing will be gone by, or -1 when the
        /// reader doesn't know it. Vanilla's CompDestroyAfterDelay is read without one.</summary>
        public static void RegisterLifetime(Func<Thing, int> endTick)
        {
            if (endTick != null && !lifetimeReaders.Contains(endTick)) lifetimeReaders.Add(endTick);
        }

        /// <summary>When the thing will run out (end) and when that lifetime began (start), or false when nothing
        /// says. fallbackStart: when the spot started keeping it, for systems that don't record a spawn tick.</summary>
        internal static bool TryGetLifetime(Thing thing, int fallbackStart, out int start, out int end)
        {
            start = fallbackStart;
            end = -1;
            var delay = thing.TryGetComp<CompDestroyAfterDelay>();
            if (delay != null)
            {
                start = delay.spawnTick;
                end = delay.DestructionTick;
                return true;
            }
            for (int i = 0; i < lifetimeReaders.Count; i++)
            {
                try { end = lifetimeReaders[i](thing); }
                catch (Exception e)
                {
                    Log.Error($"[Ability AI] a lifetime reader threw for {thing.def.defName}, so it is dropped: {e}");
                    lifetimeReaders.RemoveAt(i--);
                    continue;
                }
                if (end >= 0) return true;
            }
            return false;
        }

        /// <summary>The tooltip line for an ability with a Recast rule, or null.</summary>
        internal static string Hint(Def abilityDef)
        {
            var rule = RuleOf(abilityDef);
            return rule == null ? null : (string)"AAI_Recast_Hint".Translate(rule.recastThing.label);
        }
    }

    /// <summary>One place the player's colonists keep a thing going: where it was first cast (anchor), and the thing
    /// standing for it now, which a recast beside the old one (an ice crystal can't be cast onto itself) moves to the
    /// next cell over and back.</summary>
    public class RecastSpot : IExposable
    {
        public IntVec3 anchor;
        public ThingDef thingDef;
        public Thing thing;
        public int startTick;
        // The ability that cast it last, kept by name and def type (a VEF def and a vanilla one aren't the same
        // Def type); empty for a thing the player switched on with its gizmo
        private string castDefName, castDefType;
        private Def castDef;
        private bool castDefResolved;

        // Not saved: a walk or cast under way is started again after a load
        internal Pawn claimant;
        internal int claimJobId = -1;
        internal RecastMarker marker;
        internal bool due;
        private Dictionary<Pawn, int> retryAfter;

        public RecastSpot() { }

        internal RecastSpot(IntVec3 cell, ThingDef thingDef, Def castDef, int tick)
        {
            anchor = cell;
            this.thingDef = thingDef;
            startTick = tick;
            CastDef = castDef;
        }

        internal RecastSpot(Thing thing, Def castDef, int tick)
        {
            anchor = thing.Position;
            thingDef = thing.def;
            this.thing = thing;
            startTick = tick;
            CastDef = castDef;
        }

        public Def CastDef
        {
            get
            {
                if (!castDefResolved)
                {
                    castDefResolved = true;
                    Type type = castDefType.NullOrEmpty() ? null : GenTypes.GetTypeInAnyAssembly(castDefType);
                    castDef = type == null || castDefName.NullOrEmpty() ? null : GenDefDatabase.GetDefSilentFail(type, castDefName, false);
                }
                return castDef;
            }
            set
            {
                castDef = value;
                castDefResolved = true;
                castDefName = value?.defName;
                castDefType = value?.GetType().FullName;
            }
        }

        /// <summary>Cast by an ability another autocast mod decides for the player: no marker, no recast, until it
        /// hands the ability back.</summary>
        public bool Dormant
        {
            get
            {
                var def = CastDef;
                return def != null && AbilityAIRecast.Ceded != null && AbilityAIRecast.Ceded(def);
            }
        }

        /// <summary>The claimant's walk or cast for this spot is still its current or a queued job.</summary>
        internal bool ClaimHolds()
        {
            if (claimant == null) return false;
            if (claimant.Spawned && !claimant.Dead && claimant.jobs != null)
            {
                if (claimant.CurJob != null && claimant.CurJob.loadID == claimJobId) return true;
                var queue = claimant.jobs.jobQueue;
                for (int i = 0; i < queue.Count; i++)
                    if (queue[i].job.loadID == claimJobId) return true;
            }
            claimant = null;
            claimJobId = -1;
            return false;
        }

        internal bool ClaimedByOther(Pawn pawn) => claimant != null && claimant != pawn && ClaimHolds();

        internal void Claim(Pawn pawn, int jobId)
        {
            claimant = pawn;
            claimJobId = jobId;
        }

        // A pawn that found no way to cast here leaves it for a while instead of searching again at every work check
        internal bool Waiting(Pawn pawn, int tick) => retryAfter != null && retryAfter.TryGetValue(pawn, out int t) && tick < t;

        internal void Wait(Pawn pawn, int untilTick)
        {
            if (retryAfter == null) retryAfter = new Dictionary<Pawn, int>();
            else if (retryAfter.Count > 16) retryAfter.Clear();
            retryAfter[pawn] = untilTick;
        }

        public void ExposeData()
        {
            // A reference to a destroyed thing would not resolve on load. One in transit (on a gravship) is saved
            // with its ship, so its reference stays.
            if (Scribe.mode == LoadSaveMode.Saving && thing != null && thing.Destroyed) thing = null;
            Scribe_Values.Look(ref anchor, "anchor");
            Scribe_Defs.Look(ref thingDef, "thingDef");
            Scribe_References.Look(ref thing, "thing");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref castDefName, "castDef");
            Scribe_Values.Look(ref castDefType, "castDefType");
        }
    }

    /// <summary>The spots on one map. Checked once a second: a thing gone (expired or destroyed) leaves its marker
    /// then, rather than from its despawn, so nothing spawns in the middle of another thing's despawn and a pinhole
    /// that a recast in place wipes (the new one already stands for the spot) never leaves one.</summary>
    public class RecastSpots : MapComponent
    {
        private const int CheckIntervalTicks = 60;
        private static readonly Color MarkerColor = ThingDefGenerator_Buildings.BlueprintColor;

        private List<RecastSpot> spots = new List<RecastSpot>();
        internal int dueCount;

        public RecastSpots(Map map) : base(map) { }

        internal List<RecastSpot> Spots => spots;

        internal static RecastSpots Of(Map map) => map?.GetComponent<RecastSpots>();

        internal static Color GhostColor => MarkerColor;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (spots.Count > 0) Refresh();
        }

        public override void MapComponentTick()
        {
            if (spots.Count == 0) return;
            if ((Find.TickManager.TicksGame + map.uniqueID) % CheckIntervalTicks != 0) return;
            Refresh();
        }

        internal void Refresh()
        {
            int tick = Find.TickManager.TicksGame;
            float recastAt = AbilityAISettings.RecastAt;
            dueCount = 0;
            for (int i = spots.Count - 1; i >= 0; i--)
            {
                RecastSpot s = spots[i];
                if (s.thing != null)
                {
                    if (s.thing.Destroyed) s.thing = null;
                    else if (!s.thing.Spawned)
                    {
                        // In transit: nothing to recast and no marker until it lands somewhere
                        if (s.marker != null) RemoveMarker(s);
                        if (s.due) s.due = false;
                        continue;
                    }
                    else if (s.thing.Map != map)
                    {
                        // On another map without Notify_Landed having moved the spot (a way of moving things this
                        // mod doesn't know): the spot goes with it
                        spots.RemoveAt(i);
                        if (s.marker != null) RemoveMarker(s);
                        s.due = false;
                        s.claimant = null;
                        s.anchor = s.thing.Position;
                        Of(s.thing.Map)?.spots.Add(s);
                        continue;
                    }
                }
                bool dormant = s.Dormant;
                bool wantMarker = s.thing == null && !dormant;
                if (wantMarker && (s.marker == null || !s.marker.Spawned)) SpawnMarker(s);
                else if (!wantMarker && s.marker != null) RemoveMarker(s);
                s.due = !dormant && (s.thing == null || DueSoon(s, tick, recastAt));
                if (s.due) dueCount++;
            }
        }

        private static bool DueSoon(RecastSpot s, int tick, float recastAt)
        {
            if (recastAt <= 0f || !AbilityAIRecast.TryGetLifetime(s.thing, s.startTick, out int start, out int end)) return false;
            int total = end - start;
            if (total <= 0) return true;
            return end - tick <= total * recastAt;
        }

        private void SpawnMarker(RecastSpot s)
        {
            if (!s.anchor.InBounds(map)) return;
            var m = (RecastMarker)ThingMaker.MakeThing(AbilityAIRecastDefOf.AAI_RecastMarker);
            m.spot = s;
            s.marker = m;
            GenSpawn.Spawn(m, s.anchor, map, WipeMode.Vanish);
        }

        private static void RemoveMarker(RecastSpot s)
        {
            var m = s.marker;
            s.marker = null;
            if (m != null && !m.Destroyed) m.Destroy();
        }

        internal RecastSpot For(Thing thing)
        {
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].thing == thing) return spots[i];
            return null;
        }

        /// <summary>A thing with a Recast rule spawned (not from a load). A colonist's recast for a spot it claimed
        /// carries that spot on, a cast at a spot's own cell takes the spot over (a pinhole recast by hand onto its
        /// marker), and any other colonist cast starts a new spot when the setting is on.</summary>
        internal void Notify_Spawned(Thing thing)
        {
            Pawn caster = null;
            AIAbility castWith = null;
            var pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            float best = float.MaxValue;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (!AIAbilitySources.TryGetRunningCast(p, out AIAbility a, out _)) continue;
                if (AbilityAIRecast.RuleOf(a.Def)?.recastThing != thing.def) continue;
                // The one whose claim this is, else the nearest: two colonists casting the same psycast in the same
                // tick is the only way this guesses
                float d = HasClaim(p, thing.def) ? -1f : (p.Position - thing.Position).LengthHorizontalSquared;
                if (d < best)
                {
                    best = d;
                    caster = p;
                    castWith = a;
                }
            }
            int tick = Find.TickManager.TicksGame;
            RecastSpot spot = null;
            if (caster != null)
                for (int i = 0; i < spots.Count && spot == null; i++)
                    if (spots[i].claimant == caster && spots[i].thingDef == thing.def && spots[i].ClaimHolds()) spot = spots[i];
            if (spot == null)
                for (int i = 0; i < spots.Count && spot == null; i++)
                    if (spots[i].anchor == thing.Position && spots[i].thingDef == thing.def) spot = spots[i];
            if (spot != null)
            {
                if (spot.thing != null && spot.thing != thing) CompRecastSpot.Mark(spot.thing, false);
                CompRecastSpot.Mark(thing, true);
                spot.thing = thing;
                spot.startTick = tick;
                if (castWith != null) spot.CastDef = castWith.Def;
                spot.claimant = null;
                spot.claimJobId = -1;
                if (spot.marker != null) RemoveMarker(spot);
                if (spot.due)
                {
                    spot.due = false;
                    dueCount--;
                }
                return;
            }
            if (caster == null || !AbilityAISettings.RecastTrackNewCasts || castWith.PlayerAutocastElsewhere) return;
            spots.Add(new RecastSpot(thing, castWith.Def, tick));
            CompRecastSpot.Mark(thing, true);
        }

        /// <summary>Whether the player may put a marker for this thing at cell: the cells it would cover are open
        /// (no wall or other building, nothing that fills the cell, a building's on ground a pawn could stand on),
        /// and no spot of the same thing is there yet. The ability's own rules aren't asked: a pawn's psyfocus or
        /// cooldown would refuse a place that is fine, and the recast checks them (falling back to a cell beside
        /// the marker) when someone comes to cast.</summary>
        internal AcceptanceReport CanPlace(IntVec3 cell, ThingDef def)
        {
            if (!cell.InBounds(map) || cell.Fogged(map)) return false;
            foreach (IntVec3 c in GenAdj.OccupiedRect(cell, Rot4.North, def.Size))
            {
                if (!c.InBounds(map) || c.Fogged(map) || c.Filled(map)) return "AAI_RecastPlace_Blocked".Translate();
                if (def.category == ThingCategory.Building ? c.GetFirstBuilding(map) != null || !c.Standable(map) : c.GetEdifice(map) != null)
                    return "AAI_RecastPlace_Blocked".Translate();
            }
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].thingDef == def && (spots[i].anchor == cell || spots[i].thing != null && spots[i].thing.OccupiedRect().Contains(cell)))
                    return "AAI_RecastPlace_Taken".Translate();
            var things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
                if (things[i].def == def) return "AAI_RecastPlace_Standing".Translate(def.label);
            return true;
        }

        /// <summary>A marker the player put down: a spot with nothing standing for it yet, due at once.</summary>
        internal void Place(IntVec3 cell, ThingDef def, Def castDef)
        {
            spots.Add(new RecastSpot(cell, def, castDef, Find.TickManager.TicksGame));
            Refresh();
        }

        /// <summary>A kept-up thing set down by a gravship: its spot moves to where it landed, from whichever map
        /// still has it. A map left without a grav anchor is abandoned with its spots, so then the thing's own
        /// record that it was kept up starts a new one.</summary>
        internal static void Notify_Landed(Thing thing, bool kept)
        {
            var here = Of(thing.Map);
            if (here == null) return;
            var maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                var from = Of(maps[m]);
                var spot = from?.For(thing);
                if (spot == null) continue;
                from.spots.Remove(spot);
                if (spot.marker != null) RemoveMarker(spot);
                if (spot.due) from.dueCount--;
                spot.due = false;
                spot.claimant = null;
                spot.anchor = thing.Position;
                here.spots.Add(spot);
                return;
            }
            if (kept) here.spots.Add(new RecastSpot(thing, null, Find.TickManager.TicksGame));
        }

        private bool HasClaim(Pawn p, ThingDef def)
        {
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].claimant == p && spots[i].thingDef == def) return true;
            return false;
        }

        /// <summary>The thing's gizmo: stop keeping it up, or start for one nobody was (cast before this mod, or by
        /// a visitor).</summary>
        internal void Toggle(Thing thing)
        {
            var spot = For(thing);
            if (spot != null) Remove(spot);
            else
            {
                spots.Add(new RecastSpot(thing, null, Find.TickManager.TicksGame));
                CompRecastSpot.Mark(thing, true);
            }
        }

        internal void Remove(RecastSpot spot)
        {
            if (!spots.Remove(spot)) return;
            if (spot.due) dueCount--;
            CompRecastSpot.Mark(spot.thing, false);
            RemoveMarker(spot);
        }

        /// <summary>The spot a recast walk is for: the one at its anchor this pawn claimed, else one there that no
        /// one has (a walk loaded from a save, whose claim wasn't saved), which it claims for the walk.</summary>
        internal RecastSpot ClaimFor(Pawn pawn, IntVec3 anchor, int jobId)
        {
            RecastSpot free = null;
            for (int i = 0; i < spots.Count; i++)
            {
                var s = spots[i];
                if (s.anchor != anchor) continue;
                if (s.claimant == pawn && s.ClaimHolds()) return s;
                if (free == null && !s.ClaimHolds()) free = s;
            }
            free?.Claim(pawn, jobId);
            return free;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref spots, "spots", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (spots == null) spots = new List<RecastSpot>();
                // A def from a mod since removed
                spots.RemoveAll(s => s == null || s.thingDef == null);
                for (int i = 0; i < spots.Count; i++) CompRecastSpot.Mark(spots[i].thing, true);
            }
        }
    }

    /// <summary>On a thing a Recast rule names (added at startup by AbilityAIRecast.Note).</summary>
    public class CompRecastSpot : ThingComp
    {
        // A spot keeps this thing up. Saved with the thing, so a gravship that abandons the spot's map along with
        // its spots still lands it as kept up (RecastSpots.Notify_Landed).
        private bool kept;

        internal static void Mark(Thing thing, bool on)
        {
            var comp = thing?.TryGetComp<CompRecastSpot>();
            if (comp != null) comp.kept = on;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (respawningAfterLoad) return;
            // A gravship setting it down isn't a cast
            if (parent.BeingTransportedOnGravship) RecastSpots.Notify_Landed(parent, kept);
            else RecastSpots.Of(parent.Map)?.Notify_Spawned(parent);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref kept, "aaiRecastKept");
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) yield break;
            var spots = RecastSpots.Of(parent.Map);
            if (spots == null) yield break;
            // Kept up already, or on a map of the player's: not a pinhole on a visited settlement's map
            if (!parent.Map.IsPlayerHome && spots.For(parent) == null) yield break;
            Thing thing = parent;
            yield return new Command_Toggle
            {
                defaultLabel = "AAI_RecastToggle".Translate(),
                defaultDesc = "AAI_RecastToggle_Desc".Translate(thing.def.label),
                icon = thing.def.uiIcon,
                isActive = () => spots.For(thing) != null,
                toggleAction = () => spots.Toggle(thing),
            };
        }
    }

    /// <summary>Puts recast markers down, one per click, until right-click or Escape: opened from the autocast menu of
    /// an ability with a Recast rule, never from the Architect menu. Colonists with the ability on autocast then cast
    /// at the markers as Basic work, as for a kept-up thing that ran out.</summary>
    public class Designator_RecastMarker : Designator
    {
        private static readonly Color CannotPlaceColor = new Color(1f, 0.25f, 0.25f, 0.45f);

        private readonly ThingDef thingDef;
        private readonly Def castDef;

        public Designator_RecastMarker(ThingDef thingDef, Def castDef)
        {
            this.thingDef = thingDef;
            this.castDef = castDef;
            defaultLabel = "AAI_RecastPlace".Translate(thingDef.label);
            defaultDesc = "AAI_RecastPlace_Desc".Translate(thingDef.label);
            icon = thingDef.uiIcon;
            useMouseIcon = true;
            soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
        }

        public override bool CanRemainSelected() => RecastSpots.Of(Map) != null;

        public override AcceptanceReport CanDesignateCell(IntVec3 loc) =>
            RecastSpots.Of(Map)?.CanPlace(loc, thingDef) ?? AcceptanceReport.WasRejected;

        public override void DesignateSingleCell(IntVec3 c) => RecastSpots.Of(Map)?.Place(c, thingDef, castDef);

        public override void SelectedUpdate()
        {
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(Map) || thingDef.graphic == null || thingDef.graphic == BaseContent.BadGraphic) return;
            Color color = CanDesignateCell(cell).Accepted ? RecastSpots.GhostColor : CannotPlaceColor;
            GhostDrawer.DrawGhostThing(cell, Rot4.North, thingDef, null, color, AltitudeLayer.Blueprint, drawPlaceWorkers: false);
        }
    }

    /// <summary>Where a kept-up thing ran out: a ghost of it in blueprint colours until a colonist recasts it. Not
    /// saved (RecastSpots makes it again on load); its spot holds everything.</summary>
    public class RecastMarker : Thing
    {
        internal RecastSpot spot;
        private Graphic ghost;

        public override Graphic Graphic
        {
            get
            {
                if (ghost == null)
                {
                    ThingDef def = spot?.thingDef;
                    ghost = def?.graphic != null && def.graphicData != null
                        ? GhostUtility.GhostGraphicFor(def.graphic, def, RecastSpots.GhostColor)
                        : BaseContent.BadGraphic;
                }
                return ghost;
            }
        }

        public override string LabelNoCount =>
            spot?.thingDef != null ? (string)"AAI_RecastMarker_Label".Translate(spot.thingDef.label) : base.LabelNoCount;

        public override string GetInspectString()
        {
            if (spot == null) return base.GetInspectString();
            string text = "AAI_RecastMarker_Inspect".Translate();
            if (spot.ClaimHolds()) text += "\n" + "AAI_RecastMarker_Claimed".Translate(spot.claimant.LabelShort);
            return text;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            RecastMarker marker = this;
            yield return new Command_Action
            {
                defaultLabel = "AAI_RecastMarker_Remove".Translate(),
                defaultDesc = "AAI_RecastMarker_Remove_Desc".Translate(),
                icon = TexButton.Delete,
                action = () =>
                {
                    if (marker.spot != null && marker.Spawned) RecastSpots.Of(marker.Map)?.Remove(marker.spot);
                    if (!marker.Destroyed) marker.Destroy();
                },
            };
        }
    }
}
