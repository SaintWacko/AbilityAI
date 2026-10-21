# Handoff: let Bastle Framework use Ability AI (soft dependency)

From the Ability AI agent. Ability AI is a new mod (packageId `saintwacko.AbilityAI`), folder
`/Users/tirrella/Library/Application Support/CircinusDevSuite/workspace/Mods/4b7a8f666d21`. It is the ability AI that
used to live inside Entad Framework, now shared: per-ability "workers" that decide whether and where to cast
(AoE placement, interrupting enemy casts, rescuing downed allies, walking into range), plus player autocast from a
right-click on the ability gizmo. Read `Source/AbilityAI/AbilityAIWorkers.cs` and `AbilityAIWorkersTargeted.cs`
there for what each worker does and which `AbilityAIExtension` fields it reads.

The operator's decision: with Ability AI installed, Bastle's abilities are cast by Ability AI. Without it, Bastle
keeps its current behaviour (JobGiver_BastleAbilities + vanilla's simple casting). Nothing is released yet, so no
save compatibility is needed.

## The rule that shapes everything

Ability AI is a SOFT dependency. `BastleFramework.dll` must not contain any type whose base type, interface or field
type comes from `AbilityAI.dll`. If it does, RimWorld throws a ReflectionTypeLoadException when Ability AI is
missing and skips the whole Bastle assembly. Simplest: the main project does not reference AbilityAI.dll at all.
All integration goes into a folder that LoadFolders.xml loads only when Ability AI is active.

## What Ability AI already does for you

`Patch_Ability_AICanTargetNow_StandDown` (Ability AI) makes `Ability.AICanTargetNow` return false for any ability
Ability AI is deciding for that pawn. `JobGiver_BastleAbilities` steps 2 and 3 already gate on
`ability.AICanTargetNow`, so once Ability AI claims a bastle ability, Bastle's own giver skips it with no code
change, and when Ability AI is absent or the player turned that pawn kind off in its settings, Bastle's giver
works as before. Keep `aiCanUse` on in the base defs: it is the fallback.

Step 1 (per-trait `aiThinkTree`) is not affected unless the tree's job giver also checks `AICanTargetNow` (vanilla's
jump givers do). So if `BF_Leap` gets an Ability AI extension, the `BF_LeapAtTarget` tree stands down for it while
Ability AI handles it. That is the intended outcome; just be aware of it.

For the player's own pawns (tame bastles included) the stand-down is wider: with Ability AI's player autocast on,
vanilla and Bastle's giver are told no for every ability that has an Ability AI extension, whatever the autocast
mode. A tame bastle then casts such an ability only when the player set it to autocast. The tame-animal rules
(Ability AI build 0012 on):
- Any time: workers that aren't combat-only (Recovery, Rescue, Calm, Firefighting, or any worker a def sets to
  `<combat>Any</combat>`/`OutOfCombat`).
- While fleeing: Movement and Recovery as well.
- While fighting back (an attack job, a hostile target, or attacked or hurt in the last 10 seconds: defending itself
  or its master, or released on an enemy): every worker, the same set a drafted colonist gets. An enemy elsewhere on
  the map doesn't count. The attack it was making is queued to resume after the cast.
- The autocast menu is on the ability gizmo's right-click, and there is no second route to it. An animal can't be
  drafted, so its ability gizmo has to be visible undrafted: set `displayGizmoWhileUndrafted` on any bastle ability
  the player should be able to set to autocast. Without it the player never sees the button, so never sees the menu.
- "Autocast, may move" (the walk into range) is drafted only, so a tame bastle never gets it.

`AbilityAIDriver.OthersStandDown(pawn, ability)` is public if Bastle's compat code ever needs to ask directly
(it is what the stand-down uses; `Handles` is the narrower "Ability AI decides now").

## Steps

### 1. Restructure for load folders

Bastle has no LoadFolders.xml yet and builds to a root `Assemblies/`. The operator's standing preference is
LoadFolders + version folders with shared content at the top level, and Krafs.Rimworld.Ref instead of the local
game DLL hint paths in `Source/BastleFramework.csproj`. Entad Framework's layout is a working model
(`../EntadFramework`: `LoadFolders.xml`, `1.6/Source/EntadFramework.csproj`, `Source/EntadFramework.Build.csproj`).
At minimum:

```xml
<loadFolders>
  <v1.6>
    <li>/</li>
    <li>1.6</li>
    <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI</li>
    <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI/1.6</li>
  </v1.6>
</loadFolders>
```

Make sure the main DLL is loaded from exactly one place afterwards (a leftover root `Assemblies/BastleFramework.dll`
plus `1.6/Assemblies/BastleFramework.dll` loads it twice).

### 2. Compat assembly: `BastleFramework.AbilityAICompat`

```
Mods/AbilityAI/
  Patches/BF_AbilityAI.xml
  Source/BastleAbilityAICompat.cs
  1.6/Source/BastleFramework.AbilityAICompat.csproj  -> output ..\Assemblies\ (Mods/AbilityAI/1.6/Assemblies)
```

The csproj: same references as the main project, plus
- `<AssemblyName>BastleFramework.AbilityAICompat</AssemblyName>`, `<Compile Include="..\..\Source\*.cs" />`
- a ProjectReference to the main Bastle project with `<Private>false</Private>`
- Ability AI's DLL, not copied (it ships with Ability AI):
  ```xml
  <Reference Include="AbilityAI">
    <HintPath>$(MSBuildThisFileDirectory)..\..\..\..\..\4b7a8f666d21\1.6\Assemblies\AbilityAI.dll</HintPath>
    <Private>false</Private>
  </Reference>
  ```
  (Five levels up from `Mods/AbilityAI/1.6/Source/` is the workspace Mods folder; `4b7a8f666d21` is Ability AI's
  folder. Adjust if your layout differs.)
- Harmony is probably not needed in this assembly.

Wire it into whatever build entry point Source/ ends up with so build_mod builds it. The compat Assemblies folder
must contain only `BastleFramework.AbilityAICompat.dll`.

`Source/BastleAbilityAICompat.cs`, roughly:

```csharp
using AbilityAI;
using RimWorld;
using Verse;

namespace BastleFramework.AbilityAICompat
{
    // Loaded only when Ability AI is active. Claims the abilities a bastle's traits grant it; no opinion on anything
    // else (left to Ability AI's "abilities no mod claims" setting).
    public class BastleAbilityAIHook : IAbilityAIHook
    {
        public static readonly BastleAbilityAIHook Instance = new BastleAbilityAIHook();

        public AbilityAIVote Vote(Pawn pawn, Ability ability)
        {
            var bastle = BastleUtility.GetBastle(pawn);
            if (bastle == null) return AbilityAIVote.NoOpinion;
            var traits = bastle.TraitsForReading;
            for (int i = 0; i < traits.Count; i++)
                if (traits[i].Abilities != null && traits[i].Abilities.Contains(ability.def)) return AbilityAIVote.Claim;
            return AbilityAIVote.NoOpinion;
        }

        public void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest) { }
    }

    [StaticConstructorOnStartup]
    internal static class BastleAbilityAIStartup
    {
        static BastleAbilityAIStartup() => AbilityAIHooks.Register(BastleAbilityAIHook.Instance);
    }
}
```

Vote is asked on the main thread (think tree), so `GetBastle` is fine; `PeekBastle` is internal, and the compat
assembly can't see it without InternalsVisibleTo. Vote is called for every ready ability with an extension on
every evaluated pawn (at most once per 30 ticks per pawn), so keep it to the registry lookup and the trait loop.

Trait reveal needs nothing new: `Patch_AbilityStartedCasting` and Notify_PawnUsedVerb fire for Ability AI's casts
the same as for Bastle's own.

### 3. Extensions for Bastle's abilities

`Mods/AbilityAI/Patches/BF_AbilityAI.xml`, one `PatchOperationAddModExtension` per ability, with
`Class="AbilityAI.AbilityAIExtension"` and `<workerClass>AbilityAI.AbilityAIWorker_...</workerClass>`.

Three of the five defs are `MayRequire` a DLC (FireBreath and AcidSpit: Biotech; DreadHowl: Odyssey). A patch
whose xpath finds nothing logs a patch failure, so put `MayRequire="Ludeon.RimWorld.Biotech"` (or Odyssey) on those
`<Operation>` elements, or wrap them in `PatchOperationFindMod`. Do not use `<success>Always</success>` to hide it.

Starting points, to check against each worker's notes in Ability AI's source:
- BF_QuillVolley: `AbilityAIWorker_TargetHostile`.
- BF_AcidSpit: `AbilityAIWorker_AoEHostile` with a small `radius` (it hits 9 cells) and `avoidAllies`.
- BF_FireBreath: `AbilityAIWorker_AoEHostile` with `avoidAllies` (the line is about 3 wide at the end) and a
  `radius` around 1.5; fire near allies is the main risk.
- BF_Leap: `AbilityAIWorker_Movement` (gap close for melee, escape when hurt). Decide whether the trait's
  `BF_LeapAtTarget` tree or the Movement worker should own it.
- BF_DreadHowl: `AbilityAIWorker_AoEHostile` (self-cast area, Ability_EffectRadius is read automatically) with
  `minTargets` 2 or 3 so it isn't wasted on one enemy.
Content mods that add bastle traits with their own abilities can add extensions the same way.

### 4. About.xml

Add `<li>saintwacko.AbilityAI</li>` to `<loadAfter>` only, never to `<modDependencies>`. Naming a mod that isn't
installed in loadAfter is harmless.

### 5. Check

- build_mod green.
- With Ability AI (`run_test_cycle` with `companionMods: ["saintwacko.AbilityAI"]`): spawn a hostile bastle with a
  quill or acid trait; with Ability AI's debug log on (dev mode, its settings page) the log shows
  `[Ability AI] ... casts ...` lines and Bastle's own giver no longer casts those abilities.
- Without Ability AI: no errors, no "Could not find type AbilityAI..." in the log, bastles cast as they do today.

## Ability AI public API

- `IAbilityAIHook` { `AbilityAIVote Vote(Pawn, Ability)`; `void Notify_AICast(Pawn, Ability, LocalTargetInfo target, LocalTargetInfo dest)` }
- `AbilityAIVote` { NoOpinion, Claim, Veto }
- `AbilityAIHooks.Register(IAbilityAIHook)`
- `AbilityAIInfo.Register<T>(Func<T,float> radius, Func<T,HediffDef> hediff = null, Func<T,bool> stuns = null) where T : AbilityCompProperties`
  for a Bastle comp of its own whose area/hediff/stun the workers can't read (vanilla comps are read already).
- `AbilityAIDriver.Handles(Pawn, Ability)`
- `AbilityAIExtension` and the `AbilityAIWorker_*` classes.

If anything doesn't match, or Bastle needs more from Ability AI's API, write it down and the operator will bring it
back to the Ability AI agent.
