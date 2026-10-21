# Handoff: move Entad Framework onto Ability AI (soft dependency)

From the Ability AI agent. Ability AI is now its own mod (packageId `saintwacko.AbilityAI`), folder
`/Users/tirrella/Library/Application Support/CircinusDevSuite/workspace/Mods/4b7a8f666d21`. It holds the code that
used to be Entad's `Source/AbilityAI/`, renamed:

- namespace `EntadFramework.AbilityAI` -> `AbilityAI`
- keyed prefix `EF_AI_` -> `AAI_`
- its own settings page (Entad no longer draws or saves any AI setting)
- `Patches/AbilityAI_Royalty.xml` moved there too (Royalty psycast rules, still gated on Royalty)
- builds `1.6/Assemblies/AbilityAI.dll` and `1.7/Assemblies/AbilityAI.dll` (assembly name `AbilityAI`, version 1.0.0.0)

No save or settings compatibility is needed (nothing released yet).

## The rule that shapes everything

Ability AI is a SOFT dependency. Entad's main `EntadFramework.dll` must not contain any type whose base type,
interface or field type comes from `AbilityAI.dll`. If it does, RimWorld throws a ReflectionTypeLoadException when
Ability AI is missing and skips the whole Entad assembly. That is why `CompProperties_AbilityEntadPulse :
IAbilityAIInfo` and `EntadAbilityAIHook : IAbilityAIHook` have to leave the main assembly. Simplest: the main
project does not reference AbilityAI.dll at all.

All Ability AI integration goes into a folder that LoadFolders.xml loads only when Ability AI is active.

Without Ability AI, entad abilities fall back to vanilla's own casting (the operator's decision): turn `aiCanUse` on
for them. Ability AI's `Patch_Ability_AICanTargetNow_StandDown` makes vanilla's fight AI skip any ability Ability AI
is handling for that pawn, so leaving `aiCanUse` on does not make both AIs cast it.

## Steps

### 1. Remove the moved code from Entad

- Delete `Source/AbilityAI/` (all four files).
- Delete `Languages/English/Keyed/AbilityAI.xml` and `Patches/AbilityAI_Royalty.xml`.
- In `1.6/Source/EntadFramework.csproj` and `1.7/Source/EntadFramework.csproj`, remove
  `<Compile Include="..\..\Source\AbilityAI\*.cs" />` and its comment.
- `Source/EntadSettings.cs`: remove `AbilityAI.AbilityAISettings.Reset()`, `AbilityAI.AbilityAISettings.ExposeData()`,
  `AbilityAI.AbilityAIHooks.Register(...)` in the `EntadMod` constructor, and the `Tab.AI` tab (enum value,
  TabRecord, the DoSettings branch). Drop the `EF_Tab_AI` key from `Keyed/EntadFramework.xml` unless you keep a tab
  that points players at Ability AI's own settings.
- Move `Source/EntadAbilityAI.cs` out of the main project (it becomes the compat code in step 3). The
  `CompProperties_AbilityEntadPulse` partial there goes away; `partial` on the main declaration in
  `EntadAbilityEffects.cs` can stay or go, and update its "IAbilityAIInfo half is in EntadAbilityAI.cs" comment.

### 2. Defs: fallback in the base defs, Ability AI rules in a patch

`Defs/EntadAbilities.xml` has inline `<li Class="EntadFramework.AbilityAI.AbilityAIExtension">` blocks on
Entad_SecondWind, Entad_Sprint, Entad_BattleCry, Entad_Blink, Entad_Smokescreen, Entad_Flashbang (two extensions)
and Entad_Artillery. Those class names would fail to resolve when Ability AI is missing, so:

- Remove every AbilityAIExtension `<li>` from `Defs/EntadAbilities.xml` (and the header comment about them). If a
  def's `modExtensions` is left empty, remove it.
- Give each of those abilities the vanilla fallback: `<aiCanUse>true</aiCanUse>`, and `<ai_IsOffensive>true</ai_IsOffensive>`
  on the ones vanilla should fire at an enemy (Flashbang, Artillery). Vanilla's fight AI
  (`JobGiver_AIFightEnemy.GetAbilityJob`) only considers `ai_IsOffensive` abilities: it casts them at the current
  enemy if the verb can hit, else at the first valid thing in range when `ai_SearchAOEForTargets` is set, else on
  the caster when the verb can target self. Non-offensive self buffs (Second Wind, Battle Cry, Sprint) get no
  vanilla fight casting at all; decide per ability whether that is acceptable or whether `ai_IsOffensive` +
  `canTargetSelf` is a better crude fallback. Check `Entad_ArtilleryCarrier` too.
- Add `Mods/AbilityAI/Patches/EntadAbilityAI.xml` putting the same extensions back with
  `PatchOperationAddModExtension`, namespaces renamed:
  `Class="AbilityAI.AbilityAIExtension"`, `<workerClass>AbilityAI.AbilityAIWorker_Recovery</workerClass>` etc.
  Same field values as today. Copy them from the current defs before deleting.
- Search Defs/ and Patches/ for any other `EntadFramework.AbilityAI.` reference (furniture abilities, trait defs)
  and treat them the same way.

### 3. Compat assembly: `EntadFramework.AbilityAICompat`

Layout (shared C# at the top, per-version projects, as the main assembly does):

```
Mods/AbilityAI/
  Patches/EntadAbilityAI.xml
  Source/EntadAbilityAICompat.cs
  1.6/Source/EntadFramework.AbilityAICompat.csproj  -> output ..\Assemblies\ (Mods/AbilityAI/1.6/Assemblies)
  1.7/Source/EntadFramework.AbilityAICompat.csproj  -> output ..\Assemblies\ (Mods/AbilityAI/1.7/Assemblies)
```

Each csproj is a copy of the matching main csproj (Krafs refs, Lib.Harmony 2.4.1 compile-only if it patches
anything; it probably doesn't need Harmony), with:

- `<AssemblyName>EntadFramework.AbilityAICompat</AssemblyName>`, `<RootNamespace>EntadFramework.AbilityAICompat</RootNamespace>`
- `<Compile Include="..\..\Source\*.cs" />`
- a ProjectReference to the main project of the same version, NOT copied:
  ```xml
  <ProjectReference Include="..\..\..\..\1.6\Source\EntadFramework.csproj">
    <Private>false</Private>
  </ProjectReference>
  ```
- a reference to Ability AI's built DLL, NOT copied (it ships with Ability AI):
  ```xml
  <PropertyGroup>
    <AbilityAIDir>$(MSBuildThisFileDirectory)..\..\..\..\..\4b7a8f666d21\</AbilityAIDir>
  </PropertyGroup>
  <Reference Include="AbilityAI">
    <HintPath>$(AbilityAIDir)1.6\Assemblies\AbilityAI.dll</HintPath>
    <Private>false</Private>
  </Reference>
  ```
  (`4b7a8f666d21` is Ability AI's workspace folder; use `1.7\Assemblies` in the 1.7 project.) Ability AI must be
  built first; its DLLs are already there.

Add both compat projects to `Source/EntadFramework.Build.csproj` as ProjectReferences with
`ReferenceOutputAssembly=false`, so build_mod builds all four. Check the Assemblies output contains only
`EntadFramework.AbilityAICompat.dll`: no `EntadFramework.dll`, no `AbilityAI.dll`, no `0Harmony.dll`.

`Source/EntadAbilityAICompat.cs` (this is the old EntadAbilityAI.cs plus the registrations):

```csharp
using AbilityAI;
using RimWorld;
using Verse;

namespace EntadFramework.AbilityAICompat
{
    // Loaded only when Ability AI is active (LoadFolders IfModActive). Claim: gear the pawn wears or wields grants the
    // ability through an active trait (EntadAbilities.Grants checks binding). Anything else: no opinion, left to
    // Ability AI's "abilities no mod claims" setting.
    public class EntadAbilityAIHook : IAbilityAIHook
    {
        public static readonly EntadAbilityAIHook Instance = new EntadAbilityAIHook();

        public AbilityAIVote Vote(Pawn pawn, Ability ability) =>
            EntadAbilities.Grants(pawn, ability.def, out _) ? AbilityAIVote.Claim : AbilityAIVote.NoOpinion;

        public void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest) { }
    }

    [StaticConstructorOnStartup]
    internal static class EntadAbilityAIStartup
    {
        static EntadAbilityAIStartup()
        {
            AbilityAIHooks.Register(EntadAbilityAIHook.Instance);
            AbilityAIInfo.Register<CompProperties_AbilityEntadPulse>(p => p.radius, p => p.hediff, p => p.stunSeconds > 0f);
        }
    }
}
```

`AbilityAIInfo` caches its answer per comp properties object. If Entad ever changes a pulse comp's
radius/hediff/stunSeconds after startup (per item rolls written into the def's comp), tell the Ability AI agent: the
cache would need a way to be cleared.

### 4. LoadFolders.xml and About.xml

```xml
<v1.6>
  <li>/</li>
  <li>1.6</li>
  <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI</li>
  <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI/1.6</li>
</v1.6>
```

and the same for v1.7 with `Mods/AbilityAI/1.7`. Source/ folders under Mods/AbilityAI are not shipped by the
publish allowlist; Assemblies and Patches are.

About.xml: add `<li>saintwacko.AbilityAI</li>` to `<loadAfter>` only. Do NOT add it to `<modDependencies>`: it is
optional. Naming a mod that isn't installed in loadAfter is harmless. Optionally mention Ability AI in the
description so players know it is what makes NPCs use entad abilities well.

### 5. Check

- build_mod green for all four projects.
- Test with Ability AI present: raiders with entad gear use abilities with judgement, right-click autocast menu on
  colonists' entad ability gizmos, Ability AI's settings page appears under its own name, no `EF_AI_` keys left.
  run_test_cycle with `companionMods: ["saintwacko.AbilityAI"]`.
- Test without Ability AI: Entad loads with no errors, no "Could not find type AbilityAI..." in the log, and
  offensive entad abilities get vanilla's simple casting.
- grep the mod for `AbilityAI` afterwards: the only hits should be under `Mods/AbilityAI/`, LoadFolders.xml and
  About.xml.

## Ability AI public API (what the compat code may use)

- `IAbilityAIHook` { `AbilityAIVote Vote(Pawn, Ability)`; `void Notify_AICast(Pawn, Ability, LocalTargetInfo target, LocalTargetInfo dest)` }
- `AbilityAIVote` { NoOpinion, Claim, Veto }
- `AbilityAIHooks.Register(IAbilityAIHook)`
- `AbilityAIInfo.Register<T>(Func<T,float> radius, Func<T,HediffDef> hediff = null, Func<T,bool> stuns = null) where T : AbilityCompProperties`
- `AbilityAIDriver.Handles(Pawn, Ability)`: whether Ability AI is the one deciding for this pawn and ability now.
- `AbilityAIDriver.OthersStandDown(Pawn, Ability)`: what the vanilla stand-down uses, and what a host fallback AI
  should ask to step aside: Handles, plus every player-faction pawn (not prisoners) with an ability that has a
  working extension, whatever its autocast mode or the hooks' votes (added at Entad's request).
- The autocast menu and badge now ask the hooks: a Veto for a player pawn hides the menu and the badge.
- Player autocast modes (build 0012 on): Off, "Autocast in place" (Stationary), and "Autocast, may move" (Full,
  offered only for abilities with an `approachDistance`: a drafted ranged pawn walks into range, casts, walks back).
  Undrafted player pawns cast by situation: any time the non-combat workers (Recovery, Rescue, Calm,
  Firefighting); while fleeing also Movement; while fighting back with the hostility response on Attack (attacking,
  or hit in the last 10 seconds) every worker. `Handles` for an undrafted player pawn is true when the ability has a
  worker the pawn could ever use undrafted, not only right now.
- Psycast limits (heat cap, psyfocus floor, per pawn) apply to real `Psycast`s only. Entad's psycast copies are
  plain Abilities and are not limited by them.
- `AbilityAIExtension` (DefModExtension) and the workers `AbilityAIWorker_*`: same fields and names as before, new
  namespace.

If something here doesn't match what you find, or Entad needs more from Ability AI's API, write it down and the
operator will bring it back to the Ability AI agent.
