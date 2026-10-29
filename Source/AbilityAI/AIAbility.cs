using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;
using Verse.AI;

// One ability as the AI sees it, whatever system it comes from. The driver and the workers only ever hold an
// AIAbility: vanilla's RimWorld.Ability is wrapped by VanillaAIAbility here, and a compat assembly adds its own kind
// (VEF's abilities) through AIAbilitySources without the core naming its types. One wrapper per ability object,
// made the first time the AI looks at it and kept while the ability lives, so the hot path allocates nothing.
//
// The host API (IAbilityAIHook, Handles, OthersStandDown, AbilityAIDriver.Valid on RimWorld.Ability) stays on
// vanilla's type: hooks vote on vanilla abilities only, and those entry points forward to the wrapper.
namespace AbilityAI
{
    /// <summary>What an ability does to the pawns it hits and what it leaves behind, read once per def. Shared
    /// between every wrapper of that def; read-only after it is built.</summary>
    public class AIAbilityEffects
    {
        public bool stuns, mentalState, psychic, notOnMechs;
        /// <summary>The hediff it puts on its target (not one it gives only the caster).</summary>
        public HediffDef targetHediff;
        /// <summary>The first hediff any of its effects gives, the caster's own buff included.</summary>
        public HediffDef anyHediff;
        /// <summary>The area the first effect that states one gives (a host comp through AbilityAIInfo, smokepop).
        /// Only meaningful with hasEffectRadius; without one the ability's own area radius applies.</summary>
        public bool hasEffectRadius;
        public float effectRadius;
        public bool smoke;
        public ThingDef spawns;
        public bool wall;
        public List<IntVec2> wallPattern;
        /// <summary>A cone from the caster (fire spew): its length and its width at the far end. Read by the cone
        /// worker; a host states its own through AbilityAIInfo.RegisterCone.</summary>
        public bool cone;
        public float coneRange, coneWidth;
        /// <summary>For a thing that isn't a pawn (a turret): whether the ability's effect is already on it, which
        /// skipAffected then leaves alone. Pawns go by targetHediff. Null = never.</summary>
        public Func<Thing, bool> thingAffected;
    }

    public abstract class AIAbility
    {
        public abstract Pawn Pawn { get; }

        /// <summary>The def the AI keys everything on: its extensions, the player's autocast mode, per-ability gaps.</summary>
        public abstract Def Def { get; }

        /// <summary>The wrapped object, for a compat assembly that knows its type.</summary>
        public abstract object Raw { get; }

        /// <summary>The vanilla ability, or null for any other kind. Hooks only vote on these.</summary>
        public virtual Ability Vanilla => null;

        public string Label => Def.label;

        /// <summary>The driver can cast it: some systems' abilities carry an AbilityAIExtension in a shape the AI
        /// can't target (VEF's multi-target abilities), and those stay with their own system.</summary>
        public virtual bool Drivable => true;

        /// <summary>A touch ability: it needs a target but has no range, and its cast job walks the caster up to it
        /// (the Word psycasts). The autocast menu calls its in-place mode plain "Autocast".</summary>
        public virtual bool IsTouch => false;

        /// <summary>Another autocast mod decides this ability for this player pawn (Better Autocasting, when the
        /// player gave it the abilities both mods have rules for). Ability AI then offers no menu and never casts it
        /// for the player; NPCs are unaffected.</summary>
        public virtual bool PlayerAutocastElsewhere => false;

        /// <summary>Castable this moment by its own system's rules: cooldown, charges, comps, and the cost it would
        /// pay (a psycast the pawn has the psyfocus for).</summary>
        public abstract bool CanCastNow { get; }

        /// <summary>Why CanCastNow is false, for the dev log; only asked when it is.</summary>
        public virtual string NotReadyReason => "not castable now";

        public virtual bool UsesCharges => false;
        public virtual int RemainingCharges => 0;

        /// <summary>Paid for with psyfocus and neural heat: the player's psycast limits apply, and a target without
        /// psychic sensitivity is immune.</summary>
        public abstract bool IsPsycast { get; }

        /// <summary>How far it reaches. 0 or less for a touch ability, whose cast job walks up to the target.</summary>
        public abstract float Range { get; }

        /// <summary>The ability's own area radius, used when no effect states one (AIAbilityEffects.effectRadius).</summary>
        public abstract float DefaultRadius { get; }

        /// <summary>Cast on the caster only.</summary>
        public abstract bool IsSelfCast { get; }

        /// <summary>Accepts the caster as its target (Painblock, Invisibility, Focus).</summary>
        public abstract bool CanTargetSelf { get; }

        public abstract float WarmupSeconds { get; }

        /// <summary>The verb CastPositionFinder looks for a cast position with.</summary>
        public abstract Verb Verb { get; }

        public abstract AIAbilityEffects Effects { get; }

        /// <summary>Meant for enemies: the Interrupt worker only stuns a caster warming up one of these.</summary>
        public abstract bool Hostile { get; }

        /// <summary>Every check a player's cast at this target would pass short of range and line of sight, plus the
        /// effects' own AI vetoes. Under the player rules (AbilityAIDriver.EvaluatingPlayer) vetoes that only exist
        /// to keep vanilla's AI off the player's pawns are replaced by their rule for everyone else.</summary>
        public abstract bool ValidIgnoringRange(LocalTargetInfo target);

        /// <summary>The verb's own check: range, line of sight, reachability.</summary>
        public abstract bool ValidInRange(LocalTargetInfo target);

        public bool Valid(LocalTargetInfo target) => ValidIgnoringRange(target) && ValidInRange(target);

        /// <summary>Psyfocus the cast would cost at this target; without a target, the least it can cost. 0 for
        /// anything that isn't a psycast.</summary>
        public abstract float PsyfocusCost(LocalTargetInfo target);

        /// <summary>Neural heat the cast adds before the caster's PsychicEntropyGain scales it. 0 for anything that
        /// isn't a psycast.</summary>
        public abstract float HeatGain { get; }

        /// <summary>The job that casts it, handed back to the think tree rather than started; null when the ability's
        /// own system refuses the cast at the last moment. It may set up the cast on its system's side (VEF records
        /// the ability and targets on CompAbilities), so only call it for a job that will be returned.</summary>
        public abstract Job MakeCastJob(LocalTargetInfo target, LocalTargetInfo dest);

        /// <summary>For an ability that sends its target to a second cell the caster picks (vanilla's and VPE's Skip):
        /// how far that cell may be and from where. False for every other ability.</summary>
        public virtual bool TryGetDestRule(out AIDestRule rule)
        {
            rule = default;
            return false;
        }

        /// <summary>The system's own checks on a destination for this target, past the rule's range and sight and the
        /// cell being standable, which the Skip worker checks first. Asked for the best few cells only.</summary>
        public virtual bool ValidDest(LocalTargetInfo target, IntVec3 cell) => true;
    }

    /// <summary>Where a two-target ability may send its first target (AIAbility.TryGetDestRule).</summary>
    public struct AIDestRule
    {
        /// <summary>How far from the origin the cell may be; 0 when the system states none (the worker then uses the
        /// ability's range).</summary>
        public float range;
        /// <summary>Range and sight are measured from the caster (VEF: every target is a CanHitTarget from the caster);
        /// otherwise from the target (vanilla's CompAbilityEffect_WithDest).</summary>
        public bool fromCaster;
        public bool lineOfSight;
    }

    /// <summary>A system of abilities the AI can drive. The core registers vanilla's; a compat assembly registers
    /// its own. None of its methods may throw: a source that does is dropped for the rest of the session, and its
    /// abilities go back to their own AI.</summary>
    public interface IAIAbilitySource
    {
        /// <summary>Whether the pawn holds any ability of this kind at all. Asked whenever the constant think tree has
        /// nothing to do, before anything else: keep it to a few field reads.</summary>
        bool HasAny(Pawn pawn);

        /// <summary>Whether any of them has a def with an AbilityAIExtension. Asked once the pawn is known to be one
        /// the AI acts for, before the per-pawn interval: one def lookup per ability, no wrappers. May answer true more
        /// loosely where finding out costs more (VEF's: any ability held on an item, AIHeldAbilities); Collect then
        /// adds only the ones that qualify.</summary>
        bool AnyWithExtension(Pawn pawn);

        /// <summary>Append the pawn's abilities of this kind whose def carries an AbilityAIExtension, each as its
        /// one cached wrapper.</summary>
        void Collect(Pawn pawn, List<AIAbility> into);

        /// <summary>Whether the pawn's current job casts one of this system's abilities, any ability at all, not only
        /// those with an extension. warmupTicksLeft: ticks until it takes effect once the warmup has begun, -1 while
        /// the job is still walking up to the target or the system can't tell. Asked for every pawn the driver
        /// evaluates and for every enemy the Interrupt worker looks at: answer from the current job's def first.</summary>
        bool TryGetRunningCast(Pawn pawn, out AIAbility ability, out int warmupTicksLeft);
    }

    public static class AIAbilitySources
    {
        private static readonly List<IAIAbilitySource> sources = new List<IAIAbilitySource> { VanillaAIAbility.Source };

        public static void Register(IAIAbilitySource source)
        {
            if (source != null && !sources.Contains(source)) sources.Add(source);
        }

        internal static bool HasAny(Pawn pawn)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                try { if (sources[i].HasAny(pawn)) return true; }
                catch (Exception e) { if (Drop(i, nameof(IAIAbilitySource.HasAny), e)) i--; }
            }
            return false;
        }

        internal static bool AnyWithExtension(Pawn pawn)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                try { if (sources[i].AnyWithExtension(pawn)) return true; }
                catch (Exception e) { if (Drop(i, nameof(IAIAbilitySource.AnyWithExtension), e)) i--; }
            }
            return false;
        }

        internal static void Collect(Pawn pawn, List<AIAbility> into)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                try { sources[i].Collect(pawn, into); }
                catch (Exception e) { if (Drop(i, nameof(IAIAbilitySource.Collect), e)) i--; }
            }
        }

        internal static bool TryGetRunningCast(Pawn pawn, out AIAbility ability, out int warmupTicksLeft)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                try { if (sources[i].TryGetRunningCast(pawn, out ability, out warmupTicksLeft)) return true; }
                catch (Exception e) { if (Drop(i, nameof(IAIAbilitySource.TryGetRunningCast), e)) i--; }
            }
            ability = null;
            warmupTicksLeft = -1;
            return false;
        }

        /// <summary>The pawn's current job casts an ability of any system.</summary>
        internal static bool IsCasting(Pawn pawn) => TryGetRunningCast(pawn, out _, out _);

        // These run on every idle think check of every pawn: a source that throws once would throw every time, so
        // it is dropped for the rest of the session. Vanilla's own source is never dropped.
        private static bool Drop(int i, string method, Exception e)
        {
            var source = sources[i];
            if (source == VanillaAIAbility.Source)
            {
                Log.ErrorOnce($"[Ability AI] vanilla abilities: {method} threw: {e}", 0x5A3 ^ method.GetHashCode());
                return false;
            }
            Log.Error($"[Ability AI] {source.GetType().FullName}.{method} threw, so its abilities are left to their own AI from now on: {e}");
            sources.RemoveAt(i);
            return true;
        }
    }

    /// <summary>
    /// Abilities a pawn can cast that its own trackers don't list, because they live on items it wears or wields
    /// (Entad Framework's gear). Each provider hands out objects of some ability system's own type, already pointed at
    /// the pawn; each ability source picks out the ones of its type (VEF's: VEF.Abilities.Ability). The core never
    /// looks inside them. Nothing here is saved: the objects belong to the items, and the AI keys everything by def.
    /// A provider that throws is dropped for the session, as an ability source is.
    /// </summary>
    public static class AIHeldAbilities
    {
        private struct Provider
        {
            public Func<Pawn, bool> hasAny;
            public Action<Pawn, List<object>> collect;
        }

        private static readonly List<Provider> providers = new List<Provider>();

        /// <summary>hasAny: whether the pawn holds any such ability, asked whenever its constant think tree has
        /// nothing to do, so keep it to a few field reads. collect: append them, each pointed at the pawn and ready
        /// to be asked whether it can cast now.</summary>
        public static void Register(Func<Pawn, bool> hasAny, Action<Pawn, List<object>> collect)
        {
            if (hasAny == null || collect == null) return;
            for (int i = 0; i < providers.Count; i++) if (providers[i].collect == collect) return;
            providers.Add(new Provider { hasAny = hasAny, collect = collect });
        }

        /// <summary>Any provider is registered: an ability source can skip asking with one count read.</summary>
        public static bool Any => providers.Count > 0;

        public static bool HasAny(Pawn pawn)
        {
            for (int i = 0; i < providers.Count; i++)
            {
                try { if (providers[i].hasAny(pawn)) return true; }
                catch (Exception e) { Drop(i--, "hasAny", e); }
            }
            return false;
        }

        public static void Collect(Pawn pawn, List<object> into)
        {
            for (int i = 0; i < providers.Count; i++)
            {
                try { providers[i].collect(pawn, into); }
                catch (Exception e) { Drop(i--, "collect", e); }
            }
        }

        private static void Drop(int i, string what, Exception e)
        {
            Log.Error($"[Ability AI] held-ability provider {providers[i].collect.Method.DeclaringType?.FullName}: {what} threw, so its abilities are left to their own AI from now on: {e}");
            providers.RemoveAt(i);
        }
    }

    public sealed class VanillaAIAbility : AIAbility
    {
        public readonly Ability ability;

        private static readonly ConditionalWeakTable<Ability, VanillaAIAbility> wrappers = new ConditionalWeakTable<Ability, VanillaAIAbility>();
        private static readonly ConditionalWeakTable<Ability, VanillaAIAbility>.CreateValueCallback Make = a => new VanillaAIAbility(a);

        private VanillaAIAbility(Ability ability) { this.ability = ability; }

        public static VanillaAIAbility Of(Ability ability) => wrappers.GetValue(ability, Make);

        internal static readonly IAIAbilitySource Source = new VanillaSource();

        private sealed class VanillaSource : IAIAbilitySource
        {
            public bool HasAny(Pawn pawn)
            {
                var tracker = pawn.abilities;
                return tracker != null && tracker.AllAbilitiesForReading.Count > 0;
            }

            public bool AnyWithExtension(Pawn pawn)
            {
                var list = pawn.abilities?.AllAbilitiesForReading;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].def.GetModExtension<AbilityAIExtension>() != null) return true;
                return false;
            }

            public void Collect(Pawn pawn, List<AIAbility> into)
            {
                var list = pawn.abilities?.AllAbilitiesForReading;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].def.GetModExtension<AbilityAIExtension>() != null) into.Add(Of(list[i]));
            }

            public bool TryGetRunningCast(Pawn pawn, out AIAbility ability, out int warmupTicksLeft)
            {
                Ability running = pawn.CurJob?.ability;
                if (running == null)
                {
                    ability = null;
                    warmupTicksLeft = -1;
                    return false;
                }
                ability = Of(running);
                warmupTicksLeft = pawn.stances?.curStance is Stance_Warmup w && w.verb == running.verb ? w.ticksLeft : -1;
                return true;
            }
        }

        public override Pawn Pawn => ability.pawn;
        public override Def Def => ability.def;
        public override object Raw => ability;
        public override Ability Vanilla => ability;

        public override bool CanCastNow => ability.verb != null && ability.CanCast && PsycastAffordable();

        public override string NotReadyReason
        {
            get
            {
                if (ability.verb == null) return "it has no verb";
                AcceptanceReport report = ability.CanCast;
                if (!report.Accepted)
                {
                    if (!report.Reason.NullOrEmpty()) return report.Reason;
                    return ability.OnCooldown ? "on cooldown" : "vanilla refused it";
                }
                return "not enough psyfocus";
            }
        }

        // Checked up front because Verb_CastPsycast.ValidateTarget posts its "not enough psyfocus" and similar
        // messages whatever showMessages says, and Psycast.CanCast only checks psyfocus for psycasts without neural
        // heat
        private bool PsycastAffordable()
        {
            if (!(ability is Psycast)) return true;
            var pe = ability.pawn.psychicEntropy;
            if (pe == null || pe.PsychicSensitivity < float.Epsilon) return false;
            return ability.def.PsyfocusCost <= pe.CurrentPsyfocus + Pawn_PsychicEntropyTracker.PsyfocusCostTolerance;
        }

        public override bool IsTouch => AbilityAIAutocast.IsTouch(ability.def);
        public override bool UsesCharges => ability.UsesCharges;
        public override int RemainingCharges => ability.RemainingCharges;
        public override bool IsPsycast => ability is Psycast;
        public override float Range => ability.verb.EffectiveRange;
        public override float DefaultRadius => ability.def.HasAreaOfEffect ? ability.def.EffectRadius : 0f;

        // No target needed, or an untargetable verb that may target itself. (Range 0 alone isn't: the Word of
        // psycasts have no range because the caster walks up and touches.)
        public override bool IsSelfCast
        {
            get
            {
                var vp = ability.def.verbProperties;
                if (vp == null || !ability.def.targetRequired) return true;
                return !vp.targetable && vp.targetParams?.canTargetSelf == true;
            }
        }

        public override bool CanTargetSelf => IsSelfCast || ability.def.verbProperties?.targetParams?.canTargetSelf == true;
        // Scaled as Verb.TryStartCastOn scales the warmup stance (Careful Shooter's 1.25)
        public override float WarmupSeconds => ability.verb == null ? 0f
            : ability.verb.WarmupTime * ability.pawn.GetStatValue(StatDefOf.AimingDelayFactor);
        public override Verb Verb => ability.verb;
        public override AIAbilityEffects Effects => EffectsOf(ability.def);
        public override bool Hostile => ability.def.hostile;

        public override bool ValidIgnoringRange(LocalTargetInfo target)
        {
            if (!target.IsValid) return false;
            var comps = ability.EffectComps;
            if (AreaAroundCaster(target))
            {
                // An area ability with no target (TerrorRoar, Bastle's DreadHowl) is cast at the caster, and its comps'
                // Valid judge each pawn it reaches (OnlyTargetHostiles: HostileTo(caster), never true; AnimalRoar: the
                // caster's own hearing). Vanilla never asks them about the caster: its gizmo and
                // JobGiver_AICastAbilityOnSelf queue the cast straight away and GetAffectedTargets asks them per pawn.
                // A comp that turns the caster down is such a per-pawn judge and is left out, its AICanTargetNow too;
                // the worker has counted who would be affected. Other comps' AI rules still apply.
                if (!ability.CanApplyOn(target)) return false;
                if (comps != null)
                {
                    bool player = AbilityAIDriver.EvaluatingPlayer;
                    for (int i = 0; i < comps.Count; i++)
                        if (comps[i].Valid(target, false) && !CompAllowsTarget(comps[i], target, player)) return false;
                }
                return !(ability is Psycast area) || PsycastValidOn(area, target);
            }
            // The comps' own Valid first and quietly: some comps' CanApplyOn calls Valid with messages on
            // (CompAbilityEffect_Wallraise), which would post "occupied cells" for every spot the AI tries
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                    if (!comps[i].Valid(target, false)) return false;
            if (!ability.CanApplyOn(target)) return false;
            if (comps != null)
            {
                bool player = AbilityAIDriver.EvaluatingPlayer;
                for (int i = 0; i < comps.Count; i++)
                    if (!CompAllowsTarget(comps[i], target, player)) return false;
            }
            if (ability is Psycast ps && !PsycastValidOn(ps, target)) return false;
            return true;
        }

        // The caster, or the cell it stands on for an ability that wants a location (AbilityAIUtility.PawnOrCell)
        // Only while a worker that counted the affected pawns itself (AoEHostileSelf) is choosing or confirming: a
        // self-buff worker's comps may judge the caster for real
        private bool AreaAroundCaster(LocalTargetInfo target) => AbilityAIDriver.AffectedCounted && !ability.def.targetRequired
            && (target.Thing == ability.pawn || (!target.HasThing && target.Cell == ability.pawn.Position));

        // The verb's check runs every comp's Valid on the target again, which an area cast at the caster fails (see
        // ValidIgnoringRange); the caster is always in range of itself
        public override bool ValidInRange(LocalTargetInfo target) => AreaAroundCaster(target) || ability.verb.ValidateTarget(target, false);

        public override float PsyfocusCost(LocalTargetInfo target)
        {
            if (!(ability is Psycast ps)) return 0f;
            return target.IsValid ? ps.FinalPsyfocusCost(target) : ability.def.PsyfocusCostRange.min;
        }

        public override float HeatGain => ability is Psycast ? ability.def.EntropyGain : 0f;

        public override Job MakeCastJob(LocalTargetInfo target, LocalTargetInfo dest) => ability.GetJob(target, dest);

        // The comp's own CanHitTarget and CanPlaceSelectedTargetAt read the targeting UI's selection, so the Skip
        // worker applies the rule itself
        public override bool TryGetDestRule(out AIDestRule rule)
        {
            rule = default;
            var comp = ability.CompOfType<CompAbilityEffect_WithDest>();
            if (comp == null || comp.Props.destination != AbilityEffectDestination.Selected) return false;
            rule.range = comp.Props.range;
            rule.lineOfSight = comp.Props.requiresLineOfSight;
            return true;
        }

        // Some vanilla comps open AICanTargetNow with "if the caster is the player's, no": vanilla's AI never casts
        // for colonists, so that line only ever stopped vanilla's own AI. For our player autocast it fails every
        // target (Painblock, Focus, Invisibility, Burden... never fired). For those comps, and only for player
        // casters, the rest of the method's rule is applied here instead. Decided once per runtime comp type: a
        // subclass that declares its own AICanTargetNow is asked as usual, since its author's rule may be real.
        // RequiresTrainable says no once the trick is learned (the trained animal's own think tree casts it then),
        // which is the only time the player can set it to autocast at all: the gizmo is hidden until then.
        private enum PlayerAIRule : byte { Ask, TargetIsPawn, TargetAimsAtCaster, Allow }
        private static readonly Dictionary<Type, PlayerAIRule> playerAIRules = new Dictionary<Type, PlayerAIRule>();

        private static bool CompAllowsTarget(CompAbilityEffect comp, LocalTargetInfo target, bool playerCaster)
        {
            if (!playerCaster) return comp.AICanTargetNow(target);
            var type = comp.GetType();
            if (!playerAIRules.TryGetValue(type, out var rule))
            {
                var declaring = type.GetMethod(nameof(CompAbilityEffect.AICanTargetNow), new[] { typeof(LocalTargetInfo) })?.DeclaringType;
                rule = declaring == typeof(CompAbilityEffect_GiveHediff) ? PlayerAIRule.TargetIsPawn
                     : declaring == typeof(CompAbilityEffect_FireBurst) ? PlayerAIRule.TargetAimsAtCaster
                     : declaring == typeof(CompAbilityEffect_RequiresTrainable) ? PlayerAIRule.Allow
                     : PlayerAIRule.Ask;
                playerAIRules[type] = rule;
            }
            switch (rule)
            {
                case PlayerAIRule.Allow: return true;
                case PlayerAIRule.TargetIsPawn: return target.Pawn != null;
                case PlayerAIRule.TargetAimsAtCaster:
                    return target.Thing is Pawn p && p.TargetCurrentlyAimingAt == comp.parent.pawn;
                default: return comp.AICanTargetNow(target);
            }
        }

        // The rest of Verb_CastPsycast.ValidateTarget, which would post messages, plus immunity: a psycast on a pawn
        // with no psychic sensitivity only shows "immune" and spends the heat
        private static bool PsycastValidOn(Psycast ps, LocalTargetInfo target)
        {
            var pe = ps.pawn.psychicEntropy;
            float cost = ps.FinalPsyfocusCost(target);
            if (cost > float.Epsilon && cost > pe.CurrentPsyfocus + Pawn_PsychicEntropyTracker.PsyfocusCostTolerance) return false;
            Pawn p = target.Pawn;
            if (p == null) return true;
            if (p.kindDef.isBoss)
            {
                var comps = ps.EffectComps;
                for (int i = 0; i < comps.Count; i++) if (!comps[i].Props.canTargetBosses) return false;
            }
            return ps.CanApplyPsycastTo(target);
        }

        private static readonly Dictionary<AbilityDef, AIAbilityEffects> effects = new Dictionary<AbilityDef, AIAbilityEffects>();

        /// <summary>Forget what was read from the comps: AbilityAIInfo.Register calls it, since a new reader can
        /// change the answer.</summary>
        internal static void ClearEffectsCache() => effects.Clear();

        private static AIAbilityEffects EffectsOf(AbilityDef def)
        {
            if (effects.TryGetValue(def, out var e)) return e;
            e = new AIAbilityEffects();
            var comps = def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    var info = AbilityAIInfo.Of(c);
                    if (info != null)
                    {
                        e.stuns |= info.AIStuns;
                        if (e.targetHediff == null) e.targetHediff = info.AIHediff;
                        if (e.anyHediff == null) e.anyHediff = info.AIHediff;
                        if (!e.hasEffectRadius) { e.hasEffectRadius = true; e.effectRadius = info.AIRadius; }
                    }
                    if (!e.cone && AbilityAIInfo.TryGetCone(c, out float coneRange, out float coneWidth))
                    {
                        e.cone = true;
                        e.coneRange = coneRange;
                        e.coneWidth = coneWidth;
                    }
                    else if (!e.cone && c is CompProperties_AbilityFireSpew spew)
                    {
                        e.cone = true;
                        e.coneRange = spew.range;
                        e.coneWidth = spew.lineWidthEnd;
                    }
                    // The target cell and the cells around it furthest from the caster: about a disc of that many cells
                    if (c is CompProperties_AbilitySprayLiquid spray && !e.hasEffectRadius)
                    {
                        e.hasEffectRadius = true;
                        e.effectRadius = spray.numCellsToHit < 10 ? 1.5f : GenRadial.RadiusOfNumCells(spray.numCellsToHit);
                    }
                    if (c.compClass != null && typeof(CompAbilityEffect_Stun).IsAssignableFrom(c.compClass)) e.stuns = true;
                    if (c is CompProperties_AbilityGiveHediff give && give.hediffDef != null)
                    {
                        if (e.anyHediff == null) e.anyHediff = give.hediffDef;
                        // As CompAbilityEffect_GiveHediff.Apply decides who gets it
                        if (give.applyToTarget && !give.onlyApplyToSelf && e.targetHediff == null) e.targetHediff = give.hediffDef;
                    }
                    if (c is CompProperties_AbilityGiveMentalState) e.mentalState = true;
                    if (c is CompProperties_AbilityEffect ae)
                    {
                        if (ae.psychic) e.psychic = true;
                        // Psycast.CanApplyPsycastTo: once any comp is psychic, any comp not for mechs rules them out
                        if (!ae.applicableToMechs) e.notOnMechs = true;
                    }
                    if (c is CompProperties_AbilitySmokepop smoke)
                    {
                        e.smoke = true;
                        if (!e.hasEffectRadius) { e.hasEffectRadius = true; e.effectRadius = smoke.smokeRadius; }
                    }
                    else if (c is CompProperties_AbilitySpawn sp && sp.thingDef != null) e.spawns = sp.thingDef;
                    else if (c is CompProperties_AbilityWallraise w) { e.wall = true; e.wallPattern = w.pattern; }
                }
            effects[def] = e;
            return e;
        }
    }
}
