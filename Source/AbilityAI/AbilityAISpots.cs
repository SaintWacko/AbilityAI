using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

// Cast markers: psycasts that grow things on open ground (VPE's Crop Burst and Tree Sprout) are cast by the player's
// colonists at markers the player puts down, whenever the ground around a marker is ready for another cast. Unlike a
// recast spot, nothing the cast leaves stands for the marker; the ground decides (AbilityAISpots.Evaluate). No crop may
// grow within the marker's radius, since VPE's plant spawners wipe every plant but a tree from the cells they take, and
// enough cells in reach have to be open for a cast to be worth it: Tree Sprout has no cooldown, and a full grove would
// otherwise be sprouted at forever. Cast as Basic work (WorkGiver_CastSpot in AbilityAISpotWork.cs).
namespace AbilityAI
{
    /// <summary>What a marker of a Spot rule waits for.</summary>
    public enum AbilityAISpotKind : byte
    {
        None,
        /// <summary>No crop in the radius, and enough cells a plant spawner can take: fertile ground with no tree and no
        /// building (Crop Burst).</summary>
        Crops,
        /// <summary>No crop in the radius, and enough cells a tree of the map's biome can grow on with no plant beside it
        /// that blocks sowing (Tree Sprout; VPE's tree spawner applies the same adjacency rule).</summary>
        Trees,
    }

    /// <summary>Why a marker isn't cast at now. Shown on the marker.</summary>
    public enum AbilityAISpotState : byte
    {
        Due,
        Fogged,
        CropsGrowing,
        Landing,
        TooFewCells,
        /// <summary>Trees: the biome has no wild trees. VPE then sprouts any wild plant, which leaves the cells as open
        /// as before, so a marker with no cooldown would be cast at forever.</summary>
        NoTrees,
    }

    /// <summary>Spot: cast at markers the player puts down, when the ground there is ready (spotKind). Not run by the
    /// driver: it is Basic work for the player's colonists (AbilityAISpotWork). The worker exists so the rule reads like
    /// every other one and so the player's autocast menu is offered for the ability.</summary>
    public class AbilityAIWorker_Spot : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;

        public override bool DriverCasts => false;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            return false;
        }
    }

    [DefOf]
    public static class AbilityAISpotDefOf
    {
        public static ThingDef AAI_CastSpotMarker;
        public static JobDef AAI_CastSpotWalk;

        static AbilityAISpotDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(AbilityAISpotDefOf));
    }

    public static class AbilityAISpots
    {
        private static readonly List<Def> ruleDefs = new List<Def>();
        private static readonly HashSet<Def> noted = new HashSet<Def>();
        private static readonly bool[] kindsUsed = new bool[3];
        private static readonly Dictionary<BiomeDef, List<ThingDef>> biomeTrees = new Dictionary<BiomeDef, List<ThingDef>>();

        /// <summary>Some ability has a Spot rule: the settings rows and the work giver have something to do.</summary>
        public static bool Any => ruleDefs.Count > 0;

        internal static List<Def> RuleDefs => ruleDefs;

        internal static bool Uses(AbilityAISpotKind kind) => kindsUsed[(int)kind];

        /// <summary>The ability def's Spot rule, or null.</summary>
        public static AbilityAIExtension RuleOf(Def def)
        {
            var exts = def?.modExtensions;
            if (exts == null) return null;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.Worker is AbilityAIWorker_Spot && ext.spotKind != AbilityAISpotKind.None
                    && ext.spotRadius > 0f) return ext;
            return null;
        }

        /// <summary>Called at startup for every ability def of every system, beside AbilityAIZones.Note.</summary>
        public static void Note(Def abilityDef)
        {
            var rule = RuleOf(abilityDef);
            if (rule == null || !noted.Add(abilityDef)) return;
            ruleDefs.Add(abilityDef);
            kindsUsed[(int)rule.spotKind] = true;
        }

        internal static int MinCells(AbilityAISpotKind kind) =>
            kind == AbilityAISpotKind.Trees ? AbilityAISettings.SpotTreeMinCells : AbilityAISettings.SpotCropMinCells;

        /// <summary>The tooltip line for an ability with a Spot rule, or null.</summary>
        internal static string Hint(Def abilityDef)
        {
            var rule = RuleOf(abilityDef);
            return rule == null ? null : (string)("AAI_Spot_Hint_" + rule.spotKind).Translate();
        }

        /// <summary>Whether a cast centred on centre is wanted now. clearRadius: no crop and no spawner still landing
        /// within it. openRadius: the cells counted as open (the caster's own radius when one is about to cast).</summary>
        /// <summary>Tree sprout's reach grows with psychic sensitivity, and spotRadius is its largest, so the ring and the
        /// marker's cell count are for the most sensitive caster.</summary>
        internal static string RingNote(Def castDef)
        {
            var rule = RuleOf(castDef);
            if (rule == null || rule.spotKind != AbilityAISpotKind.Trees) return null;
            return "AAI_SpotMarker_RingNote_Trees".Translate(rule.spotRadius.ToString("0.#"));
        }

        internal static AbilityAISpotState Evaluate(Map map, IntVec3 centre, AbilityAISpotKind kind, float clearRadius, float openRadius,
            int need, out int open) => Evaluate(map, centre, kind, clearRadius, openRadius, need, out open, out _);

        /// <summary>As Evaluate; crop: the plant that keeps it CropsGrowing.</summary>
        internal static AbilityAISpotState Evaluate(Map map, IntVec3 centre, AbilityAISpotKind kind, float clearRadius, float openRadius,
            int need, out int open, out ThingDef crop)
        {
            open = 0;
            crop = null;
            if (!centre.InBounds(map) || centre.Fogged(map)) return AbilityAISpotState.Fogged;
            float max = GenRadial.MaxRadialPatternRadius - 1f;
            int clearCount = GenRadial.NumCellsInRadius(Mathf.Min(clearRadius, max));
            int openCount = GenRadial.NumCellsInRadius(Mathf.Min(openRadius, max));
            int count = Mathf.Max(clearCount, openCount);
            List<ThingDef> trees = kind == AbilityAISpotKind.Trees ? TreesOf(map) : null;
            if (trees != null && trees.Count == 0) return AbilityAISpotState.NoTrees;
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = centre + GenRadial.RadialPattern[i];
                if (!c.InBounds(map)) continue;
                bool clear = i < clearCount;
                // PlantSpawner.CheckSpawnLoc: terrain fertility (a floor has none), no tree, no edifice
                bool openHere = i < openCount && !c.Fogged(map) && c.GetTerrain(map).fertility > 0f;
                var things = map.thingGrid.ThingsListAtFast(c);
                for (int j = 0; j < things.Count; j++)
                {
                    Thing t = things[j];
                    if (t is Plant plant)
                    {
                        if (plant.def.plant.IsTree) openHere = false;
                        else if (clear && plant.def.plant.Sowable)
                        {
                            crop = plant.def;
                            return AbilityAISpotState.CropsGrowing;
                        }
                    }
                    else if (t is GroundSpawner)
                    {
                        // A cast that just landed: its plants aren't up yet
                        if (clear) return AbilityAISpotState.Landing;
                        openHere = false;
                    }
                    else if (t.def.IsEdifice()) openHere = false;
                }
                if (openHere && trees != null) openHere = TreeCanGrow(trees, c, map);
                if (openHere) open++;
            }
            return open >= need ? AbilityAISpotState.Due : AbilityAISpotState.TooFewCells;
        }

        // VPE's TreeSpawner takes a random wild tree of the biome and gives up when that one can't grow on the cell, so a
        // cell any of them can take is counted
        private static bool TreeCanGrow(List<ThingDef> trees, IntVec3 c, Map map)
        {
            for (int i = 0; i < trees.Count; i++)
                if (trees[i].CanEverPlantAt(c, map, true, true) && PlantUtility.AdjacentSowBlocker(trees[i], c, map) == null) return true;
            return false;
        }

        private static List<ThingDef> TreesOf(Map map)
        {
            BiomeDef biome = map.Biome;
            if (!biomeTrees.TryGetValue(biome, out var trees))
            {
                trees = new List<ThingDef>();
                foreach (ThingDef plant in biome.AllWildPlants)
                    if (plant.plant != null && plant.plant.IsTree) trees.Add(plant);
                biomeTrees[biome] = trees;
            }
            return trees;
        }
    }

    /// <summary>A marker the player put down for a Spot rule's ability. Only the cell and the ability are saved.</summary>
    public class CastSpot : MarkedSpot
    {
        // Not saved: found again on the first check after a load
        internal CastSpotMarker marker;
        internal AbilityAISpotState state;
        internal int openCells;
        // A wild plant that happens to be sowable holds the marker as a crop does, so the marker names it
        internal ThingDef crop;

        public CastSpot() { }

        internal CastSpot(IntVec3 cell, Def castDef)
        {
            anchor = cell;
            CastDef = castDef;
        }
    }

    /// <summary>The cast markers of one map. The ground around each is looked at every 250 ticks; a colonist about to
    /// cast looks again with its own radius (AbilityAISpotWork), since a cast may have landed since.</summary>
    public class CastSpots : MapComponent
    {
        private const int CheckIntervalTicks = 250;

        private List<CastSpot> spots = new List<CastSpot>();
        internal int dueCount;

        public CastSpots(Map map) : base(map) { }

        internal List<CastSpot> Spots => spots;

        internal static CastSpots Of(Map map) => map?.GetComponent<CastSpots>();

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
            dueCount = 0;
            for (int i = 0; i < spots.Count; i++)
            {
                CastSpot s = spots[i];
                // A psycast whose mod is gone keeps its markers, unused and unseen, for when it comes back
                var rule = AbilityAISpots.RuleOf(s.CastDef);
                bool live = rule != null && !s.Dormant;
                if (live && (s.marker == null || !s.marker.Spawned)) SpawnMarker(s);
                else if (!live && s.marker != null) RemoveMarker(s);
                s.due = false;
                if (!live) continue;
                s.state = AbilityAISpots.Evaluate(map, s.anchor, rule.spotKind, rule.spotRadius, rule.spotRadius,
                    AbilityAISpots.MinCells(rule.spotKind), out s.openCells, out s.crop);
                s.due = s.state == AbilityAISpotState.Due;
                if (s.due) dueCount++;
            }
        }

        private void SpawnMarker(CastSpot s)
        {
            if (!s.anchor.InBounds(map)) return;
            var m = (CastSpotMarker)ThingMaker.MakeThing(AbilityAISpotDefOf.AAI_CastSpotMarker);
            m.spot = s;
            s.marker = m;
            GenSpawn.Spawn(m, s.anchor, map, WipeMode.Vanish);
        }

        private static void RemoveMarker(CastSpot s)
        {
            var m = s.marker;
            s.marker = null;
            if (m != null && !m.Destroyed) m.Destroy();
        }

        /// <summary>A marker may go on any open cell without one of the same psycast: VEF's spawn refuses a cell that is
        /// filled or holds a building. Whether the ground around it is ready is the marker's own business.</summary>
        internal AcceptanceReport CanPlace(IntVec3 cell, Def castDef)
        {
            if (!cell.InBounds(map) || cell.Fogged(map)) return false;
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].anchor == cell && spots[i].CastDef == castDef) return "AAI_SpotPlace_Taken".Translate();
            if (cell.Filled(map) || cell.GetFirstBuilding(map) != null) return "AAI_RecastPlace_Blocked".Translate();
            return true;
        }

        internal void Place(IntVec3 cell, Def castDef)
        {
            spots.Add(new CastSpot(cell, castDef));
            Refresh();
        }

        internal void Remove(CastSpot spot)
        {
            if (!spots.Remove(spot)) return;
            if (spot.due) dueCount--;
            spot.due = false;
            RemoveMarker(spot);
        }

        /// <summary>The marker a walk is for: the one at its cell this pawn claimed, else one there no one has (a walk
        /// loaded from a save, whose claim wasn't saved), which it claims for the walk. Markers of two psycasts may share
        /// a cell, and the walk doesn't say which, so the free one has to be of a psycast the pawn has on autocast.</summary>
        internal CastSpot ClaimFor(Pawn pawn, IntVec3 anchor, int jobId)
        {
            CastSpot free = null;
            for (int i = 0; i < spots.Count; i++)
            {
                var s = spots[i];
                if (s.anchor != anchor) continue;
                if (s.claimant == pawn && s.ClaimHolds()) return s;
                if (free == null && !s.ClaimHolds() && s.CastDef != null
                    && AbilityAIAutocast.ModeFor(pawn, s.CastDef) != AbilityAIAutocastMode.Off) free = s;
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
                if (spots == null) spots = new List<CastSpot>();
                spots.RemoveAll(s => s == null);
            }
        }
    }

    /// <summary>Puts cast markers down, one per click, until right-click or Escape. Opened from the autocast menu of an
    /// ability with a Spot rule. Draws the reach of the marker under the mouse and of the ones already down.</summary>
    public class Designator_CastSpot : Designator
    {
        private readonly Def castDef;
        private readonly float radius;

        public Designator_CastSpot(Def castDef)
        {
            this.castDef = castDef;
            radius = AbilityAISpots.RuleOf(castDef)?.spotRadius ?? 0f;
            defaultLabel = "AAI_SpotPlace".Translate(castDef.label);
            defaultDesc = "AAI_SpotPlace_Desc".Translate(castDef.label);
            string ringNote = AbilityAISpots.RingNote(castDef);
            if (ringNote != null) defaultDesc += "\n\n" + ringNote;
            icon = AbilityAIZones.IconOf(castDef);
            useMouseIcon = true;
            soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
        }

        public override bool CanRemainSelected() => CastSpots.Of(Map) != null;

        public override AcceptanceReport CanDesignateCell(IntVec3 loc) =>
            CastSpots.Of(Map)?.CanPlace(loc, castDef) ?? AcceptanceReport.WasRejected;

        public override void DesignateSingleCell(IntVec3 c) => CastSpots.Of(Map)?.Place(c, castDef);

        public override void SelectedUpdate()
        {
            if (radius <= 0f || radius >= GenRadial.MaxRadialPatternRadius) return;
            var spots = CastSpots.Of(Map);
            if (spots != null)
                for (int i = 0; i < spots.Spots.Count; i++)
                    if (spots.Spots[i].CastDef == castDef) GenDraw.DrawRadiusRing(spots.Spots[i].anchor, radius);
            IntVec3 cell = UI.MouseCell();
            if (cell.InBounds(Map)) GenDraw.DrawRadiusRing(cell, radius, CanDesignateCell(cell).Accepted ? Color.white : Color.red);
        }
    }

    /// <summary>A cast marker on the map: the psycast's icon in blueprint colours, faded while the ground isn't ready.
    /// Not saved (CastSpots makes it again on load); its spot holds everything.</summary>
    public class CastSpotMarker : Thing
    {
        private static readonly Color DueColor = new Color(0.7f, 0.85f, 1f, 0.75f);
        private static readonly Color WaitingColor = new Color(0.7f, 0.85f, 1f, 0.3f);
        private static readonly Vector3 IconScale = new Vector3(0.8f, 1f, 0.8f);

        internal CastSpot spot;

        private float Radius => AbilityAISpots.RuleOf(spot?.CastDef)?.spotRadius ?? 0f;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            Def castDef = spot?.CastDef;
            if (castDef == null) return;
            Material mat = MaterialPool.MatFrom(AbilityAIZones.IconOf(castDef), ShaderDatabase.Transparent, spot.due ? DueColor : WaitingColor);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(drawLoc, Quaternion.identity, IconScale), mat, 0);
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            float r = Radius;
            if (r > 0f && r < GenRadial.MaxRadialPatternRadius) GenDraw.DrawRadiusRing(Position, r);
        }

        public override string LabelNoCount =>
            spot?.CastDef != null ? (string)"AAI_SpotMarker_Label".Translate(spot.CastDef.label) : base.LabelNoCount;

        public override string GetInspectString()
        {
            if (spot == null) return base.GetInspectString();
            string text;
            switch (spot.state)
            {
                case AbilityAISpotState.Due: text = "AAI_RecastMarker_Inspect".Translate(); break;
                case AbilityAISpotState.CropsGrowing:
                    text = "AAI_SpotMarker_Crops".Translate(spot.crop?.label ?? "?", Radius.ToString("0.#"));
                    break;
                case AbilityAISpotState.Landing: text = "AAI_SpotMarker_Landing".Translate(); break;
                case AbilityAISpotState.NoTrees: text = "AAI_SpotMarker_NoTrees".Translate(); break;
                case AbilityAISpotState.TooFewCells:
                    var rule = AbilityAISpots.RuleOf(spot.CastDef);
                    text = rule == null ? "" : (string)("AAI_SpotMarker_TooFew_" + rule.spotKind).Translate(AbilityAISpots.MinCells(rule.spotKind), spot.openCells);
                    break;
                default: text = "AAI_SpotMarker_Fogged".Translate(); break;
            }
            if (spot.ClaimHolds()) text += "\n" + "AAI_SpotMarker_Claimed".Translate(spot.claimant.LabelShort);
            string ringNote = AbilityAISpots.RingNote(spot.CastDef);
            if (ringNote != null) text += "\n" + ringNote;
            return text;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            CastSpotMarker marker = this;
            yield return new Command_Action
            {
                defaultLabel = "AAI_RecastMarker_Remove".Translate(),
                defaultDesc = "AAI_SpotMarker_Remove_Desc".Translate(),
                icon = TexButton.Delete,
                action = () =>
                {
                    if (marker.spot != null && marker.Spawned) CastSpots.Of(marker.Map)?.Remove(marker.spot);
                    if (!marker.Destroyed) marker.Destroy();
                },
            };
        }
    }
}
