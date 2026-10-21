using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        /// ability is the host's to cast for this pawn; Veto = never now (an unbound item, say).</summary>
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
        /// <summary>Calm: any mental state, not only aggressive ones.</summary>
        public bool anyMentalState;
        /// <summary>Calm: skip a break that will end by itself within this many seconds.</summary>
        public float minBreakSeconds;
        /// <summary>Interrupt: also any aim with at least this many seconds left (0 = only abilities and explosives).</summary>
        public float minAimSeconds;

        [Unsaved] private AbilityAIWorker worker;

        // One instance per extension. Psycast copies share their source's extension list by reference, so one worker
        // serves every copy: workers must keep no per-pawn state.
        public AbilityAIWorker Worker
        {
            get
            {
                if (worker == null && workerClass != null && typeof(AbilityAIWorker).IsAssignableFrom(workerClass) && !workerClass.IsAbstract)
                {
                    worker = (AbilityAIWorker)Activator.CreateInstance(workerClass);
                    worker.ext = this;
                }
                return worker;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var e in base.ConfigErrors()) yield return e;
            if (workerClass == null) yield return "AbilityAIExtension needs a workerClass";
            else if (!typeof(AbilityAIWorker).IsAssignableFrom(workerClass)) yield return $"workerClass {workerClass} is not an AbilityAIWorker";
            else if (workerClass.IsAbstract) yield return $"workerClass {workerClass} is abstract";
            if (reserveCharges < 0) yield return "reserveCharges can't be negative";
            if (minTargets < 1) yield return "minTargets must be at least 1";
            if (approachDistance < 0f) yield return "approachDistance can't be negative";
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
        private bool enemiesBuilt, alliesBuilt, aimsBuilt, friendliesBuilt;
        private readonly List<Pawn> friendlies = new List<Pawn>();
        private readonly List<Pawn> enemies = new List<Pawn>();
        private readonly List<Pawn> allies = new List<Pawn>();
        private readonly List<Pawn> downedAllies = new List<Pawn>();
        private readonly Dictionary<Pawn, int> aimedAt = new Dictionary<Pawn, int>();
        private Thing enemyTarget;
        private bool enemyTargetKnown;
        private AbilityDef approachAbility;
        private int approachExpected;
        private bool drafted;

        /// <summary>While the pawn walks to a cell a worker chose with TryFindApproachCell for this ability: how
        /// many targets the worker expected to reach from there. 0 otherwise.</summary>
        public int ApproachExpected(Ability ability) => ability.def == approachAbility ? approachExpected : 0;

        internal void Begin(Pawn p, int t, AbilityDef walkingFor, int expected, bool drafted)
        {
            pawn = p;
            this.drafted = drafted;
            map = p.Map;
            tick = t;
            approachAbility = walkingFor;
            approachExpected = expected;
            combatKnown = enemiesBuilt = alliesBuilt = aimsBuilt = friendliesBuilt = enemyTargetKnown = false;
        }

        internal void End()
        {
            enemies.Clear();
            allies.Clear();
            downedAllies.Clear();
            aimedAt.Clear();
            friendlies.Clear();
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
                        // The cache hands back one shared list that its next caller clears, so copy out of it at once
                        var targets = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                        for (int i = 0; i < targets.Count; i++)
                            if (targets[i].Thing is Pawn p && IsEnemy(p)) enemies.Add(p);
                    }
                }
                return enemies;
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
        /// every non-hostile pawn with a faction. Built only when a worker asks (avoidAllies, meleeOnly): one pass
        /// over the map's spawned pawns.</summary>
        public List<Pawn> Friendlies
        {
            get
            {
                if (!friendliesBuilt)
                {
                    friendliesBuilt = true;
                    friendlies.AddRange(Allies);
                    var all = map.mapPawns.AllPawnsSpawned;
                    for (int i = 0; i < all.Count; i++)
                    {
                        Pawn p = all[i];
                        if (p.Faction == null || p.Faction == pawn.Faction || p.Dead || p.Downed) continue;
                        if (!p.HostileTo(pawn)) friendlies.Add(p);
                    }
                }
                return friendlies;
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
            public int nextCastTick;
            public Dictionary<AbilityDef, int> abilityNext;
            public Dictionary<AbilityDef, int> approachNext;
            public int approachJobId = -1;
            // Set by a TryFindApproachCell walk, cleared with approachJobId
            public AbilityDef approachAbility;
            public int approachExpected;

            // Player Full mode: where the pawn stood before it walked to cast, the AttackStatic it was on (a fresh
            // copy to queue once back), and the walk back. Kept from the start of the walk until the pawn is back
            // or the player takes over (a new order, undrafting). Not saved: a pawn loaded mid-walk stays wherever
            // it is, as any drafted pawn would.
            public IntVec3 home = IntVec3.Invalid;
            public Job resume;
            public int returnJobId = -1;
            public IntVec3 returnCell = IntVec3.Invalid;
            public int returnTries;
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
        // How far from its old spot a pawn returning to cover may stand
        private const float CoverSearchRadius = 6f;

        private struct Candidate
        {
            public Ability ability;
            public AbilityAIExtension ext;
        }

        // Not saved: after a load every pawn may cast again at once, limited by the ability's own cooldown and charges
        private static readonly ConditionalWeakTable<Pawn, PawnState> states = new ConditionalWeakTable<Pawn, PawnState>();
        private static readonly AbilityAIContext ctx = new AbilityAIContext();
        private static readonly List<Candidate> candidates = new List<Candidate>();
        private static bool evaluating;
        // The set of workers the pawn being evaluated may use (All for NPCs)
        private static PlayerSet curSet = PlayerSet.All;

        /// <summary>Whether any of these abilities has an AI extension. The first check that touches defs.</summary>
        internal static bool AnyWithExtension(List<Ability> abilities)
        {
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i].def.GetModExtension<AbilityAIExtension>() != null) return true;
            return false;
        }

        /// <summary>A psycast the pawn could pay for. Checked up front because Verb_CastPsycast.ValidateTarget posts
        /// its "not enough psyfocus" and similar messages whatever showMessages says, and Psycast.CanCast only checks
        /// psyfocus for psycasts without neural heat.</summary>
        private static bool PsycastAffordable(Ability a)
        {
            if (!(a is Psycast)) return true;
            var pe = a.pawn.psychicEntropy;
            if (pe == null || pe.PsychicSensitivity < float.Epsilon) return false;
            return a.def.PsyfocusCost <= pe.CurrentPsyfocus + Pawn_PsychicEntropyTracker.PsyfocusCostTolerance;
        }

        /// <summary>The player's per-pawn psycast limits (the gizmo beside vanilla's psyfocus one): the cast must not
        /// push neural heat over the cap or psyfocus under the floor. Real Psycasts only; a pawn with no limits set
        /// stops at one dictionary lookup. Without a target the cheapest cost the psycast can have is used (asked
        /// while choosing candidates); with one, the cost for that target (Word of Serenity costs more for a worse
        /// break).</summary>
        private static bool PsycastWithinLimits(Ability a, LocalTargetInfo target)
        {
            if (!(a is Psycast ps)) return true;
            if (!AbilityAIAutocast.TryGetLimits(a.pawn, out float heatCap, out float focusFloor)) return true;
            var pe = a.pawn.psychicEntropy;
            if (pe == null) return true;
            if (heatCap < 1f)
            {
                float gain = a.def.EntropyGain;
                if (gain > float.Epsilon)
                {
                    // As Pawn_PsychicEntropyTracker.TryAddEntropy scales it; both stats cached for 250 ticks
                    gain *= a.pawn.GetStatValue(StatDefOf.PsychicEntropyGain, true, 250);
                    float max = a.pawn.GetStatValue(StatDefOf.PsychicEntropyMax, true, 250);
                    if (pe.EntropyValue + gain > heatCap * max) return false;
                }
            }
            if (focusFloor > 0f)
            {
                float cost = target.IsValid ? ps.FinalPsyfocusCost(target) : a.def.PsyfocusCostRange.min;
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
            if (pawn.CurJob?.ability != null) return false;
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
            if (pawn.InMentalState || pawn.CurJob?.ability != null) return false;
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
                // A constant-tree job ends the current one: never surgery, a bill, or anything the player ordered
                return !cur.playerForced && cur.def.casualInterruptible;
            }
            // Our own walk into range (Full mode) is a move, but casting from it is what it is for
            if (state != null && state.approachJobId >= 0 && cur != null && cur.loadID == state.approachJobId) return true;
            if (pawn.pather != null && pawn.pather.Moving) return false;
            JobDef job = cur?.def;
            return job == null || job == JobDefOf.Wait_Combat || job == JobDefOf.Wait || job == JobDefOf.Wait_MaintainPosture
                || job == JobDefOf.AttackStatic;
        }

        private static bool IsFightJob(JobDef def) => def == JobDefOf.AttackStatic || def == JobDefOf.AttackMelee;

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
        private static bool PlayerAllowed(Pawn pawn, Ability a)
        {
            return AbilityAIAutocast.ModeFor(pawn, a.def) != AbilityAIAutocastMode.Off && AbilityAIHooks.NotVetoed(pawn, a);
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
            if (!AbilityAISettings.Enabled || pawn == null || ability?.def == null) return false;
            if (!HasWorker(ability.def, PlayerSet.All)) return false;
            switch (ControlOf(pawn))
            {
                case Control.NPC: return AbilityAIHooks.Allowed(pawn, ability);
                case Control.Player:
                    if (!PlayerAllowed(pawn, ability)) return false;
                    return pawn.Drafted || (AbilityAISettings.UndraftedAutocast && HasWorker(ability.def, UndraftedReach(pawn)));
                default: return false;
            }
        }

        /// <summary>Whether every other AI (vanilla's through Ability.AICanTargetNow, or a host's own fallback) should
        /// leave this pawn and ability alone: whenever Handles is true, and for the player's own pawns (not prisoners)
        /// whenever the ability has a working extension at all, whatever its autocast mode or the hooks' votes. The
        /// player's pawns then only cast an ability this mod has rules for when the player set it to autocast; turning
        /// off Enabled or PlayerAutocast hands them back to vanilla. A pawn in a mental break is out of the player's
        /// hands, so for it only Handles counts: vanilla keeps the break unless BreakCasting has this mod take it
        /// over. Our driver never asks this, so it never stands itself down.</summary>
        public static bool OthersStandDown(Pawn pawn, Ability ability)
        {
            if (!AbilityAISettings.Enabled || pawn == null || ability?.def == null) return false;
            if (AbilityAISettings.PlayerAutocast && pawn.Faction != null && pawn.Faction.IsPlayer && !pawn.IsPrisoner
                && !pawn.InMentalState)
                return HasWorker(ability.def, PlayerSet.All);
            return Handles(pawn, ability);
        }

        /// <summary>Any of its extensions has a working worker (TryGiveJob tries them all) in the given set.</summary>
        internal static bool HasWorker(AbilityDef def, PlayerSet set)
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
        public static bool Valid(Ability ability, LocalTargetInfo target)
        {
            if (!ValidIgnoringRange(ability, target)) return false;
            return ability.verb.ValidateTarget(target, false);
        }

        /// <summary>Valid, short of the verb's own check (range, line of sight, reachability; it repeats the comps'
        /// Valid). For targets the pawn means to walk into range of.</summary>
        public static bool ValidIgnoringRange(Ability ability, LocalTargetInfo target)
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
                for (int i = 0; i < comps.Count; i++)
                    if (!comps[i].AICanTargetNow(target)) return false;
            if (ability is Psycast ps && !PsycastValidOn(ps, target)) return false;
            return true;
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

        internal static Job TryGiveJob(Pawn pawn, List<Ability> abilities, Control control)
        {
            if (evaluating) return null;
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
                state.nextEvalTick = tick;
            }
            if (state.home.IsValid) CheckHome(pawn, state, player);
            // A Full walk is over and nothing has sent the pawn back yet: unless it casts now, it heads home
            bool returnDue = state.home.IsValid && state.approachJobId < 0 && state.returnJobId < 0;
            if (tick < state.nextEvalTick) return null;
            if (tick < state.nextCastTick) return returnDue ? ReturnJob(pawn, state) : null;
            state.nextEvalTick = tick + EvalIntervalTicks;
            if (player ? !CanActNowPlayer(pawn, state) : !CanActNow(pawn)) return null;
            bool drafted = pawn.Drafted;

            evaluating = true;
            ctx.Begin(pawn, tick, state.approachJobId >= 0 ? state.approachAbility : null, state.approachExpected, player && drafted);
            try
            {
                curSet = player && !drafted ? UndraftedSetNow(pawn) : PlayerSet.All;

                // Ready abilities with a worker, highest priority first (insertion sort: a pawn holds a handful). An
                // ability with several extensions is a candidate once per extension.
                candidates.Clear();
                for (int i = 0; i < abilities.Count; i++)
                {
                    Ability a = abilities[i];
                    var exts = a.def.modExtensions;
                    if (exts == null || a.verb == null) continue;
                    bool checkedReady = false, ready = false;
                    for (int j = 0; j < exts.Count; j++)
                    {
                        if (!(exts[j] is AbilityAIExtension ext) || ext.Worker == null) continue;
                        if (!InSet(ext.Worker, curSet)) continue;
                        if (!checkedReady)
                        {
                            checkedReady = true;
                            ready = a.CanCast && PsycastAffordable(a)
                                && (state.abilityNext == null || !state.abilityNext.TryGetValue(a.def, out int next) || tick >= next)
                                && (player ? PlayerAllowed(pawn, a) && PsycastWithinLimits(a, LocalTargetInfo.Invalid) : AbilityAIHooks.Allowed(pawn, a));
                        }
                        if (!ready) break;
                        if (ext.reserveCharges > 0 && a.UsesCharges && a.RemainingCharges <= ext.reserveCharges) continue;
                        int at = candidates.Count;
                        while (at > 0 && candidates[at - 1].ext.priority < ext.priority) at--;
                        candidates.Insert(at, new Candidate { ability = a, ext = ext });
                    }
                }
                if (candidates.Count == 0) return returnDue ? ReturnJob(pawn, state) : null;

                for (int i = 0; i < candidates.Count; i++)
                {
                    Ability a = candidates[i].ability;
                    var ext = candidates[i].ext;
                    var worker = ext.Worker;
                    if (!CombatAllows(worker)) continue;
                    if (!worker.TryFindTarget(ctx, a, out LocalTargetInfo target, out LocalTargetInfo dest)) continue;
                    if (!dest.IsValid) dest = target;
                    if (!Valid(a, target)) continue;
                    if (player && !PsycastWithinLimits(a, target)) continue;

                    state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
                    if (ext.minGapSeconds > 0f)
                    {
                        if (state.abilityNext == null) state.abilityNext = new Dictionary<AbilityDef, int>();
                        state.abilityNext[a.def] = tick + Mathf.RoundToInt(ext.minGapSeconds * GenTicks.TicksPerRealSecond);
                    }
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.def.label} ({worker.GetType().Name}) on {target}");
                    AbilityAIHooks.NotifyCast(pawn, a, target, dest);
                    if (player) QueueAfterCast(pawn, state);
                    return a.GetJob(target, dest);
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
                curSet = PlayerSet.All;
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

        // The walk to the spot the pawn left, or with ReturnToCover a covered spot near it that it can shoot from.
        // Null when it is already there or can't get there. Not player-forced, so a new player order replaces it
        // (and CheckHome sees the forced job and forgets the walk).
        private static Job MakeReturnJob(Pawn pawn, PawnState state)
        {
            IntVec3 cell = state.home;
            if (AbilityAISettings.ReturnToCover && TryFindCoverCell(pawn, state.home, out IntVec3 cover)) cell = cover;
            if (cell == pawn.Position || !pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly)) return null;
            Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.locomotionUrgency = LocomotionUrgency.Jog;
            state.returnJobId = job.loadID;
            state.returnCell = cell;
            return job;
        }

        // Vanilla's cast-position search with the pawn's own weapon against the hostile nearest the old spot, wanting
        // cover from it, within CoverSearchRadius of that spot. Once per walk back.
        private static bool TryFindCoverCell(Pawn pawn, IntVec3 home, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            var targets = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
            Thing enemy = null;
            float best = float.MaxValue;
            for (int i = 0; i < targets.Count; i++)
            {
                if (!(targets[i].Thing is Pawn p) || p.Downed || targets[i].ThreatDisabled(pawn)) continue;
                float d = (p.Position - home).LengthHorizontalSquared;
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
                maxRangeFromCaster = (pawn.Position - home).LengthHorizontal + CoverSearchRadius,
                locus = home,
                maxRangeFromLocus = CoverSearchRadius,
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
            if (!player || !pawn.Spawned || !pawn.Drafted || (cur != null && cur.playerForced)) { ClearHome(state); return; }
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
                Ability a = candidates[i].ability;
                var ext = candidates[i].ext;
                if (ext.approachDistance <= 0f || ctx.HealthFraction < ext.healthThreshold || !CombatAllows(ext.Worker)) continue;
                if (player && AbilityAIAutocast.ModeFor(pawn, a.def) != AbilityAIAutocastMode.Full) continue;
                if (state.approachNext != null && state.approachNext.TryGetValue(a.def, out int next) && ctx.tick < next) continue;
                IntVec3 cell;
                int expected = 0;
                string at;
                bool byCell = ext.Worker.UsesApproachCell && AbilityAIUtility.IsSelfCast(a);
                if (byCell && ext.Worker.TryFindApproachCell(ctx, a, out cell, out expected))
                    at = $"{cell} (expects {expected})";
                else if (byCell)
                {
                    // Nothing worth walking to: that scan is m³, so don't repeat it every 30 ticks
                    if (state.approachNext == null) state.approachNext = new Dictionary<AbilityDef, int>();
                    state.approachNext[a.def] = ctx.tick + ApproachFailRetryTicks;
                    continue;
                }
                else
                {
                    float reach = AbilityAIUtility.Range(a, ext) + ext.approachDistance;
                    if (!ext.Worker.TryFindApproachTarget(ctx, a, reach, out Thing goal) || goal == null) continue;
                    if (!TryFindApproachCell(a, ext, goal, out cell))
                    {
                        // The cast-position search is the costliest step here: not again for 2 s
                        if (state.approachNext == null) state.approachNext = new Dictionary<AbilityDef, int>();
                        state.approachNext[a.def] = ctx.tick + ApproachFailRetryTicks;
                        continue;
                    }
                    expected = 0;
                    at = goal.LabelShort;
                }

                if (state.approachNext == null) state.approachNext = new Dictionary<AbilityDef, int>();
                state.approachNext[a.def] = ctx.tick + ApproachRetryTicks;
                Job job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                job.expiryInterval = ApproachExpiryTicks;
                job.checkOverrideOnExpire = false;
                job.locomotionUrgency = LocomotionUrgency.Jog;
                state.approachJobId = job.loadID;
                state.approachAbility = expected > 0 ? a.def : null;
                state.approachExpected = expected;
                if (player)
                {
                    ClearHome(state);
                    state.home = pawn.Position;
                    // Holding fire on a target the player picked: back to it once home
                    Job cur = pawn.CurJob;
                    if (cur != null && cur.def == JobDefOf.AttackStatic && cur.def.suspendable) state.resume = Resume(cur);
                }
                if (AbilityAISettings.DebugLog && Prefs.DevMode)
                    Log.Message($"[Ability AI] {pawn.LabelShort} moves to {cell} to cast {a.def.label} ({ext.Worker.GetType().Name}) at {at}");
                return job;
            }
            return null;
        }

        // A self-cast ability walks toward the goal itself (Battle Cry: toward the allies); a targeted one to a
        // vanilla cast position for its verb, preferring cover from the target as the fight AI does
        private static bool TryFindApproachCell(Ability a, AbilityAIExtension ext, Thing goal, out IntVec3 cell)
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
                verb = a.verb,
                maxRangeFromTarget = Mathf.Max(1.5f, AbilityAIUtility.Range(a, ext) - 1f),
                maxRangeFromCaster = ext.approachDistance + 3f,
                wantCoverFromTarget = true,
            };
            return CastPositionFinder.TryFindCastPosition(req, out cell) && cell != pawn.Position;
        }
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
        public static bool Unclaimed;
        public static bool Approach = true;
        public static bool PlayerAutocast = true;
        public static bool BreakCasting;
        public static bool UndraftedAutocast = true;
        public static bool ReturnToCover;
        public const float DefaultMinGapSeconds = 2.5f;
        public static float MinGapSeconds = DefaultMinGapSeconds;
        public static bool DebugLog;

        public static void Reset()
        {
            Enabled = Hostiles = NonHostiles = Animals = Mechs = Approach = VanillaStandDown = true;
            PlayerAutocast = UndraftedAutocast = true;
            Unclaimed = BreakCasting = ReturnToCover = false;
            MinGapSeconds = DefaultMinGapSeconds;
            DebugLog = false;
        }

        public static void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "abilityAI_enabled", true);
            Scribe_Values.Look(ref Hostiles, "abilityAI_hostiles", true);
            Scribe_Values.Look(ref NonHostiles, "abilityAI_nonHostiles", true);
            Scribe_Values.Look(ref Animals, "abilityAI_animals", true);
            Scribe_Values.Look(ref Mechs, "abilityAI_mechs", true);
            Scribe_Values.Look(ref Unclaimed, "abilityAI_unclaimed", false);
            Scribe_Values.Look(ref VanillaStandDown, "abilityAI_vanillaStandDown", true);
            Scribe_Values.Look(ref Approach, "abilityAI_approach", true);
            Scribe_Values.Look(ref PlayerAutocast, "abilityAI_playerAutocast", true);
            Scribe_Values.Look(ref UndraftedAutocast, "abilityAI_undraftedAutocast", true);
            Scribe_Values.Look(ref ReturnToCover, "abilityAI_returnToCover", false);
            Scribe_Values.Look(ref BreakCasting, "abilityAI_breakCasting", false);
            Scribe_Values.Look(ref MinGapSeconds, "abilityAI_minGapSeconds", DefaultMinGapSeconds);
            MinGapSeconds = Mathf.Clamp(MinGapSeconds, 0f, 30f);
            Scribe_Values.Look(ref DebugLog, "abilityAI_debugLog", false);
        }

        public static void DoSettings(Listing_Standard list)
        {
            list.CheckboxLabeled("AAI_Enabled".Translate(), ref Enabled, "AAI_Enabled_Tip".Translate());
            list.Gap(6f);
            list.Label("AAI_Who".Translate());
            list.CheckboxLabeled("  " + "AAI_Hostiles".Translate(), ref Hostiles, "AAI_Hostiles_Tip".Translate());
            list.CheckboxLabeled("  " + "AAI_NonHostiles".Translate(), ref NonHostiles, "AAI_NonHostiles_Tip".Translate());
            list.CheckboxLabeled("  " + "AAI_Animals".Translate(), ref Animals, "AAI_Animals_Tip".Translate());
            list.CheckboxLabeled("  " + "AAI_Mechs".Translate(), ref Mechs, "AAI_Mechs_Tip".Translate());
            list.Label("AAI_Who_Desc".Translate());
            list.Gap();
            list.CheckboxLabeled("AAI_Unclaimed".Translate(), ref Unclaimed, "AAI_Unclaimed_Tip".Translate());
            list.CheckboxLabeled("AAI_VanillaStandDown".Translate(), ref VanillaStandDown, "AAI_VanillaStandDown_Tip".Translate());
            list.CheckboxLabeled("AAI_Approach".Translate(), ref Approach, "AAI_Approach_Tip".Translate());
            list.Gap();
            list.CheckboxLabeled("AAI_PlayerAutocast".Translate(), ref PlayerAutocast, "AAI_PlayerAutocast_Tip".Translate());
            list.CheckboxLabeled("  " + "AAI_UndraftedAutocast".Translate(), ref UndraftedAutocast, "AAI_UndraftedAutocast_Tip".Translate());
            list.CheckboxLabeled("  " + "AAI_ReturnToCover".Translate(), ref ReturnToCover, "AAI_ReturnToCover_Tip".Translate());
            list.CheckboxLabeled("AAI_BreakCasting".Translate(), ref BreakCasting, "AAI_BreakCasting_Tip".Translate());
            list.Gap();
            list.Label("AAI_MinGap".Translate(MinGapSeconds.ToString("0.#")));
            MinGapSeconds = Mathf.Round(list.Slider(MinGapSeconds, 0f, 30f) * 2f) / 2f;
            list.Label("AAI_MinGap_Desc".Translate());
            if (Prefs.DevMode)
            {
                list.Gap();
                list.CheckboxLabeled("AAI_DebugLog".Translate(), ref DebugLog, "AAI_DebugLog_Tip".Translate());
            }
        }
    }

    /// <summary>Runs the ability AI whenever the constant think tree has nothing to do, as Bastle Framework's
    /// BastleAI does. Pawns with no abilities return after three field reads; pawns whose abilities have no AI
    /// extension after one def lookup per ability.</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextConstantThinkTreeJob")]
    internal static class Patch_AbilityAI_ConstantThinkTree
    {
        static void Postfix(Pawn ___pawn, ref ThinkResult __result)
        {
            if (__result.IsValid || !AbilityAISettings.Enabled) return;
            var tracker = ___pawn?.abilities;
            if (tracker == null) return;
            var abilities = tracker.AllAbilitiesForReading;
            if (abilities.Count == 0) return;
            // The player's pawns with nothing set to autocast stop at one dictionary lookup here
            var control = AbilityAIDriver.ControlOf(___pawn);
            if (control == AbilityAIDriver.Control.None || !AbilityAIDriver.AnyWithExtension(abilities)) return;
            // Vanilla starts these jobs against the constant tree and saves the job giver's key in it; with no
            // constant tree a saved job would throw on load.
            var root = ___pawn.thinker?.ConstantThinkTree?.thinkRoot;
            if (root == null) return;
            try
            {
                Job job = AbilityAIDriver.TryGiveJob(___pawn, abilities, control);
                // The tree's root as the source: the AI has no think node of its own, so a pawn saved mid-cast would
                // otherwise log a missing think node on load
                if (job != null) __result = new ThinkResult(job, root);
            }
            catch (Exception e)
            {
                // Vanilla's constant-tree call has its own try/catch; this runs after it
                Log.ErrorOnce($"[Ability AI] threw for {___pawn.def.defName}: {e}", ___pawn.def.shortHash ^ 0x7A12);
            }
        }
    }
}
