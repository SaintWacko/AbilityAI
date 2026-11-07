# Writing Ability AI rules

This guide covers how to make Ability AI cast an ability: which worker to pick, what its fields do, how to change a rule for other factions, and how to add rules to worn gear. Everything here is XML. The C# side (claiming abilities, plugging in another ability system, writing your own worker) is in [csharp-api.md](csharp-api.md).

The [rule builder](https://saintwacko.github.io/AbilityAI/rule-builder.html) writes the XML for you: pick a worker, fill in its fields and copy the result. This guide explains what the fields mean.

The rules Ability AI ships for Royalty, Biotech, Anomaly, Odyssey, Vanilla Psycasts Expanded and its addons, and worn gear are all in `Patches/`, and they are the best examples of each worker. Each one has a comment saying why it is set up the way it is.

## How it works

A rule is an `AbilityAI.AbilityAIExtension` in an ability def's `modExtensions`. It names a worker class, which is the part that looks at the map and decides whether there is something worth casting at. An ability with no rule is never touched by Ability AI.

About twice a second, for each pawn it covers that has a ready ability with a rule, Ability AI tries the rules of all of that pawn's ready abilities together, highest `priority` first, and casts the first one that finds a target. An ability can have several rules, one per situation: Royalty's Stun has an Interrupt rule (priority 9, for an enemy aiming a grenade or psycast) and a Target hostile rule (priority 3, for the worst threat otherwise).

Every check the game makes for a player's cast still applies: cooldown, charges, psyfocus and heat, each effect comp's `Valid` and `AICanTargetNow`, the verb's range and line of sight.

Who uses a rule:
- **Other factions** (raiders, visitors, allied soldiers, animals, mechs) cast with it, each kind behind a setting. A player setting, "Also use abilities no mod claims" (on by default), lets them use any ability with a rule; with it off, only abilities a mod claims in C#.
- **The player's pawns** get an autocast menu on the ability's gizmo (right-click): "Don't autocast", "Stationary Autocast" (cast from where it stands) and "Full Autocast" (walk into range, cast, walk back). Full is only offered for a rule with `approachDistance` on a pawn that can be drafted; touch abilities, abilities that move the caster and the rest get a plain "Autocast". Rules that aren't combat-only also run while the pawn is undrafted.

While Ability AI handles an ability, vanilla's own NPC ability AI (`aiCanUse`) and VEF's autocast are stood down for that pawn and ability, so two systems never cast it. You can leave `aiCanUse` on as a fallback for players who don't have Ability AI.

## Setup

Rules can go on:
- a vanilla `AbilityDef` (abilities, psycasts, gene abilities, mech abilities);
- a `VEF.Abilities.AbilityDef` (Vanilla Expanded Framework abilities and VPE psycasts). Ability AI drives VEF abilities that target a pawn, a thing, a cell, the caster or nothing, plus VPE's thing-then-cell Skip. Abilities with several targets, world or tile targets, random targets or a confirmation dialog are left to VEF, with a warning in the log;
- the `ThingDef` of worn gear that casts through a verb (smokepop belts, jump packs, turret packs, grenade belts). See [Worn gear](#worn-gear).

### Your own abilities, Ability AI required

Add Ability AI to `<modDependencies>` and `<loadAfter>` in About.xml:

```xml
<modDependencies>
  <li>
    <packageId>saintwacko.AbilityAI</packageId>
    <displayName>Ability AI</displayName>
    <steamWorkshopUrl>steam://url/CommunityFilePage/3831009845</steamWorkshopUrl>
  </li>
</modDependencies>
<loadAfter>
  <li>saintwacko.AbilityAI</li>
</loadAfter>
```

Then put the rules straight into your def:

```xml
<AbilityDef>
  <defName>MyMod_StunBolt</defName>
  ...
  <modExtensions>
    <li Class="AbilityAI.AbilityAIExtension">
      <workerClass>AbilityAI.AbilityAIWorker_TargetHostile</workerClass>
      <priority>3</priority>
    </li>
  </modExtensions>
</AbilityDef>
```

### Your own abilities, Ability AI optional

A def that names `AbilityAI.AbilityAIExtension` logs an error when Ability AI isn't loaded, so the rules have to be added by a patch that only runs when it is. Either put the patch in a folder that only loads with Ability AI:

```xml
<!-- LoadFolders.xml -->
<loadFolders>
  <v1.6>
    <li>/</li>
    <li IfModActive="saintwacko.AbilityAI">Mods/AbilityAI</li>
  </v1.6>
</loadFolders>
```

with the patch in `Mods/AbilityAI/Patches/`, or wrap the patch in a `PatchOperationFindMod` on `Ability AI` (the rule builder's default). Keep `saintwacko.AbilityAI` in `<loadAfter>` either way.

```xml
<Patch>
  <Operation Class="PatchOperationFindMod">
    <mods>
      <li>Ability AI</li>
    </mods>
    <match Class="PatchOperationAddModExtension">
      <xpath>Defs/AbilityDef[defName="MyMod_StunBolt"]</xpath>
      <value>
        <li Class="AbilityAI.AbilityAIExtension">
          <workerClass>AbilityAI.AbilityAIWorker_TargetHostile</workerClass>
          <priority>3</priority>
        </li>
      </value>
    </match>
  </Operation>
</Patch>
```

### Someone else's abilities

The same patch, aimed at their def. Wrap the `PatchOperationAddModExtension` in a `PatchOperationConditional` on the same xpath, so the patch skips quietly when their mod isn't loaded or the def is renamed, instead of logging a failure:

```xml
<match Class="PatchOperationConditional">
  <xpath>Defs/VEF.Abilities.AbilityDef[defName="Their_Fireball"]</xpath>
  <match Class="PatchOperationAddModExtension">
    <xpath>Defs/VEF.Abilities.AbilityDef[defName="Their_Fireball"]</xpath>
    <value>
      ...
    </value>
  </match>
</match>
```

Give each ability its own operation (one `PatchOperationConditional` each, inside a `PatchOperationSequence` if you like), so a renamed ability loses only its own rules.

## What Ability AI reads by itself

Workers read the ability's area, the hediff it gives, and whether it stuns from its effect comps, once per def. You only need `radius` or `hediff` in a rule when these can't be read or are wrong for the AI's purpose.

Vanilla comps that are read: `CompProperties_AbilityGiveHediff` (the hediff), `CompProperties_AbilityGiveMentalState` (mental state, psychic), `CompProperties_AbilityEffect` stun fields, `CompProperties_AbilityExplosion`, `CompProperties_AbilityFireBurst` and `CompProperties_AbilityFirefoampop` (radius), `CompProperties_AbilityLaunchProjectile` (the projectile's explosion radius), `CompProperties_AbilitySmokepop`, `CompProperties_AbilitySpawn`, `CompProperties_AbilityWallraise`, `CompProperties_AbilityFireSpew` and `CompProperties_AbilitySprayLiquid` (cone length and width). With none of those, the radius is the AbilityDef's own effect radius. The verb's `targetParams` decide whether it is cast on the caster.

For VEF abilities the range and radius are the def's own, for the caster; the VPE compat reads VPE's own extensions too. For gear, from the verb's projectile (explosion radius, smoke, stun), the smokepop and broadshield packs, `CompExplosive` and gas releases.

Set `radius` when the area comes from C# or a comp of your own. A mod with its own comp class can also tell Ability AI how to read it in C# (`AbilityAIInfo.Register`, see csharp-api.md).

## A rule's common fields

These work on any rule.

| Field | Default | What it does |
|---|---|---|
| `workerClass` | | Required. The worker, `AbilityAI.AbilityAIWorker_<Name>` (see Workers below). |
| `priority` | 0 | Higher is tried first, across all of the pawn's ready abilities. The shipped rules use 1 to 9: interrupts and rescues high, damage in the middle, buffs low. |
| `combat` | the worker's | `InCombat`, `OutOfCombat` or `Any`. In combat means the pawn has an enemy target, attacked or was hurt in the last 10 seconds, or is drafted with an enemy on the map. A rule that isn't `InCombat` also runs for the player's undrafted pawns. |
| `enabled` | true | false turns the rule off. Meant for an `npc` or `friendlyNpc` block. |
| `exclusive` | the worker's | Leave a target alone while a faction-mate is casting this ability at it or walking into range to. On by default for every worker that doesn't target enemies, and for Interrupt and Skip; set it true on a hostile rule whose effect doesn't stack (a stun keeps the longer of two). |
| `reserveCharges` | 0 | Charges the AI never spends, for abilities with charges. |
| `minGapSeconds` | 0 | Seconds after an AI cast of this ability before the AI considers it again, on top of the cooldown. |
| `requires` | None | `Melee` or `Ranged`: only while the caster fights that way. `Melee` means no ranged weapon. |
| `maxLight` | 1 | Only while the light on the caster's cell is at most this (0 to 1). |
| `unlessCasterHas` | | A HediffDef: not while the caster has it, for an aura or channel a second cast would only restart. |
| `currentMapOnly` | false | Only on the map the player is looking at, for an ability whose code lands its effect on `Find.CurrentMap`. |
| `startsFires` | false | The ability sets fires, so an enemy holding it counts as a fire threat for `fireDanger` rules. |
| `npcGate` | | `InstantKill`, `BaseWrecking`: see [Other factions](#other-factions). |
| `heatVent` | false | A psycast that vents neural heat: see [Heat vents](#heat-vents). |
| `npc`, `friendlyNpc` | | Changes for other factions: see [Other factions](#other-factions). |

Fields that several workers share:

| Field | Default | What it does |
|---|---|---|
| `radius` | read | The area the effect reaches around its target. Leave it out when the ability's comps state it. Each worker that reads it says what for. |
| `minTargets` | 1 | How many pawns the cast must affect: enemies for hostile workers, allies for AoE friendly. |
| `minRange`, `maxRange` | 0, the range | Distance band from the caster to the target. |
| `healthThreshold` | 0.5 | Summary health (0 to 1) below which a pawn counts as badly hurt. A caster below it also stops walking toward targets. |
| `hediff` | read | Skip pawns that already have this HediffDef. Leave it out when the ability gives it through `CompProperties_AbilityGiveHediff`. |
| `targetKinds` | every pawn | Which pawns the rule may target or count: `Humanlike`, `Animal`, `Mechanoid`, `Other` (entities and the rest). Also `Turret` (hostile area workers and Target hostile), `Shielded` (a raised shield belt) and `Psycaster` (has a psylink), which aren't in the default and must be named. Write several as `Humanlike, Animal`. |
| `harmless` | false | Hostile rules: the ability disables without lasting harm (a stun, a slow, a blind). With the player's setting at its default, such a rule may also be used on an ally in a hostile mental break, and `avoidAllies` doesn't count downed faction-mates. |
| `skipAffected` | true | Hostile rules: leave out pawns the cast would be wasted on (already stunned by a stunning ability, already carrying its hediff, already in a mental state from one that gives one, no psychic sensitivity for a psycast). Turn it off when the damage alone makes the cast worth it. |
| `avoidAllies` | false | Keep the caster's side out of the area. What "the area" is depends on the worker. |
| `allyKinds` | every pawn | Which of the caster's side `avoidAllies` keeps clear of: only those the effect would harm. An EMP: `Mechanoid, Turret, Shielded`. |
| `avoidBuildings` | false | Not with a building of the caster's side, or of a faction not hostile to it, in the area. For fire and blasts, so visitors don't burn the player's base. |
| `approachDistance` | 0 | How far past the ability's range a ranged caster walks to cast it in combat, when nothing is in range now. Read by AoE hostile, AoE hostile around the caster, Cone and Beam hostile, Target hostile, and self-cast AoE friendly. It is also what offers Full Autocast to the player. |

## Workers

The class name is `AbilityAI.AbilityAIWorker_` plus the name in each heading. "Combat" is the worker's default for `combat`. The examples are shipped rules; the file each comes from has more.

### At enemies

#### AoEHostile

An area cast at the spot that catches the most enemies (Flashbang, Berserk pulse, Artillery). Each enemy in range is a candidate centre, scored by the enemies within `radius` of it that the ability would affect. The best five are checked against the verb (line of sight). Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | Enemies the area must catch. |
| `avoidAllies` | false | No centre with anyone of the caster's side (of `allyKinds`) inside the radius. |
| `avoidSelf` | false | No centre within the radius of the caster, for an area that hits its caster too. Follows the radius when a stat scales it, unlike `minRange`. |
| `avoidBuildings` | false | No centre with a friendly building inside. |
| `targetKinds` | every pawn | With `Turret`, hostile turrets count and can be the centre (Haywire). |
| `anchorThings` | | A list of ThingDefs: centre only on these things, never on a pawn or cell (Runesmith's Invokes on a rune circle). A thing with a faction counts only when it is the caster's. The radius is then what the effect reaches around the thing. |
| `anchorCorpses` | | Kinds of corpse to centre on instead (Corpse Explosion): corpses of pawns hostile to the caster or of no faction. |
| `minCorpses` | 0 | With `anchorCorpses`: a centre needs this many of those corpses in the radius and is scored by them. `minTargets` may then be 0. |
| `waitForGather` | false | Anchored only: hold the cast for up to 5 seconds while more enemies are walking into the area, and skip a spot an ally is walking into. |

An anchored rule validates up to 20 centres instead of five.
| `coveredBy`, `coveredFraction` | , 0.5 | Not where `coveredFraction` of the radius already holds one of these ThingDefs, for an ability that fills an area with something that lasts (Bramble Maze). |

```xml
<!-- VPE's Haywire (Patches/AbilityAI_VPE.xml): enemy mechs and turrets, none of the caster's own -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_AoEHostile</workerClass>
  <priority>3</priority>
  <targetKinds>Mechanoid, Turret</targetKinds>
  <avoidAllies>true</avoidAllies>
  <allyKinds>Mechanoid, Turret</allyKinds>
  <approachDistance>6</approachDistance>
  <exclusive>true</exclusive>
</li>
```

#### AoEHostileSelf

An area centred on the caster (psychic drone, fire burst, an animal's roar). Counts what the ability would affect within the radius of the caster and casts with at least `minTargets`. Not while the caster already has the ability's hediff. With `approachDistance`, a ranged caster walks to a cell that catches more. Combat: InCombat.

Fields: `minTargets`, `avoidAllies` (nobody else of its side inside), `avoidBuildings`, `targetKinds` (`Turret` counts turrets), `anchorCorpses` with `minCorpses` (a pack that raises corpses around its wearer).

```xml
<!-- Biotech's fire burst (Patches/AbilityAI_Biotech.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_AoEHostileSelf</workerClass>
  <priority>4</priority>
  <minTargets>2</minTargets>
  <avoidAllies>true</avoidAllies>
  <avoidBuildings>true</avoidBuildings>
</li>
```

#### ConeHostile

A cone or line from the caster (fire spew, a breath). The cone always reaches its full length, whatever the distance to the aim. Each affectable enemy in reach is an aim, scored by the enemies inside the shape. Any of the caster's side inside the shape always drops the aim (walls are ignored, so an ally behind one still blocks). Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `coneWidth` | read, else 1 | Width at the far end. Read from the fire spew and spray comps. |
| `maxRange` | the length | How far off an aim may stand. The length is the effect's cone length, else the ability's range. |
| `minTargets` | 1 | Enemies inside the shape. |
| `avoidAllies` with `radius` | | Also no aim with an ally within `radius` of it: someone fighting the target may step into the cone before the effect is over. |
| `avoidBuildings` | false | No aim with a friendly building inside the shape. |

#### BeamHostile

A straight beam from the caster to the aimed cell, held for a while (VPE's Frost Ray). Cone hostile's rules with the same width all along: an ally anywhere on the way drops the aim. `coneWidth` is the width kept clear of allies (at least 1.5); make it wider than the beam, since a pawn crossing it later is hit too. Combat: InCombat.

```xml
<!-- VPE's Frost Ray (Patches/AbilityAI_VPE.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_BeamHostile</workerClass>
  <priority>3</priority>
  <approachDistance>4</approachDistance>
  <avoidAllies>true</avoidAllies>
  <radius>3</radius>
  <coneWidth>2.5</coneWidth>
</li>
```

#### TargetHostile

One enemy (Stun, Burden, Berserk). Enemies in range that the ability would affect, scored by threat: +1 for the caster's own target, +1 for one aiming at the caster, times its combat power, divided by distance. The best five are validated. Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | Above 1: the enemy needs that many of its side (itself included) within `radius`. |
| `radius` | 5 | How near the target counts for `minTargets`, `preferGrouped`, `avoidAllies` and `avoidBuildings`. |
| `preferGrouped` | false | Multiply the score by the enemies around the target. |
| `avoidAllies` | false | Skip enemies with anyone of the caster's side within `radius` (a berserk enemy attacks whoever is nearest). |
| `avoidBuildings` | false | Skip enemies with a friendly building within `radius`, for a fire or blast that spreads. |
| `meleeOnly` | false | Only enemies in melee with the caster or an ally. |
| `armedOnly` | false | Only enemies holding a weapon. |
| `inMentalState` | false | Only enemies in a mental state (calming a manhunter). |
| `permanentEnemiesOnly` | false | Not a pawn whose home faction is humanlike and not permanently hostile: killing one costs goodwill even at war. |
| `targetKinds` | every pawn | With `Turret`, enemy turrets that are a threat now are candidates too. |
| `whileHunting` | false | Also while the caster hunts for food, at its prey. |

```xml
<!-- Royalty's Stun (Patches/AbilityAI_Royalty.xml): the second of its two rules -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_TargetHostile</workerClass>
  <priority>3</priority>
  <approachDistance>8</approachDistance>
  <harmless>true</harmless>
  <exclusive>true</exclusive>
</li>
```

#### Interrupt

An enemy aiming something dangerous: a hostile ability or psycast, or a weapon with an explosive projectile (grenades, launchers). Only when more of its aim is left than this ability's own warmup. A stun pauses the aim, a mental state or a teleport breaks it. Area abilities centre on the enemy. Nearest first; exclusive by default. Usually a high-priority rule beside a Target hostile one. Combat: InCombat.

`minAimSeconds` (default 0): also any aim with at least this many seconds left.

#### Deploy

Throw out something that fights on its own (Anomaly's turret pack, drone packs) to a cell from which it sees the most enemies, on the caster's side of the fight. The player's "Turret packs" setting limits how many may stand near each caster (default 1). Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | Enemies the deployed thing must see from the cell. |
| `minRange` | 2 | The nearest ring of cells tried around the caster (never under 2). |
| `radius` | the turret's range, else 20 | How far the deployed thing reaches. |
| `minGapDistance` | 6 | No enemy this close to the cell. |
| `deployedThing` | read | What the cast puts down, when Ability AI can't read it from the verb or projectile. For a pawn, its race's ThingDef. |

### For allies and the caster

#### AoEFriendly

An area buff over allies (Battle Cry). Self-cast abilities centre on the caster, targeted ones on the ally that gathers the most. Counts allies (the caster included) within the radius who don't have the ability's hediff yet, and casts when at least `minTargets` would gain it. Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | Allies who would gain it. |
| `approachDistance` | 0 | Self-cast only: walk up to this far to a cell that covers more allies. |
| `health` | | Only allies with one of these conditions count (see Recovery). |
| `avoidEnemies` | false | No centre with an enemy inside the radius, for an effect that reaches whoever stands there. |
| `anchorThings`, `anchorCorpses` | | Targeted only: centre on these things instead of on allies. |

#### SelfBuff

Cast on the caster while an enemy is within `maxRange` (default 30) and the caster doesn't have the ability's hediff. Combat: InCombat.

With `auraKinds`: the buff is an aura that hits whatever comes near its bearer. Cast only while at least `auraMin` enemies of those kinds are within `radius` (required) of the caster, and with `avoidAllies` none of its side of `allyKinds` besides itself.

#### TargetFriendly

A buff on one ally or the caster (Focus). Allies in range without the ability's hediff; by default only allies fighting now. Scored by pain and fire danger, the caster first, nearer first. Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `minPain` | 0 | Only allies in at least this much pain (0 to 1), fighting or not. |
| `fireDanger` | false | Only allies in fire danger: burning, a fire within 2.5 tiles, or within reach of an enemy that can set fires (fire weapons, or an ability with `startsFires`). |
| `auraKinds`, `auraMin`, `radius` | | As for Self buff, around the ally. |

```xml
<!-- Royalty's Painblock (Patches/AbilityAI_Royalty.xml): get a downed ally up, or ease one in pain -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Rescue</workerClass>
  <priority>7</priority>
  <painShockOnly>true</painShockOnly>
</li>
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_TargetFriendly</workerClass>
  <priority>3</priority>
  <minPain>0.4</minPain>
</li>
```

#### Defensive

Protect the caster or an ally in trouble (Invisibility, a shield). In trouble means under `healthThreshold` with an enemy within 15 tiles, hurt in melee in the last 10 seconds, or aimed at by `minThreats` ranged enemies (being shot recently counts as one). The caster first, then whoever is worst hurt. Combat: InCombat.

`rangedOnly`: only the shooters count, for protection that only stops projectiles (VPE's Overshield).

#### Rescue

Help a downed ally (Painblock). Downed allies in range without the ability's hediff, nearest first. Combat: Any.

`painShockOnly`: only allies downed by pain alone. `minBleedRate`: only allies bleeding at least this much per day (1 = 100%), for an ability that only stops bleeding.

#### Recovery

Get the caster or an ally back to strength. Two kinds of rule, either or both: a need under `needThreshold` (default 0.3; the need is `need`, default Rest), and health conditions in `health`. A self-cast ability looks at the caster only; others at the caster and allies in reach, downed ones included for health rules. Worst off first. Combat: OutOfCombat.

`health` takes any of `Injured` (still-healing injuries, summary health under `healthThreshold`), `Disease` (an immunizable disease not yet immune), `MissingParts` (a missing part with no prosthetic), `Bleeding` (at least `minBleedRate` per day).

```xml
<!-- Biotech's Coagulate (Patches/AbilityAI_Biotech.xml): in a fight too, which Recovery doesn't do by default -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Recovery</workerClass>
  <priority>6</priority>
  <combat>Any</combat>
  <health>Bleeding</health>
  <minBleedRate>0.3</minBleedRate>
  <maxRange>12</maxRange>
</li>
```

#### Calm

End an ally's aggressive mental break (Word of Serenity). Nearest first. Touch abilities look within `maxRange` (default 20) and walk there; an undrafted player pawn walks as far as the player's "Undrafted, walk up to N tiles to calm or feed on someone" setting. Combat: Any.

`anyMentalState`: any break, not only aggressive ones. `minBreakSeconds`: skip a break that will end by itself within this many seconds.

#### Firefighting

Put out fire on or next to the caster's side (Waterskip). Burning allies weigh 3 (downed 4), fires within 2.5 tiles of an ally 1; the spot covering the most weight wins and needs `minTargets` of it. A self-cast ability only looks within its own radius. Combat: Any.

### Moving and cover

#### Movement

Close on the enemy or get away from it. Self-cast abilities (Sprint) are cast when either applies; targeted ones (jumps, Shadowskip) pick a cell. Combat: InCombat.

- Gap-close (`GapClose`, melee pawns only): the enemy is further than `minGapDistance` (default 6) and within `maxRange` (default 40); a targeted ability lands next to it, leading a moving target.
- Escape (`Escape`): health under `healthThreshold`, fleeing, or a ranged pawn caught in melee, with an enemy within 12 tiles. A targeted ability goes to the cell furthest from every enemy, if that gains at least 4 tiles.

| Field | Default | What it does |
|---|---|---|
| `modes` | both | `GapClose`, `Escape`, or both. |
| `escapeInMelee` | false | Escape melee whatever the caster's weapon, for a pawn that can't fight back. |
| `gapCloseOutOfSight` | false | Close the gap only while the caster can't see its target (an ability that walks through walls). |
| `cellMaxLight` | 1 | Targeted: only land on cells with at most this much light. |
| `avoidAllies`, `avoidBuildings`, `radius` | | Targeted: no one of the caster's side, or friendly building, within `radius` of the landing cell (an impact where it lands). |
| `pathClearance` | 0 | Targeted: nobody of its side and no friendly building within this many tiles of the line to the landing cell (a dash that burns what it passes). |
| `whileHunting` | false | Also close on the prey while the caster hunts for food. Needs `GapClose`. |
| `gapCloseJobTarget` | false | With no enemy pawn, close on the target of the caster's melee attack: a door, a turret, a downed or non-hostile pawn. Needs `GapClose`. |

A drafted player pawn's interrupted attack resumes after the cast (for a jump, on landing).

#### Skip

An ability that sends its target to a cell the caster picks (Royalty's Skip, VPE's Skip). One rule per use, chosen by `skipMode`, each behind its own player setting. Always set `skipMode`: unset reads as `Repel`. Exclusive by default.

- `Repel`: an enemy next to someone of the caster's side who can't hold it off (downed, hurt, or a ranged fighter) goes at least 10 tiles from that side, toward its own. Never a prisoner or an enemy carrying someone, never into the player's home area when the caster isn't hostile to the player, and for a player caster never a pawn whose faction would lose goodwill over it. Combat: InCombat.
- `Rescue`: an ally of the caster's faction that is downed with an enemy within 10 tiles, or under `healthThreshold` and in melee or shot at, comes near the caster, behind cover. A drafted pawn still standing stays where the player put it. Combat: Any.
- `Escape`: the caster itself, away from every enemy, when Movement's escape would apply. Combat: InCombat. `escapeInMelee` as for Movement.

```xml
<!-- Royalty's Skip (Patches/AbilityAI_Royalty.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Skip</workerClass>
  <skipMode>Rescue</skipMode>
  <priority>7</priority>
  <healthThreshold>0.4</healthThreshold>
</li>
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Skip</workerClass>
  <skipMode>Repel</skipMode>
  <priority>6</priority>
  <harmless>true</harmless>
</li>
```

#### Cover

Under fire, put something between the caster and the shooters (Smokepop, Wallraise, a skipshield). Under fire means at least `minThreats` ranged enemies aiming at the caster (being shot in the last 10 seconds counts as one). Not where it would add nothing: smoke while the caster already stands in smoke, a spawned thing while one already stands within the ability's radius, a wall while the caster already has cover. Combat: InCombat.

| Field | Default | What it does |
|---|---|---|
| `minThreats` | 1 | Shooters aiming at the caster. |
| `offsetTowardThreat` | 0 | Place the cover this many tiles toward the nearest shooter. 0 = on the caster. |
| `wallsIn` | false | Self-cast with a radius: a ring that shuts everyone inside in (Ice Wall). Cast only when walling in helps the side inside. |
| `shieldsAllies` | false | Self-cast with a radius: count shooters outside the radius aiming at anyone of the caster's side inside it (a shield dome). |

#### Barrier

Self-cast a projectile barrier (VPE's Guardian Skipbarrier) when enough ranged enemies and turrets outside the radius aim at the caster's side inside it; the player's settings say how many. Not while it is already up on the caster or an ally nearby. Ability AI drops the barrier again (removes its hediff) once the fire stops. The player's pawns only while drafted. `hediff` (required) is the barrier's hediff. The barrier needs a radius: read from the ability, else set `radius`. Combat: InCombat.

### Special

#### Bloodfeed

A sanguophage's bite, out of combat. One rule per `feedOn`, tried by priority (always set it: unset reads as `DownedEnemies`). `DownedEnemies` (downed hostile humanlikes and wild men) whenever there is room for the bite's hemogen; `Prisoners` (the colony's, for the player's own casters only) and `Colony` (the caster's faction-mates) once the caster's hemogen is under the target its owner set. Never a bite that would kill, a pawn in a mental state, or one with a standing enemy within 15 tiles. The shipped rules turn Bloodfeed off for other factions with an `npc` block. Combat: OutOfCombat.

#### Heat vents

`heatVent` on any rule makes it a vent: the AI tries it only when nothing else was cast and another of the caster's psycasts had a target but neural heat alone held it back. A vent that dumps heat as damage is an ordinary AoE rule with `heatVent`.

#### ShedHeat

A self-cast heat vent that turns neural heat into psyfocus (VPE's Heat Focus). Needs `heatVent`. Cast only when it would remove at least `minHeatShed` (default 10): heat above the `heatFloorStat` value, capped by the psyfocus room left at `focusPerHeat` psyfocus per point. Combat: OutOfCombat.

```xml
<!-- VPE's Heat Focus (Patches/AbilityAI_VPE.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_ShedHeat</workerClass>
  <priority>2</priority>
  <heatVent>true</heatVent>
  <focusPerHeat>0.002</focusPerHeat>
  <heatFloorStat>VPE_PsychicEntropyMinimum</heatFloorStat>
  <minHeatShed>10</minHeatShed>
</li>
```

### Colonist work

These workers are never run by the AI that casts in a fight, and other factions never use them. The player's colonists do them as Basic work, for abilities set to autocast. They respect the pawn's psycast limits. `npcGate` and `heatVent` do nothing on them.

#### Recast

Colonists keep up the thing the ability leaves behind (Solar Pinhole). `recastThing` (required) is that thing's ThingDef; its class must hold comps. A cast the player makes starts tracking it (a setting), and the player can place markers from the autocast menu. A thing whose lifetime Ability AI can read (`CompDestroyAfterDelay`, VEF's spawned-building and duration comps) is recast shortly before it runs out; others once destroyed.

```xml
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Recast</workerClass>
  <recastThing>SolarPinhole</recastThing>
</li>
```

#### Zone

Colonists cast it on what a zone the player paints holds, or on colony pawns that want it. `zoneTarget` (required):

| zoneTarget | Cast at |
|---|---|
| `QualityItems` | Items below the "enchant up to" quality setting (Enchant). |
| `Damaged` | The player's buildings and items under the "mend below" share of hit points (Mend). |
| `Rechargeable` | The player's batteries and mechs under the "recharge below" share (Recharge). |
| `PowerConsumers` | The player's buildings drawing power (Power). |
| `Unfinished` | Unfinished items whose bill still exists (Craft Timeskip). |
| `HumanlikeCorpses` | Corpses of factionless or hostile humanlikes (Enthrall). |
| `ConsumableCorpses` | The same corpses, in heaps, by a self-cast area (Consume Bodies). |
| `Prisoners` | Prisoners being recruited or worn down (Word of Trust). No zone. |
| `LowMood` | Colony pawns below the "Word of Joy" mood setting without `hediff`. No zone; needs `hediff`. |
| `Inspirable` | Colony pawns that can be inspired. No zone. |
| `ColonistsWithout` | Colony pawns without `hediff`. No zone; needs `hediff`. |
| `Visitors` | Non-hostile pawns of factions below the goodwill setting (Word of Alliance). No zone. |
| `LeechHosts` | Prisoners (and slaves, by setting) without `hediff`. No zone; needs `hediff`. |

`sensitivityAbove`: only casters whose psychic sensitivity is above this (VPE's Mend restores nothing at 100% or below). `unlessCasterHas`: not while the caster has this hediff (Brain Leech keeps one leech per caster).

```xml
<!-- VPE's Mend (Patches/AbilityAI_VPE_Zones.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Zone</workerClass>
  <zoneTarget>Damaged</zoneTarget>
  <sensitivityAbove>1</sensitivityAbove>
</li>
```

#### Spot

Colonists cast it at markers the player places from the autocast menu, once the ground there is ready. `spotKind` (required): `Crops` (no crop within the radius and enough fertile open cells; Crop Burst) or `Trees` (no crop and enough cells a wild tree of the biome can grow on; Tree Sprout). `spotRadius` (required): how far from the marker the cast reaches at most.

## Group links

`AbilityAI.AbilityAIWorker_GroupLink` marks a psycast that links the caster to the pawns around it (VPE's Group Link). It is never cast for itself: the AI casts it just before a Word of psycast when the link would reach enough of that Word's targets, for other factions too, and Word of Trust's zone work does the same for the player. Only ability systems that tell Ability AI how to read their links in C# can use it; VPE's is built in. Combat: Any.

On the Words themselves (Target hostile, Target friendly, Recovery, Calm and Zone rules): `linkSafe` (`Targets`, `Enemies` or `NonHostile`) says who besides the rule's own targets may take the Word through a link, and `linkFirst` (default true) lets the link be cast first when it would reach enough targets. Turn `linkFirst` off for a Word priced per target.

## Other factions

A rule applies to every caster as written. Two blocks inside a rule change it for other factions only; each holds any of the rule's own fields, and only the fields it names change:

- `<npc>`: every caster that isn't one of the player's pawns.
- `<friendlyNpc>`: on top of `npc`, casters not hostile to the player (visitors, traders, allied soldiers, wild animals). They are usually on the player's map, near the player's people and buildings.

```xml
<!-- VPE's Ice Wall (Patches/AbilityAI_VPE.xml): never for friendly NPCs, who would wall the player's people in -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Cover</workerClass>
  <priority>5</priority>
  <radius>5.9</radius>
  <wallsIn>true</wallsIn>
  <minThreats>2</minThreats>
  <friendlyNpc>
    <enabled>false</enabled>
  </friendlyNpc>
</li>
```

A rule turned off this way still keeps vanilla's and VEF's own AI off the ability for those casters. To leave an ability to its own AI for NPCs, leave the rule out entirely or claim it away in C#.

`npcGate` puts a rule behind one of two player settings (both off by default) for other factions. The player's own pawns are never held back by it.

- `InstantKill`: the cast kills or takes a pawn for good (Mind Break, Reap, shamblerification). Every NPC caster, friendly ones too.
- `BaseWrecking`: the cast wrecks buildings over a wide area (meteors, a tornado, wildfire). Hostile NPCs only; friendly NPCs never use it.

The settings page shows each gate only when some rule uses it.

## Worn gear

Gear that casts through a verb (`CompApparelVerbOwner` or a subclass: packs, belts, launchers, lances) takes rules on its ThingDef. The worker and fields are the same as for an ability. Ability AI reads the radius, smoke, stun and spawned thing from the verb's projectile and the vanilla pack comps.

- With several verbs on one item, `verbIndex` says which verb a rule is for (by position in its `verbs` list, from 0). A rule without one is for the verb of the rule before it, and the first rule for verb 0, so an item whose first rule names its verb keeps every rule on that verb. Each ruled verb has its own autocast setting and its own gizmo menu; verbs with no rules keep their plain gizmo.
- Single-use gear (a charged comp that destroys the item when empty) is used by other factions only while the player's "single-use gear" setting is on (on by default).
- Vanilla's own triggers for the smokepop belt, the low-shield pack and the tox, firefoam and disruptor packs stand down where Ability AI handles the wearer.
- Gear with its own job (a drone launcher that makes a job of its own) may need C# (`ApparelVerbJobs.Register`, see csharp-api.md).

```xml
<!-- The smokepop belt (Patches/AbilityAI_Apparel.xml) -->
<Operation Class="PatchOperationConditional">
  <xpath>Defs/ThingDef[defName="Apparel_SmokepopBelt"]</xpath>
  <match Class="PatchOperationAddModExtension">
    <xpath>Defs/ThingDef[defName="Apparel_SmokepopBelt"]</xpath>
    <value>
      <li Class="AbilityAI.AbilityAIExtension">
        <workerClass>AbilityAI.AbilityAIWorker_Cover</workerClass>
        <priority>4</priority>
        <minThreats>2</minThreats>
      </li>
    </value>
  </match>
</Operation>
```

`Patches/AbilityAI_ApparelModded.xml` has rules for the grenade belts, drone packs and jump gear of a dozen other mods, one `PatchOperationFindMod` block per mod.

## Testing

Rule mistakes show at load as config errors naming the def: a missing required field, a field the worker doesn't read, a value out of range. Fix every one; most mean the rule never casts.

In the mod settings, General tab, turn on "Log ability AI decisions" (needs dev mode). The log then gets a line for each cast, and for each pawn that holds ready abilities but casts nothing, a line giving each ability's first reason (out of range, no target worth it, too few enemies, held by a setting). A pawn gets at most one line every 10 seconds, and the same line again only after about 40.

To watch a rule work, give a pawn the ability in dev mode, set it to autocast from its gizmo's right-click menu, and draft it near enemies; or spawn a raid with a pawn that has it.

## Rules that need code

Some things can't be said in XML: claiming or vetoing an ability per pawn, telling Ability AI the area of your own comp class, gear verbs that make their own jobs, abilities of another ability system, or a worker of your own. [csharp-api.md](csharp-api.md) covers them.
