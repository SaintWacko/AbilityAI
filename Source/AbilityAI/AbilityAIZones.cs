using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Zone casting: psycasts that tend to things rather than fight (VPE's Enchant, Mend, Recharge, Power, Craft Timeskip,
// Enthrall) are cast by the player's colonists on what lies in an area the player paints for that psycast, as Basic
// work (WorkGiver_PsycastZone in AbilityAIZoneWork.cs). One area per psycast def per map, shared by every caster. It
// overlaps every vanilla zone and area, is not one of the map's Areas (so it is in no allowed-area picker and not in
// Manage Areas), and is drawn only while one of its designators is selected; those open from the psycast's autocast
// menu, never the Architect menu. A rule whose targets are pawns needs no area (Word of Trust: the prisoners the wardens
// are recruiting; the colony Words; Word of Alliance's visitors; Brain Leech's prisoners) and runs through the same work
// without one.
namespace AbilityAI
{
    /// <summary>What a zone rule casts at. Each kind's own filter (AbilityAIZones.Wants) reads the Zone casting
    /// settings; the ability's own checks still decide each target.</summary>
    public enum AbilityAIZoneTarget : byte
    {
        None,
        /// <summary>Items with a quality below the "enchant up to" setting (Enchant). Not books: VPE's cast skips them.</summary>
        QualityItems,
        /// <summary>The player's buildings and any items under the "mend below" share of their hit points (Mend).</summary>
        Damaged,
        /// <summary>The player's batteries and mechs under the "recharge below" share of their charge (Recharge).</summary>
        Rechargeable,
        /// <summary>The player's buildings drawing power now (Power).</summary>
        PowerConsumers,
        /// <summary>Unfinished items whose bill still exists (Craft Timeskip).</summary>
        Unfinished,
        /// <summary>Humanlike corpses of factionless pawns and of factions hostile to the player (Enthrall).</summary>
        HumanlikeCorpses,
        /// <summary>The colony's prisoners set to be recruited or have their resistance worn down, with resistance left.
        /// Needs no zone (Word of Trust).</summary>
        Prisoners,
        /// <summary>The same corpses as HumanlikeCorpses, consumed in heaps by a self-cast area (Consume Bodies): the
        /// caster walks to the cell with the most of them in reach (AbilityAIZoneWork.TryFindConsumeCell).</summary>
        ConsumableCorpses,
        /// <summary>The colony's pawns, awake and below the "Word of Joy below" mood, without the rule's hediff (Joyfuzz).
        /// No zone.</summary>
        LowMood,
        /// <summary>The colony's pawns, awake, not inspired and able to be (Word of Inspiration). No zone.</summary>
        Inspirable,
        /// <summary>The colony's pawns without the rule's hediff (Word of Productivity). No zone.</summary>
        ColonistsWithout,
        /// <summary>Pawns of other factions that aren't hostile, whose faction's goodwill is below the setting (Word of
        /// Alliance). No zone.</summary>
        Visitors,
        /// <summary>The colony's prisoners (and slaves, by setting) without the rule's hediff (Brain Leech). No zone.</summary>
        LeechHosts,
    }

    /// <summary>Zone: casts the ability on what its zone holds (zoneTarget). Not run by the driver: zone casting is
    /// Basic work for the player's colonists (AbilityAIZoneWork). The worker exists so the rule reads like every
    /// other one and so the player's autocast menu is offered for the ability.</summary>
    public class AbilityAIWorker_Zone : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;

        public override bool DriverCasts => false;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            return false;
        }

        // Word of Trust: the prisoners its zone work would cast at
        protected internal override bool IsLinkTarget(AbilityAIContext ctx, AIAbility ability, Pawn p) => AbilityAIZones.Wants(ext, p);
    }

    [DefOf]
    public static class AbilityAIZoneDefOf
    {
        public static JobDef AAI_ZoneCastWalk;
        public static JobDef AAI_ZoneLinkWalk;

        static AbilityAIZoneDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(AbilityAIZoneDefOf));
    }

    public static class AbilityAIZones
    {
        private static readonly List<Def> ruleDefs = new List<Def>();
        private static readonly Dictionary<string, Def> byName = new Dictionary<string, Def>();
        private static readonly bool[] kindsUsed = new bool[Enum.GetValues(typeof(AbilityAIZoneTarget)).Length];
        private static readonly List<Func<Thing, Def, bool>> busyChecks = new List<Func<Thing, Def, bool>>();
        private static readonly Dictionary<Def, Texture2D> icons = new Dictionary<Def, Texture2D>();
        private static readonly Dictionary<Def, Color> colors = new Dictionary<Def, Color>();

        /// <summary>Some ability has a zone rule: the settings section and the work giver have something to do.</summary>
        public static bool Any => ruleDefs.Count > 0;

        internal static List<Def> RuleDefs => ruleDefs;

        /// <summary>Some rule casts at this kind: its setting is shown.</summary>
        internal static bool Uses(AbilityAIZoneTarget kind) => kindsUsed[(int)kind];

        internal static bool NeedsZone(AbilityAIZoneTarget kind) => !TargetsPawns(kind);

        // Kinds whose targets are pawns found on the map rather than things in a painted zone
        internal static bool TargetsPawns(AbilityAIZoneTarget kind) =>
            kind == AbilityAIZoneTarget.Prisoners || kind == AbilityAIZoneTarget.LowMood || kind == AbilityAIZoneTarget.Inspirable
            || kind == AbilityAIZoneTarget.ColonistsWithout || kind == AbilityAIZoneTarget.Visitors || kind == AbilityAIZoneTarget.LeechHosts;

        internal static Def DefNamed(string defName) => defName != null && byName.TryGetValue(defName, out Def def) ? def : null;

        /// <summary>The ability def's zone rule, or null.</summary>
        public static AbilityAIExtension RuleOf(Def def)
        {
            var exts = def?.modExtensions;
            if (exts == null) return null;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.Worker is AbilityAIWorker_Zone && ext.zoneTarget != AbilityAIZoneTarget.None) return ext;
            return null;
        }

        /// <summary>Called at startup for every ability def of every system, beside AbilityAIRecast.Note.</summary>
        public static void Note(Def abilityDef)
        {
            var rule = RuleOf(abilityDef);
            if (rule == null || byName.ContainsKey(abilityDef.defName)) return;
            ruleDefs.Add(abilityDef);
            byName[abilityDef.defName] = abilityDef;
            kindsUsed[(int)rule.zoneTarget] = true;
        }

        /// <summary>For another ability system: true when this ability's effect is already under way on the thing (VPE's
        /// Recharge, a hediff on the caster that charges its target for a while), so no second cast is sent. Asked for
        /// the few targets a colonist is about to cast at. A check that throws is dropped for the session.</summary>
        public static void RegisterBusy(Func<Thing, Def, bool> busy)
        {
            if (busy != null && !busyChecks.Contains(busy)) busyChecks.Add(busy);
        }

        internal static bool Busy(Thing thing, Def def)
        {
            for (int i = 0; i < busyChecks.Count; i++)
            {
                try { if (busyChecks[i](thing, def)) return true; }
                catch (Exception e)
                {
                    Log.Error($"[Ability AI] a zone busy check threw for {def.defName}, so it is dropped: {e}");
                    busyChecks.RemoveAt(i--);
                }
            }
            return false;
        }

        /// <summary>The kind's own rule for a thing, from the Zone casting settings. Read when the zone's target list is
        /// built and again for the target a colonist is about to take, since the list is up to 250 ticks old.</summary>
        internal static bool Wants(AbilityAIExtension rule, Thing t) => Wants(rule.zoneTarget, rule.hediff, t);

        internal static bool Wants(AbilityAIZoneTarget kind, Thing t) => Wants(kind, null, t);

        private static bool Wants(AbilityAIZoneTarget kind, HediffDef hediff, Thing t)
        {
            if (t == null || t.Destroyed || !t.Spawned) return false;
            switch (kind)
            {
                case AbilityAIZoneTarget.QualityItems:
                {
                    if (t.def.category != ThingCategory.Item || t is Book) return false;
                    var quality = t.GetInnerIfMinified().TryGetComp<CompQuality>();
                    return quality != null && quality.Quality < AbilityAISettings.ZoneEnchantUpTo;
                }
                case AbilityAIZoneTarget.Damaged:
                {
                    if (!t.def.useHitPoints || t.HitPoints >= t.MaxHitPoints) return false;
                    if (t.def.category == ThingCategory.Building)
                    {
                        // Natural rock and ruins have no faction: a mend would undo the mining. Frames and blueprints
                        // are finished by construction.
                        if (t.Faction != Faction.OfPlayer || t is Frame || t is Blueprint) return false;
                    }
                    else if (t.def.category != ThingCategory.Item || t is Corpse) return false;
                    return t.HitPoints < t.MaxHitPoints * AbilityAISettings.ZoneMendBelow;
                }
                case AbilityAIZoneTarget.Rechargeable:
                {
                    if (t is Pawn p)
                    {
                        var energy = p.needs?.energy;
                        // One on a charger is being charged anyway
                        return p.Faction == Faction.OfPlayer && !p.Dead && p.RaceProps.IsMechanoid && energy != null
                            && energy.currentCharger == null && energy.CurLevelPercentage < AbilityAISettings.ZoneRechargeBelow;
                    }
                    if (t.Faction != Faction.OfPlayer) return false;
                    var battery = t.TryGetComp<CompPowerBattery>();
                    return battery != null && battery.Props.storedEnergyMax > 0f && battery.StoredEnergyPct < AbilityAISettings.ZoneRechargeBelow;
                }
                case AbilityAIZoneTarget.PowerConsumers:
                {
                    if (t.def.category != ThingCategory.Building || t.Faction != Faction.OfPlayer) return false;
                    var power = t.TryGetComp<CompPowerTrader>();
                    // VPE's Power sets the output to 0 while it lasts, so a powered building drops out by itself
                    return power != null && power.PowerOutput < 0f;
                }
                case AbilityAIZoneTarget.Unfinished:
                {
                    // A deleted bill leaves its unfinished thing behind; VPE's cast would finish it with no bill to count
                    if (!(t is UnfinishedThing u) || u.BoundBill == null || u.Recipe == null) return false;
                    // VPE's Ability_Finish takes the ingredient matching the stuff with First(), which throws without one
                    if (u.def.MadeFromStuff)
                    {
                        var ingredients = u.ingredients;
                        if (ingredients == null) return false;
                        for (int i = 0; i < ingredients.Count; i++)
                            if (ingredients[i].def == u.Stuff) return true;
                        return false;
                    }
                    return true;
                }
                case AbilityAIZoneTarget.HumanlikeCorpses:
                case AbilityAIZoneTarget.ConsumableCorpses:
                {
                    // Only enemies' and the factionless: the corpse is destroyed, so the colony's own dead or a friendly
                    // visitor could never be buried
                    Pawn inner = (t as Corpse)?.InnerPawn;
                    return inner != null && inner.RaceProps.Humanlike && (inner.Faction == null || inner.Faction.HostileTo(Faction.OfPlayer));
                }
                case AbilityAIZoneTarget.Prisoners:
                {
                    // Asleep: VEF's social-interaction check refuses it and posts "is asleep" whatever it is told
                    if (!(t is Pawn p) || !p.IsPrisonerOfColony || p.Downed || p.InMentalState || !p.Awake() || p.guest == null) return false;
                    if (p.guest.Resistance <= 0f) return false;
                    return p.guest.IsInteractionEnabled(PrisonerInteractionModeDefOf.AttemptRecruit)
                        || p.guest.IsInteractionEnabled(PrisonerInteractionModeDefOf.ReduceResistance);
                }
                case AbilityAIZoneTarget.LowMood:
                {
                    // Awake: VEF's social-interaction check refuses a sleeper, as for Word of Trust. The mood it is
                    // heading for, not where it stands now, so a pawn already recovering isn't fuzzed for five days.
                    if (!(t is Pawn p) || !OfColony(p) || p.Downed || p.InMentalState || !p.Awake() || AbilityAIUtility.Has(p, hediff)) return false;
                    var mood = p.needs?.mood;
                    return mood != null && mood.CurInstantLevelPercentage < AbilityAISettings.ZoneJoyBelowMood;
                }
                case AbilityAIZoneTarget.Inspirable:
                {
                    // Vanilla's Word of Inspiration refuses babies (canTargetBaby) and anyone not IsColonist (a slave who is
                    // released, escaping or rebelling, a ghoul); kept, they stay wanted and every pass refuses them again
                    if (!(t is Pawn p) || !OfColony(p) || !p.IsColonist || p.DevelopmentalStage.Baby() || p.Downed || p.InMentalState
                        || !p.Awake() || p.mindState?.inspirationHandler == null) return false;
                    return AbilityUtility.ValidateNoInspiration(p, false, null) && AbilityUtility.ValidateCanGetInspiration(p, false, null);
                }
                case AbilityAIZoneTarget.ColonistsWithout:
                {
                    return t is Pawn p && OfColony(p) && !p.Downed && !p.InMentalState && !AbilityAIUtility.Has(p, hediff);
                }
                case AbilityAIZoneTarget.Visitors:
                {
                    if (!(t is Pawn p) || !p.RaceProps.Humanlike || p.Downed || p.InMentalState || p.IsPrisoner || p.IsSlave) return false;
                    Faction f = p.Faction;
                    // CanChangeGoodwillFor: a defeated faction, or goodwill a quest has locked, takes VPE's cast and
                    // nothing changes, so it would be wanted forever
                    if (f == null || f.IsPlayer || p.HostileTo(Faction.OfPlayer) || !f.CanChangeGoodwillFor(Faction.OfPlayer, 1)) return false;
                    return f.PlayerGoodwill < AbilityAISettings.ZoneAllianceBelowGoodwill;
                }
                case AbilityAIZoneTarget.LeechHosts:
                {
                    // A pawn leeched by anyone: VPE's cast takes the old leech off it, so another caster would lose theirs
                    if (!(t is Pawn p) || p.InMentalState || AbilityAIUtility.Has(p, hediff)) return false;
                    if (p.IsPrisonerOfColony) return AbilityAISettings.ZoneLeechPrisoners;
                    return p.IsSlaveOfColony && AbilityAISettings.ZoneLeechSlaves;
                }
                default: return false;
            }
        }

        // The colony's free pawns, and its slaves while the Words setting lets them in
        private static bool OfColony(Pawn p)
        {
            if (p.Faction != Faction.OfPlayer || !p.RaceProps.Humanlike || p.IsPrisoner) return false;
            return !p.IsSlave || AbilityAISettings.ZoneWordsOnSlaves;
        }

        /// <summary>Whether this colonist may cast the rule now, before any target is looked at: the settings that are
        /// about the caster or the map rather than the target.</summary>
        internal static bool CasterMay(AbilityAIExtension rule, Pawn pawn)
        {
            if (rule.sensitivityAbove >= 0f && pawn.GetStatValue(StatDefOf.PsychicSensitivity, true, 250) <= rule.sensitivityAbove) return false;
            // Brain Leech: VPE keeps one leech per caster and a new cast ends the old one
            if (rule.unlessCasterHas != null && AbilityAIUtility.Has(pawn, rule.unlessCasterHas)) return false;
            switch (rule.zoneTarget)
            {
                case AbilityAIZoneTarget.Unfinished:
                    // Each cast ages the caster a year (VPE's AbilityExtension_Age): never past the setting
                    return pawn.ageTracker != null && pawn.ageTracker.AgeBiologicalYearsFloat + 1f <= AbilityAISettings.ZoneTimeskipMaxAge;
                case AbilityAIZoneTarget.HumanlikeCorpses:
                    return !AbilityAISettings.ZoneEnthrallOnlyWithEnemies || GenHostility.AnyHostileActiveThreatToPlayer(pawn.Map);
                default: return true;
            }
        }

        /// <summary>The tooltip line for an ability with a zone rule, or null.</summary>
        internal static string Hint(Def abilityDef, Pawn pawn)
        {
            var rule = RuleOf(abilityDef);
            if (rule == null) return null;
            string hint = ("AAI_Zone_Hint_" + rule.zoneTarget).Translate();
            string blocked = pawn == null ? null : CasterBlockedReason(rule, pawn);
            return blocked == null ? hint : hint + " " + blocked;
        }

        // The lasting reasons CasterMay turns this pawn away, said in the tooltip: without it a colonist at 100%
        // sensitivity with Mend on autocast just never mends, with nothing to say why. Enthrall's wait for enemies
        // passes by itself, so it isn't one.
        private static string CasterBlockedReason(AbilityAIExtension rule, Pawn pawn)
        {
            if (rule.sensitivityAbove >= 0f)
            {
                float sensitivity = pawn.GetStatValue(StatDefOf.PsychicSensitivity, true, 250);
                if (sensitivity <= rule.sensitivityAbove)
                    return "AAI_Zone_NeedsSensitivity".Translate(rule.sensitivityAbove.ToStringPercent(), sensitivity.ToStringPercent());
            }
            if (rule.zoneTarget == AbilityAIZoneTarget.Unfinished && pawn.ageTracker != null
                && pawn.ageTracker.AgeBiologicalYearsFloat + 1f > AbilityAISettings.ZoneTimeskipMaxAge)
                return "AAI_Zone_TooOld".Translate(AbilityAISettings.ZoneTimeskipMaxAge);
            return null;
        }

        /// <summary>The psycast's own icon: vanilla's AbilityDef.uiIcon, else another system's public Texture2D field
        /// "icon" (VEF's).</summary>
        internal static Texture2D IconOf(Def def)
        {
            if (!icons.TryGetValue(def, out Texture2D tex))
            {
                tex = def is AbilityDef ad ? ad.uiIcon : def.GetType().GetField("icon")?.GetValue(def) as Texture2D;
                if (tex == null) tex = BaseContent.BadTex;
                icons[def] = tex;
            }
            return tex;
        }

        /// <summary>A colour of its own per psycast, so two zones drawn one after the other don't look the same.</summary>
        internal static Color ColorOf(Def def)
        {
            if (!colors.TryGetValue(def, out Color c))
            {
                float hue = (def.shortHash % 360) / 360f;
                c = Color.HSVToRGB(hue, 0.65f, 0.95f);
                colors[def] = c;
            }
            return c;
        }
    }

    /// <summary>One psycast's zone on one map. Only the cells are saved.</summary>
    public class PsycastZone : IExposable, ICellBoolGiver
    {
        public string defName;
        private BoolGrid grid;

        internal Map map;
        private CellBoolDrawer drawer;
        private readonly List<IntVec3> cells = new List<IntVec3>();
        private bool cellsDirty = true;
        private Def def;

        public PsycastZone() { }

        internal PsycastZone(Map map, Def def)
        {
            this.map = map;
            this.def = def;
            defName = def.defName;
            grid = new BoolGrid(map);
        }

        internal Def Def => def ?? (def = AbilityAIZones.DefNamed(defName));

        internal bool this[IntVec3 c] => grid[c];

        internal int CellCount => grid?.TrueCount ?? 0;

        /// <summary>Set by a designator; the zone's targets are found again on the next look.</summary>
        internal void Set(IntVec3 c, bool on)
        {
            if (grid[c] == on) return;
            grid[c] = on;
            cellsDirty = true;
            drawer?.SetDirty();
            PsycastZones.Of(map)?.Invalidate(Def);
        }

        internal List<IntVec3> Cells
        {
            get
            {
                if (cellsDirty)
                {
                    cells.Clear();
                    foreach (IntVec3 c in grid.ActiveCells) cells.Add(c);
                    cellsDirty = false;
                }
                return cells;
            }
        }

        public Color Color => Def != null ? AbilityAIZones.ColorOf(Def) : Color.white;

        public bool GetCellBool(int index) => grid[index];

        public Color GetCellExtraColor(int index) => Color.white;

        // Areas' render queue, so the zone draws over the ground as vanilla areas do
        internal void MarkForDraw()
        {
            if (drawer == null) drawer = new CellBoolDrawer(this, map.Size.x, map.Size.z, 3650);
            drawer.MarkForDraw();
        }

        internal void Update() => drawer?.CellBoolDrawerUpdate();

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Deep.Look(ref grid, "grid");
        }
    }

    /// <summary>The psycast zones of one map, the targets each one holds, and who is on the way to which target.
    /// Targets are found again at most every 250 ticks per psycast, and only when a colonist looks for work.</summary>
    public class PsycastZones : MapComponent
    {
        internal const int TargetsRefreshTicks = 250;

        private List<PsycastZone> zones = new List<PsycastZone>();

        private sealed class Targets
        {
            public readonly List<Thing> things = new List<Thing>();
            public int builtTick = -TargetsRefreshTicks;
        }

        private struct TargetClaim
        {
            public Pawn pawn;
            public int jobId;
        }

        // Not saved: a walk or cast under way is started again after a load, and the lists are found again
        private readonly Dictionary<Def, Targets> targets = new Dictionary<Def, Targets>();
        private readonly Dictionary<Thing, TargetClaim> claims = new Dictionary<Thing, TargetClaim>();
        private readonly Dictionary<(Pawn, Thing), int> retryAfter = new Dictionary<(Pawn, Thing), int>();
        private readonly Dictionary<(Pawn, Def), int> zoneRetryAfter = new Dictionary<(Pawn, Def), int>();
        private static readonly HashSet<Thing> seen = new HashSet<Thing>();
        private static readonly List<Thing> expired = new List<Thing>();
        private static readonly List<(Pawn, Thing)> expiredWaits = new List<(Pawn, Thing)>();

        public PsycastZones(Map map) : base(map) { }

        internal static PsycastZones Of(Map map) => map?.GetComponent<PsycastZones>();

        internal PsycastZone ZoneFor(Def def, bool create)
        {
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].defName == def.defName) return zones[i];
            if (!create) return null;
            var zone = new PsycastZone(map, def);
            zones.Add(zone);
            return zone;
        }

        internal bool HasCells(Def def) => ZoneFor(def, false)?.CellCount > 0;

        internal void Invalidate(Def def)
        {
            if (def != null && targets.TryGetValue(def, out var t)) t.builtTick = -TargetsRefreshTicks;
        }

        /// <summary>What the rule casts at on this map now, at most TargetsRefreshTicks old. Things in the zone that
        /// Wants accepts, or for a rule without a zone the pawns it is for.</summary>
        internal List<Thing> TargetsFor(Def def, AbilityAIExtension rule, int tick)
        {
            if (!targets.TryGetValue(def, out var t)) targets[def] = t = new Targets();
            if (tick - t.builtTick < TargetsRefreshTicks) return t.things;
            t.builtTick = tick;
            t.things.Clear();
            AbilityAIZoneTarget kind = rule.zoneTarget;
            if (AbilityAIZones.TargetsPawns(kind))
            {
                switch (kind)
                {
                    case AbilityAIZoneTarget.Prisoners:
                        AddWanted(t.things, rule, map.mapPawns.PrisonersOfColonySpawned);
                        break;
                    case AbilityAIZoneTarget.LeechHosts:
                        AddWanted(t.things, rule, map.mapPawns.PrisonersOfColonySpawned);
                        AddWanted(t.things, rule, map.mapPawns.SlavesOfColonySpawned);
                        break;
                    case AbilityAIZoneTarget.Visitors:
                        AddWanted(t.things, rule, map.mapPawns.AllHumanlikeSpawned);
                        break;
                    default:
                        // FreeColonistsSpawned already holds the slaves (their host faction is null); OfColony applies
                        // the slaves setting
                        AddWanted(t.things, rule, map.mapPawns.FreeColonistsSpawned);
                        break;
                }
                return t.things;
            }
            var zone = ZoneFor(def, false);
            if (zone == null || zone.CellCount == 0) return t.things;
            var cells = zone.Cells;
            try
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    var list = map.thingGrid.ThingsListAtFast(cells[i]);
                    for (int j = 0; j < list.Count; j++)
                    {
                        Thing thing = list[j];
                        // A building larger than a cell is listed on each of its cells
                        if ((thing.def.size.x > 1 || thing.def.size.z > 1) && !seen.Add(thing)) continue;
                        if (AbilityAIZones.Wants(rule, thing)) t.things.Add(thing);
                    }
                }
            }
            finally { seen.Clear(); }
            return t.things;
        }

        private static void AddWanted(List<Thing> into, AbilityAIExtension rule, IReadOnlyList<Pawn> pawns)
        {
            for (int i = 0; i < pawns.Count; i++)
                if (AbilityAIZones.Wants(rule, pawns[i])) into.Add(pawns[i]);
        }

        /// <summary>Some rule has something to cast at on this map. JobGiver_Work asks this (through
        /// WorkGiver_PsycastZone.ShouldSkip) for every colonist with an autocast mode that looks for work.</summary>
        internal bool AnyDue(int tick)
        {
            var defs = AbilityAIZones.RuleDefs;
            for (int i = 0; i < defs.Count; i++)
            {
                var rule = AbilityAIZones.RuleOf(defs[i]);
                if (rule == null) continue;
                if (AbilityAIZones.NeedsZone(rule.zoneTarget) && !HasCells(defs[i])) continue;
                int need = rule.zoneTarget == AbilityAIZoneTarget.ConsumableCorpses ? AbilityAISettings.ZoneConsumeMinCorpses : 1;
                if (TargetsFor(defs[i], rule, tick).Count >= need) return true;
            }
            return false;
        }

        private static bool Holds(TargetClaim c) => AbilityAIZoneWork.JobPending(c.pawn, c.jobId);

        internal bool ClaimedByOther(Thing thing, Pawn pawn)
        {
            if (!claims.TryGetValue(thing, out var c)) return false;
            if (Holds(c)) return c.pawn != pawn;
            claims.Remove(thing);
            return false;
        }

        internal void Claim(Thing thing, Pawn pawn, int jobId)
        {
            claims[thing] = new TargetClaim { pawn = pawn, jobId = jobId };
            if (claims.Count > 64) PruneClaims();
        }

        /// <summary>A zone walk's claim on its target: its own, or the target's when nobody else holds it (a walk loaded
        /// from a save, whose claim wasn't saved), which it then takes.</summary>
        internal bool ClaimFor(Pawn pawn, Thing thing, int jobId)
        {
            if (thing == null) return false;
            if (claims.TryGetValue(thing, out var c) && Holds(c)) return c.pawn == pawn;
            Claim(thing, pawn, jobId);
            return true;
        }

        private void PruneClaims()
        {
            foreach (var kv in claims)
                if (!Holds(kv.Value)) expired.Add(kv.Key);
            for (int i = 0; i < expired.Count; i++) claims.Remove(expired[i]);
            expired.Clear();
        }

        // A colonist that found no place to cast an area psycast (Consume Bodies) leaves the whole zone for a while,
        // since finding one looks at every corpse in it
        internal bool WaitingZone(Pawn pawn, Def def, int tick) => zoneRetryAfter.TryGetValue((pawn, def), out int t) && tick < t;

        internal void WaitZone(Pawn pawn, Def def, int untilTick)
        {
            if (zoneRetryAfter.Count > 64) zoneRetryAfter.Clear();
            zoneRetryAfter[(pawn, def)] = untilTick;
        }

        // A colonist that found no way to cast at a target leaves it for a while instead of trying again at every work
        // check
        internal bool Waiting(Pawn pawn, Thing thing, int tick) => retryAfter.TryGetValue((pawn, thing), out int t) && tick < t;

        internal void Wait(Pawn pawn, Thing thing, int untilTick)
        {
            if (retryAfter.Count > 256)
            {
                int tick = Find.TickManager.TicksGame;
                foreach (var kv in retryAfter)
                    if (kv.Value <= tick) expiredWaits.Add(kv.Key);
                for (int i = 0; i < expiredWaits.Count; i++) retryAfter.Remove(expiredWaits[i]);
                expiredWaits.Clear();
            }
            retryAfter[(pawn, thing)] = untilTick;
        }

        public override void MapComponentUpdate()
        {
            for (int i = 0; i < zones.Count; i++) zones[i].Update();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) zones.RemoveAll(z => z.CellCount == 0);
            Scribe_Collections.Look(ref zones, "zones", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (zones == null) zones = new List<PsycastZone>();
                zones.RemoveAll(z => z == null || z.defName.NullOrEmpty() || z.CellCount == 0);
                // A zone whose psycast has gone (its mod removed) is kept, unused, for when it comes back
                for (int i = 0; i < zones.Count; i++) zones[i].map = map;
            }
        }
    }

    /// <summary>Paints a psycast's zone, or rubs it out, by dragging as vanilla's area designators do. Opened from the
    /// psycast's autocast menu; the zone is drawn in its own colour while either one is selected.</summary>
    public class Designator_PsycastZone : Designator_Cells
    {
        private static readonly Color ShrinkIconColor = new Color(1f, 0.6f, 0.6f);

        private readonly Def castDef;
        private readonly bool expand;

        public Designator_PsycastZone(Def castDef, bool expand)
        {
            this.castDef = castDef;
            this.expand = expand;
            defaultLabel = (expand ? "AAI_ZoneExpand" : "AAI_ZoneShrink").Translate(castDef.label);
            defaultDesc = (expand ? "AAI_ZoneExpand_Desc" : "AAI_ZoneShrink_Desc").Translate(castDef.label);
            icon = AbilityAIZones.IconOf(castDef);
            if (!expand) defaultIconColor = ShrinkIconColor;
            useMouseIcon = true;
            soundDragSustain = expand ? SoundDefOf.Designate_DragAreaAdd : SoundDefOf.Designate_DragAreaDelete;
            soundDragChanged = expand ? SoundDefOf.Designate_DragZone_Changed : null;
            soundSucceeded = expand ? SoundDefOf.Designate_ZoneAdd : SoundDefOf.Designate_ZoneDelete;
        }

        public override bool DragDrawMeasurements => true;

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Areas;

        public override bool CanRemainSelected() => PsycastZones.Of(Map) != null;

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map)) return false;
            var zone = PsycastZones.Of(Map)?.ZoneFor(castDef, false);
            return expand ? zone == null || !zone[c] : zone != null && zone[c];
        }

        public override void DesignateSingleCell(IntVec3 c) => PsycastZones.Of(Map)?.ZoneFor(castDef, true).Set(c, expand);

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
            if (Find.WindowStack.FloatMenu == null) PsycastZones.Of(Map)?.ZoneFor(castDef, false)?.MarkForDraw();
        }
    }
}
