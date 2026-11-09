# Ability AI from C#

Most mods only need XML rules ([writing-rules.md](writing-rules.md)). This page is for what XML can't say:

- deciding per pawn which abilities Ability AI may cast (a framework that hands out abilities through gear or traits);
- telling Ability AI the area, hediff or stun of your own effect comp;
- stepping your own fallback AI aside while Ability AI handles an ability;
- gear verbs that need a job of their own, or a readiness check their `Available()` leaves out;
- abilities of another ability system, or abilities kept on items;
- a worker of your own.

Everything is in the `AbilityAI` namespace of `AbilityAI.dll`, except the VEF extension points, which are in `AbilityAI.Vef` in `AbilityAI.VEF.dll` (loaded only with Vanilla Expanded Framework).

## Setup: a compat assembly

Unless your mod requires Ability AI, keep every reference to it in a separate small assembly that only loads when Ability AI is active. A type in your main DLL that derives from, implements, or has a field of an Ability AI type makes RimWorld skip the whole DLL when Ability AI is missing (`ReflectionTypeLoadException`).

Entad Framework does it like this:

```xml
<!-- LoadFolders.xml -->
<v1.6>
  <li>/</li>
  <li>1.6</li>
  <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI</li>
  <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI/1.6</li>
</v1.6>
```

`Mods/AbilityAI/Patches/` holds the XML rules, `Mods/AbilityAI/1.6/Assemblies/` the compat DLL. Its csproj references `AbilityAI.dll` with `<Private>false</Private>`, so the DLL isn't copied into your mod, and your main project the same way. Add `saintwacko.AbilityAI` to `<loadAfter>` in About.xml.

Register from a `[StaticConstructorOnStartup]` class in the compat assembly. Defs are loaded and patched by then, and Ability AI's own startup has run.

```csharp
[StaticConstructorOnStartup]
internal static class MyModAbilityAIStartup
{
    static MyModAbilityAIStartup()
    {
        AbilityAIHooks.Register(MyModAbilityAIHook.Instance);
        AbilityAIInfo.Register<CompProperties_MyPulse>(p => p.radius, p => p.hediff, p => p.stunSeconds > 0f);
    }
}
```

All registration happens on the main thread at startup. Callbacks are called on the main thread during play.

### Versions

`AbilityAI.dll`'s AssemblyVersion stays at 1.0.0.0 across releases, so a player with an older Ability AI loads your compat assembly without complaint, and a call to a member that copy doesn't have throws `MissingMethodException` when the calling method is compiled. Put calls to newer members in their own method marked `[MethodImpl(MethodImplOptions.NoInlining)]` and catch it around the call:

```csharp
try { RegisterHeld(); }
catch (Exception e) when (e is MissingMethodException || e is TypeLoadException || e is MissingMemberException)
{
    Log.Warning("[My Mod] Ability AI is older than this mod was built for; update it for NPCs to use My Mod's held abilities.");
}
```

## Claiming abilities: hooks and votes

```csharp
public enum AbilityAIVote { NoOpinion, Claim, Veto }

public interface IAbilityAIHook
{
    AbilityAIVote Vote(Pawn pawn, Ability ability);
    void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest);
}

AbilityAIHooks.Register(IAbilityAIHook hook);
AbilityAIHooks.RegisterVote(Func<Pawn, object, AbilityAIVote> vote);
```

`Vote` is asked for every ready vanilla ability with a rule, before any targeting.

- Any `Veto` wins: Ability AI doesn't cast it for that pawn and doesn't stand other AIs down for it.
- `Claim` lets other factions use it even when the player has turned off "Also use abilities no mod claims".
- `NoOpinion` leaves it to that setting (on by default).
- For the player's own pawns only `Veto` counts, since the player's autocast mode is their claim. A veto also hides the autocast menu and badge on the gizmo (Entad vetoes abilities whose trait is still hidden, so the menu doesn't give them away).

Keep `Vote` cheap: it runs for each ready ability of each pawn Ability AI looks at, about twice a second per pawn. A hook that throws is logged once and counts as `NoOpinion`.

`Notify_AICast` is called when Ability AI has handed the game a cast job. The cast may still be dropped before it resolves; patch `Ability.Activate` for anything that must only happen on a real cast.

`RegisterVote` is the same for abilities of other systems (VEF's). The voter gets the system's own ability object (`VEF.Abilities.Ability` for VEF), typed `object`, so your compat assembly doesn't need to reference that system. A voter that throws counts as `Veto` for that call.

## Job owners: your own arbiter decides

For a mod that decides every job its pawns start (a combat AI with one arbiter per pawn). Ability AI then starts nothing for the pawns you own and suggests casts when you ask.

```csharp
public interface IAbilityAIJobOwner { bool Owns(Pawn pawn); }
AbilityAIHooks.RegisterJobOwner(IAbilityAIJobOwner owner);

AbilityAIDriver.GetCandidates(Pawn pawn, List<AbilityAICandidate> into, int max = 3);
Job AbilityAIDriver.Start(Pawn pawn, AbilityAICandidate candidate);
bool AbilityAIDriver.IsAICast(Pawn pawn, out int warmupTicksLeft);
```

- `Owns` is asked on every constant-tree check of a pawn with abilities. Answer from a field or a cached lookup. The first owner that says yes has the pawn; an owner that throws is logged once and dropped for the session.
- Owned pawns get nothing from Ability AI's constant-tree postfix, including the cast it would put ahead of a colonist's hostility response. Barriers and group links it raised still come down, and recasting, zone casting and cast markers still run as colonist work.
- `GetCandidates` runs the same evaluation Ability AI runs for its own casts, under the same settings, autocast modes, votes and intervals: a pawn checked in the last 30 ticks, or within the gap after a cast, gets an empty list. Ask once per pawn per check. Casts come first, best `score` first; a walk into range (`Approach`) or a Full autocast pawn's walk back (`Return`) is offered only when no cast is. `LinkFirst` (a Group link before a Word) is always followed by the Word alone, so the list can be one longer than `max`. `LinkWord` (the Word once the link is up) comes alone.
- A candidate holds nothing: no job is made and no target is claimed, so you can drop it without a call. Pass the one you pick to `Start` in the same tick. `Start` makes the job and does Ability AI's own bookkeeping for a cast (gaps, barrier and link tracking, `Notify_AICast`, and for the player's pawns the attack queued to resume after the cast). It returns null when the candidate is stale or the ability refused when its job was made; try the next one. A `Return` whose walk can't be made returns the attack the pawn was on before it walked out (or null), not a walk. Start the job at once, from your own think node.
- `score` is what the candidate was ranked by, in priority units. With the player's Dynamic priority setting on, it is the rule's `priority` plus the bonus for `value` (see writing-rules.md, Dynamic priority), and the list holds the top `max` by score: a rule that can't reach the top `max` even at its cap isn't searched, so asking for 3 searches a little more than asking for 1. With the setting off, the list is the first `max` rules by priority that find a target, as before. `score` equals `priority` for any rule whose worker reports no value, for every candidate while the setting is off, and for `LinkWord`, `Approach` and heat vents; `Return` is `float.MinValue`. `priority` is unchanged, so `score - priority` is the bonus.
- `value` is what the worker counted for this target (weighted enemies hit, allies helped, fire weight, threats, or urgency points for help workers), before the worker's `ValueBase` is taken off (`minTargets` for the area workers, `minCorpses` for corpse rules, `minThreats` for Cover, 0 for help workers: see the worker table); -1 when it reports none or the setting is off. A `LinkFirst` carries its Word's score and value.
- `category` is the rule's `category` field, else its worker's default (`Approach` and `Return` are `Movement`).
- `IsAICast` is true while the pawn runs a cast Ability AI or your `Start` made. Not saved: after a load, a cast you started is only recognised once the pawn starts its next one.

### Target weight

```csharp
public interface IAbilityAITargetWeight { float WeightOf(Pawn caster, Thing target); }
AbilityAIHooks.RegisterTargetWeight(IAbilityAITargetWeight provider);
```

How much one enemy counts toward a rule's value. Without a provider it is the pawn kind's `combatPower / 100`, and 1 for turrets and anything without a kind. Register one when your mod has its own threat model, so both mods value the same raider the same way. 1 means an ordinary enemy; Ability AI clamps the answer to [1, 3] (NaN counts as 1), so a weak pawn still counts as a whole target. It is asked on the main thread, at most once per target per evaluation of a caster, and only while Dynamic priority is on: answer from a cache. The first provider registered answers; one that throws is logged once and dropped for the session.

In dev mode the action "Ability AI > Job owner test" registers a stand-in owner that owns every pawn and either starts nothing or starts the first candidate it is offered.

## Telling Ability AI about your effect comp

```csharp
AbilityAIInfo.Register<T>(Func<T, float> radius, Func<T, HediffDef> hediff = null, Func<T, bool> stuns = null)
    where T : AbilityCompProperties;
AbilityAIInfo.RegisterCone<T>(Func<T, float> length, Func<T, float> widthAtEnd) where T : AbilityCompProperties;
```

Ability AI reads the vanilla effect comps by itself (see writing-rules.md). For your own `AbilityCompProperties` subclass, register readers so the workers know its area (`radius`, 0 for one target), the hediff it gives (so the AI doesn't recast it on pawns that have it), and whether it stuns (so hostile rules skip stunned pawns). `RegisterCone` gives Cone hostile the length and far-end width of a cone-shaped comp (a length of 0 or less means the ability's range).

A reader also covers subclasses of `T` that don't register their own. Answers are read once per comp and cached, so register at startup and don't change the comp afterwards. A reader that throws is logged once and treated as no information.

A mod that requires Ability AI can implement `IAbilityAIInfo` (`AIRadius`, `AIHediff`, `AIStuns`) on the comp properties class instead.

## Your own fallback AI

```csharp
bool AbilityAIDriver.Handles(Pawn pawn, Ability ability);
bool AbilityAIDriver.Handles(Pawn pawn, AIAbility ability);
bool AbilityAIDriver.OthersStandDown(Pawn pawn, Ability ability);
```

If your mod has its own NPC ability AI for when Ability AI isn't installed (a think node, a job giver), have it step aside where Ability AI decides:

- `Handles` is true when Ability AI decides when this pawn casts this ability: it has a working rule, the settings cover the pawn, and no hook vetoes it. It doesn't depend on the moment, so it is true while the ability is on cooldown.
- `OthersStandDown` is what Ability AI uses for vanilla's `aiCanUse` AI. For the player's own pawns it is true whenever the ability has a rule, whatever its autocast mode, so the player's choice of Off isn't overridden by another AI. For everyone else it equals `Handles`.

Vanilla's `aiCanUse` AI and VEF's autocast are already stood down by Ability AI. You only need these for an AI of your own.

## Worn gear verbs

```csharp
ApparelVerbJobs.Register(Type verbType, Func<Verb, LocalTargetInfo, Job> make, bool skipValidate = false, JobDef castJob = null);
ApparelVerbJobs.RegisterReady(Type verbType, Func<Verb, bool> ready);
```

Gear rules cast the verb with vanilla's verb jobs (`UseVerbOnThing`, `UseVerbOnThingStatic`, `CastJump`). For a verb whose order makes a job of its own, `Register` says how to make it. `skipValidate` skips the verb's `ValidateTarget` for a verb that posts a message or reads the targeter there; `castJob` names the job so Ability AI knows a pawn on it is casting. The most derived registered type wins. A `make` that throws is dropped and the verb goes back to vanilla's job.

`RegisterReady` adds a quiet check that the verb's `Available()` leaves out but the cast would refuse after its warmup (Apex's mechamancer pack: at most so many satellites alive). It must not post messages. A check that throws counts as not ready and is dropped.

## Abilities kept on items

```csharp
AIHeldAbilities.Register(Func<Pawn, bool> hasAny, Action<Pawn, List<object>> collect);
```

For abilities a pawn can cast that no tracker of its lists, because they live on items it wears or wields (Entad's VPE psycasts on gear). `collect` appends the ability objects, of their own system's type and already pointed at the pawn; the source for that system picks out the ones it knows (VEF's takes `VEF.Abilities.Ability`). `hasAny` is asked whenever the pawn's think tree has nothing else to do, so keep it to a few field reads. A provider that throws is dropped for the session. Nothing about these objects is saved.

Claim them with `RegisterVote`.

## Another ability system

```csharp
AIAbilitySources.Register(IAIAbilitySource source);

public interface IAIAbilitySource
{
    bool HasAny(Pawn pawn);
    bool AnyWithExtension(Pawn pawn);
    void Collect(Pawn pawn, List<AIAbility> into);
    bool TryGetRunningCast(Pawn pawn, out AIAbility ability, out int warmupTicksLeft);
}
```

Workers never see a `RimWorld.Ability`; they see an `AIAbility`, an adapter that each ability system implements. Vanilla abilities, worn gear verbs and VEF abilities are built in. To add another system, implement `AIAbility` for its ability type (one wrapper per ability, cached, so the hot path allocates nothing) and register a source that hands them out. Rules stay `AbilityAIExtension`s, read from the system's def.

- `HasAny`: does the pawn hold any ability of this system. Asked often; a few field reads.
- `AnyWithExtension`: does any of them have a rule. May answer true loosely.
- `Collect`: append the pawn's abilities that have a rule.
- `TryGetRunningCast`: is the pawn's current job a cast of this system's (any ability, ruled or not), with the warmup ticks left (-1 while walking up or unknown). Asked for every pawn evaluated and every enemy the Interrupt worker looks at, so check the job def first.

A source that throws is dropped for the session and its abilities go back to their own AI.

`AIAbility`'s abstract members: `Pawn`, `Def` (what rules, autocast modes and gaps are keyed on), `Raw` (the wrapped object), `CanCastNow`, `IsPsycast`, `Range` (0 or less for a touch ability), `DefaultRadius`, `IsSelfCast`, `CanTargetSelf`, `WarmupSeconds`, `Verb` (for `CastPositionFinder`), `Effects` (an `AIAbilityEffects` read once per def), `Hostile`, `ValidIgnoringRange(target)`, `ValidInRange(target)`, `PsyfocusCost(target)`, `HeatGain`, and `MakeCastJob(target, dest)`, which returns the cast job without starting it (or null to refuse). Virtual members with defaults cover touch abilities, charges, single use, resource costs (`ResourceCost(AIResourceDef)`, by default the resource def's own readers on `Def`; it replaces `HemogenCost`), flight time, two-target destinations and the dev log's reason text. `VefAIAbility` in `Source/VEF/VefAIAbility.cs` is the complete example.

At startup, also call these for each of the system's defs, so its Recast, gather, zone, spot, heat, barrier, group link and NPC gate rules are known (the VEF compat does this in `VefStartup`):

```csharp
AbilityAIRecast.Note(def); AbilityAIGather.Note(def); AbilityAIZones.Note(def); AbilityAISpots.Note(def);
AbilityAIHeat.Note(def); AbilityAIBarrier.Note(def); AbilityAIGroupLink.Note(def); AbilityAINpcGates.Note(def);
```

Two more registries for a system's things:

- `AbilityAIRecast.RegisterLifetime(Func<Thing, int> endTick)`: the tick a thing the system spawned will be gone by, or -1, so Recast rules can recast it before it runs out. Vanilla's `CompDestroyAfterDelay` is read without one.
- `AbilityAIZones.RegisterBusy(Func<Thing, Def, bool> busy)`: true when this ability's effect is already under way on the thing, so zone work doesn't send a second cast.

### VEF abilities

In `AbilityAI.Vef` (only with VEF loaded):

- `VefAIAbility.RegisterCosts(IVefAbilityCosts costs)`: VEF has no cost system of its own, so without one every VEF ability is free and none is a psycast. `IVefAbilityCosts` has `Covers(def)`, `Affordable(ability)` (answer quietly), `PsyfocusCost(ability, target)` and `HeatGain(ability)`; the first registered system that covers a def prices it. VPE's is built in. Implement `IVefAbilityHeat` as well for heat vents.
- `VefAIAbility.RegisterEffectsReader(Action<VefAbilityDef, AIAbilityEffects> reader)`: fill in the effects from extensions Ability AI doesn't know. Runs after the built-in readers, once per def.
- `VefAIAbility.RegisterReady(Type abilityType, Func<VefAbility, bool> ready)`: a check the ability leaves to its gizmo, so VEF's `IsEnabledForPawn` passes without it (VFE Pirates' Power jump only greys out its gizmo when fuel is low). Exact type only.
- `VefAIAbility.RegisterPlayerAutocaster(Func<VefAbility, bool> decides)`: another autocast mod decides these abilities for the player's pawns, so Ability AI offers no menu and never casts them for the player. Asked per gizmo; answer from a cached set.

## Writing a worker

A rule's `workerClass` can name your own class. It must derive from `AbilityAIWorker`, not be abstract, and have a parameterless constructor.

```csharp
public class MyWorker_ShieldTheMedic : AbilityAIWorker
{
    protected override AbilityAICombat DefaultCombat => AbilityAICombat.InCombat;

    public override bool TryFindTarget(AbilityAIContext ctx, AIAbility ability, out LocalTargetInfo target, out LocalTargetInfo dest)
    {
        target = dest = LocalTargetInfo.Invalid;
        float range = AbilityAIUtility.Range(ability, ext);
        HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
        var allies = ctx.Allies;
        for (int i = 0; i < allies.Count; i++)
        {
            Pawn p = allies[i];
            if (!p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && !AbilityAIUtility.Has(p, hediff)
                && p.Position.InHorDistOf(ctx.pawn.Position, range) && ctx.AimedAtCount(p) >= ext.minThreats
                && AbilityAIDriver.Valid(ability, p))
            {
                target = p;
                return true;
            }
        }
        return false;
    }
}
```

One worker instance serves every pawn and every psycast copy of the def, so keep no per-pawn state in it. Its rule is the `ext` field; read any of the extension's fields from there.

What a worker can override:

| Member | Default | What it means |
|---|---|---|
| `TryFindTarget(ctx, ability, out target, out dest)` | required | A target worth casting at now, or false. The driver runs `AbilityAIDriver.Valid` on the result again, so call it yourself only when choosing between candidates. `dest` is for two-target abilities. |
| `DefaultCombat` | InCombat | The `combat` used when the rule doesn't set it. |
| `TargetsEnemies` | false | The worker casts at enemies. Read by systems that can't tell from the def (VEF without `isPositive`) and by the Interrupt worker. |
| `DefaultExclusive` | `!TargetsEnemies` | Whether a faction-mate's cast at a target keeps others off it, when the rule doesn't say. |
| `ForFleeing` | false | Worth using while running away (Movement, Recovery). |
| `CountsAffectedAroundCaster` | false | An area cast at the caster whose affected pawns the worker counts itself, so the comps aren't asked about the caster. |
| `DriverCasts` | true | False for a rule cast from somewhere else (work, as Recast does). The rule still gives the player an autocast menu and stands other AIs down. |
| `ReportsValue` | false | The worker sets `ctx.TargetValue` for the target it returns, while `ctx.WantsValue` is true, for Dynamic priority. Leave it false and the rule scores at its priority. |
| `ValueBase` | `ext.minTargets` | The value at which the bonus starts. A worker whose value is urgency rather than a count returns 0 and reports `AbilityAIScore.UrgencyPoints` (4) x urgency (0..1), as Recovery, Target friendly and Rescue do; Defensive also returns 0 and reports the larger of that, threats past `minThreats`, and 2 in melee. Cover returns `minThreats`. |
| `ValueBound(ctx, ability)` | `float.MaxValue` | The most `TargetValue` could be for this rule now, from what the context already holds, before the search: cheap, and never lower than what the search could report. -1 = no value now. A rule whose priority plus the bonus for its bound can't beat the best option found so far is not searched. `AbilityAIUtility.WeightWithin(ctx, ext, cell, reach)` sums the weights of the rule's enemies within reach of a cell. The default knows no bound, so the rule is searched whenever its cap could win. |
| `TryFindApproachTarget(ctx, ability, reach, out goal)` | false | Only when no ability found a target and the rule has `approachDistance`: a thing to walk toward, within `reach`. Check candidates with `ValidIgnoringRange`. |

`AbilityAIContext` holds what workers share during one evaluation of one pawn, built on first use: `pawn`, `map`, `tick`, `Enemies` (standing, visible, hostile pawns), `Allies` (standing pawns of the caster's faction, itself included), `DownedAllies`, `Friendlies`, `BrokenAllies`, `EnemyTurrets`, `FriendlyTurrets`, `EnemyTarget`, `Prey`, `InCombat`, `HealthFraction`, `UsesRanged`, `NearestEnemy(out distSq)`, `AimedAtCount(pawn)`, `RecentlyShot(pawn)`, `IsBrokenAlly(pawn)`. The lists are reused for the next pawn, so copy one before keeping it. Set `NoTargetWhy` when a worker had a target but chose to wait; it shows in the dev log. For Dynamic priority, `WantsValue` says whether a value is wanted this search, `TargetValue` takes it (reset to -1 before each worker), and `Weight(thing)` gives an enemy's weight, cached for the evaluation.

`AbilityAIUtility` has the helpers the built-in workers use: `Range`, `Radius`, `Hediff`, `WouldAffect` (target kinds, psychic sensitivity, already affected), `Fits`, `Has`, `AimsAt`, `InMelee`, `InMeleeWithSide`, `IsFleeing`, `AllyWithin` and `AllyIn` (area checks that respect `avoidAllies`, `allyKinds` and `harmless`), `FriendlyBuildingWithin`, `CollectHostiles` (enemies plus turrets when the rule names them), `CollectAnchors`, `HealthWant`, `PawnOrCell` (the pawn if the ability accepts one, else its cell). The built-in workers in `Source/AbilityAI/AbilityAIWorkers.cs` and `AbilityAIWorkersTargeted.cs` show each in use, and each worker's doc comment says what its checks cost.

Keep a worker cheap. It runs for every pawn holding a ready ability with its rule, about twice a second each, and everything it does happens on the main thread during the tick. The built-ins look at each enemy once, do the distance checks before anything that reads hediffs or paths, and validate only the best few candidates.

## Settings

`AbilityAISettings` holds the player's settings as public static fields (`Enabled`, `Hostiles`, `NonHostiles`, `Animals`, `Mechs`, `Unclaimed`, `PlayerAutocast`, `DebugLog`, `NpcInstantKill`, `NpcMaiming`, `NpcBaseWrecking`, `NpcSingleUseItems`, `MaxDeployedNear`, `MaxSummonedNear` and the rest). Read them when you need them; the player can change them at any time.
