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
        /// From the attack-target cache, so turrets and other buildings are left out.</summary>
        public List<Pawn> Enemies
        {
            get
            {
                if (!enemiesBuilt)
                {
                    enemiesBuilt = true;
                    // The cache hands back one shared list that its next caller clears, so copy out of it at once
                    var targets = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (!(targets[i].Thing is Pawn p) || !p.Spawned || p.Dead || p.Downed) continue;
                        if (targets[i].ThreatDisabled(pawn) || p.IsPsychologicallyInvisible() || !p.HostileTo(pawn)) continue;
                        enemies.Add(p);
                    }
                }
                return enemies;
            }
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
        }

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
        /// abilities the player set to autocast (for now only pawns that can be drafted get the menu: colonists, slaves,
        /// ghouls, mechs, and animals a mod makes draftable); prisoners never.</summary>
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
        /// Drafted: only while standing still in position or shooting, never during an ordered move. Undrafted
        /// pawns don't autocast yet.</summary>
        public static bool CanActNowPlayer(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || !pawn.Awake() || pawn.IsBurning()) return false;
            if (pawn.InMentalState || pawn.CurJob?.ability != null) return false;
            if (pawn.stances?.stunner?.Stunned == true) return false;
            if (!pawn.Drafted) return false;
            if (pawn.pather != null && pawn.pather.Moving) return false;
            JobDef job = pawn.CurJobDef;
            return job == null || job == JobDefOf.Wait_Combat || job == JobDefOf.Wait || job == JobDefOf.Wait_MaintainPosture
                || job == JobDefOf.AttackStatic;
        }

        // The player's own pawn: their mode counts as the claim, but a hook can still veto (an item bound to someone else)
        private static bool PlayerAllowed(Pawn pawn, Ability a)
        {
            return AbilityAIAutocast.ModeFor(pawn, a.def) != AbilityAIAutocastMode.Off && AbilityAIHooks.NotVetoed(pawn, a);
        }

        /// <summary>Whether this mod is the one that decides when this pawn casts this ability: the AI is on, the
        /// ability has a working extension, the settings cover the pawn and a hook (or Unclaimed, or the player's
        /// autocast mode) lets it through. Independent of the moment: an ability on cooldown or held back by
        /// reserveCharges is still ours. Public so a host's own fallback AI can step aside for the same abilities.</summary>
        public static bool Handles(Pawn pawn, Ability ability)
        {
            if (!AbilityAISettings.Enabled || pawn == null || ability?.def == null) return false;
            var ext = ability.def.GetModExtension<AbilityAIExtension>();
            if (ext?.Worker == null) return false;
            switch (ControlOf(pawn))
            {
                case Control.NPC: return AbilityAIHooks.Allowed(pawn, ability);
                case Control.Player: return PlayerAllowed(pawn, ability);
                default: return false;
            }
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
            // A walk into range just ended (arrived or timed out): look again now rather than up to 30 ticks later,
            // before vanilla's own positioning walks the pawn back out
            if (state.approachJobId >= 0 && pawn.CurJob?.loadID != state.approachJobId)
            {
                state.approachJobId = -1;
                state.approachAbility = null;
                state.approachExpected = 0;
                state.nextEvalTick = tick;
            }
            if (tick < state.nextEvalTick || tick < state.nextCastTick) return null;
            state.nextEvalTick = tick + EvalIntervalTicks;
            bool player = control == Control.Player;
            if (player ? !CanActNowPlayer(pawn) : !CanActNow(pawn)) return null;

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
                    if (!checkedReady)
                    {
                        checkedReady = true;
                        ready = a.CanCast && PsycastAffordable(a)
                            && (state.abilityNext == null || !state.abilityNext.TryGetValue(a.def, out int next) || tick >= next)
                            && (player ? PlayerAllowed(pawn, a) : AbilityAIHooks.Allowed(pawn, a));
                    }
                    if (!ready) break;
                    if (ext.reserveCharges > 0 && a.UsesCharges && a.RemainingCharges <= ext.reserveCharges) continue;
                    int at = candidates.Count;
                    while (at > 0 && candidates[at - 1].ext.priority < ext.priority) at--;
                    candidates.Insert(at, new Candidate { ability = a, ext = ext });
                }
            }
            if (candidates.Count == 0) return null;

            evaluating = true;
            ctx.Begin(pawn, tick, state.approachJobId >= 0 ? state.approachAbility : null, state.approachExpected, player && pawn.Drafted);
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    Ability a = candidates[i].ability;
                    var ext = candidates[i].ext;
                    var worker = ext.Worker;
                    if (!CombatAllows(worker)) continue;
                    if (!worker.TryFindTarget(ctx, a, out LocalTargetInfo target, out LocalTargetInfo dest)) continue;
                    if (!dest.IsValid) dest = target;
                    if (!Valid(a, target)) continue;

                    state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
                    if (ext.minGapSeconds > 0f)
                    {
                        if (state.abilityNext == null) state.abilityNext = new Dictionary<AbilityDef, int>();
                        state.abilityNext[a.def] = tick + Mathf.RoundToInt(ext.minGapSeconds * GenTicks.TicksPerRealSecond);
                    }
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.def.label} ({worker.GetType().Name}) on {target}");
                    AbilityAIHooks.NotifyCast(pawn, a, target, dest);
                    // A player's attack order would be lost: vanilla starts constant-tree jobs without resuming the
                    // current one. Queue a copy so the pawn goes back to it after the cast.
                    if (player && pawn.CurJobDef == JobDefOf.AttackStatic && pawn.CurJob.def.suspendable)
                    {
                        // Clone copies loadID; two live jobs with one id break loading a save made while both exist
                        Job resume = pawn.CurJob.Clone();
                        resume.loadID = Find.UniqueIDsManager.GetNextJobID();
                        pawn.jobs.jobQueue.EnqueueFirst(resume);
                    }
                    return a.GetJob(target, dest);
                }
                // The player's pawns don't walk to cast yet (Full mode comes later)
                return player ? null : TryApproach(state);
            }
            finally
            {
                ctx.End();
                candidates.Clear();
                evaluating = false;
            }
        }

        private static bool CombatAllows(AbilityAIWorker worker)
        {
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
        // things to do. Cost: one worker scan like the one above, then one CastPositionFinder search, which checks
        // line of sight from every cell within approachDistance + 3 of the caster and range of the target (about
        // 27x27 cells for Flashbang), per ability per pawn once per 10 s after a walk starts and once per 2 s while
        // no cell is found.
        private static Job TryApproach(PawnState state)
        {
            if (!AbilityAISettings.Approach) return null;
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
        public const float DefaultMinGapSeconds = 2.5f;
        public static float MinGapSeconds = DefaultMinGapSeconds;
        public static bool DebugLog;

        public static void Reset()
        {
            Enabled = Hostiles = NonHostiles = Animals = Mechs = Approach = VanillaStandDown = true;
            PlayerAutocast = true;
            Unclaimed = BreakCasting = false;
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
