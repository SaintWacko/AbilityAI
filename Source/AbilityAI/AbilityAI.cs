using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

// The ability AI: lets NPC pawns (raiders, visitors, tamed animals, mechs) cast abilities with some judgement.
//
// Split out of Entad Framework into its own mod so Entad and Bastle Framework can share one copy. Both treat it as a
// SOFT dependency: each ships a small compat assembly and patches in a load folder gated on IfModActive, and that is
// the only code of theirs that references this assembly. A host plugs in through:
// - IAbilityAIHook, registered with AbilityAIHooks.Register, which claims or vetoes abilities per pawn and hears
//   about each cast the AI starts;
// - AbilityAIInfo.Register, which tells the workers the radius / hediff / stun of a host's own effect comp. The
//   IAbilityAIInfo interface still works for a mod that hard-depends on this one, but a soft-dependent host can't
//   put it on its own comp class (the whole host DLL would fail to load without this mod);
// - AbilityAIExtension on its AbilityDefs, added by PatchOperationAddModExtension from the gated folder.
// The settings are this mod's own (AbilityAIMod.cs).
//
// Vanilla stand-down (Patch_Ability_AICanTargetNow_StandDown): a host may leave aiCanUse on so its abilities still get
// vanilla's simple casting when this mod is absent. When this mod is present and would act for that pawn and ability,
// vanilla's fight AI is told it can't use the ability, so the two never both cast it.
//
// How an ability opts in: an AbilityAIExtension on its AbilityDef naming a worker class (AbilityAIWorkers.cs). No
// extension, no AI casts. Vanilla's own aiCanUse flag is deliberately NOT required: it is also what vanilla's fight
// AI (JobGiver_AIFightEnemy.GetAbilityJob) and JobGiver_NPCCastNonCombatAbilities read, and those cast anything
// offensive straight at the current enemy, or anything else at random. Abilities using this AI keep aiCanUse off so
// vanilla leaves them alone. Every other vanilla gate still applies: CanCast (charges, cooldown, comps), CanApplyOn,
// each effect comp's AICanTargetNow, and the verb's own ValidateTarget (range, line of sight, target rules).
//
namespace AbilityAI
{
    public enum AbilityAIVote { NoOpinion, Claim, Veto }

    /// <summary>A host mod's say over which abilities the AI may cast for which pawns.</summary>
    public interface IAbilityAIHook
    {
        /// <summary>Asked for every ready ability with an AI extension, before any targeting work. Claim = this
        /// ability is the host's to cast for this pawn; Veto = never now (an unbound item, say). On the player's own
        /// pawns (outside a mental break) Veto means Ability AI neither casts it nor stops anyone else casting it:
        /// vanilla's AI or the host's own fallback keeps it (OthersStandDown).</summary>
        AbilityAIVote Vote(Pawn pawn, Ability ability);

        /// <summary>The AI has just handed vanilla a job to cast this ability. Vanilla may still discard it without
        /// starting it (a job override check with a minimum priority, say), and a started cast resolves later
        /// (Ability.Activate) or not at all if interrupted: hook anything that must only happen on a real cast to
        /// Ability.Activate instead.</summary>
        void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest);
    }

    public static class AbilityAIHooks
    {
        private static readonly List<IAbilityAIHook> hooks = new List<IAbilityAIHook>();

        public static void Register(IAbilityAIHook hook)
        {
            if (hook != null && !hooks.Contains(hook)) hooks.Add(hook);
        }

        // Any veto wins; otherwise a claim, or the "unclaimed abilities" setting, lets it through
        internal static bool Allowed(Pawn pawn, Ability ability)
        {
            bool claimed = false;
            for (int i = 0; i < hooks.Count; i++)
            {
                AbilityAIVote v;
                try { v = hooks[i].Vote(pawn, ability); }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[Ability AI] {hooks[i].GetType().Name}.Vote threw: {e}", hooks[i].GetType().GetHashCode() ^ 0x5A1);
                    continue;
                }
                if (v == AbilityAIVote.Veto) return false;
                if (v == AbilityAIVote.Claim) claimed = true;
            }
            return claimed || AbilityAISettings.Unclaimed;
        }

        // For the player's own pawns, whose autocast mode is the claim: only a veto matters
        internal static bool NotVetoed(Pawn pawn, Ability ability)
        {
            for (int i = 0; i < hooks.Count; i++)
            {
                AbilityAIVote v;
                try { v = hooks[i].Vote(pawn, ability); }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[Ability AI] {hooks[i].GetType().Name}.Vote threw: {e}", hooks[i].GetType().GetHashCode() ^ 0x5A1);
                    continue;
                }
                if (v == AbilityAIVote.Veto) return false;
            }
            return true;
        }

        // Hooks vote on vanilla abilities only (their API takes one): any other kind goes by the Unclaimed setting for
        // NPCs and is never vetoed for the player's pawns
        internal static bool Allowed(Pawn pawn, AIAbility ability) =>
            ability.Vanilla != null ? Allowed(pawn, ability.Vanilla) : AbilityAISettings.Unclaimed;

        internal static bool NotVetoed(Pawn pawn, AIAbility ability) => ability.Vanilla == null || NotVetoed(pawn, ability.Vanilla);

        internal static void NotifyCast(Pawn pawn, AIAbility ability, LocalTargetInfo target, LocalTargetInfo dest)
        {
            if (ability.Vanilla != null) NotifyCast(pawn, ability.Vanilla, target, dest);
        }

        internal static void NotifyCast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest)
        {
            for (int i = 0; i < hooks.Count; i++)
            {
                try { hooks[i].Notify_AICast(pawn, ability, target, dest); }
                catch (Exception e) { Log.ErrorOnce($"[Ability AI] {hooks[i].GetType().Name}.Notify_AICast threw: {e}", hooks[i].GetType().GetHashCode() ^ 0x5A2); }
            }
        }
    }

    /// <summary>Optional, on an ability's effect comp properties: what the AI can't read from vanilla comps. Lets a
    /// mod's own comp tell the workers its area and the hediff it gives without the AI knowing the comp's type.
    /// Only for mods that hard-depend on Ability AI; a soft-dependent mod uses AbilityAIInfo.Register instead.</summary>
    public interface IAbilityAIInfo
    {
        /// <summary>Radius of the affected area around the target, or 0 for one target.</summary>
        float AIRadius { get; }
        /// <summary>The hediff the ability gives, so the AI doesn't recast it on pawns that still have it. Null for none.</summary>
        HediffDef AIHediff { get; }
        /// <summary>Whether the ability stuns, so hostile workers skip pawns that are already stunned.</summary>
        bool AIStuns { get; }
    }

    public enum AbilityAICombat
    {
        /// <summary>The worker's own default.</summary>
        Default,
        /// <summary>Only while the caster is fighting.</summary>
        InCombat,
        /// <summary>Only while it isn't.</summary>
        OutOfCombat,
        /// <summary>Either.</summary>
        Any,
    }

    [Flags]
    public enum AbilityAIMoveModes
    {
        None = 0,
        /// <summary>Close the distance to the enemy (melee pawns).</summary>
        GapClose = 1,
        /// <summary>Get away: badly hurt, fleeing, or a ranged pawn caught in melee.</summary>
        Escape = 2,
        Both = GapClose | Escape,
    }

    /// <summary>What the caster must be fighting with for a rule to apply (Blade Focus: melee).</summary>
    public enum AbilityAIRequires
    {
        None,
        /// <summary>No ranged weapon or verb: a melee weapon, or fists.</summary>
        Melee,
        Ranged,
    }

    /// <summary>Which pawns (and, named, turrets) a rule may target or count.</summary>
    [Flags]
    public enum AbilityAITargetKinds
    {
        None = 0,
        Humanlike = 1,
        Animal = 2,
        Mechanoid = 4,
        /// <summary>Anything else: entities, and races that are none of the above.</summary>
        Other = 8,
        /// <summary>Every kind of pawn. Turrets are not in it: a rule that should hit them names Turret.</summary>
        All = Humanlike | Animal | Mechanoid | Other,
        /// <summary>Turrets (Haywire). Only the hostile area workers (AoE hostile, AoE hostile around the caster)
        /// target and count them; in allyKinds, avoidAllies also keeps clear of friendly turrets.</summary>
        Turret = 16,
        /// <summary>A pawn whose worn shield (a shield belt) is up, whatever its race: EMP breaks it. Not in All.
        /// Matching it reads the pawn's apparel, so only rules that name it pay for that.</summary>
        Shielded = 32,
    }

    /// <summary>Recovery: the health conditions a rule treats.</summary>
    [Flags]
    public enum AbilityAIHealth
    {
        None = 0,
        /// <summary>Injuries that are still healing, with summary health under healthThreshold.</summary>
        Injured = 1,
        /// <summary>A disease the pawn isn't yet immune to (plague, flu, infection...).</summary>
        Disease = 2,
        /// <summary>A missing body part not replaced by a prosthetic.</summary>
        MissingParts = 4,
    }

    /// <summary>Whose version of a rule applies: the extension as written for the player's pawns, its npc block for
    /// everyone else, and its friendlyNpc block on top of that for casters not hostile to the player.</summary>
    internal enum AbilityAIRole : byte
    {
        Player,
        Npc,
        FriendlyNpc,
    }

    /// <summary>
    /// An extension's &lt;npc&gt; or &lt;friendlyNpc&gt; block: any of the extension's own fields, replacing the rule's
    /// values for those casters only (&lt;enabled&gt;false&lt;/enabled&gt; keeps them off the rule). Parsed with the
    /// game's own loader, so def references, flags and lists read as they do at the top level; which fields the
    /// block named is kept, so only those change. The rule versions are built once, on first use.
    /// </summary>
    public class AbilityAIOverride
    {
        private AbilityAIExtension values;
        private readonly List<FieldInfo> fields = new List<FieldInfo>();
        internal readonly List<string> errors = new List<string>();

        // Called by reflection: the 1.7 project compiles net472 against net10 reference assemblies, where a direct call
        // to a game method that takes an XmlNode fails with CS1705 (System.Xml.ReaderWriter 10.0 vs 4.1.1)
        private static MethodInfo objectFromXml;

        public void LoadDataFromXmlCustom(XmlNode xmlRoot)
        {
            try
            {
                if (objectFromXml == null)
                    objectFromXml = typeof(DirectXmlToObject).GetMethod("ObjectFromXml", BindingFlags.Public | BindingFlags.Static)
                        .MakeGenericMethod(typeof(AbilityAIExtension));
                values = (AbilityAIExtension)objectFromXml.Invoke(null, new object[] { xmlRoot, false });
            }
            catch (Exception e)
            {
                // The block is left out (the rule applies to these casters unchanged) rather than the whole def
                errors.Add($"<{xmlRoot.Name}> could not be read, so it is ignored: {(e.InnerException ?? e).Message}");
                return;
            }
            foreach (XmlNode child in xmlRoot.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                // An unknown name has already been reported by the loader
                var field = typeof(AbilityAIExtension).GetField(child.Name, BindingFlags.Public | BindingFlags.Instance);
                if (field == null) continue;
                if (field.FieldType == typeof(AbilityAIOverride))
                {
                    errors.Add($"<{xmlRoot.Name}> can't hold <{child.Name}>");
                    continue;
                }
                fields.Add(field);
            }
        }

        internal AbilityAIExtension ApplyTo(AbilityAIExtension rule)
        {
            var copy = rule.CopyForRole();
            if (values != null)
                for (int i = 0; i < fields.Count; i++) fields[i].SetValue(copy, fields[i].GetValue(values));
            return copy;
        }
    }

    /// <summary>Put on an AbilityDef to let the AI cast it. Only workerClass is required; the rest are tuning a
    /// worker reads if it cares (see each worker's notes in AbilityAIWorkers.cs). An ability may carry several, each
    /// with its own worker and priority (Stun: interrupt first, any enemy otherwise); they are tried in priority order
    /// with every other ready ability's.</summary>
    public class AbilityAIExtension : DefModExtension
    {
        public Type workerClass;
        /// <summary>Higher is tried first when a pawn has several ready abilities.</summary>
        public int priority;
        public AbilityAICombat combat = AbilityAICombat.Default;
        /// <summary>Charges the AI never spends (abilities with charges only).</summary>
        public int reserveCharges;
        /// <summary>Seconds after an AI cast of this ability before the AI considers it again, on top of its cooldown.</summary>
        public float minGapSeconds;

        /// <summary>Area affected around the target; negative = read it from the effect comps.</summary>
        public float radius = -1f;
        /// <summary>Pawns the cast must affect (enemies for hostile workers, allies for friendly ones).</summary>
        public int minTargets = 1;
        /// <summary>Distance band from the caster to the target or threat; maxRange negative = the verb's range.</summary>
        public float minRange;
        public float maxRange = -1f;
        /// <summary>Summary health below which a pawn counts as badly hurt.</summary>
        public float healthThreshold = 0.5f;
        /// <summary>Recovery: the need to watch and the level below which to cast.</summary>
        public NeedDef need;
        public float needThreshold = 0.3f;
        /// <summary>Skip pawns that already have this; null = read it from the effect comps.</summary>
        public HediffDef hediff;
        /// <summary>Movement: which situations to move in.</summary>
        public AbilityAIMoveModes modes = AbilityAIMoveModes.Both;
        /// <summary>Movement gap-close: don't bother below this distance to the enemy.</summary>
        public float minGapDistance = 6f;
        /// <summary>Cover: how far from the caster toward the threat to place the cover; 0 = on the caster.</summary>
        public float offsetTowardThreat;
        /// <summary>Cover: ranged enemies aiming at the caster before it takes cover (being shot recently also counts).</summary>
        public int minThreats = 1;
        /// <summary>Hostile area workers: don't pick a centre with an ally of the caster inside the radius.</summary>
        public bool avoidAllies;
        /// <summary>Hostile workers: leave out pawns the ability would be wasted on: already stunned (a stunning
        /// ability), already have its hediff, already in a mental state (one that gives one). A psycast also skips
        /// pawns it can't affect (no psychic sensitivity). Turn off for an ability whose damage makes a stunned pawn
        /// still worth hitting.</summary>
        public bool skipAffected = true;
        /// <summary>How far past the ability's range the pawn will walk to cast it, in combat, when nothing is in range
        /// now (0 = never). Ranged pawns only (melee pawns close in anyway); vanilla's own positioning takes over
        /// after the cast. Workers that support it: AoE hostile, Target hostile, AoE friendly (self-cast only: walks
        /// toward allies).</summary>
        public float approachDistance;
        /// <summary>Target hostile: only enemies fighting the caster or an ally in melee.</summary>
        public bool meleeOnly;
        /// <summary>Target hostile: prefer an enemy with more of its own side around it (within radius, default 5).</summary>
        public bool preferGrouped;
        /// <summary>Target friendly: only allies in at least this much pain (0-1).</summary>
        public float minPain;
        /// <summary>Rescue: only allies downed by pain alone.</summary>
        public bool painShockOnly;
        /// <summary>Rescue: only allies bleeding at least this much per day (1 = 100%/day; 0 = any). For abilities
        /// that only stop bleeding (VPE's Stabilize).</summary>
        public float minBleedRate;
        /// <summary>Defensive: only shooters count as trouble (minThreats), not melee or low health. For protection
        /// that only stops projectiles (VPE's Overshield).</summary>
        public bool rangedOnly;
        /// <summary>Calm: any mental state, not only aggressive ones.</summary>
        public bool anyMentalState;
        /// <summary>Calm: skip a break that will end by itself within this many seconds.</summary>
        public float minBreakSeconds;
        /// <summary>Interrupt: also any aim with at least this many seconds left (0 = only abilities and explosives).</summary>
        public float minAimSeconds;
        /// <summary>Hostile workers: the ability disables without lasting harm (stun, burden, blinding), so with the
        /// BrokenAllies setting at HarmlessOnly it may be used on an ally in a hostile mental break.</summary>
        public bool harmless;
        /// <summary>Leave a target alone while another pawn of the caster's faction is casting this ability at it or
        /// walking into range to (so a raid doesn't all stabilize one downed pirate). Unset = the worker's default: on
        /// for every worker but the two that cast at enemies for damage (Target hostile, AoE hostile), where piling on
        /// one enemy is the point. Set it on a hostile rule whose effect doesn't stack (a stun keeps the longer of two).</summary>
        public bool? exclusive;
        /// <summary>Off: the rule does nothing. Meant for an npc or friendlyNpc block, to keep those casters off a rule
        /// the player's pawns keep (Ability AI still stands vanilla's and VEF's own AI down for them).</summary>
        public bool enabled = true;
        /// <summary>Only while the caster fights with this (Blade Focus: Melee).</summary>
        public AbilityAIRequires requires;
        /// <summary>Which pawns the rule may target or count; others are ignored (Haywire: Mechanoid).</summary>
        public AbilityAITargetKinds targetKinds = AbilityAITargetKinds.All;
        /// <summary>Which of the caster's side avoidAllies keeps clear of: those the effect would harm (Haywire:
        /// Mechanoid, Turret; it doesn't touch people). Default every pawn.</summary>
        public AbilityAITargetKinds allyKinds = AbilityAITargetKinds.All;
        /// <summary>Target friendly, Self buff: the buff is an aura that hits whatever comes near its bearer (Static
        /// Aura's EMP). Cast only while at least auraMin enemies of these kinds (turrets when Turret is named) are
        /// within the radius of the bearer, and with avoidAllies none of the caster's side of allyKinds besides the
        /// bearer. None = no such check.</summary>
        public AbilityAITargetKinds auraKinds = AbilityAITargetKinds.None;
        public int auraMin = 1;
        /// <summary>Only while the light on the caster's cell is at most this (0-1; Obscure, Shadow Focus: 0.3).
        /// 1 = any light.</summary>
        public float maxLight = 1f;
        /// <summary>Recovery: the health conditions it treats. None = the need rule alone.</summary>
        public AbilityAIHealth health;
        /// <summary>AoE hostile: never centre the area within its radius of the caster, for areas that hit the caster
        /// too (berserk pulse). Unlike a fixed minRange it follows the radius when a stat or another mod scales it.</summary>
        public bool avoidSelf;
        /// <summary>Hostile area and cone workers: not when a building of the caster's side, or of a faction not
        /// hostile to it, is inside (incendiaries cast by visitors near the player's base).</summary>
        public bool avoidBuildings;
        /// <summary>Cone hostile: width at the far end of the cone; negative = read it from the effects, else 1 (a line).</summary>
        public float coneWidth = -1f;
        /// <summary>Changes to this rule for every caster that isn't the player's own pawn.</summary>
        public AbilityAIOverride npc;
        /// <summary>Further changes, over npc, for casters not hostile to the player (visitors, traders, allied
        /// soldiers, wild animals): usually on the player's map.</summary>
        public AbilityAIOverride friendlyNpc;

        [Unsaved] private AbilityAIWorker worker;
        [Unsaved] private AbilityAIExtension npcRule, friendlyNpcRule;
        [Unsaved] private bool rulesBuilt;

        // One instance per extension. Psycast copies share their source's extension list by reference, so one worker
        // serves every copy: workers must keep no per-pawn state. A rule switched off has none, which every caller
        // already reads as "not an AI rule".
        public AbilityAIWorker Worker
        {
            get
            {
                if (worker == null && enabled && workerClass != null && typeof(AbilityAIWorker).IsAssignableFrom(workerClass) && !workerClass.IsAbstract)
                {
                    worker = (AbilityAIWorker)Activator.CreateInstance(workerClass);
                    worker.ext = this;
                }
                return worker;
            }
        }

        /// <summary>The version of this rule for the role; the extension itself without npc or friendlyNpc blocks.
        /// Each version has its own worker, so the workers read their own ext as usual.</summary>
        internal AbilityAIExtension For(AbilityAIRole role)
        {
            if (role == AbilityAIRole.Player || (npc == null && friendlyNpc == null)) return this;
            if (!rulesBuilt)
            {
                npcRule = npc != null ? npc.ApplyTo(this) : this;
                friendlyNpcRule = friendlyNpc != null ? friendlyNpc.ApplyTo(npcRule) : npcRule;
                rulesBuilt = true;
            }
            return role == AbilityAIRole.Npc ? npcRule : friendlyNpcRule;
        }

        internal AbilityAIExtension CopyForRole()
        {
            var copy = (AbilityAIExtension)MemberwiseClone();
            copy.worker = null;
            copy.npc = copy.friendlyNpc = null;
            copy.npcRule = copy.friendlyNpcRule = null;
            copy.rulesBuilt = false;
            return copy;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var e in base.ConfigErrors()) yield return e;
            var own = new List<string>(RuleErrors());
            foreach (var e in own) yield return e;
            if (npc != null) foreach (var e in npc.errors) yield return e;
            if (friendlyNpc != null) foreach (var e in friendlyNpc.errors) yield return e;
            // Cross-references are resolved by now, so the versions can be built and checked as they will run. Only
            // what a block brought in is reported; the rule's own errors are above.
            var npcVersion = For(AbilityAIRole.Npc);
            var npcErrors = npcVersion != this ? new List<string>(npcVersion.RuleErrors()) : own;
            if (npcVersion != this)
                foreach (var e in npcErrors) if (!own.Contains(e)) yield return "<npc>: " + e;
            var friendlyVersion = For(AbilityAIRole.FriendlyNpc);
            if (friendlyVersion != npcVersion)
                foreach (var e in friendlyVersion.RuleErrors()) if (!own.Contains(e) && !npcErrors.Contains(e)) yield return "<friendlyNpc>: " + e;
        }

        private IEnumerable<string> RuleErrors()
        {
            if (!enabled) yield break;
            if (workerClass == null) yield return "AbilityAIExtension needs a workerClass";
            else if (!typeof(AbilityAIWorker).IsAssignableFrom(workerClass)) yield return $"workerClass {workerClass} is not an AbilityAIWorker";
            else if (workerClass.IsAbstract) yield return $"workerClass {workerClass} is abstract";
            if (reserveCharges < 0) yield return "reserveCharges can't be negative";
            if (minTargets < 1) yield return "minTargets must be at least 1";
            if (approachDistance < 0f) yield return "approachDistance can't be negative";
            if (minBleedRate < 0f) yield return "minBleedRate can't be negative";
            if (rangedOnly && minThreats < 1) yield return "rangedOnly needs minThreats of at least 1";
            if (targetKinds == AbilityAITargetKinds.None) yield return "targetKinds is empty: the rule could never target anyone";
            else if (targetKinds == AbilityAITargetKinds.Turret && workerClass != null
                && !typeof(AbilityAIWorker_AoEHostile).IsAssignableFrom(workerClass) && !typeof(AbilityAIWorker_AoEHostileSelf).IsAssignableFrom(workerClass))
                yield return $"targetKinds names only Turret, which {workerClass.Name} never targets: the rule could never target anyone";
            if (avoidAllies && allyKinds == AbilityAITargetKinds.None) yield return "allyKinds is empty: avoidAllies would avoid nobody";
            if (maxLight < 0f) yield return "maxLight can't be negative";
            if (auraKinds != AbilityAITargetKinds.None)
            {
                if (auraMin < 1) yield return "auraMin must be at least 1";
                if (radius < 0f) yield return "auraKinds needs a radius: the aura's reach is rarely the ability's own";
                if (workerClass != null && !typeof(AbilityAIWorker_TargetFriendly).IsAssignableFrom(workerClass)
                    && !typeof(AbilityAIWorker_SelfBuff).IsAssignableFrom(workerClass))
                    yield return $"auraKinds is only read by Target friendly and Self buff, not {workerClass.Name}";
            }
        }
    }

    /// <summary>What a worker can ask about the caster's situation. Lists are built on first use and shared by
    /// every worker tried in one evaluation, so a pawn with three ready abilities scans the map once.</summary>
    public class AbilityAIContext
    {
        public Pawn pawn;
        public Map map;
        public int tick;

        private bool combatKnown, inCombat;
        private bool enemiesBuilt, alliesBuilt, aimsBuilt, friendliesBuilt, brokenBuilt, supportBuilt;
        private readonly List<Pawn> brokenAllies = new List<Pawn>();
        private readonly List<Pawn> supportAllies = new List<Pawn>();
        private readonly List<Pawn> friendlies = new List<Pawn>();
        private readonly List<Pawn> enemies = new List<Pawn>();
        private readonly List<Pawn> allies = new List<Pawn>();
        private readonly List<Pawn> downedAllies = new List<Pawn>();
        private readonly Dictionary<Pawn, int> aimedAt = new Dictionary<Pawn, int>();
        private readonly List<Thing> enemyTurrets = new List<Thing>();
        private readonly List<Thing> friendlyTurrets = new List<Thing>();
        private bool friendlyTurretsBuilt;
        private Thing enemyTarget;
        private bool enemyTargetKnown;
        private Def approachAbility;
        private int approachExpected;
        private bool drafted;

        /// <summary>While the pawn walks to a cell a worker chose with TryFindApproachCell for this ability: how
        /// many targets the worker expected to reach from there. 0 otherwise.</summary>
        public int ApproachExpected(AIAbility ability) => ability.Def == approachAbility ? approachExpected : 0;

        internal void Begin(Pawn p, int t, Def walkingFor, int expected, bool drafted)
        {
            pawn = p;
            this.drafted = drafted;
            map = p.Map;
            tick = t;
            approachAbility = walkingFor;
            approachExpected = expected;
            combatKnown = enemiesBuilt = alliesBuilt = aimsBuilt = friendliesBuilt = brokenBuilt = supportBuilt = enemyTargetKnown = false;
            friendlyTurretsBuilt = false;
        }

        internal void End()
        {
            enemies.Clear();
            enemyTurrets.Clear();
            friendlyTurrets.Clear();
            allies.Clear();
            downedAllies.Clear();
            aimedAt.Clear();
            friendlies.Clear();
            brokenAllies.Clear();
            supportAllies.Clear();
            enemyTarget = null;
            pawn = null;
            map = null;
        }

        public const int RecentTicks = 600;

        /// <summary>Has a hostile target, or attacked or was hurt in the last 10 seconds.</summary>
        public bool InCombat
        {
            get
            {
                if (!combatKnown)
                {
                    var mind = pawn.mindState;
                    // A drafted pawn is in a fighting stance: any threat on the map counts, so it can act before
                    // the first shot
                    inCombat = (drafted && Enemies.Count > 0) || EnemyTarget != null
                        || (mind != null && (tick - mind.lastHarmTick < RecentTicks || tick - mind.lastAttackTargetTick < RecentTicks
                            || (mind.lastRangedHarmTick > 0 && tick - mind.lastRangedHarmTick < RecentTicks)));
                    combatKnown = true;
                }
                return inCombat;
            }
        }

        /// <summary>The pawn's current hostile target: the fight job givers' enemy target, the target of an attack
        /// job (manhunters), or whoever is attacking it in melee.</summary>
        public Thing EnemyTarget
        {
            get
            {
                if (!enemyTargetKnown)
                {
                    enemyTargetKnown = true;
                    Thing t = pawn.mindState?.enemyTarget;
                    var job = pawn.CurJob;
                    if (t == null && job != null && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic)) t = job.targetA.Thing;
                    if (t == null) t = pawn.mindState?.meleeThreat;
                    if (t != null && (!t.Spawned || t.Map != map || (t is Pawn p && (p.Dead || p.Downed)))) t = null;
                    // A wild animal hit by a hunter isn't hostile to it, but is fighting back
                    if (t != null && !t.HostileTo(pawn) && t != pawn.mindState?.meleeThreat) t = null;
                    enemyTarget = t;
                }
                return enemyTarget;
            }
        }

        /// <summary>Hostile pawns on the map that are a threat now: standing, visible and not threat-disabled.
        /// From the attack-target cache, so turrets and other buildings are left out. A caster with no faction
        /// (manhunter, wild animal) gets nothing from the cache's faction lists, so for it every spawned pawn is
        /// checked instead (one HostileTo per pawn on the map), plus whoever it is fighting back against.</summary>
        public List<Pawn> Enemies
        {
            get
            {
                if (!enemiesBuilt)
                {
                    enemiesBuilt = true;
                    if (pawn.Faction == null)
                    {
                        var all = map.mapPawns.AllPawnsSpawned;
                        for (int i = 0; i < all.Count; i++)
                            if (all[i] != pawn && IsEnemy(all[i])) enemies.Add(all[i]);
                        // A wild animal hit by a hunter isn't hostile to it, but EnemyTarget keeps it as its target
                        if (EnemyTarget is Pawn target && !enemies.Contains(target) && !target.IsPsychologicallyInvisible())
                            enemies.Add(target);
                    }
                    else
                    {
                        // The cache hands back one shared list that its next caller clears, so copy out of it at once.
                        // It can list a pawn twice: a hostile raider in an aggro break comes from both the faction set
                        // and the mental-state set, a factionless humanlike from the factionless set and the faction set
                        // of a faction hostile to them (mechanoids, entities), and a prison-breaking or rebelling caster
                        // adds its host's pawns on top. Counting it twice would inflate area casts, so those are checked
                        // against the list.
                        bool casterBreaking = pawn.guest != null && (PrisonBreakUtility.IsPrisonBreaking(pawn)
                            || (ModsConfig.IdeologyActive && SlaveRebellionUtility.IsRebelling(pawn)));
                        var targets = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                        for (int i = 0; i < targets.Count; i++)
                        {
                            if (targets[i].Thing is Pawn p)
                            {
                                if (IsEnemy(p) && ((p.MentalState == null && p.Faction != null && !casterBreaking) || !enemies.Contains(p)))
                                    enemies.Add(p);
                            }
                            // Collected in the same pass, since the cache list can't be asked for twice cheaply
                            else if (targets[i] is Building_Turret turret && turret.Spawned && !turret.ThreatDisabled(pawn) && turret.HostileTo(pawn))
                                enemyTurrets.Add(turret);
                        }
                    }
                }
                return enemies;
            }
        }

        /// <summary>Hostile to the caster only because of its own mental state (a berserk colonist, a tame animal gone
        /// manhunter, a raider's friend in a rage): its faction is the caster's or isn't hostile to it. Such a pawn is
        /// in Enemies, since it is a threat, but hostile workers spare it unless the BrokenAllies setting allows
        /// (AbilityAIUtility.MayHitBrokenAllies). A factionless caster or target never counts. One field read for a
        /// pawn not in a mental state.</summary>
        public bool IsBrokenAlly(Pawn p)
        {
            var state = p.MentalState;
            if (state == null) return false;
            Faction mine = pawn.Faction, theirs = p.Faction;
            if (mine == null || theirs == null) return false;
            return (theirs == mine || !theirs.HostileTo(mine)) && state.ForceHostileTo(pawn);
        }

        /// <summary>The broken allies among Enemies (IsBrokenAlly), for workers that keep clear of them as they do of
        /// Friendlies. Built on first use: k mental-state reads.</summary>
        public List<Pawn> BrokenAllies
        {
            get
            {
                if (!brokenBuilt)
                {
                    brokenBuilt = true;
                    var list = Enemies;
                    for (int i = 0; i < list.Count; i++)
                        if (IsBrokenAlly(list[i])) brokenAllies.Add(list[i]);
                }
                return brokenAllies;
            }
        }

        private bool IsEnemy(Pawn p)
        {
            if (!p.Spawned || p.Dead || p.Downed) return false;
            return !p.ThreatDisabled(pawn) && !p.IsPsychologicallyInvisible() && p.HostileTo(pawn);
        }

        /// <summary>Standing pawns of the caster's faction on the map, the caster included. A pawn with no faction
        /// has only itself.</summary>
        public List<Pawn> Allies
        {
            get
            {
                BuildAllies();
                return allies;
            }
        }

        /// <summary>Allies a buff may go to: Allies without those in a hostile break (IsBrokenAlly), so Focus,
        /// Painblock or Invisibility never target a berserker. An area buff (AoEFriendly) doesn't count them or centre
        /// on them, but still covers one standing in its radius. The caster is always in it.
        /// Built on first use: one mental-state read per ally.</summary>
        public List<Pawn> SupportAllies
        {
            get
            {
                if (!supportBuilt)
                {
                    supportBuilt = true;
                    var own = Allies;
                    for (int i = 0; i < own.Count; i++)
                        if (own[i] == pawn || !IsBrokenAlly(own[i])) supportAllies.Add(own[i]);
                }
                return supportAllies;
            }
        }

        /// <summary>Downed, living pawns of the caster's faction on the map, prisoners left out.</summary>
        public List<Pawn> DownedAllies
        {
            get
            {
                BuildAllies();
                return downedAllies;
            }
        }

        private void BuildAllies()
        {
            if (alliesBuilt) return;
            alliesBuilt = true;
            if (pawn.Faction == null) { allies.Add(pawn); return; }
            var list = map.mapPawns.SpawnedPawnsInFaction(pawn.Faction);
            // A captured raider keeps its faction: not someone its friends can buff from across the map
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p.Dead || p.IsPrisoner) continue;
                (p.Downed ? downedAllies : allies).Add(p);
            }
        }

        /// <summary>Standing pawns the caster mustn't catch in a hostile area or provoke a berserker next to: its
        /// allies (the caster included) plus every other pawn with a faction that isn't hostile to it, so a friendly
        /// visitor's pulse spares the colonists it fights beside. Wild animals are left out. Prisoners held by the
        /// caster's faction are in it (spared by area casts), unlike Allies; captured members of the caster's own
        /// faction are in neither list. A caster with no faction (a wild animal) gets
        /// every non-hostile pawn with a faction. Allies in a hostile break that are in BrokenAllies are left out. Built only when a worker asks (avoidAllies, meleeOnly): one pass
        /// over the map's spawned pawns.</summary>
        public List<Pawn> Friendlies
        {
            get
            {
                if (!friendliesBuilt)
                {
                    friendliesBuilt = true;
                    // An ally in a hostile break (own faction or a friendly one) goes either here or in BrokenAllies (which then
                    // decides whether it is spared), never both, never neither: one that Enemies drops (threat-disabled,
                    // invisible, missing from the attack-target cache) stays here. Contains runs only for a broken ally.
                    var own = Allies;
                    for (int i = 0; i < own.Count; i++)
                        if (own[i] == pawn || !IsBrokenAlly(own[i]) || !BrokenAllies.Contains(own[i])) friendlies.Add(own[i]);
                    var all = map.mapPawns.AllPawnsSpawned;
                    for (int i = 0; i < all.Count; i++)
                    {
                        Pawn p = all[i];
                        if (p.Faction == null || p.Faction == pawn.Faction || p.Dead || p.Downed) continue;
                        if (!p.HostileTo(pawn) || (IsBrokenAlly(p) && !BrokenAllies.Contains(p))) friendlies.Add(p);
                    }
                }
                return friendlies;
            }
        }

        /// <summary>Hostile turrets that are a threat now (powered, manned where they need it), from the same
        /// attack-target pass as Enemies. Empty for a caster with no faction.</summary>
        public List<Thing> EnemyTurrets
        {
            get
            {
                _ = Enemies;
                return enemyTurrets;
            }
        }

        /// <summary>Spawned turrets of the caster's faction or of one not hostile to it, for avoidAllies with Turret
        /// in allyKinds. Built only when such a rule asks: one pass over the map's attack targets (pawns and
        /// turrets).</summary>
        public List<Thing> FriendlyTurrets
        {
            get
            {
                if (!friendlyTurretsBuilt)
                {
                    friendlyTurretsBuilt = true;
                    var list = map.listerThings.ThingsInGroup(ThingRequestGroup.AttackTarget);
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] is Building_Turret t && t.Spawned && t.Faction != null && !t.HostileTo(pawn)) friendlyTurrets.Add(t);
                }
                return friendlyTurrets;
            }
        }

        /// <summary>How many ranged enemies are aiming at this pawn now: their fight target or current attack job.
        /// Built once per evaluation from Enemies (k checks), then a lookup.</summary>
        public int AimedAtCount(Pawn p)
        {
            if (!aimsBuilt)
            {
                aimsBuilt = true;
                var list = Enemies;
                for (int i = 0; i < list.Count; i++)
                {
                    Pawn e = list[i];
                    if (!FightsAtRange(e)) continue;
                    Thing t = AbilityAIUtility.AimTarget(e);
                    if (t is Pawn victim)
                    {
                        aimedAt.TryGetValue(victim, out int n);
                        aimedAt[victim] = n + 1;
                    }
                }
            }
            return aimedAt.TryGetValue(p, out int c) ? c : 0;
        }

        /// <summary>Shot at in the last 10 seconds.</summary>
        public bool RecentlyShot(Pawn p)
        {
            var mind = p.mindState;
            return mind != null && mind.lastRangedHarmTick > 0 && tick - mind.lastRangedHarmTick < RecentTicks;
        }

        /// <summary>Whether the pawn fights at range: its primary weapon, or for a pawn without one (animals, some
        /// mechs) its first ranged verb.</summary>
        public bool UsesRanged => FightsAtRange(pawn);

        public static bool FightsAtRange(Pawn p)
        {
            var primary = p.equipment?.Primary;
            if (primary != null) return primary.def.IsRangedWeapon;
            var verbs = p.verbTracker?.AllVerbs;
            if (verbs != null) for (int i = 0; i < verbs.Count; i++) if (!verbs[i].IsMeleeAttack) return true;
            return false;
        }

        public float HealthFraction => pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;

        /// <summary>The nearest enemy and its squared distance, or null.</summary>
        public Pawn NearestEnemy(out float distSq)
        {
            Pawn best = null;
            distSq = float.MaxValue;
            var list = Enemies;
            for (int i = 0; i < list.Count; i++)
            {
                float d = (list[i].Position - pawn.Position).LengthHorizontalSquared;
                if (d < distSq) { distSq = d; best = list[i]; }
            }
            return best;
        }
    }

    public static class AbilityAIDriver
    {
        // Between evaluations of one pawn. Vanilla asks the constant think tree every 30 ticks and again each time
        // the pawn picks a job, so without this a pawn changing jobs often would be evaluated more than that.
        public const int EvalIntervalTicks = 30;

        // A walk into casting range ends after this long whether or not it got there, and the same ability isn't
        // walked toward again for ApproachRetryTicks after one starts, or for ApproachFailRetryTicks after no
        // approach cell is found
        public const int ApproachExpiryTicks = 300;
        public const int ApproachRetryTicks = 600;
        public const int ApproachFailRetryTicks = 120;

        private class PawnState
        {
            public int nextEvalTick;
            // The last interval set was the slow undrafted one: drafting, fighting back or fleeing cuts it short
            public bool slowEval;
            public int nextCastTick;
            public Dictionary<Def, int> abilityNext;
            public Dictionary<Def, int> approachNext;
            public int approachJobId = -1;
            // Set by a TryFindApproachCell walk, cleared with approachJobId
            public Def approachAbility;
            public int approachExpected;
            // What a TryFindApproachTarget walk is for, cleared with approachJobId: an ally's claim on that target
            public Def approachDef;
            public LocalTargetInfo approachGoal = LocalTargetInfo.Invalid;

            // Player Full mode: where the pawn stood before it walked to cast, the AttackStatic it was on (a fresh
            // copy to queue once back), and the walk back. Kept from the start of the walk until the pawn is back
            // or the player takes over (a new order, undrafting). Not saved: a pawn loaded mid-walk stays wherever
            // it is, as any drafted pawn would.
            public IntVec3 home = IntVec3.Invalid;
            public Job resume;
            public int returnJobId = -1;
            public IntVec3 returnCell = IntVec3.Invalid;
            public int returnTries;

            // The dev log's "casts nothing" line: when the next may be written, and the last one, so an unchanged
            // reason isn't repeated every few seconds
            public int nextWhyNotTick;
            public string lastWhyNot;
            public int lastWhyNotTick;
        }

        /// <summary>Which workers a player's pawn may use at the moment.</summary>
        internal enum PlayerSet
        {
            /// <summary>Undrafted: workers that aren't combat-only (WorksUndrafted).</summary>
            Safe,
            /// <summary>Undrafted and fleeing: those plus Movement and Recovery (WorksFleeing).</summary>
            Fleeing,
            /// <summary>Drafted, or undrafted and fighting back: every worker.</summary>
            All,
        }

        // Steps on the way back after a Full walk that were interrupted (shot at: the drafted think tree stops the
        // pawn where it is) before giving up on going home
        private const int MaxReturnTries = 2;
        // How far from where it ended up (after the cast, or where a walk out without one stopped) a pawn looking for
        // new cover may go
        private const float CoverSearchRadius = 6f;

        private struct Candidate
        {
            public AIAbility ability;
            public AbilityAIExtension ext;
        }

        // Not saved: after a load every pawn may cast again at once, limited by the ability's own cooldown and charges
        private static readonly ConditionalWeakTable<Pawn, PawnState> states = new ConditionalWeakTable<Pawn, PawnState>();
        private static readonly AbilityAIContext ctx = new AbilityAIContext();
        private static readonly List<Candidate> candidates = new List<Candidate>();
        // The pawn's abilities with an extension, from every source; filled and emptied by TryGiveJob
        private static readonly List<AIAbility> collected = new List<AIAbility>();
        private static bool evaluating;
        // Set for one evaluation when the dev log is on and the pawn is due a "casts nothing" line; null otherwise, so
        // with the log off no reason is ever worked out
        private static StringBuilder whyNot;
        private static readonly StringBuilder whyNotBuffer = new StringBuilder();
        private const int WhyNotIntervalTicks = 600;
        private const int WhyNotRepeatTicks = 2500;
        // The set of workers the pawn being evaluated may use (All for NPCs)
        private static PlayerSet curSet = PlayerSet.All;
        // The pawn being evaluated is under the player rules (Control.Player). A player pawn in a break run by the
        // NPC rules (BreakCasting) is not, and keeps vanilla's player-faction AI vetoes (CompAllowsTarget).
        private static bool curPlayer;

        // Casts the evaluated pawn's faction-mates have under way (running, or walking into range for), for exclusive
        // workers: built on the first exclusive target check of an evaluation, from the mates' current jobs rather
        // than a record of what this AI started, so a cast the player ordered or vanilla's or VEF's AI began counts too
        private struct Claim { public Def def; public LocalTargetInfo target; }
        private static readonly List<Claim> claims = new List<Claim>();
        private static bool claimsBuilt;
        // The candidate being tried, while its worker is exclusive: Valid refuses a target a mate has claimed for it
        private static Def exclusiveDef;

        /// <summary>True while TryGiveJob evaluates a pawn under the player rules: workers read it for rules that
        /// differ for the player's own pawns (what Calm will end, how far it walks).</summary>
        internal static bool EvaluatingPlayer => evaluating && curPlayer;




        /// <summary>The player's per-pawn psycast limits (the gizmo beside vanilla's psyfocus one): the cast must not
        /// push neural heat over the cap or psyfocus under the floor. Real Psycasts only; a pawn with no limits set
        /// stops at one dictionary lookup. Without a target the cheapest cost the psycast can have is used (asked
        /// while choosing candidates); with one, the cost for that target (Word of Serenity costs more for a worse
        /// break).</summary>
        private static bool PsycastWithinLimits(AIAbility a, LocalTargetInfo target)
        {
            if (!a.IsPsycast) return true;
            Pawn pawn = a.Pawn;
            if (!AbilityAIAutocast.TryGetLimits(pawn, out float heatCap, out float focusFloor)) return true;
            var pe = pawn.psychicEntropy;
            if (pe == null) return true;
            if (heatCap < 1f)
            {
                float gain = a.HeatGain;
                if (gain > float.Epsilon)
                {
                    // As Pawn_PsychicEntropyTracker.TryAddEntropy scales it; both stats cached for 250 ticks
                    gain *= pawn.GetStatValue(StatDefOf.PsychicEntropyGain, true, 250);
                    float max = pawn.GetStatValue(StatDefOf.PsychicEntropyMax, true, 250);
                    if (pe.EntropyValue + gain > heatCap * max) return false;
                }
            }
            if (focusFloor > 0f)
            {
                float cost = a.PsyfocusCost(target);
                if (cost > float.Epsilon && pe.CurrentPsyfocus - cost < focusFloor - Pawn_PsychicEntropyTracker.PsyfocusCostTolerance) return false;
            }
            return true;
        }

        /// <summary>Who decides what the AI may cast for a pawn.</summary>
        public enum Control
        {
            /// <summary>The AI leaves this pawn alone.</summary>
            None,
            /// <summary>The AI's own judgement: other factions' pawns, wild animals, and with the setting on, the
            /// player's pawns in a mental break. Abilities the hooks claim (or all, with Unclaimed).</summary>
            NPC,
            /// <summary>The player's own pawn: only abilities the player set to autocast, under the player rules.</summary>
            Player,
        }

        /// <summary>Whether and how the AI acts for this pawn, by the settings. Every pawn of the player's only for
        /// abilities the player set to autocast; prisoners never. Pawns that can't be drafted (most animals) only get
        /// the menu for abilities with a worker that runs undrafted.</summary>
        public static Control ControlOf(Pawn pawn)
        {
            if (pawn.IsPrisoner) return Control.None;
            if (pawn.Faction != null && pawn.Faction.IsPlayer)
            {
                // A break takes the pawn out of the player's hands, and with it the player's choices: see BreakCasting.
                // Among the per-pawn settings that one alone decides for the player's pawns; the Hostiles, NonHostiles,
                // Animals and Mechs boxes are about everyone else's.
                if (pawn.InMentalState) return AbilityAISettings.BreakCasting ? Control.NPC : Control.None;
                // In a lord: forming a caravan, roped, in a ritual. A constant-tree job interrupts whatever they are
                // doing, so leave them to it
                if (pawn.GetLord() != null) return Control.None;
                return AbilityAISettings.PlayerAutocast && AbilityAIAutocast.HasAny(pawn) ? Control.Player : Control.None;
            }
            return Covered(pawn) ? Control.NPC : Control.None;
        }

        /// <summary>Whether the NPC rules act for this kind of pawn at all, by the settings. Only for pawns that aren't
        /// the player's (ControlOf decides for those) and aren't prisoners.</summary>
        public static bool Covered(Pawn pawn)
        {
            if (pawn.Faction != null && pawn.Faction.IsPlayer) return false;
            if (pawn.IsPrisoner) return false;
            if (pawn.RaceProps.IsMechanoid) return AbilityAISettings.Mechs;
            if (pawn.RaceProps.Animal) return AbilityAISettings.Animals;
            return pawn.HostileTo(Faction.OfPlayer) ? AbilityAISettings.Hostiles : AbilityAISettings.NonHostiles;
        }

        /// <summary>Awake, standing, not on fire, not drafted, not stunned, not already casting, and not in a mental
        /// state other than an aggressive one or panic (a fleeing pawn may still blink away).</summary>
        public static bool CanActNow(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || !pawn.Awake() || pawn.IsBurning()) return false;
            if (pawn.Drafted) return false;
            if (pawn.InMentalState && !pawn.InAggroMentalState && pawn.MentalStateDef != MentalStateDefOf.PanicFlee) return false;
            if (AIAbilitySources.IsCasting(pawn)) return false;
            if (pawn.stances?.stunner?.Stunned == true) return false;
            return true;
        }

        /// <summary>The player rules: awake, standing, not on fire, not in a mental state, not casting or stunned.
        /// Drafted: only while standing still in position or shooting, never during an ordered move (the pawn's own
        /// Full-mode walk into range excepted). Undrafted (with UndraftedAutocast on): not lying down, not carrying
        /// anything, and only interrupting a fight, or a job that may be casually interrupted and that the player
        /// didn't order. The driver then picks the set of workers by what the pawn is doing (PlayerSet).</summary>
        public static bool CanActNowPlayer(Pawn pawn)
        {
            states.TryGetValue(pawn, out var state);
            return CanActNowPlayer(pawn, state);
        }

        private static bool CanActNowPlayer(Pawn pawn, PawnState state)
        {
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || !pawn.Awake() || pawn.IsBurning()) return false;
            if (pawn.InMentalState || AIAbilitySources.IsCasting(pawn)) return false;
            if (pawn.stances?.stunner?.Stunned == true) return false;
            Job cur = pawn.CurJob;
            if (!pawn.Drafted)
            {
                if (!AbilityAISettings.UndraftedAutocast) return false;
                // In bed, resting, or carrying someone to one: the cast would get them up or drop what they hold
                if (pawn.GetPosture() != PawnPosture.Standing || pawn.carryTracker?.CarriedThing != null) return false;
                if (cur == null) return true;
                // Fighting back (a hostility response, an animal defending itself or released on a target): attack
                // jobs can't be casually interrupted, but the cast is part of the fight, and TryGiveJob queues the
                // attack to resume after it
                if (IsFightJob(cur.def)) return true;
                // A constant-tree job ends the current one: never surgery, a bill, or anything the player ordered,
                // nor a job on the player's do-not-interrupt list (meals, recreation, gatherings by default)
                return !cur.playerForced && cur.def.casualInterruptible && !AbilityAIBlockedJobs.IsBlocked(cur.def);
            }
            // Our own walk into range (Full mode) is a move, but casting from it is what it is for
            if (state != null && state.approachJobId >= 0 && cur != null && cur.loadID == state.approachJobId) return true;
            if (pawn.pather != null && pawn.pather.Moving) return false;
            JobDef job = cur?.def;
            return job == null || job == JobDefOf.Wait_Combat || job == JobDefOf.Wait || job == JobDefOf.Wait_MaintainPosture
                || job == JobDefOf.AttackStatic;
        }

        private static bool IsFightJob(JobDef def) => def == JobDefOf.AttackStatic || def == JobDefOf.AttackMelee;

        /// <summary>Whether an undrafted player pawn should keep the quick 30-tick check: it fights back
        /// (FightsUndrafted) and is in a fight by InCombat's undrafted meaning, or it is fleeing. A pawn that doesn't
        /// fight back never uses combat abilities undrafted, so being hurt alone doesn't count. A handful of field
        /// reads, no allocation: it runs every time vanilla asks the constant think tree, before the interval check.
        /// The undrafted InCombat test minus the melee-attacker scan, which the recent-harm ticks already cover.</summary>
        private static bool UndraftedNeedsQuickCheck(Pawn pawn, int tick)
        {
            // No PanicFlee test: a pawn in a mental state is never under the player rules (ControlOf)
            var job = pawn.CurJob;
            if (job != null && (job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower)) return true;
            var mind = pawn.mindState;
            bool recent = mind != null && (tick - mind.lastHarmTick < AbilityAIContext.RecentTicks
                || tick - mind.lastAttackTargetTick < AbilityAIContext.RecentTicks
                || (mind.lastRangedHarmTick > 0 && tick - mind.lastRangedHarmTick < AbilityAIContext.RecentTicks));
            if (job != null && job.exitMapOnArrival && recent) return true;
            if (!FightsUndrafted(pawn)) return false;
            return recent || (job != null && IsFightJob(job.def)) || (mind != null && mind.enemyTarget != null);
        }

        /// <summary>Whether this player pawn fights back while undrafted: a colonist or colony subhuman with the
        /// hostility response set to Attack, or a tame animal (no hostility response of its own; it defends itself
        /// and its master, or attacks when released). Colony mechs undrafted have no response to set and don't.</summary>
        internal static bool FightsUndrafted(Pawn pawn)
        {
            var ps = pawn.playerSettings;
            if (ps != null && ps.UsesConfigurableHostilityResponse) return ps.hostilityResponse == HostilityResponseMode.Attack;
            return pawn.RaceProps.Animal || pawn.drafter == null;
        }

        /// <summary>The widest set this player pawn could ever use undrafted, whatever is happening right now: every
        /// worker if it fights back, else the fleeing set (anyone may end up fleeing).</summary>
        internal static PlayerSet UndraftedReach(Pawn pawn) => FightsUndrafted(pawn) ? PlayerSet.All : PlayerSet.Fleeing;

        // The set for the pawn in ctx now, undrafted: every worker while it fights back (fighting by InCombat's
        // undrafted meaning: a hostile target, or attacked or hurt in the last 10 s; an enemy elsewhere on the map
        // doesn't count), the fleeing set while it runs, else the safe set
        private static PlayerSet UndraftedSetNow(Pawn pawn)
        {
            if (FightsUndrafted(pawn) && ctx.InCombat) return PlayerSet.All;
            return AbilityAIUtility.IsFleeing(ctx) ? PlayerSet.Fleeing : PlayerSet.Safe;
        }

        internal static bool InSet(AbilityAIWorker worker, PlayerSet set)
        {
            switch (set)
            {
                case PlayerSet.All: return true;
                case PlayerSet.Fleeing: return worker.WorksFleeing;
                default: return worker.WorksUndrafted;
            }
        }

        // The player's own pawn: their mode counts as the claim, but a hook can still veto (an item bound to someone else)
        private static bool PlayerAllowed(Pawn pawn, AIAbility a)
        {
            return AbilityAIAutocast.ModeFor(pawn, a.Def) != AbilityAIAutocastMode.Off && !a.PlayerAutocastElsewhere
                && AbilityAIHooks.NotVetoed(pawn, a);
        }

        /// <summary>Whether this mod is the one that decides when this pawn casts this ability: the AI is on, the
        /// ability has a working extension, the settings cover the pawn and a hook (or Unclaimed, or the player's
        /// autocast mode) lets it through. Independent of the moment: an ability on cooldown or held back by
        /// reserveCharges is still ours. Public so a host's own fallback AI can step aside for the same abilities.
        /// The player's pawns undrafted only with that setting on and when the ability has a worker the pawn could
        /// use undrafted (UndraftedReach: every worker for one that fights back, else the safe and fleeing ones):
        /// otherwise the driver never casts it for them, so whatever vanilla or the host would do is left alone.</summary>
        public static bool Handles(Pawn pawn, Ability ability)
        {
            // The cheap tests before the wrapper lookup: vanilla's stand-down asks this for every ready aiCanUse ability
            if (!AbilityAISettings.Enabled || pawn == null || ability?.def == null) return false;
            if (!HasWorker(ability.def, PlayerSet.All)) return false;
            return Handles(pawn, VanillaAIAbility.Of(ability));
        }

        /// <summary>Handles for an ability of any system. A compat assembly asks it before letting the ability's own
        /// AI cast (VEF's autocast through Pawn.TryGetAttackVerb), so the two never both cast it.</summary>
        public static bool Handles(Pawn pawn, AIAbility ability)
        {
            if (!AbilityAISettings.Enabled || pawn == null || ability?.Def == null) return false;
            if (!HasWorker(ability.Def, PlayerSet.All)) return false;
            switch (ControlOf(pawn))
            {
                case Control.NPC: return AbilityAIHooks.Allowed(pawn, ability);
                case Control.Player:
                    if (!PlayerAllowed(pawn, ability)) return false;
                    return pawn.Drafted || (AbilityAISettings.UndraftedAutocast && HasWorker(ability.Def, UndraftedReach(pawn)));
                default: return false;
            }
        }

        /// <summary>A cast this AI started is running: the current job casts an ability and came from the constant
        /// tree's root, which vanilla never issues a job from. warmupTicksLeft as IAIAbilitySource.TryGetRunningCast.</summary>
        internal static bool IsOwnCast(Pawn pawn, out int warmupTicksLeft)
        {
            warmupTicksLeft = -1;
            Job cur = pawn.CurJob;
            if (cur?.jobGiver == null || cur.jobGiver != pawn.thinker?.ConstantThinkTree?.thinkRoot) return false;
            return AIAbilitySources.TryGetRunningCast(pawn, out _, out warmupTicksLeft);
        }

        /// <summary>Whether every other AI (vanilla's through Ability.AICanTargetNow, or a host's own fallback) should
        /// leave this pawn and ability alone: whenever Handles is true, and for the player's own pawns (not prisoners)
        /// whenever the ability has a working extension at all, whatever its autocast mode, unless a hook vetoes it for
        /// that pawn (the veto hands it back to vanilla or the host's fallback). The
        /// player's pawns then only cast an ability this mod has rules for when the player set it to autocast; turning
        /// off Enabled or PlayerAutocast hands them back to vanilla. A pawn in a mental break is out of the player's
        /// hands, so for it only Handles counts: vanilla keeps the break unless BreakCasting has this mod take it
        /// over. Our driver never asks this, so it never stands itself down.</summary>
        public static bool OthersStandDown(Pawn pawn, Ability ability)
        {
            if (!AbilityAISettings.Enabled || pawn == null || ability?.def == null) return false;
            // A host's veto hands the ability back to vanilla or the host's own fallback (HasWorker first: it is the
            // cheap test)
            if (AbilityAISettings.PlayerAutocast && pawn.Faction != null && pawn.Faction.IsPlayer && !pawn.IsPrisoner
                && !pawn.InMentalState)
                return HasWorker(ability.def, PlayerSet.All) && AbilityAIHooks.NotVetoed(pawn, ability);
            return Handles(pawn, ability);
        }

        /// <summary>Any of its extensions has a working worker (TryGiveJob tries them all) in the given set.</summary>
        internal static bool HasWorker(Def def, PlayerSet set)
        {
            var exts = def.modExtensions;
            if (exts == null) return false;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.Worker != null && InSet(ext.Worker, set)) return true;
            return false;
        }

        /// <summary>Every vanilla check a player's cast would pass, minus aiCanUse (see the file header), plus each
        /// effect comp's AI veto. Workers call it on each target they consider; it is also run once more on the
        /// target they return.</summary>
        public static bool Valid(Ability ability, LocalTargetInfo target) => Valid(VanillaAIAbility.Of(ability), target);

        public static bool Valid(AIAbility ability, LocalTargetInfo target) => !ClaimedByMate(ability, target) && ability.Valid(target);

        /// <summary>Valid, short of the verb's own check (range, line of sight, reachability; it repeats the comps'
        /// Valid). For targets the pawn means to walk into range of. The player-caster comp rules (CompAllowsTarget)
        /// apply only while TryGiveJob is evaluating a pawn under Control.Player; called from anywhere else, every
        /// comp's own AICanTargetNow decides.</summary>
        public static bool ValidIgnoringRange(Ability ability, LocalTargetInfo target) => ValidIgnoringRange(VanillaAIAbility.Of(ability), target);

        public static bool ValidIgnoringRange(AIAbility ability, LocalTargetInfo target) => !ClaimedByMate(ability, target) && ability.ValidIgnoringRange(target);

        private static bool ClaimedByMate(AIAbility ability, LocalTargetInfo target)
        {
            if (exclusiveDef == null || !evaluating || ability.Def != exclusiveDef || !target.IsValid) return false;
            if (!claimsBuilt) BuildClaims(ctx.pawn);
            for (int i = 0; i < claims.Count; i++)
            {
                if (claims[i].def != exclusiveDef) continue;
                LocalTargetInfo c = claims[i].target;
                // A location-targeted cast at a pawn aims at its cell: compare cells unless both name a thing
                if (c.HasThing && target.HasThing ? c.Thing == target.Thing : c.Cell == target.Cell) return true;
            }
            return false;
        }

        // Cost: one pass over the faction's spawned pawns, a job def test each (the sources answer from it first)
        private static void BuildClaims(Pawn pawn)
        {
            claimsBuilt = true;
            if (pawn.Faction == null) return;
            var mates = pawn.Map.mapPawns.SpawnedPawnsInFaction(pawn.Faction);
            for (int i = 0; i < mates.Count; i++)
            {
                Pawn p = mates[i];
                if (p == pawn) continue;
                Job cur = p.CurJob;
                if (cur == null) continue;
                // Both vanilla's and VEF's cast jobs carry the target as targetA
                if (AIAbilitySources.TryGetRunningCast(p, out AIAbility running, out _))
                {
                    if (cur.targetA.IsValid) claims.Add(new Claim { def = running.Def, target = cur.targetA });
                }
                else if (states.TryGetValue(p, out PawnState s) && s.approachDef != null && cur.loadID == s.approachJobId)
                    claims.Add(new Claim { def = s.approachDef, target = s.approachGoal });
            }
        }

        internal static Job TryGiveJob(Pawn pawn, Control control)
        {
            if (evaluating) return null;
            try
            {
                Job job = Evaluate(pawn, control);
                if (job == null && whyNot != null && whyNot.Length > 0) LogWhyNot(pawn, control);
                return job;
            }
            finally
            {
                // The postfix catches a throw: a buffer left set would be logged under the next pawn's name
                whyNot = null;
            }
        }

        private static void LogWhyNot(Pawn pawn, Control control)
        {
            int tick = Find.TickManager.TicksGame;
            var state = states.GetValue(pawn, _ => new PawnState());
            state.nextWhyNotTick = tick + WhyNotIntervalTicks;
            string text = whyNot.ToString();
            if (text == state.lastWhyNot && tick < state.lastWhyNotTick + WhyNotRepeatTicks) return;
            state.lastWhyNot = text;
            state.lastWhyNotTick = tick;
            string who = control == Control.Player ? (pawn.Drafted ? "player, drafted" : "player, undrafted")
                : pawn.HostileTo(Faction.OfPlayer) ? "hostile NPC" : "friendly NPC";
            Log.Message($"[Ability AI] {pawn.LabelShort} ({who}) casts nothing: {text}");
        }

        private static void WhyNot(AIAbility a, AbilityAIExtension ext, string reason)
        {
            if (whyNot == null) return;
            if (whyNot.Length > 0) whyNot.Append("; ");
            if (a != null)
            {
                whyNot.Append(a.Label);
                if (ext != null) whyNot.Append(" (").Append(ext.Worker != null ? ext.Worker.GetType().Name : ext.workerClass?.Name).Append(')');
                whyNot.Append(": ");
            }
            whyNot.Append(reason);
        }

        // The first of the ready checks in Evaluate that failed, in the same order
        private static string NotReadyWhy(Pawn pawn, AIAbility a, PawnState state, int tick, bool player)
        {
            if (!a.CanCastNow) return a.NotReadyReason;
            if (state.abilityNext != null && state.abilityNext.TryGetValue(a.Def, out int next) && tick < next)
                return $"waiting out its minGapSeconds ({(next - tick) / 60f:0.#} s)";
            if (player)
            {
                if (AbilityAIAutocast.ModeFor(pawn, a.Def) == AbilityAIAutocastMode.Off) return "autocast is off";
                if (a.PlayerAutocastElsewhere) return "Better Autocasting decides it";
                if (!AbilityAIHooks.NotVetoed(pawn, a)) return "a hook vetoed it";
                return "over this pawn's psycast limits";
            }
            if (!AbilityAIHooks.NotVetoed(pawn, a)) return "a hook vetoed it";
            return "no mod claims it and \"Also use abilities no mod claims\" is off";
        }

        // The first of CanActNow's or CanActNowPlayer's checks that failed, roughly in their order
        private static string CantActWhy(Pawn pawn, bool player)
        {
            if (!pawn.Spawned || pawn.Dead) return "not on the map";
            if (pawn.Downed) return "downed";
            if (!pawn.Awake()) return "asleep";
            if (pawn.IsBurning()) return "on fire";
            if (pawn.stances?.stunner?.Stunned == true) return "stunned";
            if (AIAbilitySources.IsCasting(pawn)) return "already casting";
            if (!player)
                return pawn.Drafted ? "drafted" : $"in a mental state ({pawn.MentalStateDef?.defName})";
            if (pawn.InMentalState) return $"in a mental state ({pawn.MentalStateDef.defName})";
            if (pawn.Drafted) return "moving, or on a drafted order other than holding or shooting";
            if (!AbilityAISettings.UndraftedAutocast) return "undrafted casting is off";
            if (pawn.GetPosture() != PawnPosture.Standing) return "lying down";
            if (pawn.carryTracker?.CarriedThing != null) return "carrying something";
            return $"busy with a job it shouldn't drop ({pawn.CurJob?.def.defName})";
        }

        private static Job Evaluate(Pawn pawn, Control control)
        {
            int tick = Find.TickManager.TicksGame;
            var state = states.GetValue(pawn, _ => new PawnState());
            bool player = control == Control.Player;
            // A walk into range just ended (arrived, timed out, or cut short): look again now rather than up to 30
            // ticks later, before vanilla's own positioning walks the pawn back out
            if (state.approachJobId >= 0 && pawn.CurJob?.loadID != state.approachJobId)
            {
                state.approachJobId = -1;
                state.approachAbility = null;
                state.approachExpected = 0;
                state.approachDef = null;
                state.approachGoal = LocalTargetInfo.Invalid;
                state.nextEvalTick = tick;
            }
            if (state.home.IsValid) CheckHome(pawn, state, player);
            // A Full walk is over and nothing has sent the pawn back yet: unless it casts now, it heads home
            bool returnDue = state.home.IsValid && state.approachJobId < 0 && state.returnJobId < 0;
            // Undrafted player pawns with nothing combat-related going on are checked at the slow interval; the
            // moment they are drafted, start fighting back or flee, the wait is cut short
            bool quick = !player || pawn.Drafted || UndraftedNeedsQuickCheck(pawn, tick);
            if (quick && state.slowEval)
            {
                state.slowEval = false;
                if (state.nextEvalTick > tick) state.nextEvalTick = tick;
            }
            if (tick < state.nextEvalTick) return null;
            if (tick < state.nextCastTick) return returnDue ? ReturnJob(pawn, state) : null;
            int interval = quick ? EvalIntervalTicks : Math.Max(EvalIntervalTicks, AbilityAISettings.UndraftedIntervalTicks);
            state.slowEval = interval > EvalIntervalTicks;
            state.nextEvalTick = tick + interval;
            whyNot = AbilityAISettings.DebugLog && Prefs.DevMode && tick >= state.nextWhyNotTick ? whyNotBuffer.Clear() : null;
            if (player ? !CanActNowPlayer(pawn, state) : !CanActNow(pawn))
            {
                if (whyNot != null) WhyNot(null, null, CantActWhy(pawn, player));
                return null;
            }
            bool drafted = pawn.Drafted;

            evaluating = true;
            ctx.Begin(pawn, tick, state.approachJobId >= 0 ? state.approachAbility : null, state.approachExpected, player && drafted);
            try
            {
                // Only now, past the interval checks: a pawn that isn't due pays no wrapper lookups
                var abilities = collected;
                AIAbilitySources.Collect(pawn, abilities);
                curSet = player && !drafted ? UndraftedSetNow(pawn) : PlayerSet.All;
                curPlayer = player;
                // Only extensions with an npc or friendlyNpc block care, but the hostility test is once per evaluation
                var role = player ? AbilityAIRole.Player
                    : pawn.HostileTo(Faction.OfPlayer) ? AbilityAIRole.Npc : AbilityAIRole.FriendlyNpc;

                // Ready abilities with a worker, highest priority first (insertion sort: a pawn holds a handful). An
                // ability with several extensions is a candidate once per extension.
                candidates.Clear();
                for (int i = 0; i < abilities.Count; i++)
                {
                    AIAbility a = abilities[i];
                    var exts = a.Def.modExtensions;
                    if (exts == null) continue;
                    bool checkedReady = false, ready = false;
                    for (int j = 0; j < exts.Count; j++)
                    {
                        if (!(exts[j] is AbilityAIExtension rule)) continue;
                        var ext = rule.For(role);
                        if (ext.Worker == null)
                        {
                            if (whyNot != null) WhyNot(a, ext, ext.enabled ? "its worker class is missing" : $"rule is off for {role}");
                            continue;
                        }
                        if (!InSet(ext.Worker, curSet))
                        {
                            WhyNot(a, ext, curSet == PlayerSet.Fleeing ? "not used while fleeing" : "not used undrafted outside a fight");
                            continue;
                        }
                        if (!checkedReady)
                        {
                            checkedReady = true;
                            ready = a.CanCastNow
                                && (state.abilityNext == null || !state.abilityNext.TryGetValue(a.Def, out int next) || tick >= next)
                                && (player ? PlayerAllowed(pawn, a) && PsycastWithinLimits(a, LocalTargetInfo.Invalid) : AbilityAIHooks.Allowed(pawn, a));
                        }
                        if (!ready)
                        {
                            if (whyNot != null) WhyNot(a, null, NotReadyWhy(pawn, a, state, tick, player));
                            break;
                        }
                        if (ext.reserveCharges > 0 && a.UsesCharges && a.RemainingCharges <= ext.reserveCharges)
                        {
                            WhyNot(a, ext, "keeping its last charges");
                            continue;
                        }
                        if (!CasterSuits(ext))
                        {
                            if (whyNot != null) WhyNot(a, ext, CasterSuitsWhy(ext));
                            continue;
                        }
                        int at = candidates.Count;
                        while (at > 0 && candidates[at - 1].ext.priority < ext.priority) at--;
                        candidates.Insert(at, new Candidate { ability = a, ext = ext });
                    }
                }
                if (candidates.Count == 0) return returnDue ? ReturnJob(pawn, state) : null;

                for (int i = 0; i < candidates.Count; i++)
                {
                    AIAbility a = candidates[i].ability;
                    var ext = candidates[i].ext;
                    var worker = ext.Worker;
                    if (!CombatAllows(worker))
                    {
                        WhyNot(a, ext, worker.Combat == AbilityAICombat.InCombat ? "not in combat" : "in combat");
                        continue;
                    }
                    exclusiveDef = worker.Exclusive ? a.Def : null;
                    if (!worker.TryFindTarget(ctx, a, out LocalTargetInfo target, out LocalTargetInfo dest))
                    {
                        WhyNot(a, ext, "no target");
                        continue;
                    }
                    if (!dest.IsValid) dest = target;
                    if (!Valid(a, target))
                    {
                        if (whyNot != null) WhyNot(a, ext, $"the ability refused {target}");
                        continue;
                    }
                    if (player && !PsycastWithinLimits(a, target))
                    {
                        if (whyNot != null) WhyNot(a, ext, $"over this pawn's psycast limits for {target}");
                        continue;
                    }
                    // Made before anything is recorded: another system's ability may still refuse at this point
                    // (VEF runs its extensions' own Valid and PreCast here)
                    Job cast = a.MakeCastJob(target, dest);
                    if (cast == null)
                    {
                        if (whyNot != null) WhyNot(a, ext, $"refused when its cast job was made, at {target}");
                        continue;
                    }

                    state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
                    if (ext.minGapSeconds > 0f)
                    {
                        if (state.abilityNext == null) state.abilityNext = new Dictionary<Def, int>();
                        state.abilityNext[a.Def] = tick + Mathf.RoundToInt(ext.minGapSeconds * GenTicks.TicksPerRealSecond);
                    }
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.Label} ({worker.GetType().Name}) on {target}");
                    exclusiveDef = null;
                    AbilityAIHooks.NotifyCast(pawn, a, target, dest);
                    if (player) QueueAfterCast(pawn, state);
                    return cast;
                }
                if (!player) return TryApproach(state, false);
                if (returnDue) return ReturnJob(pawn, state);
                // Full walks are drafted only, one at a time, and not while the last one is still on its way back
                if (!drafted || state.home.IsValid) return null;
                return TryApproach(state, true);
            }
            finally
            {
                ctx.End();
                candidates.Clear();
                claims.Clear();
                claimsBuilt = false;
                exclusiveDef = null;
                // The wrappers hold their pawns: don't keep them past the evaluation
                collected.Clear();
                curSet = PlayerSet.All;
                curPlayer = false;
                evaluating = false;
            }
        }

        // What a player's pawn does after an autocast. Vanilla starts constant-tree jobs without resuming the current
        // one, so an attack would be lost: queue a copy to resume. After a Full walk, the walk back goes first and the
        // attack it was on before the walk after that.
        private static void QueueAfterCast(Pawn pawn, PawnState state)
        {
            var queue = pawn.jobs.jobQueue;
            if (state.home.IsValid && state.returnJobId < 0)
            {
                Job back = MakeReturnJob(pawn, state);
                if (state.resume != null) queue.EnqueueFirst(state.resume);
                state.resume = null;
                if (back != null) queue.EnqueueFirst(back);
                else ClearHome(state);
                return;
            }
            Job cur = pawn.CurJob;
            if (cur != null && IsFightJob(cur.def) && cur.def.suspendable) queue.EnqueueFirst(Resume(cur));
        }

        // Clone copies loadID; two live jobs with one id break loading a save made while both exist
        private static Job Resume(Job job)
        {
            Job copy = job.Clone();
            copy.loadID = Find.UniqueIDsManager.GetNextJobID();
            return copy;
        }

        // A Full walk ended without a cast: the walk back now, with the attack from before the walk queued behind it.
        // Already there (the walk was cut short at once): straight back to that attack, if any.
        private static Job ReturnJob(Pawn pawn, PawnState state)
        {
            Job back = MakeReturnJob(pawn, state);
            Job resume = state.resume;
            state.resume = null;
            if (back == null)
            {
                ClearHome(state);
                return resume;
            }
            if (resume != null) pawn.jobs.jobQueue.EnqueueFirst(resume);
            if (AbilityAISettings.DebugLog && Prefs.DevMode)
                Log.Message($"[Ability AI] {pawn.LabelShort} walks back to {state.returnCell}");
            return back;
        }

        // The walk to the spot the pawn left, or with ReturnToCover a covered spot it can shoot from, found around
        // where it is now (falling back to the old spot when there is none). The cover spot is chosen once,
        // at the first walk back: a retry after the pawn was stopped heads for the same cell rather than searching
        // again from wherever it was stopped. Null when it is already there or can't get there. Not player-forced, so
        // a new player order replaces it (and CheckHome sees the forced job and forgets the walk).
        private static Job MakeReturnJob(Pawn pawn, PawnState state)
        {
            IntVec3 cell = state.home;
            if (state.returnCell.IsValid) cell = state.returnCell;
            else if (AbilityAISettings.ReturnToCover && TryFindCoverCell(pawn, out IntVec3 cover)) cell = cover;
            if (cell == pawn.Position || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return null;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.locomotionUrgency = LocomotionUrgency.Jog;
            state.returnJobId = job.loadID;
            state.returnCell = cell;
            return job;
        }

        // Vanilla's cast-position search with the pawn's own weapon against the hostile nearest the pawn, wanting
        // cover from it, within CoverSearchRadius of where the pawn stands now (where it cast, or where a walk out that
        // ended without a cast stopped). Its current cell is a candidate too, so a pawn that cast from good cover stays.
        // Only cells with some cover from that enemy count: vanilla's search otherwise settles for any cell it can
        // shoot from (usually the one the pawn is on), which would leave it standing in the open instead of falling
        // back to its old spot. Once per walk back.
        private static bool TryFindCoverCell(Pawn pawn, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            var targets = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            Thing enemy = null;
            float best = float.MaxValue;
            for (int i = 0; i < targets.Count; i++)
            {
                if (!(targets[i].Thing is Pawn p) || p.Downed || targets[i].ThreatDisabled(pawn)) continue;
                float d = (p.Position - pawn.Position).LengthHorizontalSquared;
                if (d < best) { best = d; enemy = p; }
            }
            if (enemy == null) return false;
            Verb verb = pawn.TryGetAttackVerb(enemy, false);
            if (verb == null || verb.IsMeleeAttack) return false;
            var req = new CastPositionRequest
            {
                caster = pawn,
                target = enemy,
                verb = verb,
                maxRangeFromTarget = verb.EffectiveRange,
                maxRangeFromCaster = CoverSearchRadius,
                validator = c => CoverUtility.CalculateOverallBlockChance(c, enemy.Position, pawn.Map) > 0f,
                wantCoverFromTarget = true,
            };
            return CastPositionFinder.TryFindCastPosition(req, out cell);
        }

        // Whether the Full walk this pawn is on (out, cast, back) still stands. Forgotten when the player takes over
        // (a forced job: their own order, or the attack the pawn resumes once home), when the pawn is undrafted
        // (which also clears its queue) or stops being the player's to command, and once the walk back is over. A walk
        // back cut short somewhere else (shot at: the drafted think tree stands the pawn still) is sent off again, up
        // to MaxReturnTries times.
        private static void CheckHome(Pawn pawn, PawnState state, bool player)
        {
            Job cur = pawn.CurJob;
            // VEF's cast job marks itself player-forced while it runs, so our own cast doesn't count as an order
            if (!player || !pawn.Spawned || !pawn.Drafted || (cur != null && cur.playerForced && !IsOwnCast(pawn, out _)))
            {
                ClearHome(state);
                return;
            }
            if (state.returnJobId < 0) return;
            if (cur != null && cur.loadID == state.returnJobId) return;
            var queue = pawn.jobs.jobQueue;
            for (int i = 0; i < queue.Count; i++)
                if (queue[i].job.loadID == state.returnJobId) return;
            if (pawn.Position == state.returnCell || state.returnTries >= MaxReturnTries) { ClearHome(state); return; }
            state.returnTries++;
            state.returnJobId = -1;
        }

        private static void ClearHome(PawnState state)
        {
            state.home = IntVec3.Invalid;
            state.resume = null;
            state.returnJobId = -1;
            state.returnCell = IntVec3.Invalid;
            state.returnTries = 0;
        }

        // requires (the caster's weapon) and maxLight (the light where it stands)
        private static bool CasterSuits(AbilityAIExtension ext)
        {
            if (ext.maxLight < 1f && ctx.map.glowGrid.GroundGlowAt(ctx.pawn.Position) > ext.maxLight) return false;
            switch (ext.requires)
            {
                case AbilityAIRequires.Melee: return !ctx.UsesRanged;
                case AbilityAIRequires.Ranged: return ctx.UsesRanged;
                default: return true;
            }
        }

        private static string CasterSuitsWhy(AbilityAIExtension ext)
        {
            if (ext.maxLight < 1f && ctx.map.glowGrid.GroundGlowAt(ctx.pawn.Position) > ext.maxLight) return "too bright where it stands";
            return ext.requires == AbilityAIRequires.Melee ? "needs a melee weapon" : "needs a ranged weapon";
        }

        private static bool CombatAllows(AbilityAIWorker worker)
        {
            // A fleeing pawn nobody has hit yet isn't in combat, but getting away is the point of these
            if (curSet == PlayerSet.Fleeing && worker.ForFleeing) return true;
            switch (worker.Combat)
            {
                case AbilityAICombat.InCombat: return ctx.InCombat;
                case AbilityAICombat.OutOfCombat: return !ctx.InCombat;
                default: return true;
            }
        }

        // Nothing could be cast from here: walk toward the first candidate (by priority) that has something just out
        // of reach. Not while already walking to cast, and only for a fighting ranged pawn in decent shape: melee
        // pawns close in anyway, and a pawn that is hurt, fleeing, held in melee or carrying someone has better
        // things to do. NPCs with the Approach setting on; the player's drafted pawns for abilities set to Full (the
        // caller has checked it isn't mid-move or already on a walk), recording where they stood so they go back
        // after the cast. Cost: one worker scan like the one above, then one CastPositionFinder search, which checks
        // line of sight from every cell within approachDistance + 3 of the caster and range of the target (about
        // 27x27 cells for Flashbang), per ability per pawn once per 10 s after a walk starts and once per 2 s while
        // no cell is found.
        private static Job TryApproach(PawnState state, bool player)
        {
            if (!player && !AbilityAISettings.Approach) return null;
            Pawn pawn = ctx.pawn;
            // Already walking to cast: let that walk finish (another ability's walk would replace it)
            if (state.approachJobId >= 0 && pawn.CurJob?.loadID == state.approachJobId) return null;
            if (!ctx.InCombat || !ctx.UsesRanged || pawn.InMentalState || pawn.carryTracker?.CarriedThing != null) return null;
            if (AbilityAIUtility.IsFleeing(ctx) || AbilityAIUtility.InMelee(ctx)) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                AIAbility a = candidates[i].ability;
                var ext = candidates[i].ext;
                if (ext.approachDistance <= 0f || ctx.HealthFraction < ext.healthThreshold || !CombatAllows(ext.Worker)) continue;
                if (player && AbilityAIAutocast.ModeFor(pawn, a.Def) != AbilityAIAutocastMode.Full) continue;
                if (state.approachNext != null && state.approachNext.TryGetValue(a.Def, out int next) && ctx.tick < next) continue;
                exclusiveDef = ext.Worker.Exclusive ? a.Def : null;
                IntVec3 cell;
                LocalTargetInfo claim = LocalTargetInfo.Invalid;
                int expected = 0;
                string at;
                bool byCell = ext.Worker.UsesApproachCell && AbilityAIUtility.IsSelfCast(a);
                if (byCell && ext.Worker.TryFindApproachCell(ctx, a, out cell, out expected))
                    at = $"{cell} (expects {expected})";
                else if (byCell)
                {
                    // Nothing worth walking to: that scan is m³, so don't repeat it every 30 ticks
                    if (state.approachNext == null) state.approachNext = new Dictionary<Def, int>();
                    state.approachNext[a.Def] = ctx.tick + ApproachFailRetryTicks;
                    continue;
                }
                else
                {
                    float reach = AbilityAIUtility.Range(a, ext) + ext.approachDistance;
                    if (!ext.Worker.TryFindApproachTarget(ctx, a, reach, out Thing goal) || goal == null) continue;
                    if (!TryFindApproachCell(a, ext, goal, out cell))
                    {
                        // The cast-position search is the costliest step here: not again for 2 s
                        if (state.approachNext == null) state.approachNext = new Dictionary<Def, int>();
                        state.approachNext[a.Def] = ctx.tick + ApproachFailRetryTicks;
                        continue;
                    }
                    expected = 0;
                    at = goal.LabelShort;
                    claim = goal;
                }

                if (state.approachNext == null) state.approachNext = new Dictionary<Def, int>();
                state.approachNext[a.Def] = ctx.tick + ApproachRetryTicks;
                Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                job.expiryInterval = ApproachExpiryTicks;
                job.checkOverrideOnExpire = false;
                job.locomotionUrgency = LocomotionUrgency.Jog;
                state.approachJobId = job.loadID;
                state.approachAbility = expected > 0 ? a.Def : null;
                state.approachExpected = expected;
                state.approachDef = claim.IsValid ? a.Def : null;
                state.approachGoal = claim;
                exclusiveDef = null;
                if (player)
                {
                    ClearHome(state);
                    state.home = pawn.Position;
                    // Holding fire on a target the player picked: back to it once home
                    Job cur = pawn.CurJob;
                    if (cur != null && cur.def == JobDefOf.AttackStatic && cur.def.suspendable) state.resume = Resume(cur);
                }
                if (AbilityAISettings.DebugLog && Prefs.DevMode)
                    Log.Message($"[Ability AI] {pawn.LabelShort} moves to {cell} to cast {a.Label} ({ext.Worker.GetType().Name}) at {at}");
                return job;
            }
            return null;
        }

        // A self-cast ability walks toward the goal itself (Battle Cry: toward the allies); a targeted one to a
        // vanilla cast position for its verb, preferring cover from the target as the fight AI does
        private static bool TryFindApproachCell(AIAbility a, AbilityAIExtension ext, Thing goal, out IntVec3 cell)
        {
            Pawn pawn = ctx.pawn;
            cell = IntVec3.Invalid;
            if (AbilityAIUtility.IsSelfCast(a))
            {
                cell = goal.Position;
                return cell != pawn.Position && pawn.CanReach(cell, PathEndMode.OnCell, Danger.Some);
            }
            var req = new CastPositionRequest
            {
                caster = pawn,
                target = goal,
                verb = a.Verb,
                maxRangeFromTarget = Mathf.Max(1.5f, AbilityAIUtility.Range(a, ext) - 1f),
                maxRangeFromCaster = ext.approachDistance + 3f,
                wantCoverFromTarget = true,
            };
            return CastPositionFinder.TryFindCastPosition(req, out cell) && cell != pawn.Position;
        }
    }

    /// <summary>Which of an ally's mental breaks a player pawn's calming ability (Calm worker) will end. Aggressive
    /// breaks always count; NPC casters only ever end those (or any, where the extension says anyMentalState).</summary>
    public enum AbilityAICalmBreaks : byte
    {
        Aggressive,
        MajorAndExtreme,
        Any,
    }

    /// <summary>Whether hostile workers may target an ally in a hostile mental break (AbilityAIContext.IsBrokenAlly).
    /// Every caster, NPC or player.</summary>
    public enum AbilityAIBrokenAllies : byte
    {
        Never,
        /// <summary>Only with an extension marked harmless.</summary>
        HarmlessOnly,
        Any,
    }

    /// <summary>The AI's settings. Static so the hot paths read a field, not an instance; saved through
    /// AbilityAIModSettings and drawn by AbilityAIMod (AbilityAIMod.cs).</summary>
    public static class AbilityAISettings
    {
        public static bool Enabled = true;
        public static bool VanillaStandDown = true;
        public static bool Hostiles = true;
        public static bool NonHostiles = true;
        public static bool Animals = true;
        public static bool Mechs = true;
        public static bool Unclaimed = true;
        public static bool Approach = true;
        public static bool PlayerAutocast = true;
        public static bool BreakCasting;
        public static bool UndraftedAutocast = true;
        public static bool ShowMarker = true;
        public static bool ReturnToCover;
        /// <summary>Better Autocasting for VPE decides the colonists' psycasts both mods have rules for (default: Ability
        /// AI does).</summary>
        public static bool BetterAutocastOwns;
        /// <summary>Better Autocasting is loaded and the VPE compat's bridge to it is up: the owner choice is shown.</summary>
        internal static bool BetterAutocastActive;
        public const AbilityAICalmBreaks DefaultCalmBreaks = AbilityAICalmBreaks.MajorAndExtreme;
        public static AbilityAICalmBreaks CalmBreaks = DefaultCalmBreaks;
        public const float DefaultCalmRange = 20f;
        public static float CalmRange = DefaultCalmRange;
        public const AbilityAIBrokenAllies DefaultBrokenAllies = AbilityAIBrokenAllies.HarmlessOnly;
        public static AbilityAIBrokenAllies BrokenAllies = DefaultBrokenAllies;
        public const int DefaultUndraftedIntervalTicks = 250;
        public static int UndraftedIntervalTicks = DefaultUndraftedIntervalTicks;
        public const float DefaultMinGapSeconds = 2.5f;
        public static float MinGapSeconds = DefaultMinGapSeconds;
        public static bool DebugLog;

        /// <summary>The settings page's tabs, in the order they are drawn.</summary>
        public enum Tab : byte
        {
            General,
            OtherFactions,
            YourPawns,
            MentalBreaks,
        }

        public static void Reset()
        {
            ResetTab(Tab.General);
            ResetTab(Tab.OtherFactions);
            ResetTab(Tab.YourPawns);
            ResetTab(Tab.MentalBreaks);
        }

        public static void ResetTab(Tab tab)
        {
            switch (tab)
            {
                case Tab.General:
                    Enabled = VanillaStandDown = true;
                    MinGapSeconds = DefaultMinGapSeconds;
                    DebugLog = false;
                    break;
                case Tab.OtherFactions:
                    Hostiles = NonHostiles = Animals = Mechs = Approach = Unclaimed = true;
                    break;
                case Tab.YourPawns:
                    PlayerAutocast = UndraftedAutocast = ShowMarker = true;
                    ReturnToCover = false;
                    UndraftedIntervalTicks = DefaultUndraftedIntervalTicks;
                    AbilityAIBlockedJobs.Reset();
                    if (BetterAutocastOwns)
                    {
                        BetterAutocastOwns = false;
                        AbilityAIAutocast.NotifyOwnerChanged();
                    }
                    break;
                case Tab.MentalBreaks:
                    CalmBreaks = DefaultCalmBreaks;
                    CalmRange = DefaultCalmRange;
                    BrokenAllies = DefaultBrokenAllies;
                    BreakCasting = false;
                    break;
            }
        }

        public static void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "abilityAI_enabled", true);
            Scribe_Values.Look(ref Hostiles, "abilityAI_hostiles", true);
            Scribe_Values.Look(ref NonHostiles, "abilityAI_nonHostiles", true);
            Scribe_Values.Look(ref Animals, "abilityAI_animals", true);
            Scribe_Values.Look(ref Mechs, "abilityAI_mechs", true);
            Scribe_Values.Look(ref Unclaimed, "abilityAI_unclaimed", true);
            Scribe_Values.Look(ref VanillaStandDown, "abilityAI_vanillaStandDown", true);
            Scribe_Values.Look(ref Approach, "abilityAI_approach", true);
            Scribe_Values.Look(ref PlayerAutocast, "abilityAI_playerAutocast", true);
            Scribe_Values.Look(ref UndraftedAutocast, "abilityAI_undraftedAutocast", true);
            Scribe_Values.Look(ref UndraftedIntervalTicks, "abilityAI_undraftedIntervalTicks", DefaultUndraftedIntervalTicks);
            UndraftedIntervalTicks = Mathf.Clamp(UndraftedIntervalTicks, 30, 1200);
            AbilityAIBlockedJobs.ExposeData();
            Scribe_Values.Look(ref ShowMarker, "abilityAI_showMarker", true);
            Scribe_Values.Look(ref ReturnToCover, "abilityAI_returnToCover", false);
            Scribe_Values.Look(ref BetterAutocastOwns, "abilityAI_betterAutocastOwns", false);
            Scribe_Values.Look(ref CalmBreaks, "abilityAI_calmBreaks", DefaultCalmBreaks);
            Scribe_Values.Look(ref CalmRange, "abilityAI_calmRange", DefaultCalmRange);
            CalmRange = Mathf.Clamp(CalmRange, 5f, 60f);
            Scribe_Values.Look(ref BreakCasting, "abilityAI_breakCasting", false);
            Scribe_Values.Look(ref BrokenAllies, "abilityAI_brokenAllies", DefaultBrokenAllies);
            Scribe_Values.Look(ref MinGapSeconds, "abilityAI_minGapSeconds", DefaultMinGapSeconds);
            MinGapSeconds = Mathf.Clamp(MinGapSeconds, 0f, 30f);
            Scribe_Values.Look(ref DebugLog, "abilityAI_debugLog", false);
        }

        public static void DoTab(Listing_Standard list, Tab tab)
        {
            switch (tab)
            {
                case Tab.General: DoGeneral(list); break;
                case Tab.OtherFactions: DoOtherFactions(list); break;
                case Tab.YourPawns: DoYourPawns(list); break;
                case Tab.MentalBreaks: DoMentalBreaks(list); break;
            }
        }

        private const float IndentWidth = 24f;

        // Listing.Indent moves the left edge only; without narrowing the column too, indented rows (and their
        // checkboxes, drawn at the right edge) would run past the scroll view
        private static void In(Listing_Standard list)
        {
            list.Indent(IndentWidth);
            list.ColumnWidth -= IndentWidth;
        }

        private static void Out(Listing_Standard list)
        {
            list.Outdent(IndentWidth);
            list.ColumnWidth += IndentWidth;
        }

        private static void Section(Listing_Standard list, string key, string tipKey = null)
        {
            list.Gap(10f);
            string label = key.Translate().Colorize(ColoredText.TipSectionTitleColor);
            if (tipKey == null) list.Label(label);
            else list.Label(label, -1f, new TipSignal(tipKey.Translate()));
        }

        private static void Hint(Listing_Standard list, string key)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = ColoredText.SubtleGrayColor;
            list.Label(key.Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void DoGeneral(Listing_Standard list)
        {
            list.CheckboxLabeled("AAI_Enabled".Translate(), ref Enabled, "AAI_Enabled_Tip".Translate());
            Hint(list, "AAI_Enabled_Desc");
            list.CheckboxLabeled("AAI_VanillaStandDown".Translate(), ref VanillaStandDown, "AAI_VanillaStandDown_Tip".Translate());
            list.Gap();
            list.Label("AAI_MinGap".Translate(MinGapSeconds.ToString("0.#")));
            MinGapSeconds = Mathf.Round(list.Slider(MinGapSeconds, 0f, 30f) * 2f) / 2f;
            Hint(list, "AAI_MinGap_Desc");
            if (Prefs.DevMode)
            {
                Section(list, "AAI_Section_Dev");
                list.CheckboxLabeled("AAI_DebugLog".Translate(), ref DebugLog, "AAI_DebugLog_Tip".Translate());
            }
        }

        private static void DoOtherFactions(Listing_Standard list)
        {
            Section(list, "AAI_Who");
            In(list);
            list.CheckboxLabeled("AAI_Hostiles".Translate(), ref Hostiles, "AAI_Hostiles_Tip".Translate());
            list.CheckboxLabeled("AAI_NonHostiles".Translate(), ref NonHostiles, "AAI_NonHostiles_Tip".Translate());
            list.CheckboxLabeled("AAI_Animals".Translate(), ref Animals, "AAI_Animals_Tip".Translate());
            list.CheckboxLabeled("AAI_Mechs".Translate(), ref Mechs, "AAI_Mechs_Tip".Translate());
            Out(list);
            Hint(list, "AAI_Who_Desc");
            Section(list, "AAI_What");
            list.CheckboxLabeled("AAI_Unclaimed".Translate(), ref Unclaimed, "AAI_Unclaimed_Tip".Translate());
            Hint(list, "AAI_Unclaimed_Desc");
            list.CheckboxLabeled("AAI_Approach".Translate(), ref Approach, "AAI_Approach_Tip".Translate());
        }

        private static void DoYourPawns(Listing_Standard list)
        {
            list.CheckboxLabeled("AAI_PlayerAutocast".Translate(), ref PlayerAutocast, "AAI_PlayerAutocast_Tip".Translate());
            list.CheckboxLabeled("AAI_ShowMarker".Translate(), ref ShowMarker, "AAI_ShowMarker_Tip".Translate());
            Section(list, "AAI_Section_Undrafted");
            list.CheckboxLabeled("AAI_UndraftedAutocast".Translate(), ref UndraftedAutocast, "AAI_UndraftedAutocast_Tip".Translate());
            if (UndraftedAutocast)
            {
                In(list);
                list.Label("AAI_UndraftedInterval".Translate(UndraftedIntervalTicks, (UndraftedIntervalTicks / 60f).ToString("0.#")),
                    -1f, "AAI_UndraftedInterval_Tip".Translate());
                UndraftedIntervalTicks = Mathf.RoundToInt(list.Slider(UndraftedIntervalTicks, 30f, 1200f) / 10f) * 10;
                Rect jobs = list.GetRect(30f);
                jobs.width = Mathf.Min(jobs.width, 320f);
                if (Widgets.ButtonText(jobs, "AAI_BlockedJobs".Translate(AbilityAIBlockedJobs.Count)))
                    Find.WindowStack.Add(new Dialog_BlockedJobs());
                TooltipHandler.TipRegion(jobs, "AAI_BlockedJobs_Tip".Translate());
                Out(list);
            }
            Section(list, "AAI_ReturnTo", "AAI_ReturnTo_Tip");
            In(list);
            if (list.RadioButton("AAI_ReturnTo_Original".Translate(), !ReturnToCover, 0f, "AAI_ReturnTo_Original_Tip".Translate()))
                ReturnToCover = false;
            if (list.RadioButton("AAI_ReturnTo_Cover".Translate(), ReturnToCover, 0f, "AAI_ReturnTo_Cover_Tip".Translate()))
                ReturnToCover = true;
            Out(list);
            if (BetterAutocastActive)
            {
                bool before = BetterAutocastOwns;
                Section(list, "AAI_AutocastOwner", "AAI_AutocastOwner_Tip");
                In(list);
                if (list.RadioButton("AAI_AutocastOwner_AbilityAI".Translate(), !BetterAutocastOwns, 0f,
                    "AAI_AutocastOwner_AbilityAI_Tip".Translate()))
                    BetterAutocastOwns = false;
                if (list.RadioButton("AAI_AutocastOwner_BetterAutocast".Translate(), BetterAutocastOwns, 0f,
                    "AAI_AutocastOwner_BetterAutocast_Tip".Translate()))
                    BetterAutocastOwns = true;
                Out(list);
                if (before != BetterAutocastOwns) AbilityAIAutocast.NotifyOwnerChanged();
            }
        }

        private static void DoMentalBreaks(Listing_Standard list)
        {
            Section(list, "AAI_CalmBreaks", "AAI_CalmBreaks_Tip");
            In(list);
            if (list.RadioButton("AAI_CalmBreaks_Aggressive".Translate(), CalmBreaks == AbilityAICalmBreaks.Aggressive, 0f,
                "AAI_CalmBreaks_Aggressive_Tip".Translate()))
                CalmBreaks = AbilityAICalmBreaks.Aggressive;
            if (list.RadioButton("AAI_CalmBreaks_Major".Translate(), CalmBreaks == AbilityAICalmBreaks.MajorAndExtreme, 0f,
                "AAI_CalmBreaks_Major_Tip".Translate()))
                CalmBreaks = AbilityAICalmBreaks.MajorAndExtreme;
            if (list.RadioButton("AAI_CalmBreaks_Any".Translate(), CalmBreaks == AbilityAICalmBreaks.Any, 0f,
                "AAI_CalmBreaks_Any_Tip".Translate()))
                CalmBreaks = AbilityAICalmBreaks.Any;
            list.Gap(6f);
            list.Label("AAI_CalmRange".Translate(CalmRange.ToString("0")), -1f, "AAI_CalmRange_Tip".Translate());
            CalmRange = Mathf.Round(list.Slider(CalmRange, 5f, 60f));
            Out(list);
            Section(list, "AAI_BrokenAllies", "AAI_BrokenAllies_Tip");
            In(list);
            if (list.RadioButton("AAI_BrokenAllies_Never".Translate(), BrokenAllies == AbilityAIBrokenAllies.Never, 0f,
                "AAI_BrokenAllies_Never_Tip".Translate()))
                BrokenAllies = AbilityAIBrokenAllies.Never;
            if (list.RadioButton("AAI_BrokenAllies_Harmless".Translate(), BrokenAllies == AbilityAIBrokenAllies.HarmlessOnly, 0f,
                "AAI_BrokenAllies_Harmless_Tip".Translate()))
                BrokenAllies = AbilityAIBrokenAllies.HarmlessOnly;
            if (list.RadioButton("AAI_BrokenAllies_Any".Translate(), BrokenAllies == AbilityAIBrokenAllies.Any, 0f,
                "AAI_BrokenAllies_Any_Tip".Translate()))
                BrokenAllies = AbilityAIBrokenAllies.Any;
            Out(list);
            Section(list, "AAI_Section_Breaks");
            list.CheckboxLabeled("AAI_BreakCasting".Translate(), ref BreakCasting, "AAI_BreakCasting_Tip".Translate());
        }
    }

    /// <summary>Runs the ability AI whenever the constant think tree has nothing to do, as Bastle Framework's
    /// BastleAI does. Pawns with no abilities return after a few field reads per ability source; pawns whose
    /// abilities have no AI extension after one def lookup per ability.</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextConstantThinkTreeJob")]
    internal static class Patch_AbilityAI_ConstantThinkTree
    {
        // Vanilla's own guard before pooling a think-tree job it won't start: never the running or a queued job
        private static void Pool(Pawn pawn, Job job)
        {
            if (job != null && job != pawn.CurJob && !pawn.jobs.jobQueue.Contains(job)) JobMaker.ReturnToPool(job);
        }

        private static bool KeepCast(Pawn pawn, Job cast, int warmupTicksLeft, Job hostility)
        {
            if (hostility.def != JobDefOf.FleeAndCower && hostility.def != JobDefOf.Flee) return true;
            if (warmupTicksLeft >= 0) return true;
            return cast.targetA.Thing is Pawn t && t.HostileTo(pawn);
        }

        static void Postfix(Pawn ___pawn, ref ThinkResult __result)
        {
            if (!AbilityAISettings.Enabled) return;
            // An undrafted colonist's hostility response (Attack or Flee) answers every constant-tree check while a
            // threat is near, so without this the AI would never get a look in for a colonist set to anything but
            // Ignore: it would attack or run from a berserk friend instead of calming it. The player's pawns only;
            // everyone else's constant tree keeps priority.
            bool overHostility = false;
            if (__result.IsValid)
            {
                if (!(__result.SourceNode is JobGiver_ConfigurableHostilityResponse)) return;
                overHostility = true;
            }
            if (___pawn == null || !AIAbilitySources.HasAny(___pawn)) return;
            // The player's pawns with nothing set to autocast stop at one dictionary lookup here
            var control = AbilityAIDriver.ControlOf(___pawn);
            if (control == AbilityAIDriver.Control.None || !AIAbilitySources.AnyWithExtension(___pawn)) return;
            if (overHostility && control != AbilityAIDriver.Control.Player) return;
            // Vanilla starts these jobs against the constant tree and saves the job giver's key in it; with no
            // constant tree a saved job would throw on load.
            var root = ___pawn.thinker?.ConstantThinkTree?.thinkRoot;
            if (root == null) return;
            if (overHostility)
            {
                // A cast this AI started (its job giver is the tree's root, which vanilla never issues from) is
                // under way: the hostility response would replace it on its next check, mid-walk or mid-warmup.
                // Attacking waits for it. Fleeing waits only once the warmup has begun, or when the cast is aimed at
                // the threat itself (a berserk friend being calmed): a long walk to calm someone else across the map
                // shouldn't carry a pawn set to flee into a raid.
                if (AbilityAIDriver.IsOwnCast(___pawn, out int warmupLeft))
                {
                    if (KeepCast(___pawn, ___pawn.CurJob, warmupLeft, __result.Job))
                    {
                        Pool(___pawn, __result.Job);
                        __result = ThinkResult.NoJob;
                    }
                    // Otherwise the flee wins; the driver has nothing to add mid-cast
                    return;
                }
            }
            try
            {
                Job job = AbilityAIDriver.TryGiveJob(___pawn, control);
                // The tree's root as the source: the AI has no think node of its own, so a pawn saved mid-cast would
                // otherwise log a missing think node on load
                if (job != null)
                {
                    // Vanilla pools the think-tree jobs it doesn't start; the hostility job set aside here is one
                    if (overHostility) Pool(___pawn, __result.Job);
                    __result = new ThinkResult(job, root);
                }
            }
            catch (Exception e)
            {
                // Vanilla's constant-tree call has its own try/catch; this runs after it
                Log.ErrorOnce($"[Ability AI] threw for {___pawn.def.defName}: {e}", ___pawn.def.shortHash ^ 0x7A12);
            }

        }
    }
}
