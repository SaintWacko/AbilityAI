using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Workers decide WHETHER an ability is worth casting now and AT WHAT. Each is a category, not an ability: an
// AbilityDef picks one with AbilityAIExtension.workerClass and tunes it with the extension's fields. Workers are
// shared between pawns and must keep no per-pawn state. A host mod's own comp states its area, hediff and stun
// through AbilityAIInfo (AbilityAIInfo.cs).
//
// Cost: a worker only runs for a pawn that holds an ability with an extension, that ability is ready (CanCast,
// reserve, gap) and a host hook claims it (or, for the player's pawns, the player set it to autocast; undrafted ones
// only try the workers their situation allows: AbilityAIDriver.PlayerSet), at most once per 30 ticks per pawn and then not again for the
// global gap after a cast. Each worker notes its own cost below; k = hostile pawns on the map.
namespace AbilityAI
{
    public abstract class AbilityAIWorker
    {
        public AbilityAIExtension ext;

        protected virtual AbilityAICombat DefaultCombat => AbilityAICombat.InCombat;

        public AbilityAICombat Combat => ext.combat == AbilityAICombat.Default ? DefaultCombat : ext.combat;

        /// <summary>Whether the player's pawns may autocast it while undrafted: any worker that isn't combat-only
        /// (Recovery, Rescue, Calm, Firefighting by default). A def can opt a combat worker in with combat Any.</summary>
        public bool WorksUndrafted => Combat != AbilityAICombat.InCombat;

        /// <summary>Whether an undrafted player pawn that is fleeing may autocast it: the undrafted set plus workers
        /// that help a pawn get away or keep going (Movement, Recovery).</summary>
        public bool WorksFleeing => WorksUndrafted || ForFleeing;

        /// <summary>True for workers that are worth using while running away. A fleeing pawn may not count as in
        /// combat (nobody has hit it yet), so these also skip the combat-mode check while it flees.</summary>
        protected internal virtual bool ForFleeing => false;

        /// <summary>An area cast at the caster whose affected pawns this worker counts itself: the ability's comps
        /// are then not asked about the caster (VanillaAIAbility.ValidIgnoringRange).</summary>
        protected internal virtual bool CountsAffectedAroundCaster => false;

        /// <summary>The worker casts at enemies. Read by ability systems that can't tell from the def whether an
        /// ability is an attack (VEF's, when isPositive isn't set), which is what the Interrupt worker asks of an
        /// enemy's running cast.</summary>
        public virtual bool TargetsEnemies => false;

        protected virtual bool DefaultExclusive => !TargetsEnemies;

        /// <summary>See AbilityAIExtension.exclusive.</summary>
        public bool Exclusive => ext.exclusive ?? DefaultExclusive;

        /// <summary>The driver tries this worker from the constant think tree. False for one whose casting happens
        /// elsewhere (Recast, as Basic work): the rule still counts for the autocast menu and the stand-down.</summary>
        public virtual bool DriverCasts => true;

        /// <summary>Whether this rule would cast at p now, wherever p stands: its own target tests short of range, the
        /// ability's own checks left to the caller. What a group link may carry a Word of psycast to
        /// (AbilityAIGroupLink). ctx is null for a Zone rule, which is asked outside an evaluation. False by default:
        /// a Word whose worker doesn't answer is never linked first, and through a link only to pawns its linkSafe
        /// allows.</summary>
        protected internal virtual bool IsLinkTarget(AbilityAIContext ctx, AIAbility ability, Pawn p) => false;

        /// <summary>A target worth casting at now, or false. The driver runs AbilityAIDriver.Valid on the result
        /// once more, so a worker only needs to call it where it is choosing between candidates.</summary>
        public abstract bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest);

        /// <summary>Only asked when TryFindTarget found nothing for any ability and the extension sets
        /// approachDistance: the thing the pawn should walk toward so that it could cast, looking as far as reach
        /// (range + approachDistance). Targets are checked with AbilityAIDriver.ValidIgnoringRange, since range and
        /// line of sight are what the walk is for. A targeted ability casts at the goal from a cast position the
        /// driver finds; a self-cast one walks to the goal.</summary>
        public virtual bool TryFindApproachTarget(AbilityAIContext ctx, AIAbility ability, float reach, out Thing goal)
        {
            goal = null;
            return false;
        }

        /// <summary>True for a worker that picks its own approach cell (TryFindApproachCell) for self-cast
        /// abilities; the driver then never asks TryFindApproachTarget for them.</summary>
        public virtual bool UsesApproachCell => false;

        /// <summary>Self-cast abilities, only when UsesApproachCell: a cell to walk to and stand on, which the worker
        /// must have checked the pawn can reach, and how many targets the cast would reach from there. While the walk
        /// lasts, ctx.ApproachExpected(ability) returns that number, so the worker can wait for the spot it chose
        /// instead of casting at the first cell that meets minTargets. False = retried in 2 s.</summary>
        public virtual bool TryFindApproachCell(AbilityAIContext ctx, AIAbility ability, out IntVec3 cell, out int expected)
        {
            cell = IntVec3.Invalid;
            expected = 0;
            return false;
        }
    }

    /// <summary>An area an effect reaches, for AbilityAIUtility.AllyIn and FriendlyBuildingIn. Implemented by structs
    /// and taken as a generic argument, so each shape gets its own compiled loop with no allocation and no virtual
    /// call. Bounds holds every cell Contains accepts.</summary>
    public interface IAIArea
    {
        bool Contains(IntVec3 c);
        CellRect Bounds { get; }
    }

    /// <summary>The cells within sqrt(r2) of a centre, as GenRadial counts them.</summary>
    public readonly struct AIDisc : IAIArea
    {
        private readonly IntVec3 centre;
        private readonly float r2;

        public AIDisc(IntVec3 centre, float r2)
        {
            this.centre = centre;
            this.r2 = r2;
        }

        public bool Contains(IntVec3 c) => (c - centre).LengthHorizontalSquared <= r2;
        public CellRect Bounds => CellRect.CenteredOn(centre, Mathf.CeilToInt(Mathf.Sqrt(r2)));
    }

    /// <summary>The cells within a distance of the segment from a to b: a line with round ends, which covers behind
    /// the start and around the end (a dash that burns what is near any point of its way).</summary>
    public readonly struct AICapsule : IAIArea
    {
        private readonly IntVec3 a, b;
        private readonly float dx, dz, lenSq, r, r2;

        public AICapsule(IntVec3 a, IntVec3 b, float r)
        {
            this.a = a;
            this.b = b;
            dx = b.x - a.x;
            dz = b.z - a.z;
            lenSq = dx * dx + dz * dz;
            this.r = r;
            r2 = r * r;
        }

        public bool Contains(IntVec3 c)
        {
            float t = lenSq > 0f ? Mathf.Clamp01(((c.x - a.x) * dx + (c.z - a.z) * dz) / lenSq) : 0f;
            float ex = a.x + t * dx - c.x, ez = a.z + t * dz - c.z;
            return ex * ex + ez * ez <= r2;
        }

        public CellRect Bounds
        {
            get
            {
                int pad = Mathf.CeilToInt(r);
                return CellRect.FromLimits(Mathf.Min(a.x, b.x) - pad, Mathf.Min(a.z, b.z) - pad, Mathf.Max(a.x, b.x) + pad, Mathf.Max(a.z, b.z) + pad);
            }
        }
    }

    public static class AbilityAIUtility
    {
        /// <summary>The extension's radius, else the first effect comp that states one, else the ability's own
        /// Ability_EffectRadius stat (the area psycasts: blinding pulse, berserk pulse).</summary>
        public static float Radius(AIAbility ability, AbilityAIExtension ext)
        {
            if (ext.radius >= 0f) return ext.radius;
            var e = ability.Effects;
            return e.hasEffectRadius ? e.effectRadius : ability.DefaultRadius;
        }

        /// <summary>The extension's hediff, else the first effect that gives one.</summary>
        public static HediffDef Hediff(AIAbility ability, AbilityAIExtension ext) => ext.hediff ?? ability.Effects.anyHediff;

        /// <summary>How far the ability reaches: its own range, capped by the extension's maxRange.</summary>
        public static float Range(AIAbility ability, AbilityAIExtension ext)
        {
            float r = ability.Range;
            return ext.maxRange >= 0f ? Mathf.Min(r, ext.maxRange) : r;
        }

        public static bool IsSelfCast(AIAbility ability) => ability.IsSelfCast;

        public static bool CanTargetSelf(AIAbility ability) => ability.CanTargetSelf;

        /// <summary>Forget what was read from the comps: AbilityAIInfo.Register calls it, since a new reader can
        /// change the answer.</summary>
        internal static void ClearEffectsCache() => VanillaAIAbility.ClearEffectsCache();

        /// <summary>Whether casting this at a hostile pawn would do anything: a psycast can't touch a pawn without
        /// psychic sensitivity (or a mech, for some), and with skipAffected a pawn already stunned by a stunning
        /// ability, already carrying its hediff, or already in a mental state from a mental-state one is skipped.
        /// Cost: a few field reads, plus a cached stat read for psycasts.</summary>
        public static bool WouldAffect(AIAbility ability, AbilityAIExtension ext, Pawn p)
        {
            if (!Fits(ext, p)) return false;
            var e = ability.Effects;
            // Psycast.CanApplyPsycastTo, without the uncached stat read; plain Ability copies of psycasts skip it
            // in vanilla too
            if (e.psychic && ability.IsPsycast)
            {
                if (p.GetStatValue(StatDefOf.PsychicSensitivity, true, 250) < float.Epsilon) return false;
                if (e.notOnMechs && p.Faction != null && p.Faction == Faction.OfMechanoids) return false;
            }
            if (!ext.skipAffected) return true;
            if (e.stuns && p.stances?.stunner != null && p.stances.stunner.Stunned) return false;
            HediffDef h = ext.hediff ?? e.targetHediff;
            if (h != null && Has(p, h)) return false;
            if (e.mentalState && p.InMentalState) return false;
            return true;
        }

        /// <summary>WouldAffect for a pawn; a turret by targetKinds (Turret named), and with skipAffected not one the
        /// effect is already on (AIAbilityEffects.thingAffected: a turret already gone haywire).</summary>
        public static bool WouldAffect(AIAbility ability, AbilityAIExtension ext, Thing t)
        {
            if (t is Pawn p) return WouldAffect(ability, ext, p);
            if (!Fits(ext, t)) return false;
            var affected = ability.Effects.thingAffected;
            return !ext.skipAffected || affected == null || !affected(t);
        }

        /// <summary>A ranged enemy (mechs and animals by their own verbs) aiming at this pawn (AimTarget).</summary>
        public static bool AimsAt(Pawn e, Pawn pawn) => AbilityAIContext.FightsAtRange(e) && AimTarget(e) == pawn;

        /// <summary>Who a pawn is attacking: its fight target, else the target of an attack job, else what its stance is
        /// aimed at (a shot's warmup or cooldown, or an ability's). Other jobs (kidnapping, stripping) don't count.</summary>
        public static Thing AimTarget(Pawn e)
        {
            Thing t = e.mindState?.enemyTarget;
            if (t != null) return t;
            var job = e.CurJob;
            if (job != null && (job.def == JobDefOf.AttackStatic || job.def == JobDefOf.AttackMelee)) return job.targetA.Thing;
            // A drafted colonist firing on its own (JobDriver_Wait's auto-attack) sets neither: only its stance knows.
            // Without this no NPC ever counted a drafted colonist as aiming at it (run 15's tunneler never smoked).
            return e.TargetCurrentlyAimingAt.Thing;
        }

        /// <summary>Standing next to the caster or anyone on its side (Friendlies, and allies in a hostile break), so
        /// presumably fighting them in melee.</summary>
        public static bool InMeleeWithSide(AbilityAIContext ctx, Pawn e)
        {
            var side = ctx.Friendlies;
            for (int i = 0; i < side.Count; i++)
                if (side[i].Position.AdjacentTo8WayOrInside(e.Position)) return true;
            var broken = ctx.BrokenAllies;
            for (int i = 0; i < broken.Count; i++)
                if (broken[i] != e && broken[i].Position.AdjacentTo8WayOrInside(e.Position)) return true;
            return false;
        }

        /// <summary>Whether a hostile worker using this extension may target an ally in a hostile mental break
        /// (AbilityAIContext.IsBrokenAlly), by the BrokenAllies setting.</summary>
        public static bool MayHitBrokenAllies(AbilityAIExtension ext)
        {
            switch (AbilityAISettings.BrokenAllies)
            {
                case AbilityAIBrokenAllies.Any: return true;
                case AbilityAIBrokenAllies.HarmlessOnly: return ext.harmless;
                default: return false;
            }
        }

        public static bool Has(Pawn pawn, HediffDef hediff) => hediff != null && pawn.health?.hediffSet?.HasHediff(hediff) == true;

        public static AbilityAITargetKinds KindOf(Pawn p)
        {
            var race = p.RaceProps;
            if (race.Humanlike) return AbilityAITargetKinds.Humanlike;
            if (race.IsMechanoid) return AbilityAITargetKinds.Mechanoid;
            if (race.Animal) return AbilityAITargetKinds.Animal;
            return AbilityAITargetKinds.Other;
        }

        /// <summary>The rule's targetKinds lets it target or count this pawn. One field read for a rule open to all.</summary>
        public static bool Fits(AbilityAIExtension ext, Pawn p) =>
            ext.targetKinds == AbilityAITargetKinds.All || IsOfKind(ext.targetKinds, p);

        public static bool IsOfKind(AbilityAITargetKinds kinds, Pawn p) =>
            (kinds & KindOf(p)) != 0 || ((kinds & AbilityAITargetKinds.Shielded) != 0 && ShieldUp(p))
            || ((kinds & AbilityAITargetKinds.Psycaster) != 0 && p.HasPsylink);

        /// <summary>A vanilla CompShield that is up now: worn (a shield belt) or the pawn's own (Biotech heavy mechs).</summary>
        public static bool ShieldUp(Pawn p)
        {
            var own = p.GetComp<CompShield>();
            if (own != null && own.ShieldState == ShieldState.Active) return true;
            var worn = p.apparel?.WornApparel;
            if (worn == null) return false;
            for (int i = 0; i < worn.Count; i++)
            {
                var shield = worn[i].GetComp<CompShield>();
                if (shield != null && shield.ShieldState == ShieldState.Active) return true;
            }
            return false;
        }

        /// <summary>auraKinds: enough enemies of those kinds (and turrets, named) within the radius of the bearer,
        /// and with avoidAllies none of the caster's side of allyKinds but the bearer. True for a rule without
        /// auraKinds. Cost: a distance check per enemy, the turret list only for Turret.</summary>
        public static bool AuraWorthIt(AbilityAIContext ctx, AIAbility ability, AbilityAIExtension ext, Pawn bearer)
        {
            var kinds = ext.auraKinds;
            if (kinds == AbilityAITargetKinds.None) return true;
            float r = Radius(ability, ext);
            float r2 = r * r;
            IntVec3 c = bearer.Position;
            bool spareBroken = !MayHitBrokenAllies(ext);
            int count = 0;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count && count < ext.auraMin; i++)
            {
                Pawn e = enemies[i];
                if ((e.Position - c).LengthHorizontalSquared > r2 || (spareBroken && ctx.IsBrokenAlly(e))) continue;
                if (IsOfKind(kinds, e)) count++;
            }
            if (count < ext.auraMin && (kinds & AbilityAITargetKinds.Turret) != 0)
            {
                var turrets = ctx.EnemyTurrets;
                for (int i = 0; i < turrets.Count && count < ext.auraMin; i++)
                    if ((turrets[i].Position - c).LengthHorizontalSquared <= r2) count++;
            }
            if (count < ext.auraMin) return false;
            return !ext.avoidAllies || !AllyWithin(ctx, ext, c, r2, spareBroken, bearer);
        }

        public static bool Fits(AbilityAIExtension ext, Thing t)
        {
            if (t is Pawn p) return Fits(ext, p);
            return t is Building_Turret && (ext.targetKinds & AbilityAITargetKinds.Turret) != 0;
        }

        public static bool WantsTurrets(AbilityAIExtension ext) => (ext.targetKinds & AbilityAITargetKinds.Turret) != 0;

        /// <summary>What a hostile area rule may hit: ctx.Enemies, plus ctx.EnemyTurrets when it names Turret.</summary>
        public static void CollectHostiles(AbilityAIContext ctx, AbilityAIExtension ext, List<Thing> into)
        {
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++) into.Add(enemies[i]);
            if (WantsTurrets(ext)) into.AddRange(ctx.EnemyTurrets);
        }

        /// <summary>CollectHostiles, only those within reach of the caster. An area scorer whose centres lie within
        /// range and whose hits lie within radius of a centre passes range + radius: anyone further can never count, and
        /// leaving them out spares their WouldAffect and their row of the k² pass (in a 250-pawn fight spread over the
        /// map, most of the enemy list).</summary>
        public static void CollectHostiles(AbilityAIContext ctx, AbilityAIExtension ext, List<Thing> into, float reach)
        {
            IntVec3 from = ctx.pawn.Position;
            float reachSq = reach * reach;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
                if ((enemies[i].Position - from).LengthHorizontalSquared <= reachSq) into.Add(enemies[i]);
            if (!WantsTurrets(ext)) return;
            var turrets = ctx.EnemyTurrets;
            for (int i = 0; i < turrets.Count; i++)
                if ((turrets[i].Position - from).LengthHorizontalSquared <= reachSq) into.Add(turrets[i]);
        }

        /// <summary>avoidAllies within r2 of c (AllyIn on a disc). The caster counts unless skipCaster (an area around
        /// the caster).</summary>
        public static bool AllyWithin(AbilityAIContext ctx, AbilityAIExtension ext, IntVec3 c, float r2, bool spareBroken, bool skipCaster) =>
            AllyIn(ctx, ext, new AIDisc(c, r2), spareBroken, skipCaster ? ctx.pawn : null);

        /// <summary>AllyWithin, leaving out one pawn (an aura's bearer, which its own aura spares).</summary>
        public static bool AllyWithin(AbilityAIContext ctx, AbilityAIExtension ext, IntVec3 c, float r2, bool spareBroken, Pawn skip) =>
            AllyIn(ctx, ext, new AIDisc(c, r2), spareBroken, skip);

        /// <summary>The one list of who an attack keeps clear of: anyone of the caster's side inside the area, of the
        /// kinds the rule's allyKinds names: Friendlies, the allies in a hostile break when they are spared, the
        /// caster's downed faction-mates for a rule that isn't harmless (a blast, a fire or an outright kill reaches a
        /// pawn lying down, where a stun would change nothing), and with Turret friendly turrets. skip: a pawn left out
        /// (the caster, or an aura's bearer). Cost: a shape test per pawn, one KindOf more for a rule with allyKinds
        /// set; the turret list only for Turret.</summary>
        public static bool AllyIn<T>(AbilityAIContext ctx, AbilityAIExtension ext, T area, bool spareBroken, Pawn skip) where T : struct, IAIArea
        {
            var kinds = ext.allyKinds;
            if (AnyOfKindIn(ctx.Friendlies, kinds, area, skip)) return true;
            if (spareBroken && AnyOfKindIn(ctx.BrokenAllies, kinds, area, skip)) return true;
            if (!ext.harmless && AnyOfKindIn(ctx.DownedAllies, kinds, area, skip)) return true;
            if ((kinds & AbilityAITargetKinds.Turret) == 0) return false;
            var turrets = ctx.FriendlyTurrets;
            for (int i = 0; i < turrets.Count; i++)
                if (area.Contains(turrets[i].Position)) return true;
            return false;
        }

        private static bool AnyOfKindIn<T>(List<Pawn> pawns, AbilityAITargetKinds kinds, T area, Pawn skip) where T : struct, IAIArea
        {
            bool all = (kinds & AbilityAITargetKinds.All) == AbilityAITargetKinds.All;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == skip || !area.Contains(p.Position)) continue;
                if (all || IsOfKind(kinds, p)) return true;
            }
            return false;
        }

        /// <summary>A building on this cell that the caster's side would not want burnt or broken: its own faction's,
        /// or a faction's not hostile to it.</summary>
        public static bool FriendlyBuildingAt(AbilityAIContext ctx, IntVec3 c, Thing except = null)
        {
            var things = c.GetThingList(ctx.map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t != except && t.def.category == ThingCategory.Building && t.Faction != null && !t.HostileTo(ctx.pawn)) return true;
            }
            return false;
        }

        /// <summary>FriendlyBuildingIn on a disc. except: a building that doesn't count (the anchor the area is centred
        /// on).</summary>
        public static bool FriendlyBuildingWithin(AbilityAIContext ctx, IntVec3 centre, float radius, Thing except = null) =>
            FriendlyBuildingIn(ctx, new AIDisc(centre, radius * radius), except);

        /// <summary>coveredBy: at least coveredFraction of the cells within radius of centre (on the map) already hold
        /// one of those defs, are walls, or, when a listed def is a plant, have no fertility, a tree or an edifice. Stops as soon as the
        /// answer is known either way. Cost: up to one thing-list read per cell (113 for radius 6), only for rules
        /// that set coveredBy, and only for the few centres the worker tries. A faction-mate's cast of the same ability
        /// close enough that its area would cover coveredFraction of this one counts as covering it
        /// (AbilityAIDriver.MateCastingNear), checked first: the claims list is short and built once per evaluation.</summary>
        public static bool Covered(AbilityAIContext ctx, AIAbility ability, AbilityAIExtension ext, IntVec3 centre, float radius)
        {
            var defs = ext.coveredBy;
            if (defs == null || defs.Count == 0) return false;
            if (AbilityAIDriver.MateCastingNear(ability.Def, centre, radius * ext.CoverDistanceFactor)) return true;
            bool plant = false;
            for (int i = 0; i < defs.Count; i++)
                if (defs[i].plant != null) { plant = true; break; }
            Map map = ctx.map;
            int cells = GenRadial.NumCellsInRadius(Mathf.Min(radius, GenRadial.MaxRadialPatternRadius - 1f));
            int inMap = 0;
            for (int i = 0; i < cells; i++)
                if ((centre + GenRadial.RadialPattern[i]).InBounds(map)) inMap++;
            if (inMap == 0) return false;
            int need = Mathf.CeilToInt(inMap * ext.coveredFraction), covered = 0, left = inMap;
            for (int i = 0; i < cells; i++)
            {
                IntVec3 c = centre + GenRadial.RadialPattern[i];
                if (!c.InBounds(map)) continue;
                left--;
                if (CellCovered(map, c, defs, plant) && ++covered >= need) return true;
                if (covered + left < need) return false;
            }
            return false;
        }

        private static bool CellCovered(Map map, IntVec3 c, List<ThingDef> defs, bool plant)
        {
            if (c.Filled(map)) return true;
            // VPE's PlantSpawner.CheckSpawnLoc refuses these cells, so a plant psycast adds nothing there
            if (plant && (c.GetTerrain(map).fertility <= 0f || c.GetEdifice(map) != null)) return true;
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                ThingDef def = things[i].def;
                if (defs.Contains(def) || (plant && def.plant != null && def.plant.IsTree)) return true;
            }
            return false;
        }

        /// <summary>FriendlyBuildingAt on any cell of the area. Only the cells of its bounding box are visited, so a long
        /// thin line doesn't scan the disc of its length. Cost: a shape test per cell of the box, one thing-list read
        /// per cell inside (about 300 for a disc of radius 10).</summary>
        public static bool FriendlyBuildingIn<T>(AbilityAIContext ctx, T area, Thing except = null) where T : struct, IAIArea
        {
            CellRect box = area.Bounds.ClipInsideMap(ctx.map);
            // CellRect's enumerator still yields cells of an empty rect (minX > maxX), off the map
            if (box.minX > box.maxX || box.minZ > box.maxZ) return false;
            foreach (IntVec3 c in box)
                if (area.Contains(c) && FriendlyBuildingAt(ctx, c, except)) return true;
            return false;
        }

        /// <summary>The pawn (or turret) itself if the ability accepts it, else the cell it stands on.</summary>
        public static LocalTargetInfo PawnOrCell(AIAbility ability, Thing target)
        {
            var pawnTarget = new LocalTargetInfo(target);
            if (AbilityAIDriver.Valid(ability, pawnTarget)) return pawnTarget;
            var cellTarget = new LocalTargetInfo(target.Position);
            return AbilityAIDriver.Valid(ability, cellTarget) ? cellTarget : LocalTargetInfo.Invalid;
        }

        /// <summary>How badly the pawn needs the rule's health conditions treated, 0 = not at all (or the rule names
        /// none). Injured: 1 - summary health, under healthThreshold with an injury still healing; Bleeding: the bleed
        /// rate per day, capped at 1, from minBleedRate; Disease, MissingParts: 0.5. Cost: field reads, plus a pass over
        /// the hediffs for Disease and Bleeding and a cached read for MissingParts.</summary>
        public static float HealthWant(AbilityAIExtension ext, Pawn p)
        {
            var health = ext.health;
            if (health == AbilityAIHealth.None) return 0f;
            var set = p.health?.hediffSet;
            if (set == null) return 0f;
            float want = 0f;
            if ((health & AbilityAIHealth.Injured) != 0)
            {
                float h = p.health.summaryHealth.SummaryHealthPercent;
                if (h < ext.healthThreshold && set.HasNaturallyHealingInjury()) want = 1f - h;
            }
            if ((health & AbilityAIHealth.Bleeding) != 0)
            {
                float bleed = set.BleedRateTotal;
                if (bleed > float.Epsilon && bleed >= ext.minBleedRate) want = Mathf.Max(want, Mathf.Min(bleed, 1f));
            }
            if ((health & AbilityAIHealth.Disease) != 0 && set.HasImmunizableNotImmuneHediff()) want = Mathf.Max(want, 0.5f);
            if ((health & AbilityAIHealth.MissingParts) != 0 && set.GetMissingPartsCommonAncestors().Count > 0) want = Mathf.Max(want, 0.5f);
            return want;
        }

        /// <summary>Centres an anchored rule validates before giving up: more than a pawn-centred rule's five, since
        /// only the ability can tell a usable anchor from a spent one (a recharging runecircle) and those cluster
        /// where the fights are.</summary>
        public const int MaxAnchorTries = 20;

        public static bool Anchored(AbilityAIExtension ext) =>
            (ext.anchorThings != null && ext.anchorThings.Count > 0) || ext.anchorCorpses != AbilityAITargetKinds.None;

        /// <summary>The things an anchored rule may centre on (anchorThings, anchorCorpses), between minRange and
        /// range of the caster. Cost: one lister read per def plus one for corpses, a distance check per thing.</summary>
        public static void CollectAnchors(AbilityAIContext ctx, AbilityAIExtension ext, float range, List<Thing> into)
        {
            var lister = ctx.map.listerThings;
            Faction own = ctx.pawn.Faction;
            IntVec3 from = ctx.pawn.Position;
            float maxSq = range * range, minSq = ext.minRange * ext.minRange;
            var defs = ext.anchorThings;
            if (defs != null)
                for (int d = 0; d < defs.Count; d++)
                {
                    var things = lister.ThingsOfDef(defs[d]);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (t.Faction != null && t.Faction != own) continue;
                        float dist = (t.Position - from).LengthHorizontalSquared;
                        if (dist <= maxSq && dist >= minSq) into.Add(t);
                    }
                }
            if (ext.anchorCorpses == AbilityAITargetKinds.None) return;
            var corpses = lister.ThingsInGroup(ThingRequestGroup.Corpse);
            for (int i = 0; i < corpses.Count; i++)
            {
                if (!(corpses[i] is Corpse c) || c.InnerPawn == null) continue;
                float dist = (c.Position - from).LengthHorizontalSquared;
                if (dist > maxSq || dist < minSq || !IsOfKind(ext.anchorCorpses, c.InnerPawn)) continue;
                Faction f = c.InnerPawn.Faction;
                if (f != null && (own == null || !f.HostileTo(own))) continue;
                into.Add(c);
            }
        }

        /// <summary>Running away: panicking, on a flee job, or leaving the map while fighting.</summary>
        public static bool IsFleeing(AbilityAIContext ctx)
        {
            Pawn pawn = ctx.pawn;
            if (pawn.MentalStateDef == MentalStateDefOf.PanicFlee) return true;
            var job = pawn.CurJob;
            if (job == null) return false;
            if (job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower) return true;
            return job.exitMapOnArrival && ctx.InCombat;
        }

        /// <summary>The caster was hurt in melee in the last 10 seconds by someone still standing next to it.</summary>
        public static bool InMelee(AbilityAIContext ctx) => InMelee(ctx, ctx.pawn);

        /// <summary>Hurt in melee in the last 10 seconds by someone still standing next to it.</summary>
        public static bool InMelee(AbilityAIContext ctx, Pawn p)
        {
            Pawn threat = p.mindState?.meleeThreat;
            if (threat == null || !threat.Spawned || threat.Dead || threat.Downed || threat.Map != ctx.map) return false;
            return ctx.tick - p.mindState.lastMeleeThreatHarmTick < AbilityAIContext.RecentTicks
                && threat.Position.InHorDistOf(p.Position, 1.5f);
        }
    }

    /// <summary>
    /// AoE hostile: an area ability at the spot that catches the most enemies (Flashbang, Artillery, blinding and
    /// berserk pulses). Each enemy in range is a candidate centre, scored by the enemies within the radius of it that
    /// the ability would affect (WouldAffect: a second flashbang skips the group the first one stunned); at least
    /// minTargets, between minRange and the ability's range, no ally inside with avoidAllies. An ally in a hostile
    /// break counts as an ally (never counted, never a centre, avoided with avoidAllies) unless MayHitBrokenAllies. The best five are checked
    /// against the verb (line of sight). Approach: the same scoring out to range + approachDistance, without the verb.
    /// avoidSelf: no centre within the radius of the caster (an area that hits its caster); avoidBuildings: no centre
    /// with a friendly building inside. A rule naming Turret in targetKinds also counts and centres on hostile
    /// turrets (Haywire); allyKinds narrows who avoidAllies keeps clear of (AbilityAIUtility.AllyWithin).
    /// Anchored (anchorThings, anchorCorpses): the candidate centres are those things instead of the enemies
    /// (AbilityAIUtility.CollectAnchors), each scored by the enemies within the radius of it, the rest as above; with
    /// minCorpses by the corpses within the radius instead, at least that many.
    /// waitForGather (anchored only): enemies counted where their paths put them when the cast lands; a spot more
    /// will reach within 3 s is held for up to 5 s, and one an ally is walking into is skipped (AbilityAIGather;
    /// k × 7 path reads more, only with anchors in range).
    /// Cost: k WouldAffect checks and k² distance checks (k × anchors when anchored), then up to 10 target
    /// validations; with avoidBuildings a pass over the cells in the radius for each of the five.
    /// </summary>
    public class AbilityAIWorker_AoEHostile : AbilityAIWorker
    {
        public override bool TargetsEnemies => true;
        private struct Centre { public Thing thing; public int count; public float distSq; }
        private static readonly List<Centre> centres = new List<Centre>();
        private static readonly List<Thing> targets = new List<Thing>();
        private static readonly List<Thing> anchors = new List<Thing>();
        private static readonly List<bool> affected = new List<bool>();
        // waitForGather: each target's cell at every sample, target-major
        private static readonly List<IntVec3> predicted = new List<IntVec3>();
        private const int MaxTries = 5;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (!Score(ctx, ability, AbilityAIUtility.Range(ability, ext))) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            int tries = AbilityAIUtility.Anchored(ext) ? AbilityAIUtility.MaxAnchorTries : MaxTries;
            for (int i = 0; i < centres.Count && i < tries; i++)
            {
                Thing centre = centres[i].thing;
                if (ext.avoidBuildings && AbilityAIUtility.FriendlyBuildingWithin(ctx, centre.Position, radius, centre))
                {
                    if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = $"a friendly building is near {centre.LabelShort}";
                    continue;
                }
                if (AbilityAIUtility.Covered(ctx, ability, ext, centre.Position, radius))
                {
                    if (AbilityAISettings.DebugLog)
                        ctx.NoTargetWhy = AbilityAIDriver.MateCastingNear(ability.Def, centre.Position, radius * ext.CoverDistanceFactor)
                            ? $"a mate's cast is already landing near {centre.LabelShort}"
                            : $"the area around {centre.LabelShort} is already covered";
                    continue;
                }
                target = AbilityAIUtility.PawnOrCell(ability, centre);
                if (target.IsValid) { ctx.NoTargetWhy = null; break; }
            }
            if (!target.IsValid && ctx.NoTargetWhy == null && centres.Count > 0 && AbilityAISettings.DebugLog)
                ctx.NoTargetWhy = $"{Mathf.Min(centres.Count, tries)} spots refused (sight, range or the verb's own check)";
            centres.Clear();
            return target.IsValid;
        }

        public override bool TryFindApproachTarget(AbilityAIContext ctx, AIAbility ability, float reach, out Thing goal)
        {
            goal = null;
            if (!Score(ctx, ability, reach)) return false;
            for (int i = 0; i < centres.Count && i < MaxTries; i++)
            {
                Thing p = centres[i].thing;
                // A spot that would be refused on arrival isn't walked to
                if (AbilityAIUtility.Covered(ctx, ability, ext, p.Position, AbilityAIUtility.Radius(ability, ext))) continue;
                if (AbilityAIDriver.ValidIgnoringRange(ability, p) || AbilityAIDriver.ValidIgnoringRange(ability, p.Position)) { goal = p; break; }
            }
            centres.Clear();
            return goal != null;
        }

        // Fills centres, best first; false when there are none
        private bool Score(AbilityAIContext ctx, AIAbility ability, float range)
        {
            centres.Clear();
            targets.Clear();
            float radius = AbilityAIUtility.Radius(ability, ext);
            // waitForGather counts enemies where their paths take them in the next few seconds, so one outside the
            // reach now may walk into it
            if (ext.waitForGather) AbilityAIUtility.CollectHostiles(ctx, ext, targets);
            // Abs: a touch verb's range is -1, which the centre check below squares into 1
            else AbilityAIUtility.CollectHostiles(ctx, ext, targets, Mathf.Abs(range) + radius + 1f);
            if (targets.Count < ext.minTargets) { targets.Clear(); return false; }
            float r2 = radius * radius;
            float maxSq = range * range, minSq = ext.minRange * ext.minRange;
            IntVec3 from = ctx.pawn.Position;

            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            affected.Clear();
            int affectable = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                bool a = (!spareBroken || !(targets[i] is Pawn p) || !ctx.IsBrokenAlly(p)) && AbilityAIUtility.WouldAffect(ability, ext, targets[i]);
                affected.Add(a);
                if (a) affectable++;
            }
            if (affectable < ext.minTargets) { targets.Clear(); return false; }

            if (AbilityAIUtility.Anchored(ext))
            {
                ScoreAnchors(ctx, ability, range, r2, radius, spareBroken);
                targets.Clear();
                centres.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
                return centres.Count > 0;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                Thing e = targets[i];
                float d = (e.Position - from).LengthHorizontalSquared;
                if (d > maxSq || d < minSq) continue;
                if (ext.avoidSelf && d <= r2) continue;
                if (spareBroken && !affected[i] && e is Pawn ep && ctx.IsBrokenAlly(ep)) continue;
                int count = 0;
                for (int j = 0; j < targets.Count; j++)
                    if (affected[j] && (j == i || (targets[j].Position - e.Position).LengthHorizontalSquared <= r2)) count++;
                if (count < ext.minTargets) continue;
                // Anyone on the caster's side, colonists included when the caster is a friendly visitor, and the
                // ones in a hostile break when they are to be spared
                if (ext.avoidAllies && radius > 0f && AbilityAIUtility.AllyWithin(ctx, ext, e.Position, r2, spareBroken, false)) continue;
                centres.Add(new Centre { thing = e, count = count, distSq = d });
            }
            targets.Clear();
            // Most enemies caught first, then the nearest (more likely to be in sight)
            centres.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
            return centres.Count > 0;
        }

        private static bool FriendlyCorpseWithin(AbilityAIContext ctx, IntVec3 c, float radius)
        {
            // NumCellsInRadius logs an error past the pattern; a rule that big can't be checked, so it is taken as clear
            if (radius >= GenRadial.MaxRadialPatternRadius) return false;
            Faction own = ctx.pawn.Faction;
            int n = GenRadial.NumCellsInRadius(radius);
            for (int i = 0; i < n; i++)
            {
                IntVec3 cell = c + GenRadial.RadialPattern[i];
                if (!cell.InBounds(ctx.map)) continue;
                var things = cell.GetThingList(ctx.map);
                for (int j = 0; j < things.Count; j++)
                    if (things[j] is Corpse corpse && corpse.InnerPawn?.Faction is Faction f && own != null && (f == own || !f.HostileTo(own))) return true;
            }
            return false;
        }

        // Fills centres from the anchors (unsorted), with targets and affected already filled in. With waitForGather
        // the enemies are counted where they will be when the cast lands, and a spot more enemies will reach within
        // the look-ahead is held (AbilityAIGather)
        private void ScoreAnchors(AbilityAIContext ctx, AIAbility ability, float range, float r2, float radius, bool spareBroken)
        {
            anchors.Clear();
            AbilityAIUtility.CollectAnchors(ctx, ext, range, anchors);
            IntVec3 from = ctx.pawn.Position;
            bool gather = anchors.Count > 0 && AbilityAIGather.On(ctx, ext);
            int samples = AbilityAIGather.SampleCount, landing = 0;
            if (gather)
            {
                landing = Mathf.RoundToInt(ability.WarmupSeconds * 60f);
                predicted.Clear();
                for (int j = 0; j < targets.Count; j++)
                {
                    if (affected[j] && targets[j] is Pawn p) AbilityAIGather.Predict(p, landing, predicted);
                    else for (int k = 0; k < samples; k++) predicted.Add(targets[j].Position);
                }
            }
            Thing heldAt = null;
            int heldNow = 0, heldLater = 0, fewCorpses = 0, friendlyCorpse = 0;
            for (int i = 0; i < anchors.Count; i++)
            {
                IntVec3 c = anchors[i].Position;
                float d = (c - from).LengthHorizontalSquared;
                if (ext.avoidSelf && d <= r2) continue;
                int count = 0, later = 0;
                if (!gather)
                {
                    for (int j = 0; j < targets.Count; j++)
                        if (affected[j] && (targets[j].Position - c).LengthHorizontalSquared <= r2) count++;
                }
                else
                {
                    for (int k = 0; k < samples; k++)
                    {
                        int n = 0;
                        for (int j = 0; j < targets.Count; j++)
                            if (affected[j] && (predicted[j * samples + k] - c).LengthHorizontalSquared <= r2) n++;
                        if (k == 0) count = n;
                        else if (n > later) later = n;
                    }
                }
                if (count < ext.minTargets) continue;
                int corpses = 0;
                if (ext.minCorpses > 0)
                {
                    // The anchors are those corpses within reach; one just past it but inside the radius isn't counted
                    for (int k = 0; k < anchors.Count; k++)
                        if ((anchors[k].Position - c).LengthHorizontalSquared <= r2) corpses++;
                    if (corpses < ext.minCorpses) { fewCorpses++; continue; }
                    // The dust raises every corpse it reaches: not over one of the caster's own dead or a friend's
                    if (FriendlyCorpseWithin(ctx, c, radius)) { friendlyCorpse++; continue; }
                }
                if (ext.avoidAllies && radius > 0f)
                {
                    if (AbilityAIUtility.AllyWithin(ctx, ext, c, r2, spareBroken, false)) continue;
                    // Never a hold: a cast that would land on an ally isn't made at all. Not the caster, which stops
                    // walking to cast
                    if (gather && (AbilityAIGather.AnyArriving(ctx.Friendlies, c, r2, landing, ext.allyKinds, ctx.pawn)
                        || (spareBroken && AbilityAIGather.AnyArriving(ctx.BrokenAllies, c, r2, landing, ext.allyKinds, ctx.pawn)))) continue;
                }
                // Only this spot waits: another circle with enough enemies now is still cast at, since each circle
                // is its own charge and the Invoke's cooldown is half a second
                if (gather && later > count && !ctx.SlowCheck && AbilityAIGather.MayHold(anchors[i], ability.Def, ctx.tick))
                {
                    if (heldAt == null || later > heldLater) { heldAt = anchors[i]; heldNow = count; heldLater = later; }
                    continue;
                }
                centres.Add(new Centre { thing = anchors[i], count = ext.minCorpses > 0 ? corpses : count, distSq = d });
            }
            if (gather) predicted.Clear();
            if (heldAt != null && centres.Count == 0 && AbilityAISettings.DebugLog)
                ctx.NoTargetWhy = $"waiting for enemies to gather at {heldAt.LabelShort} ({heldNow} when it would land, {heldLater} soon)";
            else if (ext.minCorpses > 0 && centres.Count == 0 && AbilityAISettings.DebugLog)
                ctx.NoTargetWhy = anchors.Count == 0 ? "no enemy corpse in range"
                    : $"{anchors.Count} corpses in range: {fewCorpses} with under {ext.minCorpses} near, {friendlyCorpse} beside a friendly corpse";
            anchors.Clear();
        }
    }

    /// <summary>
    /// AoE friendly: an area buff over allies (Battle Cry). Self-cast abilities centre on the caster; targeted ones
    /// on whichever ally gathers the most. Counts allies (the caster included) within the radius that don't yet have
    /// the ability's hediff; casts when at least minTargets would gain it. Approach (self-cast only): candidate cells
    /// are the allies within approachDistance + radius that lack the hediff, and the midpoint of every pair of them
    /// within two radii of each other; the standable cell within approachDistance that would cover the most (the
    /// caster counted once, at the new cell), nearest on a tie, and more than the caster covers where it stands.
    /// During the walk the pawn only shouts early once its current cell covers as many as the chosen one. Only ally
    /// cells and pair midpoints are tried, so three allies only their centroid covers count as two. Cost: with m
    /// nearby allies, m²/2 candidate cells × m distance checks (about 500 for 10) plus up to 4 reachability checks;
    /// once per 10 s after a walk starts, once per 2 s while no spot is found. Targeted cast: k² distance checks,
    /// then up to 5 target validations, best ally first.
    /// health: only allies with one of those conditions count (AbilityAIUtility.HealthWant). avoidEnemies: no centre,
    /// and no approach cell, with an enemy inside the radius. Anchored (anchorThings, anchorCorpses): targeted casts
    /// centre on those things instead of on allies, scored the same way (allies × anchors distance checks).
    /// </summary>
    public class AbilityAIWorker_AoEFriendly : AbilityAIWorker
    {
        // Its self-cast branch counts the allies in reach itself; a cast at an ally is not an area around the caster
        protected internal override bool CountsAffectedAroundCaster => true;
        private struct Spot { public IntVec3 cell; public int count; public float dist; }
        private static readonly List<Pawn> near = new List<Pawn>();
        private static readonly List<Spot> spots = new List<Spot>();

        public override bool UsesApproachCell => true;

        public override bool TryFindApproachCell(AbilityAIContext ctx, AIAbility ability, out IntVec3 cell, out int expected)
        {
            cell = IntVec3.Invalid;
            expected = 0;
            if (!AbilityAIUtility.IsSelfCast(ability)) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            Pawn pawn = ctx.pawn;
            Map map = ctx.map;
            IntVec3 from = pawn.Position;
            float maxSq = ext.approachDistance * ext.approachDistance;
            float lookSq = (ext.approachDistance + radius) * (ext.approachDistance + radius);
            bool selfCounts = Gains(pawn, hediff);

            // Allies (not the caster) that would gain the buff and could be covered from a reachable cell
            near.Clear();
            var allies = ctx.SupportAllies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn a = allies[i];
                if (a == pawn || !Gains(a, hediff)) continue;
                if ((a.Position - from).LengthHorizontalSquared <= lookSq) near.Add(a);
            }
            // As Count: a heal is for the downed too
            if (ext.health != AbilityAIHealth.None)
            {
                var downed = ctx.DownedAllies;
                for (int i = 0; i < downed.Count; i++)
                    if (Gains(downed[i], hediff) && (downed[i].Position - from).LengthHorizontalSquared <= lookSq) near.Add(downed[i]);
            }
            int here = CountNear(from, r2) + (selfCounts ? 1 : 0);
            int floor = Mathf.Max(ext.minTargets, here + 1);
            if (near.Count + (selfCounts ? 1 : 0) < floor) { near.Clear(); return false; }

            spots.Clear();
            float pair2 = 4f * r2;
            for (int i = 0; i < near.Count; i++)
            {
                IntVec3 a = near[i].Position;
                AddSpot(ctx, a, from, maxSq, r2, selfCounts, floor);
                for (int j = i + 1; j < near.Count; j++)
                {
                    IntVec3 b = near[j].Position;
                    if ((a - b).LengthHorizontalSquared > pair2) continue;
                    AddSpot(ctx, new IntVec3((a.x + b.x) / 2, 0, (a.z + b.z) / 2), from, maxSq, r2, selfCounts, floor);
                }
            }
            near.Clear();
            if (spots.Count == 0) return false;
            spots.Sort(BetterFirst);
            // A midpoint across a wall or river can be the best spot all fight: take the best one the pawn can reach
            for (int i = 0; i < spots.Count && i < MaxReachTries; i++)
            {
                if (!pawn.CanReach(spots[i].cell, PathEndMode.OnCell, Danger.Some)) continue;
                cell = spots[i].cell;
                expected = spots[i].count;
                break;
            }
            spots.Clear();
            return cell.IsValid;
        }

        private const int MaxReachTries = 4;
        private static readonly System.Comparison<Spot> BetterFirst =
            (x, y) => x.count != y.count ? y.count.CompareTo(x.count) : x.dist.CompareTo(y.dist);

        private void AddSpot(AbilityAIContext ctx, IntVec3 c, IntVec3 from, float maxSq, float r2, bool selfCounts, int floor)
        {
            float d = (c - from).LengthHorizontalSquared;
            if (d > maxSq || c == from || !c.InBounds(ctx.map) || !c.Standable(ctx.map)) return;
            if (ext.avoidEnemies && EnemyWithin(ctx, c, r2)) return;
            int n = CountNear(c, r2) + (selfCounts ? 1 : 0);
            if (n >= floor) spots.Add(new Spot { cell = c, count = n, dist = d });
        }

        private static int CountNear(IntVec3 c, float r2)
        {
            int n = 0;
            for (int i = 0; i < near.Count; i++)
                if ((near[i].Position - c).LengthHorizontalSquared <= r2) n++;
            return n;
        }

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            var allies = ctx.SupportAllies;

            if (AbilityAIUtility.IsSelfCast(ability))
            {
                int n = Count(ctx, allies, ctx.pawn.Position, r2, hediff);
                if (n < ext.minTargets) return false;
                if (ext.avoidEnemies && EnemyWithin(ctx, ctx.pawn.Position, r2)) return false;
                // Walking to a better spot: don't shout on the way unless this cell is already as good
                if (ctx.ApproachExpected(ability) > n) return false;
                target = ctx.pawn;
                return true;
            }

            float range = AbilityAIUtility.Range(ability, ext);
            float maxSq = range * range;
            // Every ally that would gather enough, best first: the best one can fail validation (out of sight) while
            // the next would pass
            centres.Clear();
            if (AbilityAIUtility.Anchored(ext))
            {
                anchors.Clear();
                AbilityAIUtility.CollectAnchors(ctx, ext, range, anchors);
                for (int i = 0; i < anchors.Count; i++) AddCentre(ctx, allies, anchors[i], r2, maxSq, hediff);
                anchors.Clear();
            }
            else
                for (int i = 0; i < allies.Count; i++) AddCentre(ctx, allies, allies[i], r2, maxSq, hediff);
            centres.Sort(MostThenNearest);
            int tries = AbilityAIUtility.Anchored(ext) ? AbilityAIUtility.MaxAnchorTries : MaxTries;
            for (int i = 0; i < centres.Count && i < tries; i++)
            {
                target = AbilityAIUtility.PawnOrCell(ability, centres[i].thing);
                if (target.IsValid) break;
            }
            centres.Clear();
            return target.IsValid;
        }

        private void AddCentre(AbilityAIContext ctx, List<Pawn> allies, Thing at, float r2, float maxSq, HediffDef hediff)
        {
            float d = (at.Position - ctx.pawn.Position).LengthHorizontalSquared;
            if (d > maxSq) return;
            int n = Count(ctx, allies, at.Position, r2, hediff);
            if (n < ext.minTargets || (ext.avoidEnemies && EnemyWithin(ctx, at.Position, r2))) return;
            centres.Add(new Centre { thing = at, count = n, distSq = d });
        }

        // Whoever stands there gets the effect, the enemy included
        private static bool EnemyWithin(AbilityAIContext ctx, IntVec3 c, float r2)
        {
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
                if ((enemies[i].Position - c).LengthHorizontalSquared <= r2) return true;
            return false;
        }

        // Would gain the effect: lacks its hediff, of the rule's kinds, and with health set, in need of it
        private bool Gains(Pawn p, HediffDef hediff) =>
            !AbilityAIUtility.Has(p, hediff) && AbilityAIUtility.Fits(ext, p)
            && (ext.health == AbilityAIHealth.None || AbilityAIUtility.HealthWant(ext, p) > 0f);

        private static readonly List<Thing> anchors = new List<Thing>();
        private struct Centre { public Thing thing; public int count; public float distSq; }
        private static readonly List<Centre> centres = new List<Centre>();
        private const int MaxTries = 5;
        private static readonly System.Comparison<Centre> MostThenNearest =
            (a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq);

        // A health rule (a heal) counts the downed too
        private int Count(AbilityAIContext ctx, List<Pawn> allies, IntVec3 c, float r2, HediffDef hediff)
        {
            int n = CountIn(allies, c, r2, hediff);
            if (ext.health != AbilityAIHealth.None) n += CountIn(ctx.DownedAllies, c, r2, hediff);
            return n;
        }

        private int CountIn(List<Pawn> pawns, IntVec3 c, float r2, HediffDef hediff)
        {
            int n = 0;
            for (int i = 0; i < pawns.Count; i++)
                if ((pawns[i].Position - c).LengthHorizontalSquared <= r2 && Gains(pawns[i], hediff)) n++;
            return n;
        }
    }

    /// <summary>
    /// Self buff: cast on the caster while an enemy is within maxRange (default 30) and the caster doesn't already
    /// have the ability's hediff; with auraKinds, only when the aura would reach enough of them
    /// (AbilityAIUtility.AuraWorthIt). Cost: k distance checks.
    /// </summary>
    public class AbilityAIWorker_SelfBuff : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (AbilityAIUtility.Has(ctx.pawn, AbilityAIUtility.Hediff(ability, ext))) return false;
            float reach = ext.maxRange >= 0f ? ext.maxRange : 30f;
            if (ctx.NearestEnemy(out float d) == null || d > reach * reach) return false;
            if (!AbilityAIUtility.AuraWorthIt(ctx, ability, ext, ctx.pawn)) return false;
            target = ctx.pawn;
            return true;
        }
    }

    /// <summary>
    /// Movement: close on the enemy or get away from it. Self-cast abilities (Sprint) are cast when either applies;
    /// targeted ones (Shadowskip) pick a cell.
    /// Gap-close (melee pawns only): the current enemy target is further than minGapDistance (default 6) and within
    /// maxRange (default 40; a targeted ability also needs it in reach); a targeted ability lands next to it.
    /// Escape: health under healthThreshold, fleeing, or a ranged pawn (any pawn with escapeInMelee) caught in melee,
    /// with an enemy within 12 tiles;
    /// a targeted ability tries 16 directions at full and 60% range for the cell furthest from every enemy, and only
    /// goes if that gains at least 4 tiles. A targeted ability's cells can be limited to dark ones (cellMaxLight).
    /// Any rule's gap-close can be limited to a target the caster can't see (gapCloseOutOfSight: one sight line,
    /// O(distance)); an NPC's own target is in sight when it is picked, so that mostly means a player's melee order.
    /// whileHunting: with no enemy target, the prey of the caster's hunt is closed on by the same rules.
    /// A walking target is led: the landing goes beside the cell its path reaches by the end of the warmup and the
    /// flight (AIAbility.FlightSeconds), else beside where it stands.
    /// A drafted pawn's interrupted attack is queued to resume after the cast, for a jump verb on landing.
    /// During a drafted pawn's melee order (ctx.Charging) only gap-close runs, at the ordered target, for a ranged
    /// pawn too. gapCloseJobTarget: with no enemy pawn (or prey), the target of the caster's melee attack job, a
    /// building included, which is landed beside on a cell it can be touched from.
    /// Targeted, avoidAllies: no one of the caster's side (allyKinds) within the radius of the landing cell, for an
    /// impact or a stun where it lands (Gravimpact, Flash skip); avoidBuildings: no friendly building there either.
    /// pathClearance: no one of its side (downed faction-mates included) and no friendly building within that many
    /// tiles of the line from the caster to the landing cell (Blazing dash); gap-close checks it on the cell it picked
    /// only.
    /// Cost: escape is 32 × k distance checks, then up to 6 validations;
    /// gap-close with a targeted ability up to 8 validations (one per cell around a building's edge), only while the
    /// target is within its reach.
    /// </summary>
    public class AbilityAIWorker_Movement : AbilityAIWorker
    {
        protected internal override bool ForFleeing => true;

        private const float EscapeDangerRange = 12f;
        private const float EscapeMinGain = 4f;
        private const int Directions = 16;
        private const int MaxTries = 6;

        private struct Spot { public IntVec3 cell; public float score; }
        private static readonly List<Spot> spots = new List<Spot>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            bool self = AbilityAIUtility.IsSelfCast(ability);
            if (self && AbilityAIUtility.Has(ctx.pawn, AbilityAIUtility.Hediff(ability, ext))) return false;

            // The player ordered this pawn into melee: getting away would undo the order
            if ((ext.modes & AbilityAIMoveModes.Escape) != 0 && !ctx.Charging && WantsEscape(ctx))
            {
                if (self) { target = ctx.pawn; return true; }
                if (TryEscapeCell(ctx, ability, out IntVec3 cell)) { target = cell; return true; }
            }
            // A gunner the player ordered into melee is going there anyway
            if ((ext.modes & AbilityAIMoveModes.GapClose) != 0 && (!ctx.UsesRanged || ctx.Charging)
                && GapCloseTarget(ctx) is Thing goal)
            {
                float d = (goal.Position - ctx.pawn.Position).LengthHorizontal;
                float reach = ext.maxRange >= 0f ? ext.maxRange : 40f;
                // A building's Position is its centre: measure the gap to its edge
                float half = goal is Pawn ? 0f : Mathf.Max(goal.def.size.x, goal.def.size.z) / 2f;
                if (d - half < ext.minGapDistance || d > reach) return false;
                if (ext.gapCloseOutOfSight && GenSight.LineOfSight(ctx.pawn.Position, goal.Position, ctx.map)) return false;
                bool prey = ext.whileHunting && goal == ctx.Prey;
                if (self) { target = ctx.pawn; ctx.CastAtPrey = prey; return true; }
                float range = AbilityAIUtility.Range(ability, ext);
                // Where a walking target will be when the caster lands: beside where it is now, a jump lands behind
                // anything running from it. Its own path read for the warmup plus the flight; a target the caster can't
                // reach there is tried where it stands. One running at the caster may end up next to it: that jump would
                // be a hop that spends a charge or a cooldown, so the lead must keep minGapDistance as well.
                if (goal is Pawn walker && walker.pather != null && walker.pather.Moving)
                {
                    IntVec3 ahead = AbilityAIGather.CellAt(walker, LandingTicks(ability, d));
                    if (ahead != goal.Position)
                        ahead = AbilityAIGather.CellAt(walker, LandingTicks(ability, (ahead - ctx.pawn.Position).LengthHorizontal));
                    float aheadDist = (ahead - ctx.pawn.Position).LengthHorizontal;
                    if (ahead != goal.Position && aheadDist <= range + 1.5f && aheadDist - 1f >= ext.minGapDistance
                        && TryCellNear(ctx, ability, goal, ahead, out IntVec3 lead) && PathClear(ctx, lead))
                    {
                        target = lead;
                        ctx.CastAtPrey = prey;
                        return true;
                    }
                }
                // Even the cell beside the target nearest the caster is out of reach: skip the validations
                if (d > range + 1.5f + half) return false;
                if (TryCellNear(ctx, ability, goal, goal.Position, out IntVec3 cell) && PathClear(ctx, cell))
                {
                    target = cell;
                    ctx.CastAtPrey = prey;
                    return true;
                }
            }
            return false;
        }

        // The caster lands about a tile short of the target's cell
        private static int LandingTicks(AIAbility ability, float distance) =>
            Mathf.RoundToInt((ability.WarmupSeconds + ability.FlightSeconds(Mathf.Max(1f, distance - 1f))) * GenTicks.TicksPerRealSecond);

        // avoidAllies, avoidBuildings: an impact or stun where the caster lands catches whatever stands there; the
        // caster itself has left
        private bool LandingClear(AbilityAIContext ctx, AIAbility ability, IntVec3 c)
        {
            if (!ext.avoidAllies && !ext.avoidBuildings) return true;
            float r = AbilityAIUtility.Radius(ability, ext);
            if (r <= 0f) return true;
            if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, c, r * r, !AbilityAIUtility.MayHitBrokenAllies(ext), true)) return false;
            return !ext.avoidBuildings || !AbilityAIUtility.FriendlyBuildingWithin(ctx, c, r);
        }

        // pathClearance: Blazing dash stuns and burns every hostile pawn within 1.9 of its line and kills every building
        // there, walls included, whoever owns them; it spares the caster's side's pawns, but the fires it starts don't.
        // The capsule (round ends) covers behind the start and around the landing as well as the way. allyKinds
        // narrows the pawns; the buildings are checked whatever avoidBuildings says, since the dash breaks them all.
        private bool PathClear(AbilityAIContext ctx, IntVec3 to)
        {
            float clear = ext.pathClearance;
            if (clear <= 0f) return true;
            var path = new AICapsule(ctx.pawn.Position, to, clear);
            if (AbilityAIUtility.AllyIn(ctx, ext, path, !AbilityAIUtility.MayHitBrokenAllies(ext), ctx.pawn)) return false;
            return !AbilityAIUtility.FriendlyBuildingIn(ctx, path);
        }

        // The player's melee order, else the enemy pawn the caster fights, else its prey (whileHunting), else whatever
        // its melee job attacks (gapCloseJobTarget)
        private Thing GapCloseTarget(AbilityAIContext ctx)
        {
            // Only what the player ordered attacked: never a stale enemy target or a melee threat instead
            if (ctx.Charging) return ctx.ChargeTarget ?? (ext.gapCloseJobTarget ? ctx.MeleeJobTarget : null);
            if (ctx.EnemyTarget is Pawn enemy) return enemy;
            if (ext.whileHunting && ctx.Prey != null) return ctx.Prey;
            return ext.gapCloseJobTarget ? ctx.MeleeJobTarget : null;
        }

        private bool WantsEscape(AbilityAIContext ctx)
        {
            bool reason = ctx.HealthFraction < ext.healthThreshold || AbilityAIUtility.IsFleeing(ctx)
                || ((ctx.UsesRanged || ext.escapeInMelee) && AbilityAIUtility.InMelee(ctx));
            if (!reason) return false;
            return ctx.NearestEnemy(out float d) != null && d <= EscapeDangerRange * EscapeDangerRange;
        }

        // The standable cell next to the target nearest the caster that the ability accepts; beside a building, only
        // a cell it can be touched from (no corner cut past a wall)
        private bool TryCellNear(AbilityAIContext ctx, AIAbility ability, Thing goal, IntVec3 centre, out IntVec3 best)
        {
            best = IntVec3.Invalid;
            float maxLight = ext.cellMaxLight;
            float bestD = float.MaxValue;
            if (!(goal is Pawn))
            {
                foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(goal))
                {
                    // A door or turret's surroundings are where traps go
                    if (!c.InBounds(ctx.map) || !c.Standable(ctx.map) || c.GetFirstThing<Building_Trap>(ctx.map) != null) continue;
                    if (maxLight < 1f && ctx.map.glowGrid.GroundGlowAt(c) > maxLight) continue;
                    float d = (c - ctx.pawn.Position).LengthHorizontalSquared;
                    if (d >= bestD || !ReachabilityImmediate.CanReachImmediate(c, goal, ctx.map, PathEndMode.Touch, ctx.pawn)
                        || !LandingClear(ctx, ability, c) || !AbilityAIDriver.Valid(ability, c)) continue;
                    bestD = d;
                    best = c;
                }
                return best.IsValid;
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 c = centre + GenAdj.AdjacentCells[i];
                // Vanilla's jump verbs refuse a cell nobody can stand on; VEF's location targeting doesn't, and VPE's
                // Power Leap would land the caster inside a wall
                if (!c.InBounds(ctx.map) || !c.Standable(ctx.map)) continue;
                if (maxLight < 1f && ctx.map.glowGrid.GroundGlowAt(c) > maxLight) continue;
                float d = (c - ctx.pawn.Position).LengthHorizontalSquared;
                if (d >= bestD || !LandingClear(ctx, ability, c) || !AbilityAIDriver.Valid(ability, c)) continue;
                bestD = d;
                best = c;
            }
            return best.IsValid;
        }

        private bool TryEscapeCell(AbilityAIContext ctx, AIAbility ability, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            var enemies = ctx.Enemies;
            if (enemies.Count == 0) return false;
            float range = AbilityAIUtility.Range(ability, ext);
            if (range < 2f) return false;
            float now = MinEnemyDist(enemies, ctx.pawn.Position);

            spots.Clear();
            for (int i = 0; i < Directions; i++)
            {
                float angle = i * (360f / Directions);
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                for (int s = 0; s < 2; s++)
                {
                    float dist = s == 0 ? range - 0.5f : range * 0.6f;
                    IntVec3 c = ctx.pawn.Position + new IntVec3(Mathf.RoundToInt(dir.x * dist), 0, Mathf.RoundToInt(dir.z * dist));
                    if (!c.InBounds(ctx.map) || !c.Standable(ctx.map)) continue;
                    if (ext.cellMaxLight < 1f && ctx.map.glowGrid.GroundGlowAt(c) > ext.cellMaxLight) continue;
                    float score = MinEnemyDist(enemies, c);
                    if (score - now < EscapeMinGain) continue;
                    spots.Add(new Spot { cell = c, score = score });
                }
            }
            spots.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < spots.Count && i < MaxTries; i++)
            {
                IntVec3 c = spots[i].cell;
                if (LandingClear(ctx, ability, c) && AbilityAIDriver.Valid(ability, c) && PathClear(ctx, c)) { result = c; break; }
            }
            spots.Clear();
            return result.IsValid;
        }

        private static float MinEnemyDist(List<Pawn> enemies, IntVec3 c)
        {
            float best = float.MaxValue;
            for (int i = 0; i < enemies.Count; i++)
            {
                float d = (enemies[i].Position - c).LengthHorizontalSquared;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }
    }

    /// <summary>
    /// Cover: when the caster is under fire, put something between it and the shooters (Smokescreen, Smokepop,
    /// Wallraise, Skipshield). Under fire = at least minThreats ranged enemies aiming at the caster, counting being
    /// shot in the last 10 seconds as one. Placed on the caster (offsetTowardThreat 0) or that many tiles toward the
    /// nearest threat. Not cast when it would add nothing: smoke while the caster stands in smoke, a spawned thing
    /// (the skipshield) while one already stands within the ability's radius of the caster, anything else (a wall)
    /// while the caster already has half cover from the nearest threat. A wall is never raised onto a pawn.
    /// A self-cast ring that walls in everyone near the caster (Ice Wall, wallsIn) has its own rule: see TryRing.
    /// Cost: k checks of each enemy's target, then up to 3 validations.
    /// </summary>
    public class AbilityAIWorker_Cover : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            var effects = ability.Effects;
            bool smoke = effects.smoke;
            ThingDef spawns = effects.spawns;
            bool wall = effects.wall;
            if (smoke && pawn.Position.AnyGas(ctx.map, GasType.BlindSmoke)) return false;
            if (spawns != null && SpawnedNear(ctx, spawns, Mathf.Max(AbilityAIUtility.Radius(ability, ext), 1.5f))) return false;
            if (AbilityAIUtility.IsSelfCast(ability))
            {
                float ring = AbilityAIUtility.Radius(ability, ext);
                // Opt-in: when every self-cast with a radius took the ring rule, the tunneler's smokepop was refused
                // whenever it was in melee or a colonist stood within 3.5 (run 16)
                if (ext.wallsIn && ring > 0f)
                {
                    if (!TryRing(ctx, ring)) return false;
                    target = pawn;
                    return true;
                }
                if (ext.shieldsAllies && ring > 0f)
                {
                    int shooters = AbilityAIBarrier.Threats(ctx, ring);
                    // A hit with nobody aiming is one shooter out of sight
                    if (shooters == 0 && ctx.RecentlyShot(pawn)) shooters = 1;
                    if (shooters < ext.minThreats)
                    {
                        if (AbilityAISettings.DebugLog) ctx.NoTargetWhy = $"{shooters} shooters outside aim inside, needs {ext.minThreats}";
                        return false;
                    }
                    target = pawn;
                    return true;
                }
            }

            int threats = ctx.RecentlyShot(pawn) ? 1 : 0;
            Pawn nearest = null;
            float nearestD = float.MaxValue;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                if (!AbilityAIUtility.AimsAt(e, pawn)) continue;
                threats++;
                float d = (e.Position - pawn.Position).LengthHorizontalSquared;
                if (d < nearestD) { nearestD = d; nearest = e; }
            }
            if (threats < ext.minThreats) return false;
            if (!smoke && spawns == null && nearest != null && CoverUtility.CalculateOverallBlockChance(pawn, nearest.Position, ctx.map) >= 0.5f) return false;
            if (AbilityAIUtility.IsSelfCast(ability))
            {
                target = pawn;
                return true;
            }

            if (ext.offsetTowardThreat <= 0f || nearest == null)
            {
                if (wall) return false;   // a wall on the caster's own cell would bury it
                target = pawn.Position;
                return true;
            }
            Vector3 dir = (nearest.Position - pawn.Position).ToVector3().normalized;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            // Straight toward the threat first, then a tile to either side
            for (int s = 0; s < 3; s++)
            {
                Vector3 off = dir * ext.offsetTowardThreat + side * (s == 0 ? 0f : s == 1 ? 1f : -1f);
                IntVec3 c = pawn.Position + new IntVec3(Mathf.RoundToInt(off.x), 0, Mathf.RoundToInt(off.z));
                if (!c.InBounds(ctx.map) || (wall && PawnInPattern(ctx.map, c, effects.wallPattern))) continue;
                if (AbilityAIDriver.Valid(ability, c)) { target = c; return true; }
            }
            return false;
        }

        // A ring blocks fire both ways and keeps everyone inside it in, so it is worth raising only when walling in
        // helps the side inside: the operator saw Ice Wall shut four shooting colonists in and their melee fighters
        // out. Cast when the enemy fire on the pawns inside is at least minThreats and either outnumbers the shooters
        // inside (caster included), or a downed ally or one under healthThreshold who is being shot at is inside.
        // Never with an enemy inside (it would be shut in with them) or an ally inside in melee (an enemy inside, or
        // one it is walking out to). Cost: a pass over the allies, the aim counts the evaluation builds anyway.
        private bool TryRing(AbilityAIContext ctx, float ring)
        {
            Pawn pawn = ctx.pawn;
            float r2 = ring * ring;
            if (TargetingUtil.AnyWithin(ctx.Enemies, pawn.Position, r2)) return false;
            int threats = 0, shooters = 0;
            bool shot = false, helpless = false;
            var allies = ctx.Allies;
            for (int i = 0; i < allies.Count; i++)
            {
                Pawn p = allies[i];
                if ((p.Position - pawn.Position).LengthHorizontalSquared > r2) continue;
                if (p.CurJobDef == JobDefOf.AttackMelee || AbilityAIUtility.InMelee(ctx, p)) return false;
                int aimed = ctx.AimedAtCount(p);
                bool hit = ctx.RecentlyShot(p);
                threats += aimed;
                shot |= hit;
                if (AbilityAIContext.FightsAtRange(p)) shooters++;
                if ((aimed > 0 || hit) && (p.health.summaryHealth?.SummaryHealthPercent ?? 1f) < ext.healthThreshold) helpless = true;
            }
            // A hit with nobody aiming is one shooter out of sight; with aimers it is probably one of them
            if (threats == 0 && shot) threats = 1;
            if (threats < ext.minThreats) return false;
            if (threats > shooters || helpless) return true;
            return TargetingUtil.AnyWithin(ctx.DownedAllies, pawn.Position, r2);
        }

        private static bool SpawnedNear(AbilityAIContext ctx, ThingDef def, float radius)
        {
            var list = ctx.map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < list.Count; i++)
                if (list[i].Position.InHorDistOf(ctx.pawn.Position, radius)) return true;
            return false;
        }

        private static bool PawnInPattern(Map map, IntVec3 c, List<IntVec2> pattern)
        {
            if (pattern == null) return c.GetFirstPawn(map) != null;
            for (int i = 0; i < pattern.Count; i++)
            {
                IntVec3 cell = c + new IntVec3(pattern[i].x, 0, pattern[i].z);
                if (cell.InBounds(map) && cell.GetFirstPawn(map) != null) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Recovery: out of combat, get the caster or an ally back to strength. Two kinds of rule, either or both:
    /// - a need (default rest) under needThreshold (default 30%), Second Wind's kind; the rule when health is None,
    ///   and alongside the health conditions when need is set;
    /// - health conditions: Injured (still-healing injuries, summary health under healthThreshold), Disease (an
    ///   immunizable disease not yet immune), MissingParts (a missing part with no prosthetic), Bleeding (at least
    ///   minBleedRate per day): AbilityAIUtility.HealthWant.
    /// A self-cast ability looks at the caster only. Any other looks at the caster (when it can target itself) and
    /// the allies in reach, downed ones too for health rules, skipping anyone with the ability's hediff (health rules,
    /// or one the extension names). Worst off first, the caster x1.5, divided by distance over reach.
    /// Cost: one need lookup self-cast; otherwise a pawns, and for Disease and MissingParts a cached hediff read each.
    /// </summary>
    public class AbilityAIWorker_Recovery : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.OutOfCombat;
        protected internal override bool ForFleeing => true;
        private static readonly List<TargetingUtil.Cand> cands = new List<TargetingUtil.Cand>();

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            // Without health rules the hediff is only skipped when the extension names one: the effects' own first
            // hediff may be a long side effect that would otherwise stop a need rule for good
            HediffDef hediff = SkipHediff(ability);
            if (AbilityAIUtility.IsSelfCast(ability))
            {
                if (Want(ctx.pawn, hediff) <= 0f) return false;
                target = ctx.pawn;
                return true;
            }
            cands.Clear();
            float range = TargetingUtil.Reach(ability, ext);
            bool self = AbilityAIUtility.CanTargetSelf(ability);
            Add(ctx, ctx.SupportAllies, hediff, self, range);
            if (ext.health != AbilityAIHealth.None) Add(ctx, ctx.DownedAllies, hediff, self, range);
            return TargetingUtil.FirstValid(cands, ability, out target);
        }

        private void Add(AbilityAIContext ctx, List<Pawn> pawns, HediffDef hediff, bool self, float range)
        {
            float maxSq = range * range;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                bool isSelf = p == ctx.pawn;
                if (isSelf && !self) continue;
                float d = (p.Position - ctx.pawn.Position).LengthHorizontalSquared;
                if (d > maxSq) continue;
                float want = Want(p, hediff);
                if (want <= 0f) continue;
                cands.Add(new TargetingUtil.Cand { pawn = p, score = want * (isSelf ? 1.5f : 1f) / (1f + Mathf.Sqrt(d) / Mathf.Max(range, 1f)) });
            }
        }

        private HediffDef SkipHediff(AIAbility ability) =>
            ext.health != AbilityAIHealth.None || ext.hediff != null ? AbilityAIUtility.Hediff(ability, ext) : null;

        protected internal override bool IsLinkTarget(AbilityAIContext ctx, AIAbility ability, Pawn p)
        {
            if (p == ctx.pawn) return false;
            if (!ctx.SupportAllies.Contains(p) && !(ext.health != AbilityAIHealth.None && ctx.DownedAllies.Contains(p))) return false;
            return Want(p, SkipHediff(ability)) > 0f;
        }

        // How badly the pawn needs it, 0 = not at all
        private float Want(Pawn p, HediffDef hediff)
        {
            if (!AbilityAIUtility.Fits(ext, p) || AbilityAIUtility.Has(p, hediff)) return 0f;
            float want = 0f;
            if (ext.need != null || ext.health == AbilityAIHealth.None)
            {
                Need need = p.needs?.TryGetNeed(ext.need ?? NeedDefOf.Rest);
                if (need != null && need.CurLevelPercentage < ext.needThreshold) want = 1f - need.CurLevelPercentage;
            }
            return Mathf.Max(want, AbilityAIUtility.HealthWant(ext, p));
        }
    }

    /// <summary>
    /// AoE hostile around the caster: an area centred on the caster itself (psychic drone, static pulse, an animal's
    /// roar). Counts the targets within the radius of the caster that the ability would affect (WouldAffect, allies
    /// in a hostile break left out unless MayHitBrokenAllies; turrets too when targetKinds names Turret) and casts
    /// with at least minTargets. Not while the caster already has the ability's hediff (a lasting aura). avoidAllies:
    /// not with anyone else on the caster's side inside, of allyKinds (AbilityAIUtility.AllyWithin); avoidBuildings:
    /// not with a friendly building inside. Targets the caster, or the cell it stands on for an ability that wants a
    /// location.
    /// Approach (self-cast only, ranged casters): for each target within approachDistance + radius, and the midpoint
    /// of each pair within two radii of each other, the cell on the way to it from which it sits at 70% of the
    /// radius; the standable cell within approachDistance that catches the most, nearest on a tie, more than the
    /// caster catches where it stands, passing avoidAllies there and avoidBuildings, reachable (best four tried).
    /// During the walk the pawn casts early only once its cell catches as many as the chosen one.
    /// Cost: k distance checks and WouldAffect for those inside; with avoidBuildings one pass over the radius's cells.
    /// Approach: m targets nearby give m²/2 cells × m distance checks, once per 2 s while no spot is found.
    /// </summary>
    public class AbilityAIWorker_AoEHostileSelf : AbilityAIWorker
    {
        public override bool TargetsEnemies => true;
        protected internal override bool CountsAffectedAroundCaster => true;
        private struct Spot { public IntVec3 cell; public int count; public float dist; }
        private static readonly List<Thing> targets = new List<Thing>();
        private static readonly List<Thing> near = new List<Thing>();
        private static readonly List<Spot> spots = new List<Spot>();
        private const int MaxReachTries = 4;
        private const float ApproachDepth = 0.7f;

        public override bool UsesApproachCell => true;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            float radius = AbilityAIUtility.Radius(ability, ext);
            if (radius <= 0f) return false;
            Pawn pawn = ctx.pawn;
            if (AuraRunning(ability, pawn)) return false;
            float r2 = radius * radius;
            IntVec3 c = pawn.Position;
            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            Collect(ctx, ability, c, r2, spareBroken);
            int count = near.Count;
            near.Clear();
            if (count < ext.minTargets) return false;
            // Walking to a better spot: not on the way unless this cell is already as good
            if (ctx.ApproachExpected(ability) > count) return false;
            if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, c, r2, spareBroken, true)) return false;
            if (ext.avoidBuildings && AbilityAIUtility.FriendlyBuildingWithin(ctx, c, radius)) return false;
            target = AbilityAIUtility.IsSelfCast(ability) ? new LocalTargetInfo(pawn) : AbilityAIUtility.PawnOrCell(ability, pawn);
            return target.IsValid;
        }

        public override bool TryFindApproachCell(AbilityAIContext ctx, AIAbility ability, out IntVec3 cell, out int expected)
        {
            cell = IntVec3.Invalid;
            expected = 0;
            if (!AbilityAIUtility.IsSelfCast(ability)) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            if (radius <= 0f || AuraRunning(ability, ctx.pawn)) return false;
            float r2 = radius * radius;
            Pawn pawn = ctx.pawn;
            IntVec3 from = pawn.Position;
            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            float look = ext.approachDistance + radius;
            Collect(ctx, ability, from, look * look, spareBroken);
            int here = CountNear(from, r2);
            int floor = Mathf.Max(ext.minTargets, here + 1);
            if (near.Count < floor) { near.Clear(); return false; }

            spots.Clear();
            float maxSq = ext.approachDistance * ext.approachDistance;
            float keep = radius * ApproachDepth;
            float pair2 = 4f * r2;
            for (int i = 0; i < near.Count; i++)
            {
                IntVec3 a = near[i].Position;
                AddSpot(ctx, Toward(from, a, keep), from, maxSq, r2, floor, spareBroken);
                for (int j = i + 1; j < near.Count; j++)
                {
                    IntVec3 b = near[j].Position;
                    if ((a - b).LengthHorizontalSquared > pair2) continue;
                    AddSpot(ctx, Toward(from, new IntVec3((a.x + b.x) / 2, 0, (a.z + b.z) / 2), keep), from, maxSq, r2, floor, spareBroken);
                }
            }
            near.Clear();
            if (spots.Count == 0) return false;
            spots.Sort((x, y) => x.count != y.count ? y.count.CompareTo(x.count) : x.dist.CompareTo(y.dist));
            int tries = 0;
            for (int i = 0; i < spots.Count && tries < MaxReachTries; i++)
            {
                if (ext.avoidBuildings && AbilityAIUtility.FriendlyBuildingWithin(ctx, spots[i].cell, radius)) continue;
                tries++;
                if (!pawn.CanReach(spots[i].cell, PathEndMode.OnCell, Danger.Some)) continue;
                cell = spots[i].cell;
                expected = spots[i].count;
                break;
            }
            spots.Clear();
            return cell.IsValid;
        }

        // A lasting aura on the caster (psychic drone) isn't cast again while it runs
        private bool AuraRunning(AIAbility ability, Pawn pawn) =>
            AbilityAIUtility.IsSelfCast(ability) && AbilityAIUtility.Has(pawn, AbilityAIUtility.Hediff(ability, ext));

        // Fills near with the targets within r2 of c the cast would affect
        private void Collect(AbilityAIContext ctx, AIAbility ability, IntVec3 c, float r2, bool spareBroken)
        {
            near.Clear();
            targets.Clear();
            AbilityAIUtility.CollectHostiles(ctx, ext, targets);
            for (int i = 0; i < targets.Count; i++)
            {
                Thing t = targets[i];
                if ((t.Position - c).LengthHorizontalSquared > r2) continue;
                if (spareBroken && t is Pawn p && ctx.IsBrokenAlly(p)) continue;
                if (AbilityAIUtility.WouldAffect(ability, ext, t)) near.Add(t);
            }
            targets.Clear();
        }

        // The cell on the way from the caster to the point at which the point sits keep tiles away
        private static IntVec3 Toward(IntVec3 from, IntVec3 to, float keep)
        {
            float d = (to - from).LengthHorizontal;
            if (d <= keep) return from;
            float f = (d - keep) / d;
            return new IntVec3(from.x + Mathf.RoundToInt((to.x - from.x) * f), 0, from.z + Mathf.RoundToInt((to.z - from.z) * f));
        }

        private void AddSpot(AbilityAIContext ctx, IntVec3 c, IntVec3 from, float maxSq, float r2, int floor, bool spareBroken)
        {
            float d = (c - from).LengthHorizontalSquared;
            if (d > maxSq || c == from || !c.InBounds(ctx.map) || !c.Standable(ctx.map)) return;
            int n = CountNear(c, r2);
            if (n < floor) return;
            if (ext.avoidAllies && AbilityAIUtility.AllyWithin(ctx, ext, c, r2, spareBroken, true)) return;
            spots.Add(new Spot { cell = c, count = n, dist = d });
        }

        private static int CountNear(IntVec3 c, float r2)
        {
            int n = 0;
            for (int i = 0; i < near.Count; i++)
                if ((near[i].Position - c).LengthHorizontalSquared <= r2) n++;
            return n;
        }
    }

    /// <summary>
    /// Cone hostile: a cone or line from the caster (fire spew, a breath). The shape is rebuilt from its numbers
    /// rather than read from the effect's own cells, which test line of sight from every cell in range: length = the
    /// effect's cone length, else the ability's range (maxRange only limits how far off an aim may stand, since the
    /// shape reaches its full length whatever the aim's distance); width at the far end = coneWidth, else the
    /// effect's, else 1 (a line). As vanilla's fire spew, the cone reaches its full length whatever the distance to
    /// the target, and covers the straight line to its end. Each affectable enemy between minRange and the length is
    /// an aim, scored by the affectable enemies inside (WouldAffect), at least minTargets. An aim with anyone on the
    /// caster's side inside (AbilityAIUtility.AllyIn: allyKinds, downed ones unless harmless) is always dropped, and with
    /// avoidBuildings one with a friendly building inside. Walls are ignored, so an ally behind one still blocks. The
    /// best five are validated. Approach: the same aims out to the length + approachDistance. With avoidAllies and a
    /// radius, an aim with anyone of the caster's side (allyKinds) within that radius of it is dropped too: someone
    /// fighting the target may step into the shape before the effect is over.
    /// Cost: k WouldAffect checks and k² point tests, k×a for the allies; with avoidBuildings a pass over the cells
    /// of the cone's bounding box for each of the five.
    /// </summary>
    public class AbilityAIWorker_ConeHostile : AbilityAIWorker
    {
        public override bool TargetsEnemies => true;
        // along: how far the shape reaches; lateral half width = max(minHalf, distance along the axis × tanHalf)
        private struct Shape { public float length, tanHalf, minHalf; }
        private struct Aim { public Pawn pawn; public int count; public float distSq; public Shape shape; }
        private static readonly List<Aim> aims = new List<Aim>();
        private static readonly List<bool> affected = new List<bool>();
        private const int MaxTries = 5;
        // Cells on the line to the cone's end count whatever the width (vanilla adds the Bresenham line)
        private const float LineHalfWidth = 0.75f;
        // A beam is drawn to the aimed cell's centre; half a cell past it covers the cell itself
        private const float BeamSlack = 0.5f;

        /// <summary>The shape ends at the aimed cell and has the same width all along (BeamHostile).</summary>
        protected virtual bool Beam => false;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            float length = Length(ability);
            float halfWidth = HalfWidth(ability);
            if (!Score(ctx, ability, length, halfWidth, AimReach(length))) return false;
            for (int i = 0; i < aims.Count && i < MaxTries; i++)
            {
                if (ext.avoidBuildings && AbilityAIUtility.FriendlyBuildingIn(ctx, ConeTo(ctx.pawn.Position, aims[i].pawn.Position, aims[i].shape))) continue;
                target = AbilityAIUtility.PawnOrCell(ability, aims[i].pawn);
                if (target.IsValid) break;
            }
            aims.Clear();
            return target.IsValid;
        }

        public override bool TryFindApproachTarget(AbilityAIContext ctx, AIAbility ability, float reach, out Thing goal)
        {
            goal = null;
            float length = Length(ability);
            if (!Score(ctx, ability, length, HalfWidth(ability), AimReach(length) + ext.approachDistance)) return false;
            for (int i = 0; i < aims.Count && i < MaxTries; i++)
            {
                Pawn p = aims[i].pawn;
                if (AbilityAIDriver.ValidIgnoringRange(ability, p) || AbilityAIDriver.ValidIgnoringRange(ability, p.Position)) { goal = p; break; }
            }
            aims.Clear();
            return goal != null;
        }

        // The shape always reaches its full length (vanilla extends the spew to its range whatever the target's
        // distance), so allies and buildings are checked over all of it; maxRange only limits how far off an aim may be
        private static float Length(AIAbility ability)
        {
            var e = ability.Effects;
            return e.cone && e.coneRange > 0f ? e.coneRange : ability.Range;
        }

        private float AimReach(float length) => ext.maxRange >= 0f ? Mathf.Min(length, ext.maxRange) : length;

        private float HalfWidth(AIAbility ability)
        {
            var e = ability.Effects;
            return (ext.coneWidth >= 0f ? ext.coneWidth : e.cone ? e.coneWidth : 1f) / 2f;
        }

        private Shape ShapeFor(float coneLength, float halfWidth, float aimDistSq)
        {
            if (Beam) return new Shape { length = Mathf.Sqrt(aimDistSq) + BeamSlack, tanHalf = 0f, minHalf = Mathf.Max(LineHalfWidth, halfWidth) };
            return new Shape { length = coneLength, tanHalf = halfWidth / coneLength, minHalf = LineHalfWidth };
        }

        // Inside the shape from the caster's cell toward aim: within its length, ahead of the caster, and no further
        // from the axis than its width there
        private static bool InCone(IntVec3 from, Vector2 dir, in Shape s, IntVec3 c)
        {
            float vx = c.x - from.x, vz = c.z - from.z;
            float along = vx * dir.x + vz * dir.y;
            if (along <= 0f || vx * vx + vz * vz > s.length * s.length) return false;
            float lateral = Mathf.Abs(vx * dir.y - vz * dir.x);
            return lateral <= Mathf.Max(s.minHalf, along * s.tanHalf);
        }

        private static Vector2 Dir(IntVec3 from, IntVec3 to) => new Vector2(to.x - from.x, to.z - from.z).normalized;

        private static ConeArea ConeTo(IntVec3 from, IntVec3 aim, in Shape s) => new ConeArea(from, Dir(from, aim), s);

        // Fills aims, best first; false when there are none. reach: how far away an aim may stand.
        private bool Score(AbilityAIContext ctx, AIAbility ability, float length, float halfWidth, float reach)
        {
            aims.Clear();
            if (length < 1f) return false;
            var enemies = ctx.Enemies;
            if (enemies.Count < ext.minTargets) return false;
            bool spareBroken = !AbilityAIUtility.MayHitBrokenAllies(ext);
            IntVec3 origin = ctx.pawn.Position;
            // Aims stand within reach and hits within length of the caster: nobody further can count, so they are
            // marked unaffected without their WouldAffect
            float outer = Mathf.Max(length, reach) + 1f, outerSq = outer * outer;
            affected.Clear();
            int affectable = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                bool a = (enemies[i].Position - origin).LengthHorizontalSquared <= outerSq
                    && (!spareBroken || !ctx.IsBrokenAlly(enemies[i])) && AbilityAIUtility.WouldAffect(ability, ext, enemies[i]);
                affected.Add(a);
                if (a) affectable++;
            }
            if (affectable < ext.minTargets) return false;

            Pawn pawn = ctx.pawn;
            IntVec3 from = pawn.Position;
            float reachSq = reach * reach, minSq = ext.minRange * ext.minRange;
            float nearAimSq = ext.avoidAllies && ext.radius > 0f ? ext.radius * ext.radius : -1f;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (!affected[i]) continue;
                IntVec3 at = enemies[i].Position;
                float d = (at - from).LengthHorizontalSquared;
                if (d > reachSq || d < minSq || at == from) continue;
                Vector2 dir = Dir(from, at);
                Shape shape = ShapeFor(length, halfWidth, d);
                // A beam's width is a margin for allies; only the pawns on the line itself are hit
                Shape hits = shape;
                if (Beam) hits.minHalf = LineHalfWidth;
                int count = 0;
                for (int j = 0; j < enemies.Count; j++)
                    if (affected[j] && InCone(from, dir, hits, enemies[j].Position)) count++;
                if (count < ext.minTargets) continue;
                if (AbilityAIUtility.AllyIn(ctx, ext, new ConeArea(from, dir, shape), spareBroken, pawn)) continue;
                if (nearAimSq > 0f && AbilityAIUtility.AllyWithin(ctx, ext, at, nearAimSq, spareBroken, true)) continue;
                aims.Add(new Aim { pawn = enemies[i], count = count, distSq = d, shape = shape });
            }
            aims.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
            return aims.Count > 0;
        }

        // The cone or beam as an IAIArea: open behind the caster and flat at the far end, unlike a disc or a capsule
        private readonly struct ConeArea : IAIArea
        {
            private readonly IntVec3 from;
            private readonly Vector2 dir;
            private readonly Shape s;

            public ConeArea(IntVec3 from, Vector2 dir, Shape s)
            {
                this.from = from;
                this.dir = dir;
                this.s = s;
            }

            public bool Contains(IntVec3 c) => InCone(from, dir, s, c);

            public CellRect Bounds
            {
                get
                {
                    float length = s.length;
                    float side = Mathf.Max(s.minHalf, length * s.tanHalf);
                    float ex = from.x + dir.x * length, ez = from.z + dir.y * length;
                    float px = -dir.y * side, pz = dir.x * side;
                    // A beam is as wide at the caster as at its end (minHalf), which a wide one aimed diagonally needs
                    float qx = -dir.y * s.minHalf, qz = dir.x * s.minHalf;
                    int minX = Mathf.FloorToInt(Mathf.Min(Mathf.Min(from.x + qx, from.x - qx), Mathf.Min(ex + px, ex - px))) - 1;
                    int maxX = Mathf.CeilToInt(Mathf.Max(Mathf.Max(from.x + qx, from.x - qx), Mathf.Max(ex + px, ex - px))) + 1;
                    int minZ = Mathf.FloorToInt(Mathf.Min(Mathf.Min(from.z + qz, from.z - qz), Mathf.Min(ez + pz, ez - pz))) - 1;
                    int maxZ = Mathf.CeilToInt(Mathf.Max(Mathf.Max(from.z + qz, from.z - qz), Mathf.Max(ez + pz, ez - pz))) + 1;
                    return CellRect.FromLimits(minX, minZ, maxX, maxZ);
                }
            }
        }
    }

    /// <summary>
    /// Beam hostile: a straight beam from the caster to the aimed cell that hurts everyone on it, held for a while
    /// (VPE's Frost Ray). Cone hostile's rules with the shape ending at the aim and coneWidth (else 1) as its width
    /// all along, never under 1.5: an ally anywhere on the way, or beside it, drops the aim, not only one near the
    /// target. A pawn crossing the beam later is hit too, so give the rule a coneWidth wider than the beam and, with
    /// avoidAllies, a radius for allies around the target. The enemies counted are the ones on the beam.
    /// </summary>
    public class AbilityAIWorker_BeamHostile : AbilityAIWorker_ConeHostile
    {
        protected override bool Beam => true;
    }
}
