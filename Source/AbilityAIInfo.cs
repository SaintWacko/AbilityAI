using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AbilityAI
{
    /// <summary>
    /// What the workers need to know about an effect comp they can't read from vanilla's own comp types: the area it
    /// covers, the hediff it gives and whether it stuns.
    ///
    /// A mod that hard-depends on Ability AI can implement IAbilityAIInfo on its comp properties. A soft-dependent
    /// host can't: an Entad class implementing an interface from this assembly would make RimWorld throw out the
    /// whole Entad DLL (ReflectionTypeLoadException) whenever this mod is missing. So the host's compat assembly,
    /// which only loads when this mod is active, registers readers here instead:
    ///
    ///   AbilityAIInfo.Register&lt;CompProperties_AbilityEntadPulse&gt;(p =&gt; p.radius, p =&gt; p.hediff, p =&gt; p.stunSeconds &gt; 0f);
    ///
    /// Register from a Mod constructor or a [StaticConstructorOnStartup] class: before play, at any rate. The answer
    /// for each comp properties object is worked out once and cached (they are per def, so the cache is bounded);
    /// registering clears it, and the workers' per-AbilityDef effects cache with it.
    /// </summary>
    public static class AbilityAIInfo
    {
        private class Reader : IAbilityAIInfo
        {
            public float AIRadius { get; set; }
            public HediffDef AIHediff { get; set; }
            public bool AIStuns { get; set; }
        }

        private static readonly Dictionary<Type, Func<AbilityCompProperties, Reader>> readers = new Dictionary<Type, Func<AbilityCompProperties, Reader>>();
        // Null values cached too: most comps have no reader, and they are asked about every evaluation
        private static readonly Dictionary<AbilityCompProperties, IAbilityAIInfo> cache = new Dictionary<AbilityCompProperties, IAbilityAIInfo>();

        /// <summary>Tell the workers how to read a comp properties type of yours (and its subclasses, unless a
        /// subclass registers its own). Hediff and stun may be left out.</summary>
        public static void Register<T>(Func<T, float> radius, Func<T, HediffDef> hediff = null, Func<T, bool> stuns = null)
            where T : AbilityCompProperties
        {
            if (radius == null) throw new ArgumentNullException(nameof(radius));
            readers[typeof(T)] = props =>
            {
                var t = (T)props;
                return new Reader { AIRadius = radius(t), AIHediff = hediff?.Invoke(t), AIStuns = stuns != null && stuns(t) };
            };
            cache.Clear();
            AbilityAIUtility.ClearEffectsCache();
        }

        private static readonly Dictionary<Type, Func<AbilityCompProperties, (float length, float width)>> coneReaders =
            new Dictionary<Type, Func<AbilityCompProperties, (float, float)>>();

        /// <summary>Tell the cone worker (AbilityAIWorker_ConeHostile) the shape of a comp properties type of yours:
        /// how far the cone reaches from the caster and how wide it is at its far end (0 or 1 for a line).</summary>
        public static void RegisterCone<T>(Func<T, float> length, Func<T, float> widthAtEnd) where T : AbilityCompProperties
        {
            if (length == null) throw new ArgumentNullException(nameof(length));
            if (widthAtEnd == null) throw new ArgumentNullException(nameof(widthAtEnd));
            coneReaders[typeof(T)] = props => (length((T)props), widthAtEnd((T)props));
            AbilityAIUtility.ClearEffectsCache();
        }

        // Asked once per AbilityDef: the workers' effects cache keeps the answer
        internal static bool TryGetCone(AbilityCompProperties props, out float length, out float width)
        {
            length = width = 0f;
            if (coneReaders.Count == 0 || props == null) return false;
            for (Type t = props.GetType(); t != null && t != typeof(AbilityCompProperties); t = t.BaseType)
            {
                if (!coneReaders.TryGetValue(t, out var read)) continue;
                try { (length, width) = read(props); }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[Ability AI] cone reader for {t.Name} threw: {e}", t.GetHashCode() ^ 0x5A4);
                    return false;
                }
                return true;
            }
            return false;
        }

        /// <summary>The info for this comp: its own IAbilityAIInfo, else a registered reader's answer, else null.</summary>
        public static IAbilityAIInfo Of(AbilityCompProperties props)
        {
            if (props == null) return null;
            if (props is IAbilityAIInfo own) return own;
            if (readers.Count == 0) return null;
            if (cache.TryGetValue(props, out var info)) return info;
            info = null;
            for (Type t = props.GetType(); t != null && t != typeof(AbilityCompProperties); t = t.BaseType)
            {
                if (!readers.TryGetValue(t, out var read)) continue;
                try { info = read(props); }
                catch (Exception e) { Log.ErrorOnce($"[Ability AI] info reader for {t.Name} threw: {e}", t.GetHashCode() ^ 0x5A3); }
                break;
            }
            cache[props] = info;
            return info;
        }
    }
}
