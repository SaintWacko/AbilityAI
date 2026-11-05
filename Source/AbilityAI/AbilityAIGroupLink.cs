using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

// Group links (VPE's Group Link): a hediff on the caster linking every humanlike within its radius when it lands, and
// every "Word of" psycast the caster casts while it is up goes to each linked pawn as well as its target, unchecked
// (Ability_WordOf.Cast). Two things follow. A Word cast while linked must be one every linked pawn may take, whoever
// cast the link (the guard). And where several of a Word's targets stand together, the link first and then one Word
// reaches them all for one Word's cost (link first). The ability system that has links registers how to read them
// (Register); nothing here knows VPE. Operator's plan: zone-psycasts.md, phase 4.
namespace AbilityAI
{
    /// <summary>Who besides a Word's own targets a group link may carry that Word to.</summary>
    public enum AbilityAILinkSafe
    {
        /// <summary>Only pawns the rule would target itself (Word of Serenity: everyone it reaches is put in a coma).</summary>
        Targets,
        /// <summary>Anyone hostile to the caster; an ally in a hostile break only as MayHitBrokenAllies allows (Word of
        /// Pain, Word of Fear).</summary>
        Enemies,
        /// <summary>Anyone not hostile to the caster (Word of Protection, Healing, Immunity, Trust).</summary>
        NonHostile,
    }

    /// <summary>
    /// Group link: marks the ability that links the caster to those around it. Never cast for itself: the driver and
    /// Word of Trust's zone work cast it just before a Word on autocast when the link would reach enough of that Word's
    /// targets (AbilityAIGroupLink). Any time, in combat or not.
    /// </summary>
    public class AbilityAIWorker_GroupLink : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.Any;

        public override bool DriverCasts => false;

        public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            return false;
        }
    }

    public static class AbilityAIGroupLink
    {
        private static Func<Pawn, Hediff> linkOf;
        private static Func<Hediff, List<Pawn>> linkedOf;
        private static Func<Def, bool> isWord;
        private static readonly Dictionary<Def, bool> words = new Dictionary<Def, bool>();
        private static readonly Dictionary<Def, AbilityAIExtension> linkers = new Dictionary<Def, AbilityAIExtension>();

        // Someone just outside the radius when the AI decides can walk in during the link's warmup and be linked: the
        // pawns the Word mustn't reach are looked for this much further out
        private const float UnsafeMargin = 1.5f;

        /// <summary>Links can be read: the guard applies to every Word.</summary>
        internal static bool Registered => linkOf != null;

        /// <summary>Links can be read and some ability has a Group link rule: the AI may link first, and the settings
        /// section is shown.</summary>
        public static bool Any => linkOf != null && linkers.Count > 0;

        /// <summary>For an ability system with group links. linkOf: the pawn's link, or null; linkedOf: the pawns that
        /// link holds now; isWord: abilities of this def go to every linked pawn when cast. Each is asked inside a
        /// try/catch only by the caller's own code, so they must not throw.</summary>
        public static void Register(Func<Pawn, Hediff> linkOf, Func<Hediff, List<Pawn>> linkedOf, Func<Def, bool> isWord)
        {
            AbilityAIGroupLink.linkOf = linkOf;
            AbilityAIGroupLink.linkedOf = linkedOf;
            AbilityAIGroupLink.isWord = isWord;
            words.Clear();
        }

        /// <summary>Called at startup for every ability def of every system, beside AbilityAIZones.Note.</summary>
        public static void Note(Def abilityDef)
        {
            var exts = abilityDef?.modExtensions;
            if (exts == null) return;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension rule && rule.Worker is AbilityAIWorker_GroupLink)
                {
                    linkers[abilityDef] = rule;
                    return;
                }
        }

        internal static bool IsWord(Def def)
        {
            if (isWord == null || def == null) return false;
            if (!words.TryGetValue(def, out bool word)) words[def] = word = isWord(def);
            return word;
        }

        internal static Hediff LinkOf(Pawn pawn) => linkOf?.Invoke(pawn);

        internal static List<Pawn> Linked(Hediff link) => link == null ? null : linkedOf(link);

        /// <summary>The tooltip line for a Group link ability, or null.</summary>
        internal static string Hint(Def abilityDef) =>
            abilityDef != null && linkers.ContainsKey(abilityDef) ? (string)"AAI_GroupLink_Hint".Translate(MinTargets) : null;

        internal static int MinTargets => Math.Max(2, AbilityAISettings.GroupLinkMinTargets);

        /// <summary>The pawn's Group link that it may cast now: on autocast (the player's) or allowed (an NPC's), ready,
        /// and within the player's limits. Null when it has none.</summary>
        internal static AIAbility FindLinker(Pawn pawn, List<AIAbility> abilities, bool player, AbilityAIRole role)
        {
            if (linkers.Count == 0) return null;
            for (int i = 0; i < abilities.Count; i++)
            {
                AIAbility a = abilities[i];
                if (!linkers.TryGetValue(a.Def, out var rule) || rule.For(role).Worker == null || !a.Drivable) continue;
                if (player)
                {
                    if (a.PlayerAutocastElsewhere || AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off
                        || !AbilityAIHooks.NotVetoed(pawn, a)) continue;
                }
                else if (!AbilityAIHooks.Allowed(pawn, a)) continue;
                if (!a.CanCastNow || (player && !AbilityAIDriver.PsycastWithinLimits(a, pawn))) continue;
                return a;
            }
            return null;
        }

        /// <summary>Whether this pawn may take the Word through a link without being one of its targets.</summary>
        internal static bool SafeBeside(Pawn caster, AbilityAIExtension rule, Pawn p)
        {
            switch (rule.linkSafe)
            {
                case AbilityAILinkSafe.Enemies:
                    if (!p.HostileTo(caster)) return false;
                    return !BrokenAlly(caster, p) || AbilityAIUtility.MayHitBrokenAllies(rule);
                case AbilityAILinkSafe.NonHostile:
                    return !p.HostileTo(caster);
                default:
                    return false;
            }
        }

        // AbilityAIContext.IsBrokenAlly, for callers outside an evaluation
        private static bool BrokenAlly(Pawn caster, Pawn p)
        {
            var state = p.MentalState;
            if (state == null) return false;
            Faction mine = caster.Faction, theirs = p.Faction;
            if (mine == null || theirs == null) return false;
            return (theirs == mine || !theirs.HostileTo(mine)) && state.ForceHostileTo(caster);
        }

        /// <summary>Whether p may take this Word through the caster's link: one of the rule's targets the ability
        /// accepts, or someone it is safe beside (linkSafe). ctx: the evaluation, or null for a Zone rule.</summary>
        internal static bool MayTake(AbilityAIContext ctx, Pawn caster, AIAbility word, AbilityAIExtension rule, Pawn p) =>
            SafeBeside(caster, rule, p) || (rule.Worker.IsLinkTarget(ctx, word, p) && AbilityAIDriver.ValidIgnoringRange(word, p));

        /// <summary>The guard: true when the caster has no link, or every pawn linked to it may take this Word;
        /// otherwise false, with the first that may not.</summary>
        internal static bool LinkedMayTake(AbilityAIContext ctx, Pawn caster, AIAbility word, AbilityAIExtension rule, out Pawn refused)
        {
            refused = null;
            if (!Registered || !IsWord(word.Def)) return true;
            var linked = Linked(LinkOf(caster));
            if (linked == null) return true;
            for (int i = 0; i < linked.Count; i++)
            {
                Pawn p = linked[i];
                if (p == null || p == caster || p.Dead || !p.Spawned) continue;
                if (!MayTake(ctx, caster, word, rule, p)) { refused = p; return false; }
            }
            return true;
        }

        /// <summary>Who a link cast at c would reach: the Word's targets within radius (the rule's targets the ability
        /// accepts) go into targets. False, with the pawn, when someone within the
        /// radius (plus UnsafeMargin, for those walking in during the warmup) is someone the Word mustn't reach.
        /// Cost: one pass over the map's humanlikes, and the ability's own check on each target in reach.</summary>
        internal static bool Survey(AbilityAIContext ctx, Pawn caster, IntVec3 c, float radius, AIAbility word, AbilityAIExtension rule,
            List<Pawn> targets, out Pawn refused)
        {
            refused = null;
            targets.Clear();
            float r2 = radius * radius;
            float outer = radius + UnsafeMargin;
            float outer2 = outer * outer;
            var pawns = caster.Map.mapPawns.AllHumanlikeSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == caster || p.Dead) continue;
                float d = (p.Position - c).LengthHorizontalSquared;
                if (d > outer2) continue;
                if (d <= r2 && rule.Worker.IsLinkTarget(ctx, word, p) && AbilityAIDriver.ValidIgnoringRange(word, p))
                {
                    targets.Add(p);
                    continue;
                }
                if (!SafeBeside(caster, rule, p))
                {
                    refused = p;
                    targets.Clear();
                    return false;
                }
            }
            return true;
        }

        /// <summary>Whether linking first pays for count targets: the link and one Word cost less than a Word for each,
        /// with psyfocus and heat each counted as a share of the caster's own pool; and the caster can pay for both, the
        /// player's limits included.</summary>
        internal static bool Worth(Pawn pawn, AIAbility linker, AIAbility word, LocalTargetInfo target, int count, bool player)
        {
            float maxHeat = pawn.GetStatValue(StatDefOf.PsychicEntropyMax, true, 250);
            float wordFocus = word.PsyfocusCost(target), linkFocus = linker.PsyfocusCost(pawn);
            float wordCost = wordFocus + (maxHeat > 0f ? word.HeatGain / maxHeat : 0f);
            float linkCost = linkFocus + (maxHeat > 0f ? linker.HeatGain / maxHeat : 0f);
            if (wordCost <= 0f || linkCost + wordCost >= count * wordCost) return false;
            var pe = pawn.psychicEntropy;
            if (pe == null) return true;
            float heatCap = 1f, focusFloor = 0f;
            if (player) AbilityAIAutocast.TryGetLimits(pawn, out heatCap, out focusFloor);
            if (pe.CurrentPsyfocus - linkFocus - wordFocus < focusFloor - Pawn_PsychicEntropyTracker.PsyfocusCostTolerance) return false;
            float heat = linker.HeatGain + word.HeatGain;
            if (heat > float.Epsilon)
            {
                heat *= pawn.GetStatValue(StatDefOf.PsychicEntropyGain, true, 250);
                if (pe.EntropyValue + heat > heatCap * maxHeat) return false;
            }
            return true;
        }

        /// <summary>The nearest linked pawn the Word's rule would cast at and the ability accepts in range, or null.</summary>
        internal static Pawn NearestLinkedTarget(AbilityAIContext ctx, Pawn caster, AIAbility word, AbilityAIExtension rule, List<Pawn> linked)
        {
            Pawn best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < linked.Count; i++)
            {
                Pawn p = linked[i];
                if (p == null || p == caster || p.Dead || !p.Spawned || p.Map != caster.Map) continue;
                float d = (p.Position - caster.Position).LengthHorizontalSquared;
                if (d >= bestD || !rule.Worker.IsLinkTarget(ctx, word, p) || !AbilityAIDriver.Valid(word, p)) continue;
                best = p;
                bestD = d;
            }
            return best;
        }
    }
}
