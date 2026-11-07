using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Verbs on worn gear (packs, belts, lances, armour launchers: CompApparelVerbOwner), as the AI sees them. Their rules
// are AbilityAIExtensions on the item's ThingDef, which is also the def the player's autocast mode is kept under. One
// wrapper per verb, made the first time the AI looks at it and kept while the verb lives. Vanilla's own AI only uses
// four of these items, from their own triggers (AbilityAIApparel.cs stands those down where this mod decides); every
// other one is onlyManualCast, so NPCs never use it without this.
namespace AbilityAI
{
    public sealed class ApparelAIAbility : AIAbility
    {
        public readonly Verb verb;
        public readonly CompApparelVerbOwner owner;

        private static readonly ConditionalWeakTable<Verb, ApparelAIAbility> wrappers = new ConditionalWeakTable<Verb, ApparelAIAbility>();

        private ApparelAIAbility(Verb verb, CompApparelVerbOwner owner)
        {
            this.verb = verb;
            this.owner = owner;
        }

        public static ApparelAIAbility Of(Verb verb, CompApparelVerbOwner owner)
        {
            if (wrappers.TryGetValue(verb, out var w)) return w;
            w = new ApparelAIAbility(verb, owner);
            wrappers.Add(verb, w);
            return w;
        }

        // Item defs that carry a rule, filled at startup (Note). The source's per-pawn checks are a set lookup per worn item.
        private static readonly HashSet<ThingDef> ruleDefs = new HashSet<ThingDef>();
        private static readonly Dictionary<ThingDef, int> verbIndex = new Dictionary<ThingDef, int>();

        internal static bool Any => ruleDefs.Count > 0;

        internal static bool HasRules(ThingDef def) => ruleDefs.Contains(def);

        internal static IEnumerable<ThingDef> RuleDefs => ruleDefs;

        /// <summary>Called at startup for every ThingDef: an item with a verb owner comp and a rule becomes one the AI
        /// can use.</summary>
        internal static void Note(ThingDef def)
        {
            var exts = def.modExtensions;
            if (exts == null || def.comps == null) return;
            AbilityAIExtension first = null;
            for (int i = 0; i < exts.Count && first == null; i++) first = exts[i] as AbilityAIExtension;
            if (first == null) return;
            bool owns = false;
            for (int i = 0; i < def.comps.Count && !owns; i++)
                owns = def.comps[i].compClass != null && typeof(CompApparelVerbOwner).IsAssignableFrom(def.comps[i].compClass);
            if (!owns)
            {
                Log.Warning($"[Ability AI] {def.defName} has an Ability AI rule but no verb owner comp (CompProperties_ApparelVerbOwner or a subclass): the rule is ignored.");
                return;
            }
            int index = Mathf.Max(0, first.verbIndex);
            if (index >= def.Verbs.Count)
            {
                Log.Warning($"[Ability AI] {def.defName}'s rule names verb {index}, but it has {def.Verbs.Count}: the rule is ignored.");
                return;
            }
            ruleDefs.Add(def);
            verbIndex[def] = index;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension e && e.workerClass != null && typeof(AbilityAIWorker_Deploy).IsAssignableFrom(e.workerClass))
                    AbilityAIWorker_Deploy.Used = true;
        }

        /// <summary>The verb the rules are for on an item with several (DMS's Stugna: missile and launcher drop).</summary>
        private static Verb RuledVerb(Apparel apparel, out CompApparelVerbOwner comp)
        {
            comp = apparel.GetComp<CompApparelVerbOwner>();
            var verbs = comp?.AllVerbs;
            if (verbs == null) return null;
            int i = verbIndex.TryGetValue(apparel.def, out int v) ? v : 0;
            return i < verbs.Count ? verbs[i] : null;
        }

        /// <summary>The wrapper for a worn item's ruled verb, or null.</summary>
        internal static ApparelAIAbility For(Apparel apparel)
        {
            if (apparel == null || !ruleDefs.Contains(apparel.def)) return null;
            Verb v = RuledVerb(apparel, out var comp);
            return v == null ? null : Of(v, comp);
        }

        internal static readonly IAIAbilitySource Source = new ApparelSource();

        private sealed class ApparelSource : IAIAbilitySource
        {
            public bool HasAny(Pawn pawn) => AnyRuled(pawn);

            public bool AnyWithExtension(Pawn pawn) => AnyRuled(pawn);

            private static bool AnyRuled(Pawn pawn)
            {
                if (ruleDefs.Count == 0) return false;
                var worn = pawn.apparel?.WornApparel;
                if (worn == null) return false;
                for (int i = 0; i < worn.Count; i++)
                    if (ruleDefs.Contains(worn[i].def)) return true;
                return false;
            }

            public void Collect(Pawn pawn, List<AIAbility> into)
            {
                if (ruleDefs.Count == 0) return;
                var worn = pawn.apparel?.WornApparel;
                if (worn == null) return;
                for (int i = 0; i < worn.Count; i++)
                {
                    if (!ruleDefs.Contains(worn[i].def)) continue;
                    Verb v = RuledVerb(worn[i], out var comp);
                    if (v != null) into.Add(Of(v, comp));
                }
            }

            public bool TryGetRunningCast(Pawn pawn, out AIAbility ability, out int warmupTicksLeft)
            {
                ability = null;
                warmupTicksLeft = -1;
                Job job = pawn.CurJob;
                Verb v = job?.verbToUse;
                if (v == null || !IsCastJob(job.def) || !(v.DirectOwner is CompApparelVerbOwner comp)) return false;
                ability = Of(v, comp);
                warmupTicksLeft = pawn.stances?.curStance is Stance_Warmup w && w.verb == v ? w.ticksLeft : -1;
                return true;
            }
        }

        private static bool IsCastJob(JobDef def) => def == JobDefOf.UseVerbOnThing || def == JobDefOf.UseVerbOnThingStatic
            || def == JobDefOf.UseVerbOnThingStaticReserve || def == JobDefOf.CastJump || ApparelVerbJobs.IsCastJob(def);

        public override Pawn Pawn => owner.Wearer;
        public override Def Def => owner.parent.def;
        public override object Raw => verb;

        private VerbProperties Props => verb.verbProps;

        public override bool CanCastNow
        {
            get
            {
                Pawn p = owner.Wearer;
                if (p == null || !p.Spawned || verb.caster != p || verb.Bursting) return false;
                // As the gizmo disables it (CompApparelVerbOwner.CreateVerbTargetCommand)
                if (Props.violent && p.WorkTagIsDisabled(WorkTags.Violent)) return false;
                // The item's own check and the registered ones first: they answer quietly, while some modded verbs'
                // Available() post a message on every refusal (More Hunter Drones' Verb_LaunchDrone with no charge,
                // Anomalies Expected's beam with no beam target)
                if (!owner.CanBeUsed(out _) || !ApparelVerbJobs.Ready(verb)) return false;
                // Only the reloadable subclass counts charges in CanBeUsed; a plain charged owner kept after its last
                // charge (More Hunter Drones' packs, destroyOnEmpty false) passed it and reached the messaging
                // Available() on every check (test run of build 0234)
                if (owner is CompApparelVerbOwner_Charged charged && charged.RemainingCharges <= 0) return false;
                return verb.Available();
            }
        }

        public override string NotReadyReason
        {
            get
            {
                Pawn p = owner.Wearer;
                if (p == null || !p.Spawned || verb.caster != p) return "not worn by this pawn";
                if (verb.Bursting) return "still firing";
                if (Props.violent && p.WorkTagIsDisabled(WorkTags.Violent)) return "incapable of violence";
                if (!owner.CanBeUsed(out string reason)) return reason.NullOrEmpty() ? "no charges left" : reason;
                if (owner is CompApparelVerbOwner_Charged charged && charged.RemainingCharges <= 0) return "no charges left";
                if (!ApparelVerbJobs.Ready(verb)) return "the item's own limit (all it allows are out, or what it needs is missing)";
                return "the item refused";
            }
        }

        public override bool UsesCharges => owner is CompApparelVerbOwner_Charged;
        public override int RemainingCharges => owner is CompApparelVerbOwner_Charged c ? c.RemainingCharges : 0;
        public override bool SingleUse => owner is CompApparelVerbOwner_Charged c && c.Props.destroyOnEmpty;

        public override bool IsPsycast => false;
        public override float Range => IsSelfCast ? 0f : verb.EffectiveRange;
        // Else what the verb's targeter highlights: VGE's bombardment targeter takes it from whichever gravship turret
        // it is linked to, which no def says
        public override float DefaultRadius => Effects.hasEffectRadius ? Effects.effectRadius : HighlightRadius();

        private float HighlightRadius()
        {
            if (IsSelfCast) return 0f;
            try { return Mathf.Max(0f, verb.HighlightFieldRadiusAroundTarget(out _)); }
            catch { return 0f; }
        }
        public override bool IsSelfCast => !Props.targetable;
        public override bool CanTargetSelf => IsSelfCast || Props.targetParams?.canTargetSelf == true;
        public override float WarmupSeconds => Props.warmupTime * (owner.Wearer?.GetStatValue(StatDefOf.AimingDelayFactor) ?? 1f);
        public override Verb Verb => verb;
        public override AIAbilityEffects Effects => EffectsOf(owner.parent.def, Props);
        // A non-violent verb aimed at enemies (the disruptor flare) is hostile too, by its rules
        public override bool Hostile => Props.targetable && (Props.violent || RulesTargetEnemies(owner.parent.def));

        private static readonly Dictionary<ThingDef, bool> rulesTargetEnemies = new Dictionary<ThingDef, bool>();

        private static bool RulesTargetEnemies(ThingDef def)
        {
            if (rulesTargetEnemies.TryGetValue(def, out bool hostile)) return hostile;
            var exts = def.modExtensions;
            if (exts != null)
                for (int i = 0; i < exts.Count && !hostile; i++)
                    hostile = exts[i] is AbilityAIExtension ext && ext.Worker != null && ext.Worker.TargetsEnemies;
            rulesTargetEnemies[def] = hostile;
            return hostile;
        }

        public override bool ValidIgnoringRange(LocalTargetInfo target)
        {
            if (!target.IsValid) return false;
            Pawn pawn = owner.Wearer;
            if (pawn?.Map == null) return false;
            if (IsSelfCast) return target.Thing == pawn || (!target.HasThing && target.Cell == pawn.Position);
            if (!target.Cell.InBounds(pawn.Map)) return false;
            if (verb is Verb_Jump) return !target.HasThing && JumpUtility.ValidJumpTarget(pawn, pawn.Map, target.Cell);
            if (target.HasThing)
            {
                if (Props.targetParams != null && !Props.targetParams.CanTarget(target.ToTargetInfo(pawn.Map))) return false;
            }
            else if (!TakesCells) return false;
            // Its own check reads Find.CurrentMap, the map the player is looking at, not the wearer's: the same check
            // here on the wearer's map (what the rest of its chain checks is charges, which CanCastNow passed). Its job
            // (UseVerbOnThingStaticReserve) reserves the target, so one another pawn holds would fail at the start.
            if (verb is Verb_LaunchProjectileStaticOneUse)
                return target.Cell.GetFirstBuilding(pawn.Map) == null && target.Cell.Standable(pawn.Map) && pawn.CanReserve(target);
            return ApparelVerbJobs.SkipsValidate(verb.GetType()) || ValidateOffGui(target);
        }

        // Verb_CastBase (the lances, the orbital targeters), Verb_LaunchProjectileStatic and Verb_Jump check charges
        // through ReloadableUtility.CanUseConsideringQueuedJobs, which reads Event.current.shift: null outside the GUI,
        // so asked from a tick it throws. A blank event stands in while the verb is asked (shift up: only the charges
        // are checked, which CanCastNow already passed, so no message is posted), and is taken away after.
        private static readonly Event blankEvent = new Event();

        private bool ValidateOffGui(LocalTargetInfo target)
        {
            if (Event.current != null) return verb.ValidateTarget(target, false);
            Event.current = blankEvent;
            try { return verb.ValidateTarget(target, false); }
            finally { Event.current = null; }
        }

        // A projectile lands on a cell as well as a pawn; a target effect (the lances) needs a pawn
        private bool TakesCells => verb is Verb_LaunchProjectile || Props.targetParams?.canTargetLocations == true;

        public override bool ValidInRange(LocalTargetInfo target) => IsSelfCast || verb.CanHitTarget(target);

        public override float PsyfocusCost(LocalTargetInfo target) => 0f;
        public override float HeatGain => 0f;

        public override Job MakeCastJob(LocalTargetInfo target, LocalTargetInfo dest)
        {
            Pawn pawn = owner.Wearer;
            if (pawn == null) return null;
            Job job;
            if (ApparelVerbJobs.TryMake(verb, target, out job)) { }
            // As Targeter.BeginTargeting starts an untargetable verb that isn't instant; an instant one
            // (nonInterruptingSelfCast) goes through the same job, which casts on its first tick
            else if (IsSelfCast) job = JobMaker.MakeJob(JobDefOf.UseVerbOnThing, pawn);
            else if (verb is Verb_Jump) job = JobMaker.MakeJob(JobDefOf.CastJump, target.Cell);
            else if (verb is Verb_LaunchProjectileStaticOneUse) job = JobMaker.MakeJob(JobDefOf.UseVerbOnThingStaticReserve, target);
            // Static for the rest: the driver has already found the cast position, and the base OrderForceTarget's
            // AttackStatic would read the pawn's weapon instead of this verb
            else job = JobMaker.MakeJob(JobDefOf.UseVerbOnThingStatic, target);
            if (job == null) return null;
            job.verbToUse = verb;
            job.endIfCantShootTargetFromCurPos = !IsSelfCast;
            return job;
        }

        private static readonly Dictionary<ThingDef, AIAbilityEffects> effects = new Dictionary<ThingDef, AIAbilityEffects>();

        internal static void ClearEffectsCache() => effects.Clear();

        // What the verb's projectile, the item's own comps and its target effects do, read once per item def
        private static AIAbilityEffects EffectsOf(ThingDef def, VerbProperties vp)
        {
            if (effects.TryGetValue(def, out var e)) return e;
            e = new AIAbilityEffects();
            var proj = vp.defaultProjectile?.projectile;
            if (proj != null)
            {
                if (proj.explosionRadius > 0f) { e.hasEffectRadius = true; e.effectRadius = proj.explosionRadius; }
                if (proj.damageDef == DamageDefOf.Smoke || proj.postExplosionGasType == GasType.BlindSmoke) e.smoke = true;
                if (proj.damageDef != null && proj.damageDef.causeStun) e.stuns = true;
                e.spawns = proj.spawnsThingDef ?? proj.postExplosionSpawnThingDef ?? proj.spawnsPawnKind?.race;
            }
            // A verb that makes a pawn itself (Fortified's Verb_Deploy, the DMS drone packs) names its kind here
            if (e.spawns == null && vp.spawnDef != null) e.spawns = vp.spawnDef;
            Type vc = vp.verbClass;
            if (vc != null && typeof(Verb_SmokePop).IsAssignableFrom(vc))
            {
                e.smoke = true;
                e.hasEffectRadius = true;
                e.effectRadius = def.GetStatValueAbstract(StatDefOf.PackRadius);
            }
            else if (vc != null && typeof(Verb_DeployBroadshield).IsAssignableFrom(vc))
            {
                ThingDef shield = ThingDefOf.BroadshieldProjector;
                e.spawns = shield;
                e.hasEffectRadius = true;
                e.effectRadius = shield.GetCompProperties<CompProperties_ProjectileInterceptor>()?.radius ?? 0f;
            }
            if (typeof(Verb_CastTargetEffectLances).IsAssignableFrom(vc ?? typeof(Verb))) e.psychic = true;
            for (int i = 0; i < def.comps.Count; i++)
            {
                var c = def.comps[i];
                if (c is CompProperties_Explosive ex && !e.hasEffectRadius) { e.hasEffectRadius = true; e.effectRadius = ex.explosiveRadius; }
                else if (c is CompProperties_ReleaseGas gas)
                {
                    if (gas.gasType == GasType.BlindSmoke) e.smoke = true;
                    if (!e.hasEffectRadius) { e.hasEffectRadius = true; e.effectRadius = GenRadial.RadiusOfNumCells(Mathf.RoundToInt(gas.cellsToFill)); }
                }
                Type cc = c.compClass;
                if (cc == null) continue;
                if (typeof(CompTargetEffect_PsychicShock).IsAssignableFrom(cc)) e.stuns = true;
                else if (typeof(CompTargetEffect_Berserk).IsAssignableFrom(cc)) e.mentalState = true;
            }
            effects[def] = e;
            return e;
        }
    }

    /// <summary>
    /// How another mod's apparel verb is cast, for one whose own order makes a job of its own (a drone or mech
    /// deployment): register the verb type and the job it needs. A type registered here, and its subclasses, are cast
    /// with that job; skipValidate for a verb whose ValidateTarget can't be asked off the GUI even with a blank
    /// Event.current standing in (it posts messages, or reads the targeter's selection).
    /// Register before play. Types are matched most derived first.
    /// </summary>
    public static class ApparelVerbJobs
    {
        private struct Entry
        {
            public Type verbType;
            public Func<Verb, LocalTargetInfo, Job> make;
            public bool skipValidate;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly HashSet<JobDef> jobDefs = new HashSet<JobDef>();
        private static readonly List<KeyValuePair<Type, Func<Verb, bool>>> readyChecks = new List<KeyValuePair<Type, Func<Verb, bool>>>();
        private static readonly Dictionary<Type, Func<Verb, bool>> readyByType = new Dictionary<Type, Func<Verb, bool>>();

        /// <summary>A check of the verb's own that its Available() leaves out and its cast would refuse after the
        /// warmup, with a message (Apex's mechamancer pack: at most two satellites out). Answer quietly; a throw counts
        /// as not ready and drops the check for the session.</summary>
        public static void RegisterReady(Type verbType, Func<Verb, bool> ready)
        {
            if (verbType == null || ready == null) return;
            readyChecks.RemoveAll(x => x.Key == verbType);
            readyChecks.Add(new KeyValuePair<Type, Func<Verb, bool>>(verbType, ready));
            readyByType.Clear();
        }

        internal static bool Ready(Verb verb)
        {
            if (readyChecks.Count == 0) return true;
            Type t = verb.GetType();
            if (!readyByType.TryGetValue(t, out var check))
            {
                Type best = null;
                for (int i = 0; i < readyChecks.Count; i++)
                    if (readyChecks[i].Key.IsAssignableFrom(t) && (best == null || best.IsAssignableFrom(readyChecks[i].Key)))
                    {
                        best = readyChecks[i].Key;
                        check = readyChecks[i].Value;
                    }
                readyByType[t] = check;
            }
            if (check == null) return true;
            try { return check(verb); }
            catch (Exception e)
            {
                Log.Error($"[Ability AI] The ready check for {t.FullName} threw; it is dropped: {e}");
                readyChecks.RemoveAll(x => x.Value == check);
                readyByType.Clear();
                return false;
            }
        }


        public static void Register(Type verbType, Func<Verb, LocalTargetInfo, Job> make, bool skipValidate = false, JobDef castJob = null)
        {
            if (verbType == null || make == null) return;
            entries.RemoveAll(x => x.verbType == verbType);
            entries.Add(new Entry { verbType = verbType, make = make, skipValidate = skipValidate });
            if (castJob != null) jobDefs.Add(castJob);
        }

        internal static bool IsCastJob(JobDef def) => jobDefs.Count > 0 && jobDefs.Contains(def);

        private static int Find(Type t)
        {
            int best = -1;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].verbType.IsAssignableFrom(t) && (best < 0 || entries[best].verbType.IsAssignableFrom(entries[i].verbType))) best = i;
            return best;
        }

        internal static bool SkipsValidate(Type t)
        {
            int i = Find(t);
            return i >= 0 && entries[i].skipValidate;
        }

        internal static bool TryMake(Verb verb, LocalTargetInfo target, out Job job)
        {
            job = null;
            if (entries.Count == 0) return false;
            int i = Find(verb.GetType());
            if (i < 0) return false;
            try { job = entries[i].make(verb, target); }
            catch (Exception e)
            {
                Log.Error($"[Ability AI] The cast job for {entries[i].verbType.FullName} threw; it is dropped and the verb is cast as vanilla's: {e}");
                entries.RemoveAt(i);
                return false;
            }
            return true;
        }
    }
}
