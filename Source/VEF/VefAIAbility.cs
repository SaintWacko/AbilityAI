using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using VEF.Abilities;
using VefAbility = VEF.Abilities.Ability;
using VefAbilityDef = VEF.Abilities.AbilityDef;

// Vanilla Expanded Framework's abilities as the AI sees them. VEF's ability system shares nothing with vanilla's:
// its Ability is its own IExposable on CompAbilities, its cast job reads the ability from the comp rather than the
// Job, and its own Ability.StartAbilityJob starts the job itself. MakeCastJob does what StartAbilityJob does short of
// starting the job, so the think tree gets the job back like any other and the driver's resume and walk-back logic
// keeps working.
//
// Psycasts² rewrites range, radius and costs through postfixes on the GetXForPawn methods and pads targetCount and
// the targeting lists on a player's left-click, so ranges are always read through those methods and the def's
// shape (single target, self-cast, touch) is read once at startup, before anyone has clicked a gizmo.
namespace AbilityAI.Vef
{
    /// <summary>What a compat assembly built on this one (VPE's) knows about paying for VEF abilities: VEF itself has
    /// no costs, so without one every VEF ability is free and none is a psycast.</summary>
    public interface IVefAbilityCosts
    {
        /// <summary>Abilities of this def are paid for by this system, and are psycasts to the AI (psycast limits,
        /// psychic immunity). Asked once per def.</summary>
        bool Covers(VefAbilityDef def);

        /// <summary>The caster can pay for a cast now. Asked before the def's extensions' IsEnabledForPawn, which builds
        /// a translated reason on every refusal: answer quietly and refuse at least where those would for cost.</summary>
        bool Affordable(VefAbility ability);

        /// <summary>Psyfocus the cast costs at this target; without one, the least it can cost.</summary>
        float PsyfocusCost(VefAbility ability, LocalTargetInfo target);

        /// <summary>Neural heat before the caster's PsychicEntropyGain scales it.</summary>
        float HeatGain(VefAbility ability);
    }

    public sealed class VefAIAbility : AIAbility
    {
        public readonly VefAbility ability;

        private static readonly ConditionalWeakTable<VefAbility, VefAIAbility> wrappers = new ConditionalWeakTable<VefAbility, VefAIAbility>();
        private static readonly ConditionalWeakTable<VefAbility, VefAIAbility>.CreateValueCallback Make = a => new VefAIAbility(a);

        private VefAIAbility(VefAbility ability) { this.ability = ability; }

        public static VefAIAbility Of(VefAbility ability) => wrappers.GetValue(ability, Make);

        internal static readonly IAIAbilitySource Source = new VefSource();

        // What the AI reads off a def once: only defs with an AbilityAIExtension that the AI can cast get one
        private sealed class DefInfo
        {
            public bool self, touch, hostile;
            // Read off any extension with a float hemogenCost field (VPE Hemosage's), so no compat assembly is needed
            public float hemogenCost;
            // Target, then the cell it is sent to (VPE's Skip): only the Skip worker can cast it (TryGetDestRule)
            public bool twoTarget;
            public TargetingParameters targetParams, destParams;
            public AIAbilityEffects effects;
            // Resolved on first use: a cost system may register after Init
            public bool costsKnown;
            public IVefAbilityCosts costs;
        }

        private static readonly Dictionary<VefAbilityDef, DefInfo> infos = new Dictionary<VefAbilityDef, DefInfo>();
        // Defs looked at and not driven: no AbilityAIExtension, or a shape the AI can't target (warned about once)
        private static readonly HashSet<VefAbilityDef> notDriven = new HashSet<VefAbilityDef>();
        // Defs the AI doesn't cast, read when TryGetRunningCast wraps an enemy's cast of one (the Interrupt worker
        // asks it Hostile)
        private static readonly Dictionary<VefAbilityDef, DefInfo> otherInfos = new Dictionary<VefAbilityDef, DefInfo>();
        // Every JobDef whose driver is VEF's cast driver: how a running VEF cast is recognised from the job alone
        private static readonly HashSet<JobDef> castJobs = new HashSet<JobDef>();
        private static readonly List<Action<VefAbilityDef, AIAbilityEffects>> effectReaders = new List<Action<VefAbilityDef, AIAbilityEffects>>();
        private static readonly List<IVefAbilityCosts> costSystems = new List<IVefAbilityCosts>();
        private static readonly List<Func<VefAbility, bool>> otherAutocasters = new List<Func<VefAbility, bool>>();
        // Runtime ability types that declare their own IsEnabledForPawn (VPE's Conjure Heat Pearls, Farskip)
        private static readonly Dictionary<Type, bool> ownEnabledCheck = new Dictionary<Type, bool>();
        // Runtime ability types that declare their own ValidateTarget (Hemosage's Corpse Explosion and Sanguine Spear,
        // Runesmith's Invokes): the rules VEF's targeter enforces through it never reach CanHitTarget
        private static readonly Dictionary<Type, bool> ownTargetCheck = new Dictionary<Type, bool>();
        private static readonly Dictionary<Type, System.Reflection.FieldInfo> hemogenCostFields = new Dictionary<Type, System.Reflection.FieldInfo>();
        private static readonly GlobalTargetInfo[] oneTarget = new GlobalTargetInfo[1];

        /// <summary>Whether the AI can cast abilities of this def: it has an AbilityAIExtension and a shape the
        /// driver can target (one map target, or a thing and the cell to send it to; no confirmation dialog).</summary>
        public static bool Supported(VefAbilityDef def) => def != null && TryGetInfo(def, out _);

        /// <summary>The pawn's ability of this def, learned or held on an item (AIHeldAbilities), or null.</summary>
        internal static VefAbility FindOn(Pawn pawn, VefAbilityDef def) => VefSource.Find(pawn, def);

        // Init reads every def there is at startup. A def added later is read the first time the AI meets it: Entad
        // Framework registers its per-trait copies of VPE's defs from its own static constructor, which runs after
        // this assembly's because Entad loads after Ability AI.
        private static bool TryGetInfo(VefAbilityDef def, out DefInfo info)
        {
            if (infos.TryGetValue(def, out info)) return true;
            if (notDriven.Contains(def)) return false;
            Consider(def);
            return infos.TryGetValue(def, out info);
        }

        private static void Consider(VefAbilityDef def)
        {
            if (def.GetModExtension<AbilityAIExtension>() == null)
            {
                notDriven.Add(def);
                return;
            }
            string why = Unsupported(def);
            if (why != null)
            {
                Log.Warning($"[Ability AI] {def.defName} has an AbilityAIExtension, but {why}, so the AI leaves it to VEF.");
                notDriven.Add(def);
                return;
            }
            infos[def] = MakeInfo(def);
        }

        /// <summary>For a compat assembly built on this one (VPE's): fill in what an ability does from extensions
        /// this assembly doesn't know. Runs after VEF's own readers, once per def, the next time the AI asks.</summary>
        public static void RegisterEffectsReader(Action<VefAbilityDef, AIAbilityEffects> reader)
        {
            if (reader == null || effectReaders.Contains(reader)) return;
            effectReaders.Add(reader);
            foreach (var info in infos.Values) info.effects = null;
            foreach (var info in otherInfos.Values) info.effects = null;
        }

        /// <summary>For a compat assembly built on this one (VPE's): a system that prices VEF abilities. The first
        /// registered system that covers a def prices it.</summary>
        public static void RegisterCosts(IVefAbilityCosts costs)
        {
            if (costs == null || costSystems.Contains(costs)) return;
            costSystems.Add(costs);
            foreach (var info in infos.Values) info.costsKnown = false;
            foreach (var info in otherInfos.Values) info.costsKnown = false;
        }

        /// <summary>For a compat assembly built on this one (VPE's): another autocast mod that decides some VEF
        /// abilities for the player's pawns. True for an ability means Ability AI offers no menu for it and never casts
        /// it for that pawn. Asked on the player's casting path and per gizmo, so answer from a cached set.</summary>
        public static void RegisterPlayerAutocaster(Func<VefAbility, bool> decides)
        {
            if (decides != null && !otherAutocasters.Contains(decides)) otherAutocasters.Add(decides);
        }

        internal static void Init()
        {
            var jobDefs = DefDatabase<JobDef>.AllDefsListForReading;
            for (int i = 0; i < jobDefs.Count; i++)
                if (jobDefs[i].driverClass != null && typeof(JobDriver_CastAbilityOnce).IsAssignableFrom(jobDefs[i].driverClass))
                    castJobs.Add(jobDefs[i]);

            var defs = DefDatabase<VefAbilityDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
                if (!infos.ContainsKey(defs[i]) && !notDriven.Contains(defs[i])) Consider(defs[i]);
        }

        private static DefInfo MakeInfo(VefAbilityDef def)
        {
            bool self = def.targetModes != null && def.targetModes.Count > 0 && def.targetModes[0] == AbilityTargetingMode.Self;
            bool two = IsTwoTarget(def);
            return new DefInfo
            {
                self = self,
                twoTarget = two,
                destParams = two ? def.targetingParametersList[1] : null,
                touch = !self && def.jobDef?.driverClass != null
                    && typeof(JobDriver_GotoTargetAndCastAbilityOnce).IsAssignableFrom(def.jobDef.driverClass),
                hostile = IsHostile(def, self),
                hemogenCost = ReadHemogenCost(def),
                targetParams = def.targetingParametersList != null && def.targetingParametersList.Count > 0 ? def.targetingParametersList[0] : null,
            };
        }

        // VEF's own rule (Ability.AICanUseOn) when the def states one. Most VPE psycasts don't, so then our extensions'
        // workers say (Overshield and Stabilize are aimed at others but aren't attacks), then a goodwill cost (VPE's
        // "considered a hostile action"), and last anything aimed away from the caster is taken as an attack.
        private static bool IsHostile(VefAbilityDef def, bool self)
        {
            if (def.isPositive.HasValue) return !def.isPositive.Value;
            var exts = def.modExtensions;
            bool any = false, enemies = false;
            if (exts != null)
                for (int i = 0; i < exts.Count; i++)
                    if (exts[i] is AbilityAIExtension ext && ext.Worker != null)
                    {
                        any = true;
                        enemies |= ext.Worker.TargetsEnemies;
                    }
            if (any) return enemies;
            if (def.goodwillImpact < 0) return true;
            return !self;
        }

        private static float ReadHemogenCost(VefAbilityDef def)
        {
            var exts = def.modExtensions;
            if (exts == null) return 0f;
            float cost = 0f;
            for (int i = 0; i < exts.Count; i++)
            {
                if (exts[i] == null) continue;
                Type type = exts[i].GetType();
                if (!hemogenCostFields.TryGetValue(type, out var field))
                {
                    field = type.GetField("hemogenCost", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (field != null && field.FieldType != typeof(float)) field = null;
                    hemogenCostFields[type] = field;
                }
                if (field != null) cost += (float)field.GetValue(exts[i]);
            }
            return cost;
        }

        private IVefAbilityCosts Costs
        {
            get
            {
                var info = Info;
                if (!info.costsKnown)
                {
                    info.costs = null;
                    for (int i = 0; i < costSystems.Count && info.costs == null; i++)
                    {
                        try { if (costSystems[i].Covers(ability.def)) info.costs = costSystems[i]; }
                        catch (Exception e) { Log.ErrorOnce($"[Ability AI] cost system {costSystems[i].GetType().FullName} threw on {ability.def.defName}: {e}", ability.def.shortHash ^ 0x5C2); }
                    }
                    info.costsKnown = true;
                }
                return info.costs;
            }
        }

        // A thing, then the cell it goes to (VPE's Skip: Ability_Teleport.Cast moves targets[i] to targets[i + 1]).
        // At least two, not exactly: Psycasts² raises targetCount on the def for more pairs, and one pair is still a
        // cast VEF accepts. Not with hasAoE, whose ModifyTargets rebuilds the array around the first cell.
        private static bool IsTwoTarget(VefAbilityDef def)
        {
            var modes = def.targetModes;
            if (def.targetCount < 2 || modes == null || modes.Count < 2 || def.hasAoE) return false;
            if (def.targetingParametersList == null || def.targetingParametersList.Count < 2) return false;
            if (modes[1] != AbilityTargetingMode.Location) return false;
            var first = modes[0];
            return first == AbilityTargetingMode.Pawn || first == AbilityTargetingMode.Thing
                || first == AbilityTargetingMode.Humanlike || first == AbilityTargetingMode.None;
        }

        private static string Unsupported(VefAbilityDef def)
        {
            if (def.targetModes == null || def.targetModes.Count == 0) return "it has no target mode";
            if (def.targetCount != 1 && !IsTwoTarget(def)) return "it takes more than one target, other than a thing and the cell to send it to";
            if (def.worldTargeting) return "it targets the world map";
            // None is a def that gives targetingParameters and no targetMode (VPE's Overshield): VEF targets by the
            // parameters alone, as ValidIgnoringRange does. A def with neither was made Self when it was loaded.
            var mode = def.targetModes[0];
            if (mode == AbilityTargetingMode.Tile || mode == AbilityTargetingMode.Random)
                return $"its target mode is {mode}";
            // Its PreCast opens a dialog and holds the job back until the player answers
            if (def.HasModExtension<AbilityExtension_ConfirmationMessage>()) return "it asks for confirmation";
            return null;
        }

        private DefInfo Info
        {
            get
            {
                var def = ability.def;
                if (TryGetInfo(def, out var info)) return info;
                if (!otherInfos.TryGetValue(def, out info)) otherInfos[def] = info = MakeInfo(def);
                return info;
            }
        }

        public override Pawn Pawn => ability.pawn;
        public override Def Def => ability.def;
        public override object Raw => ability;

        public override bool CanCastNow
        {
            get
            {
                Pawn pawn = ability.pawn;
                if (pawn?.Map == null || ability.verb == null || !TryGetInfo(ability.def, out _)) return false;
                // MakeCastJob would refuse it (see there): don't pay for the target search first
                if (ability.def.keepCarryingThing == true && pawn.carryTracker?.CarriedThing != null) return false;
                return EnabledForPawn();
            }
        }

        // Ability.IsEnabledForPawn builds a translated reason string on every call, the success path included, and
        // the driver asks for every ready ability of every pawn it evaluates. For ability types that don't declare
        // their own, its three checks are made here without the string; a type that overrides it is asked as usual.
        private bool EnabledForPawn()
        {
            var type = ability.GetType();
            if (!ownEnabledCheck.TryGetValue(type, out bool own))
            {
                var method = type.GetMethod(nameof(VefAbility.IsEnabledForPawn), new[] { typeof(string).MakeByRefType() });
                own = method != null && method.DeclaringType != typeof(VefAbility);
                ownEnabledCheck[type] = own;
            }
            if (ability.cooldown > Find.TickManager.TicksGame) return false;
            var costs = Costs;
            if (costs != null && !costs.Affordable(ability)) return false;
            if (own) return ability.IsEnabledForPawn(out _);
            var exts = ability.AbilityModExtensions;
            for (int i = 0; i < exts.Count; i++)
                if (!exts[i].IsEnabledForPawn(ability, out _)) return false;
            return ability.def.Satisfied(ability.Hediff);
        }

        // The reason strings Ability.IsEnabledForPawn builds are fine here: only the dev log asks
        public override string NotReadyReason
        {
            get
            {
                Pawn pawn = ability.pawn;
                if (pawn?.Map == null || ability.verb == null) return "not on a map";
                if (!TryGetInfo(ability.def, out _)) return "its shape isn't supported";
                if (ability.def.keepCarryingThing == true && pawn.carryTracker?.CarriedThing != null) return "carrying something";
                int left = ability.cooldown - Find.TickManager.TicksGame;
                if (left > 0) return $"on cooldown ({left / 60f:0.#} s)";
                bool enabled = ability.IsEnabledForPawn(out string reason);
                var costs = Costs;
                if (costs != null && !costs.Affordable(ability))
                    return "can't pay its cost" + (enabled || reason.NullOrEmpty() ? "" : $" ({reason})");
                if (!enabled && !reason.NullOrEmpty()) return reason;
                return "its own checks refused it";
            }
        }

        public override bool Drivable => TryGetInfo(ability.def, out _);

        public override bool IsTouch => Info.touch;

        public override bool PlayerAutocastElsewhere
        {
            get
            {
                for (int i = 0; i < otherAutocasters.Count; i++)
                    if (otherAutocasters[i](ability)) return true;
                return false;
            }
        }

        public override bool IsPsycast => Costs != null;

        // A walk-up ability (VFEA_GotoTargetAndUseAbility) states a range of 999999: its job walks to the target, so
        // to the workers it is a touch ability
        public override float Range => Info.self || Info.touch ? 0f : ability.GetRangeForPawn();

        public override float DefaultRadius
        {
            get
            {
                // Not gated on hasAoE, which only draws VEF's targeting ring: VPE leaves it off for many area psycasts
                // (Eye Blast, Thunderbolt, Meteor Shower, Flashstorm) whose radius is still their area. Only the area
                // workers ask, and they ask because the rule says the ability is an area.
                float r = ability.GetRadiusForPawn();
                return r > 0f && r < 500f ? r : 0f;
            }
        }

        public override bool IsSelfCast => Info.self;

        public override bool CanTargetSelf => Info.self || Info.targetParams?.canTargetSelf == true;

        public override float WarmupSeconds => ability.GetCastTimeForPawn() / (float)GenTicks.TicksPerRealSecond;

        public override Verb Verb => ability.verb;

        public override AIAbilityEffects Effects
        {
            get
            {
                var info = Info;
                return info.effects ?? (info.effects = ReadEffects(ability.def));
            }
        }

        public override bool Hostile => Info.hostile;

        public override bool ValidIgnoringRange(LocalTargetInfo target)
        {
            if (!target.IsValid) return false;
            Pawn pawn = ability.pawn;
            Map map = pawn.Map;
            if (map == null) return false;
            var info = Info;
            if (info.self)
            {
                if (target.Thing != pawn) return false;
            }
            else
            {
                // Ability.CanHitTarget's target rule, without its range and sight. Not through Ability.targetParams:
                // its getter writes the caster into the def's shared parameters for self-cast modes.
                var tp = info.targetParams;
                if (tp != null && !((tp.canTargetLocations && tp.CanTarget(new TargetInfo(target.Cell, map)))
                    || tp.CanTarget(target.ToTargetInfo(map))))
                    return false;
                if (target.Thing != null && !ability.AICanUseOn(target.Thing)) return false;
            }
            // Every check the extensions would make on a player's cast, quietly: CanApplyOn and ValidateTarget while
            // targeting, Valid in CreateCastJob
            oneTarget[0] = target.ToGlobalTargetInfo(map);
            var exts = ability.AbilityModExtensions;
            for (int i = 0; i < exts.Count; i++)
            {
                var e = exts[i];
                if (!e.CanApplyOn(target, ability, false) || !e.ValidateTarget(target, ability, false) || !e.Valid(oneTarget, ability, false))
                {
                    oneTarget[0] = GlobalTargetInfo.Invalid;
                    return false;
                }
            }
            oneTarget[0] = GlobalTargetInfo.Invalid;
            return true;
        }

        public override bool ValidInRange(LocalTargetInfo target)
        {
            var info = Info;
            if (info.self) return true;
            // The type's own check only for a def that wants no sight line: it ends in the base ValidateTarget, whose
            // CanHitTarget would want one from where the caster stands now, which a walk-up cast doesn't need. Zone
            // casting needs it for VPE's Enchant (the quality the caster can reach) and Mend (only damaged things).
            if (info.touch)
                return ability.pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly) && (ability.def.requireLineOfSight || OwnTargetCheck(target));
            return ability.CanHitTarget(target) && OwnTargetCheck(target);
        }

        // Last, since an override may be dear (it often calls the base ValidateTarget, CanHitTarget again) and some
        // skip the range (Runesmith's Invokes only look at the circle's charge)
        private bool OwnTargetCheck(LocalTargetInfo target)
        {
            var type = ability.GetType();
            if (!ownTargetCheck.TryGetValue(type, out bool own))
            {
                var method = type.GetMethod(nameof(VefAbility.ValidateTarget), new[] { typeof(LocalTargetInfo), typeof(bool) });
                own = method != null && method.DeclaringType != typeof(VefAbility);
                ownTargetCheck[type] = own;
            }
            return !own || ability.ValidateTarget(target, false);
        }

        public override float PsyfocusCost(LocalTargetInfo target) => Costs?.PsyfocusCost(ability, target) ?? 0f;

        public override float HeatGain => Costs?.HeatGain(ability) ?? 0f;

        public override float HemogenCost => Info.hemogenCost;

        public override bool TryGetDestRule(out AIDestRule rule)
        {
            rule = default;
            if (!Info.twoTarget) return false;
            // CanHitTarget wants the cell strictly inside the range, and the worker's own test is inclusive
            rule.range = ability.GetRangeForPawn() - 0.5f;
            rule.fromCaster = true;
            rule.lineOfSight = ability.def.requireLineOfSight;
            return true;
        }

        // Ability.CanHitTarget for the second target, short of range and sight (the worker's): not callable here, since
        // it reads targetParams by the player's currentTargetingIndex
        public override bool ValidDest(LocalTargetInfo target, IntVec3 cell)
        {
            var info = Info;
            if (!info.twoTarget) return false;
            Pawn pawn = ability.pawn;
            Map map = pawn.Map;
            float min = ability.def.minRange;
            if (min > 0f && cell.DistanceTo(pawn.Position) <= min) return false;
            var tp = info.destParams;
            if (tp != null && !(tp.canTargetLocations && tp.CanTarget(new TargetInfo(cell, map)))) return false;
            var at = new LocalTargetInfo(cell);
            var exts = ability.AbilityModExtensions;
            for (int i = 0; i < exts.Count; i++)
                if (!exts[i].CanApplyOn(at, ability, false) || !exts[i].ValidateTarget(at, ability, false)) return false;
            return true;
        }

        public override Job MakeCastJob(LocalTargetInfo target, LocalTargetInfo dest)
        {
            Pawn pawn = ability.pawn;
            // VEF starts its own cast job with the def's keepCarryingThing; a think-tree job can't carry that
            // override, so the caster would drop what it holds
            if (ability.def.keepCarryingThing == true && pawn.carryTracker?.CarriedThing != null) return null;
            GlobalTargetInfo[] targets;
            if (Info.twoTarget)
            {
                // A worker other than Skip passes the target as its own destination: a cast that moves nothing
                if (!dest.IsValid || dest.Cell == target.Cell) return null;
                targets = new[] { target.ToGlobalTargetInfo(pawn.Map), dest.ToGlobalTargetInfo(pawn.Map) };
            }
            else targets = new[] { target.ToGlobalTargetInfo(pawn.Map) };
            var exts = ability.AbilityModExtensions;
            for (int i = 0; i < exts.Count; i++)
                if (!exts[i].Valid(targets, ability, false)) return null;
            bool now = true;
            ability.PreCast(targets, ref now, NoOp);
            if (!now) return null;

            var comp = ability.Comp;
            if (comp == null) return null;
            // The cast driver reserves from currentTargets (the player's targeting array) when the def asks for it
            if (ability.def.reserveTargets) ability.currentTargets = targets;
            comp.currentlyCasting = ability;
            ability.ModifyTargets(ref targets);
            comp.currentlyCastingTargets = targets;
            return JobMaker.MakeJob(ability.def.jobDef ?? VFE_DefOf_Abilities.VFEA_UseAbility, target);
        }

        private static readonly Action NoOp = () => { };

        private static AIAbilityEffects ReadEffects(VefAbilityDef def)
        {
            var e = new AIAbilityEffects();
            var exts = def.modExtensions;
            if (exts != null)
                for (int i = 0; i < exts.Count; i++)
                {
                    switch (exts[i])
                    {
                        case AbilityExtension_Stun _:
                            e.stuns = true;
                            break;
                        case AbilityExtension_GiveMentalState ms when !ms.applyToSelf:
                            e.mentalState = true;
                            break;
                        case AbilityExtension_Hediff h when h.hediff != null:
                            if (e.anyHediff == null) e.anyHediff = h.hediff;
                            if (!h.applyToCaster && e.targetHediff == null) e.targetHediff = h.hediff;
                            break;
                        case AbilityExtension_ExtraHediffs extra:
                            if (extra.onTarget != null && extra.onTarget.Count > 0)
                            {
                                if (e.targetHediff == null) e.targetHediff = extra.onTarget[0];
                                if (e.anyHediff == null) e.anyHediff = extra.onTarget[0];
                            }
                            else if (extra.onCaster != null && extra.onCaster.Count > 0 && e.anyHediff == null)
                                e.anyHediff = extra.onCaster[0];
                            break;
                        case AbilityExtension_Explosion ex when ex.explosionRadius > 0f && !e.hasEffectRadius:
                            e.hasEffectRadius = true;
                            e.effectRadius = ex.explosionRadius;
                            break;
                        case AbilityExtension_Spawn sp when sp.thing != null:
                            e.spawns = sp.thing;
                            break;
                        case AbilityExtension_Projectile p when p.projectile != null && !e.hasEffectRadius:
                            // A chaining bolt (VPE's Chain Bolt) reaches whatever stands within its bounce range of
                            // the first target
                            var chain = p.projectile.GetModExtension<VEF.Weapons.TeslaChainingProps>();
                            if (chain != null && chain.bounceRange > 0f)
                            {
                                e.hasEffectRadius = true;
                                e.effectRadius = chain.bounceRange;
                            }
                            break;
                        case AbilityExtension_Building b when b.building != null:
                            e.spawns = b.building;
                            break;
                    }
                }
            if (e.anyHediff == null) e.anyHediff = def.casterHediff;
            for (int i = 0; i < effectReaders.Count; i++)
            {
                try { effectReaders[i](def, e); }
                catch (Exception ex) { Log.ErrorOnce($"[Ability AI] effects reader {effectReaders[i].Method.DeclaringType?.FullName} threw on {def.defName}: {ex}", def.shortHash ^ 0x5B1); }
            }
            return e;
        }

        // VEF abilities kept on items (AIHeldAbilities) are cast through the holder's CompAbilities like learned ones:
        // the cast job reads currentlyCasting there, which is also how TryGetRunningCast sees them. They are never
        // added to LearnedAbilities, which would save them on the pawn as well as on the item.
        private sealed class VefSource : IAIAbilitySource
        {
            private static readonly List<object> held = new List<object>();

            public bool HasAny(Pawn pawn)
            {
                var comp = pawn.GetComp<CompAbilities>();
                if (comp == null) return false;
                return comp.LearnedAbilities.Count > 0 || (AIHeldAbilities.Any && AIHeldAbilities.HasAny(pawn));
            }

            public bool AnyWithExtension(Pawn pawn)
            {
                var list = pawn.GetComp<CompAbilities>()?.LearnedAbilities;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                    if (TryGetInfo(list[i].def, out _)) return true;
                // Asked before the per-pawn interval, so held abilities aren't collected here (a provider's collect
                // does more than a lookup: Entad re-points and refreshes each one); Collect sorts out the driven ones
                return AIHeldAbilities.Any && AIHeldAbilities.HasAny(pawn);
            }

            public void Collect(Pawn pawn, List<AIAbility> into)
            {
                var list = pawn.GetComp<CompAbilities>()?.LearnedAbilities;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                    if (TryGetInfo(list[i].def, out _)) into.Add(Of(list[i]));
                if (!AIHeldAbilities.Any) return;
                try
                {
                    AIHeldAbilities.Collect(pawn, held);
                    for (int i = 0; i < held.Count; i++)
                        if (held[i] is VefAbility a && TryGetInfo(a.def, out _)) into.Add(Of(a));
                }
                finally { held.Clear(); }
            }

            /// <summary>The pawn's ability of this def, learned or held, or null.</summary>
            internal static VefAbility Find(Pawn pawn, VefAbilityDef def)
            {
                var list = pawn.GetComp<CompAbilities>()?.LearnedAbilities;
                if (list == null) return null;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].def == def) return list[i];
                if (!AIHeldAbilities.Any) return null;
                try
                {
                    AIHeldAbilities.Collect(pawn, held);
                    for (int i = 0; i < held.Count; i++)
                        if (held[i] is VefAbility a && a.def == def) return a;
                    return null;
                }
                finally { held.Clear(); }
            }

            public bool TryGetRunningCast(Pawn pawn, out AIAbility ability, out int warmupTicksLeft)
            {
                ability = null;
                warmupTicksLeft = -1;
                var job = pawn.CurJob;
                if (job == null || !castJobs.Contains(job.def)) return false;
                var running = pawn.GetComp<CompAbilities>()?.currentlyCasting;
                if (running == null) return false;
                ability = Of(running);
                // The warmup is the Wait toil before the cast toil: the first toil, or the second after the walk-up
                // driver's walk (which it only adds for a target other than the caster). A driver of another shape
                // isn't guessed at.
                var driver = pawn.jobs.curDriver;
                int warmupToil;
                if (driver.GetType() == typeof(JobDriver_CastAbilityOnce)) warmupToil = 0;
                else if (driver.GetType() == typeof(JobDriver_GotoTargetAndCastAbilityOnce)) warmupToil = job.targetA.Thing == pawn ? 0 : 1;
                else return true;
                if (driver.CurToilIndex == warmupToil) warmupTicksLeft = driver.ticksLeftThisToil;
                return true;
            }
        }
    }
}
