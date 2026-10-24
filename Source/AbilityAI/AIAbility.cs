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

        /// <summary>Castable this moment by its own system's rules: cooldown, charges, comps, and the cost it would
        /// pay (a psycast the pawn has the psyfocus for).</summary>
        public abstract bool CanCastNow { get; }

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

        /// <summary>The job that casts it, handed back to the think tree rather than started.</summary>
        public abstract Job MakeCastJob(LocalTargetInfo target, LocalTargetInfo dest);
    }

    /// <summary>A system of abilities the AI can drive. The core registers vanilla's; a compat assembly registers
    /// its own.</summary>
    public interface IAIAbilitySource
    {
        /// <summary>Whether the pawn holds any ability of this kind at all. Asked whenever the constant think tree has
        /// nothing to do, before anything else: keep it to a few field reads.</summary>
        bool HasAny(Pawn pawn);

        /// <summary>Whether any of them has a def with an AbilityAIExtension. Asked once the pawn is known to be one
        /// the AI acts for, before the per-pawn interval: one def lookup per ability, no wrappers.</summary>
        bool AnyWithExtension(Pawn pawn);

        /// <summary>Append the pawn's abilities of this kind whose def carries an AbilityAIExtension, each as its
        /// one cached wrapper.</summary>
        void Collect(Pawn pawn, List<AIAbility> into);
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
        }

        public override Pawn Pawn => ability.pawn;
        public override Def Def => ability.def;
        public override object Raw => ability;
        public override Ability Vanilla => ability;

        public override bool CanCastNow => ability.verb != null && ability.CanCast && PsycastAffordable();

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
        public override float WarmupSeconds => ability.verb?.verbProps?.warmupTime ?? 0f;
        public override Verb Verb => ability.verb;
        public override AIAbilityEffects Effects => EffectsOf(ability.def);

        public override bool ValidIgnoringRange(LocalTargetInfo target)
        {
            if (!target.IsValid) return false;
            // The comps' own Valid first and quietly: some comps' CanApplyOn calls Valid with messages on
            // (CompAbilityEffect_Wallraise), which would post "occupied cells" for every spot the AI tries
            var comps = ability.EffectComps;
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

        public override bool ValidInRange(LocalTargetInfo target) => ability.verb.ValidateTarget(target, false);

        public override float PsyfocusCost(LocalTargetInfo target)
        {
            if (!(ability is Psycast ps)) return 0f;
            return target.IsValid ? ps.FinalPsyfocusCost(target) : ability.def.PsyfocusCostRange.min;
        }

        public override float HeatGain => ability is Psycast ? ability.def.EntropyGain : 0f;

        public override Job MakeCastJob(LocalTargetInfo target, LocalTargetInfo dest) => ability.GetJob(target, dest);

        // Some vanilla comps open AICanTargetNow with "if the caster is the player's, no": vanilla's AI never casts
        // for colonists, so that line only ever stopped vanilla's own AI. For our player autocast it fails every
        // target (Painblock, Focus, Invisibility, Burden... never fired). For those comps, and only for player
        // casters, the rest of the method's rule is applied here instead. Decided once per runtime comp type: a
        // subclass that declares its own AICanTargetNow is asked as usual, since its author's rule may be real.
        private enum PlayerAIRule : byte { Ask, TargetIsPawn, TargetAimsAtCaster }
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
                     : PlayerAIRule.Ask;
                playerAIRules[type] = rule;
            }
            switch (rule)
            {
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
