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
| `category` | the worker's | `Interrupt`, `Rescue`, `Recovery`, `Offensive`, `Defensive`, `Buff`, `Movement` or `Utility`. Only read by a mod that decides its pawns' jobs itself and asks Ability AI for suggestions (a job owner, see csharp-api.md); Ability AI's own casting ignores it. Defaults: the hostile area, cone, beam and target workers and Deploy are Offensive; Cover, Defensive and Barrier Defensive; SelfBuff, AoE friendly and Target friendly Buff; Recovery and Bloodfeed Recovery; Rescue Rescue; Interrupt Interrupt; Movement Movement; Skip by its mode (Repel Offensive, Rescue Rescue, Escape Movement); the rest Utility. |
| `enabled` | true | false turns the rule off. Meant for an `npc` or `friendlyNpc` block. |
| `exclusive` | the worker's | Leave a target alone while a faction-mate is casting this ability at it or walking into range to. On by default for every worker that doesn't target enemies, and for Interrupt and Skip; set it true on a hostile rule whose effect doesn't stack (a stun keeps the longer of two). |
| `reserveCharges` | 0 | Charges the AI never spends, for abilities with charges. |
| `minGapSeconds` | 0 | Seconds after an AI cast of this ability before the AI considers it again, on top of the cooldown. |
| `requires` | None | `Melee` or `Ranged`: only while the caster fights that way. `Melee` means no ranged weapon. |
| `maxLight` | 1 | Only while the light on the caster's cell is at most this (0 to 1). |
| `unlessCasterHas` | | A HediffDef: not while the caster has it, for an aura or channel a second cast would only restart. |
| `currentMapOnly` | false | Only on the map the player is looking at, for an ability whose code lands its effect on `Find.CurrentMap`. |
| `startsFires` | false | The ability sets fires, so an enemy holding it counts as a fire threat for `fireDanger` rules. |
| `npcGate` | | `InstantKill`, `Maiming`, `BaseWrecking`: see [Other factions](#other-factions). |
| `heatVent` | false | A psycast that vents neural heat: see [Heat vents](#heat-vents). |
| `npc`, `friendlyNpc` | | Changes for other factions: see [Other factions](#other-factions). |

Fields that several workers share:

| Field | Default | What it does |
|---|---|---|
| `radius` | read | The area the effect reaches around its target. Leave it out when the ability's comps state it. Each worker that reads it says what for. |
| `minTargets` | 1 | How many pawns the cast must affect: enemies for hostile workers, allies for AoE friendly. |
| `minRange`, `maxRange` | 0, the range | Distance band from the caster to the target. For a touch ability (range 0, such as the Word of psycasts), `maxRange` is how far the AI looks, default 20, and the caster walks there. |
| `healthThreshold` | 0.5 | Summary health (0 to 1) below which a pawn counts as badly hurt. A caster below it also stops walking toward targets. |
| `hediff` | read | Skip pawns that already have this HediffDef. Leave it out when the ability gives it through `CompProperties_AbilityGiveHediff`. |
| `targetKinds` | every pawn | Which pawns the rule may target or count: `Humanlike`, `Animal`, `Mechanoid`, `Other` (entities and the rest). Also `Turret` (hostile area workers and Target hostile), `Shielded` (a raised shield belt) and `Psycaster` (has a psylink), which aren't in the default and must be named. Write several as `Humanlike, Animal`. |
| `harmless` | false | Hostile rules: the ability disables without lasting harm (a stun, a slow, a blind). With the player's setting at its default, such a rule may also be used on an ally in a hostile mental break, and `avoidAllies` doesn't count downed faction-mates. |
| `skipAffected` | true | Hostile rules: leave out pawns the cast would be wasted on (already stunned by a stunning ability, already carrying its hediff, already in a mental state from one that gives one, no psychic sensitivity for a psycast). Turn it off when the damage alone makes the cast worth it. |
| `targetPart` | | Hostile rules: a BodyPartDef. Only pawns with at least one part of that def left, for an ability that works on that part alone (Alpha Animals' Destroy eyes: `Eye`, Leg skip: `Leg`). A pawn whose parts of that def are all gone is skipped, and so is a body without any (a mech has no `Eye`). |
| `avoidAllies` | false | Keep the caster's side out of the area. What "the area" is depends on the worker. |
| `allyKinds` | every pawn | Which of the caster's side `avoidAllies` keeps clear of: only those the effect would harm. An EMP: `Mechanoid, Turret, Shielded`. |
| `avoidBuildings` | false | Not with a building of the caster's side, or of a faction not hostile to it, in the area. For fire and blasts, so visitors don't burn the player's base. |
| `approachDistance` | 0 | How far past the ability's range a ranged caster walks to cast it in combat, when nothing is in range now. Read by AoE hostile, AoE hostile around the caster, Cone and Beam hostile, Target hostile, and self-cast AoE friendly. It is also what offers Full Autocast to the player. |

## Workers

The class name is `AbilityAI.AbilityAIWorker_` plus the name in each heading. Every worker below has the same layout:

1. What it casts at and when. The paragraph ends with when the worker runs by default: in combat only, out of combat only, or in or out of combat. A rule's `combat` field changes that (see [A rule's common fields](#a-rules-common-fields) for what counts as in combat). The colonist work workers run as work instead.
2. A table of the fields the worker reads, beyond the ones every rule takes. Shared fields that work the same everywhere (`hediff`, `targetKinds`, `harmless`, `skipAffected`, `targetPart`) are listed only where the worker gives them a meaning of its own.
3. Anything else worth knowing.
4. A shipped rule as an example. The file it comes from has more.

### At enemies

#### AoEHostile

An area cast at the spot that catches the most enemies (Flashbang, Berserk pulse, Artillery). Each enemy in range is a candidate centre, scored by the enemies within `radius` of it that the ability would affect. The best five are checked against the verb (line of sight). In combat only.

| Field | Default | What it does |
|---|---|---|
| `radius` | read | The area around the centre. |
| `minTargets` | 1 | Enemies the area must catch. |
| `approachDistance` | 0 | A ranged caster with nothing in range walks up to this far past it. |
| `avoidAllies` | false | No centre with anyone of the caster's side (of `allyKinds`) inside the radius. |
| `avoidSelf` | false | No centre within the radius of the caster, for an area that hits its caster too. Follows the radius when a stat scales it, unlike `minRange`. |
| `avoidBuildings` | false | No centre with a friendly building inside. |
| `targetKinds` | every pawn | With `Turret`, hostile turrets count and can be the centre (Haywire). |
| `anchorThings` | | A list of ThingDefs: centre only on these things, never on a pawn or cell (Runesmith's Invokes on a rune circle). A thing with a faction counts only when it is the caster's. The radius is then what the effect reaches around the thing. |
| `anchorCorpses` | | Kinds of corpse to centre on instead (Corpse Explosion): corpses of pawns hostile to the caster or of no faction. |
| `minCorpses` | 0 | With `anchorCorpses`: a centre needs this many of those corpses in the radius and is scored by them. `minTargets` may then be 0. |
| `waitForGather` | false | Anchored only: hold the cast for up to 5 seconds while more enemies are walking into the area, and skip a spot an ally is walking into. |
| `coveredBy`, `coveredFraction` | none, 0.5 | For an ability that fills an area with something that lasts (Bramble Maze): a list of ThingDefs, and no centre where at least `coveredFraction` of the cells in the radius already hold one of them. Walls count as covered, and so, when a listed def is a plant, do cells with no fertility, a tree or a building. A faction-mate's cast of the same ability that is still in warmup covers the centres near its target, so two casters don't pick the same spot. |

An anchored rule validates up to 20 centres instead of five.

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

An area centred on the caster (psychic drone, fire burst, an animal's roar). Counts what the ability would affect within the radius of the caster and casts with at least `minTargets`. Not while the caster already has the ability's hediff. In combat only.

| Field | Default | What it does |
|---|---|---|
| `radius` | read | The area around the caster. |
| `minTargets` | 1 | Targets inside the radius. |
| `avoidAllies` | false | Not with anyone else of the caster's side (of `allyKinds`) inside. |
| `avoidBuildings` | false | Not with a friendly building inside. |
| `targetKinds` | every pawn | With `Turret`, enemy turrets count too. |
| `approachDistance` | 0 | A ranged caster walks up to this far to a cell that catches more than where it stands. |
| `anchorCorpses`, `minCorpses` | none, 0 | Count corpses instead: at least `minCorpses` corpses of these kinds within the radius, of pawns hostile to the caster or of no faction (Anomaly's Release deadlife dust). |

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

A cone or line from the caster (fire spew, a breath). The cone always reaches its full length, whatever the distance to the aim. Each affectable enemy in reach is an aim, scored by the enemies inside the shape. Any of the caster's side inside the shape always drops the aim (walls are ignored, so an ally behind one still blocks). In combat only.

| Field | Default | What it does |
|---|---|---|
| `coneWidth` | read, else 1 | Width at the far end. Read from the fire spew and spray comps. |
| `minRange`, `maxRange` | 0, the length | How near and how far off an aim may stand. The length is the effect's cone length, else the ability's range. |
| `minTargets` | 1 | Enemies inside the shape. |
| `avoidAllies` with `radius` | false | Also no aim with an ally within `radius` of it: someone fighting the target may step into the cone before the effect is over. |
| `avoidBuildings` | false | No aim with a friendly building inside the shape. |
| `approachDistance` | 0 | A ranged caster with nothing in reach walks up to this far past it. |

```xml
<!-- Biotech's fire spew (Patches/AbilityAI_Biotech.xml): two or more enemies in the cone, no friendly building -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_ConeHostile</workerClass>
  <priority>4</priority>
  <minTargets>2</minTargets>
  <avoidAllies>true</avoidAllies>
  <avoidBuildings>true</avoidBuildings>
</li>
```

#### BeamHostile

A straight beam from the caster to the aimed cell, held for a while (VPE's Frost Ray). It follows Cone hostile's rules, with the shape ending at the aim and one width all along, so an ally anywhere on the way drops the aim, not only one near the target. The enemies counted are the ones on the beam. In combat only.

| Field | Default | What it does |
|---|---|---|
| `coneWidth` | read, else 1 | The width kept clear of allies all along the beam, never under 1.5. Make it wider than the beam itself: a pawn that crosses it later is hit too. |
| `minTargets` | 1 | Enemies on the beam. |
| `avoidAllies` with `radius` | false | Also no aim with an ally within `radius` of it. |
| `avoidBuildings` | false | No aim with a friendly building on the beam. |
| `approachDistance` | 0 | A ranged caster with nothing in reach walks up to this far past it. |

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

One enemy (Stun, Burden, Berserk). Enemies in range that the ability would affect, scored by threat: +1 for the caster's own target, +1 for one aiming at the caster, times its combat power, divided by distance. The best five are validated. In combat only.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | Above 1: the enemy needs that many of its side (itself included) within `radius`. |
| `radius` | 5 | How near the target counts for `minTargets`, `preferGrouped`, `avoidAllies` and `avoidBuildings`. |
| `preferGrouped` | false | Multiply the score by the enemies around the target. |
| `avoidAllies` | false | Skip enemies with anyone of the caster's side within `radius` (a berserk enemy attacks whoever is nearest). |
| `casterLeaves` | false | With `avoidAllies`: the caster itself doesn't count, for an ability that leaps the caster onto the target before the area lands (Alpha Animals' Spin cut). Without it a melee caster refuses every enemy beside it. |
| `avoidBuildings` | false | Skip enemies with a friendly building within `radius`, for a fire or blast that spreads. |
| `meleeOnly` | false | Only enemies in melee with the caster or an ally. |
| `armedOnly` | false | Only enemies holding a weapon. |
| `inMentalState` | false | Only enemies in a mental state (calming a manhunter). |
| `permanentEnemiesOnly` | false | Not a pawn whose home faction is humanlike and not permanently hostile: killing one costs goodwill even at war. |
| `targetKinds` | every pawn | With `Turret`, enemy turrets that are a threat now are candidates too. |
| `whileHunting` | false | Also while the caster hunts for food, at its prey. |
| `approachDistance` | 0 | A ranged caster with nothing in range walks up to this far past it. |

```xml
<!-- Royalty's Stun (Patches/AbilityAI_Royalty.xml): the second of its two rules; the first is under Interrupt -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_TargetHostile</workerClass>
  <priority>3</priority>
  <approachDistance>8</approachDistance>
  <harmless>true</harmless>
  <exclusive>true</exclusive>
</li>
```

#### Interrupt

An enemy aiming something dangerous: a hostile ability or psycast, or a weapon with an explosive projectile (grenades, launchers). Only when more of its aim is left than this ability's own warmup. A stun pauses the aim, a mental state or a teleport breaks it. Area abilities centre on the enemy. Nearest first; exclusive by default. In combat only.

| Field | Default | What it does |
|---|---|---|
| `minAimSeconds` | 0 | Also any aim with at least this many seconds left, whatever the weapon. 0 = only abilities and explosive weapons. |

Give it a higher priority than the ability's other rules (usually a Target hostile one), so the interrupt is tried first.

```xml
<!-- Royalty's Stun (Patches/AbilityAI_Royalty.xml): the first of its two rules; the second is under Target hostile -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Interrupt</workerClass>
  <priority>9</priority>
  <harmless>true</harmless>
</li>
```

#### Deploy

Throw out or summon something that fights on its own (Anomaly's turret pack, drone packs, Alpha Animals' Black Hive summons) to a cell from which it sees the most enemies, on the caster's side of the fight. Two player settings limit how many of each thing the caster's side may have standing within `radius` of the caster, downed pawns not counted: "Turret packs" for worn gear (default 1) and "Summons" for abilities (default 3). In combat only.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | Enemies the deployed thing must see from the cell. |
| `minRange` | 2 | The nearest ring of cells tried around the caster (never under 2). |
| `radius` | the turret's range, else 20 | How far the deployed thing reaches, and how near the caster the ones already standing count against the limit. |
| `minGapDistance` | 6 | No enemy this close to the cell. |
| `deployedThing` | read | What the cast puts down, when Ability AI can't read it from the verb or projectile. For a pawn, its race's ThingDef. |

```xml
<!-- VFE Pirates' Controller warcasket (Patches/AbilityAI_ApparelModded.xml): VFE Pirates makes the wardrones in its own code, so the rule names them -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Deploy</workerClass>
  <priority>4</priority>
  <minTargets>2</minTargets>
  <deployedThing>VFEP_Mech_Wardrone</deployedThing>
</li>
```

### For allies and the caster

#### AoEFriendly

An area buff over allies (Battle Cry). Self-cast abilities centre on the caster, targeted ones on the ally that gathers the most. Counts allies (the caster included) within the radius who don't have the ability's hediff yet, and casts when at least `minTargets` would gain it. In combat only.

| Field | Default | What it does |
|---|---|---|
| `radius` | read | The area around the centre. |
| `minTargets` | 1 | Allies who would gain it. |
| `approachDistance` | 0 | Self-cast only: walk up to this far to a cell that covers more allies. |
| `health` | | Only allies with one of these conditions count (see Recovery). |
| `avoidEnemies` | false | No centre with an enemy inside the radius, for an effect that reaches whoever stands there. |
| `anchorThings`, `anchorCorpses` | | Targeted only: centre on these things instead of on allies. |

```xml
<!-- VPE's Shield animals (Patches/AbilityAI_VPE.xml): two or more of the caster's animals in the fight -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_AoEFriendly</workerClass>
  <priority>3</priority>
  <minTargets>2</minTargets>
  <targetKinds>Animal</targetKinds>
</li>
```

#### SelfBuff

A buff the caster puts on itself (Blade focus, Adrenaline rush). Cast while an enemy is within `maxRange` and the caster doesn't have the ability's hediff. Without a hediff, set or read, that second check is skipped and the AI casts it again whenever it is ready. In combat only.

| Field | Default | What it does |
|---|---|---|
| `maxRange` | 30 | How near the nearest enemy must be. Here it is not the ability's range, which a self-cast doesn't have. |
| `auraKinds` | | For a buff that is an aura hitting whatever comes near its bearer: only while enemies of these kinds are near the caster. `Turret` counts enemy turrets. |
| `auraMin` | 1 | With `auraKinds`: how many of them must be within `radius`. |
| `radius` | | Required with `auraKinds`: the aura's reach, which is rarely the ability's own. |
| `avoidAllies`, `allyKinds` | false, every pawn | With `auraKinds`: not while anyone of the caster's side of `allyKinds` is within `radius`. |

`requires`, `maxLight` and `unlessCasterHas` from the common fields suit this worker most: a melee-only buff, a buff that needs the dark (Shadow focus).

```xml
<!-- Ideology's Berserk trance (Patches/AbilityAI_Ideology.xml): a melee caster with two or more enemies within 6 tiles -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_SelfBuff</workerClass>
  <priority>5</priority>
  <maxRange>6</maxRange>
  <radius>6</radius>
  <auraKinds>All</auraKinds>
  <auraMin>2</auraMin>
  <requires>Melee</requires>
</li>
```

#### TargetFriendly

A buff on one ally or the caster (Focus). Allies in range without the ability's hediff; by default only allies fighting now. Scored by pain and fire danger, the caster first, nearer first. In combat only.

| Field | Default | What it does |
|---|---|---|
| `minPain` | 0 | Only allies in at least this much pain (0 to 1), fighting or not. |
| `fireDanger` | false | Only allies in fire danger: burning, a fire within 2.5 tiles, or within reach of an enemy that can set fires (fire weapons, or an ability with `startsFires`). |
| `avoidFireDanger` | false | Never an ally in fire danger (as for `fireDanger`), for a buff that makes its bearer burn worse (Alpha Animals' Fungal skin). |
| `auraKinds`, `auraMin`, `radius`, `avoidAllies`, `allyKinds` | | As for Self buff, around the ally. |

```xml
<!-- VPE's Static aura (Patches/AbilityAI_VPE.xml): an EMP aura, on an ally with enemy mechs, turrets or shields near and none of ours -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_TargetFriendly</workerClass>
  <priority>2</priority>
  <radius>6</radius>
  <auraKinds>Mechanoid, Turret, Shielded</auraKinds>
  <avoidAllies>true</avoidAllies>
  <allyKinds>Mechanoid, Turret, Shielded</allyKinds>
</li>
```

#### Defensive

Protect the caster or an ally in trouble (Invisibility, a shield). In trouble means badly hurt with an enemy within 15 tiles, hurt in melee in the last 10 seconds, or shot at. Skips anyone who already has the ability's hediff. The caster first, then whoever is worst hurt. In combat only.

| Field | Default | What it does |
|---|---|---|
| `healthThreshold` | 0.5 | Summary health below which a pawn with an enemy within 15 tiles is in trouble. |
| `minThreats` | 1 | Ranged enemies aiming at a pawn before it is in trouble. Being shot in the last 10 seconds counts as one. |
| `rangedOnly` | false | Only the shooters count, not low health or melee, for protection that only stops projectiles. |

```xml
<!-- VPE's Overshield (Patches/AbilityAI_VPE.xml): stops projectiles only, so for two shooters and never for melee -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Defensive</workerClass>
  <priority>6</priority>
  <minThreats>2</minThreats>
  <rangedOnly>true</rangedOnly>
</li>
```

#### Rescue

Help a downed ally (Painblock, Stabilize). Downed allies in range without the ability's hediff, nearest first. In or out of combat.

| Field | Default | What it does |
|---|---|---|
| `painShockOnly` | false | Only allies downed by pain alone. |
| `minBleedRate` | 0 | Only allies bleeding at least this much per day (1 = 100%), for an ability that only stops bleeding. |

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

#### Recovery

Get the caster or an ally back to strength: a need running low, a health condition, or both. A self-cast ability looks at the caster only; others at the caster and allies in reach, downed ones included for health rules. Worst off first. Out of combat only.

| Field | Default | What it does |
|---|---|---|
| `health` | | The health conditions it treats, any of: `Injured` (still-healing injuries, summary health under `healthThreshold`), `Disease` (an immunizable disease not yet immune), `MissingParts` (a missing part with no prosthetic), `Bleeding` (at least `minBleedRate` per day). |
| `need` | Rest | The need to watch. A rule without `health` always watches one; a rule with `health` watches one only when `need` is set. |
| `needThreshold` | 0.3 | Cast when the need is below this (0 to 1). |
| `healthThreshold` | 0.5 | For `Injured`. |
| `minBleedRate` | 0 | For `Bleeding`, per day (1 = 100%). |

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

End an ally's aggressive mental break (Word of Serenity). Nearest first. In or out of combat.

| Field | Default | What it does |
|---|---|---|
| `maxRange` | the range, 20 for a touch ability | How far to look. With a touch ability, an undrafted player pawn walks as far as the player's "Undrafted, walk up to N tiles to calm or feed on someone" setting instead. |
| `anyMentalState` | false | Any break, not only aggressive ones. |
| `minBreakSeconds` | 0 | Skip a break that will end by itself within this many seconds. |

```xml
<!-- Royalty's Word of serenity (Patches/AbilityAI_Royalty.xml): the target sleeps afterwards, so not for a break with under a minute left -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Calm</workerClass>
  <priority>5</priority>
  <maxRange>8</maxRange>
  <minBreakSeconds>60</minBreakSeconds>
</li>
```

#### Firefighting

Put out fire on or next to the caster's side (Waterskip). Burning allies weigh 3 (downed 4), fires within 2.5 tiles of an ally 1; the spot covering the most weight wins. Water breaks invisibility, so a spot that would reach an invisible ally is skipped unless someone there is burning. A self-cast ability only looks within its own radius. In or out of combat.

| Field | Default | What it does |
|---|---|---|
| `minTargets` | 1 | The weight of fire the spot must cover. |
| `radius` | read | The area the effect puts out. |

```xml
<!-- The firefoam pop pack (Patches/AbilityAI_Apparel.xml): one charge, so only for a fire worth it -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Firefighting</workerClass>
  <priority>6</priority>
  <minTargets>3</minTargets>
</li>
```

### Moving and cover

#### Movement

Close on the enemy or get away from it. Self-cast abilities (Sprint) are cast when either applies; targeted ones (jumps, Shadowskip) pick a cell. In combat only.

- Gap-close (`GapClose`, melee pawns only): the enemy is further than `minGapDistance` and within `maxRange`; a targeted ability lands next to it, leading a moving target.
- Escape (`Escape`): health under `healthThreshold`, fleeing, or a ranged pawn caught in melee, with an enemy within 12 tiles. A targeted ability goes to the cell furthest from every enemy, if that gains at least 4 tiles.

A drafted player pawn's interrupted attack resumes after the cast (for a jump, on landing).

| Field | Default | What it does |
|---|---|---|
| `modes` | both | `GapClose`, `Escape`, or both. |
| `minGapDistance` | 6 | Gap-close: only when the enemy is further than this. |
| `maxRange` | 40 | Gap-close: the furthest enemy it closes on. A targeted ability also needs it within reach. |
| `healthThreshold` | 0.5 | Escape: below this summary health. |
| `escapeInMelee` | false | Escape melee whatever the caster's weapon, for a pawn that can't fight back. |
| `gapCloseOutOfSight` | false | Close the gap only while the caster can't see its target (an ability that walks through walls). |
| `cellMaxLight` | 1 | Targeted: only land on cells with at most this much light. |
| `avoidAllies`, `avoidBuildings`, `radius` | | Targeted: no one of the caster's side, or friendly building, within `radius` of the landing cell (an impact where it lands). |
| `pathClearance` | 0 | Targeted: nobody of its side and no friendly building within this many tiles of the line to the landing cell (a dash that burns what it passes). |
| `whileHunting` | false | Also close on the prey while the caster hunts for food. Needs `GapClose`. |
| `gapCloseJobTarget` | false | With no enemy pawn, close on the target of the caster's melee attack: a door, a turret, a downed or non-hostile pawn. Needs `GapClose`. |

```xml
<!-- VPE Pyromancer's Blazing dash (Patches/AbilityAI_VPE_Pyromancer.xml): burns what it passes, so nobody of the caster's side within 2 tiles of the line -->
<li Class="AbilityAI.AbilityAIExtension">
  <startsFires>true</startsFires>
  <workerClass>AbilityAI.AbilityAIWorker_Movement</workerClass>
  <priority>4</priority>
  <modes>GapClose</modes>
  <pathClearance>2</pathClearance>
  <npcGate>BaseWrecking</npcGate>
</li>
```

#### Skip

An ability that sends its target to a cell the caster picks (Royalty's Skip, VPE's Skip). One rule per use, chosen by `skipMode`, each behind its own player setting. Exclusive by default.

- `Repel`: an enemy next to someone of the caster's side who can't hold it off (downed, hurt, or a ranged fighter) goes at least 10 tiles from that side, toward its own. Never a prisoner or an enemy carrying someone, never into the player's home area when the caster isn't hostile to the player, and for a player caster never a pawn whose faction would lose goodwill over it. In combat only.
- `Rescue`: an ally of the caster's faction that is downed with an enemy within 10 tiles, or under `healthThreshold` and in melee or shot at, comes near the caster, behind cover. A drafted pawn still standing stays where the player put it. In or out of combat.
- `Escape`: the caster itself, away from every enemy, when Movement's escape would apply. In combat only.

| Field | Default | What it does |
|---|---|---|
| `skipMode` | Repel | Which use this rule is (above). Always set it. |
| `healthThreshold` | 0.5 | Rescue: an ally under this summary health counts once it is in melee or shot at. Escape: as for Movement. |
| `escapeInMelee` | false | Escape: as for Movement. |

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

Under fire, put something between the caster and the shooters (Smokepop, Wallraise, a skipshield). Under fire means at least `minThreats` ranged enemies aiming at the caster (being shot in the last 10 seconds counts as one). Not where it would add nothing: smoke while the caster already stands in smoke, a spawned thing while one already stands within the ability's radius, a wall while the caster already has cover. In combat only.

| Field | Default | What it does |
|---|---|---|
| `minThreats` | 1 | Shooters aiming at the caster. |
| `offsetTowardThreat` | 0 | Place the cover this many tiles toward the nearest shooter. 0 = on the caster. |
| `wallsIn` | false | Self-cast with a radius: a ring that shuts everyone inside in (Ice Wall). Cast only when walling in helps the side inside. |
| `shieldsAllies` | false | Self-cast with a radius: count shooters outside the radius aiming at anyone of the caster's side inside it (a shield dome). |
| `radius` | read | The ability's area: no new spawned cover while one already stands within it, and the ring `wallsIn` and `shieldsAllies` look at. |

```xml
<!-- VPE's Ice block (Patches/AbilityAI_VPE.xml): ice walls four tiles toward the nearest shooter once two aim at the caster -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Cover</workerClass>
  <priority>4</priority>
  <offsetTowardThreat>4</offsetTowardThreat>
  <minThreats>2</minThreats>
</li>
```

#### Barrier

Self-cast a projectile barrier (VPE's Guardian Skipbarrier) once enough ranged enemies and turrets outside its radius aim at the caster's side inside it. The player's "Projectile barriers" settings say how many (default 3), and when Ability AI drops the barrier again by removing its hediff. Not while a barrier is already up on the caster or an ally within the radius, or a faction-mate there is casting one. The player's pawns only while drafted. In combat only.

| Field | Default | What it does |
|---|---|---|
| `hediff` | | Required: the barrier's hediff. |
| `radius` | read | The barrier's reach. Set it when the ability doesn't state one. |

```xml
<!-- VPE's Guardian skipbarrier (Patches/AbilityAI_VPE.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Barrier</workerClass>
  <priority>6</priority>
  <hediff>VPE_GuardianSkipBarrier</hediff>
</li>
```

### Special

#### Bloodfeed

A sanguophage's bite. One rule per `feedOn`, tried by priority. Never a bite that would kill, a pawn in a mental state, or one with a standing enemy within 15 tiles. The shipped rules turn Bloodfeed off for other factions with an `npc` block. Out of combat only.

| Field | Default | What it does |
|---|---|---|
| `feedOn` | DownedEnemies | Whom the rule feeds on. Always set it. `DownedEnemies` (downed hostile humanlikes and wild men) whenever there is room for the bite's hemogen; `Prisoners` (the colony's, for the player's own casters only) and `Colony` (the caster's faction-mates) once the caster's hemogen is under the target its owner set. |
| `maxRange` | 20 | How far to look. The bite is a touch ability, so an undrafted player pawn walks as far as the player's setting instead, as for Calm. |

```xml
<!-- Biotech's Bloodfeed (Patches/AbilityAI_Biotech.xml): enemies first, then prisoners, then the colony -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Bloodfeed</workerClass>
  <priority>3</priority>
  <feedOn>DownedEnemies</feedOn>
  <maxRange>8</maxRange>
  <npc><enabled>false</enabled></npc>
</li>
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Bloodfeed</workerClass>
  <priority>2</priority>
  <feedOn>Prisoners</feedOn>
  <maxRange>8</maxRange>
  <npc><enabled>false</enabled></npc>
</li>
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Bloodfeed</workerClass>
  <priority>1</priority>
  <feedOn>Colony</feedOn>
  <maxRange>8</maxRange>
  <npc><enabled>false</enabled></npc>
</li>
```

#### Heat vents

`heatVent` on any rule makes it a vent: the AI tries it only when nothing else was cast and another of the caster's psycasts had a target but neural heat alone held it back. A vent that dumps heat as damage is an ordinary AoE rule with `heatVent`.

#### ShedHeat

A self-cast heat vent that turns neural heat into psyfocus (VPE's Heat Focus). Cast only when it would remove at least `minHeatShed`: the heat above the `heatFloorStat` value, capped by the room left on the psyfocus bar. Out of combat only.

| Field | Default | What it does |
|---|---|---|
| `heatVent` | false | Required: true. See [Heat vents](#heat-vents). |
| `minHeatShed` | 10 | The least heat the cast must remove. |
| `heatFloorStat` | | A StatDef whose value is heat the cast can't remove. |
| `focusPerHeat` | 0 | Psyfocus gained per point of heat removed, which caps what the cast removes at the room on the psyfocus bar. 0 = every point above the floor goes. |

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

Colonists keep up the thing the ability leaves behind (Solar Pinhole). A cast the player makes starts tracking it (a setting), and the player can place markers from the autocast menu. A thing whose lifetime Ability AI can read (`CompDestroyAfterDelay`, VEF's spawned-building and duration comps) is recast shortly before it runs out; others once destroyed.

| Field | Default | What it does |
|---|---|---|
| `recastThing` | | Required: the ThingDef the ability leaves behind. Its class must hold comps (ThingWithComps). |

```xml
<!-- Royalty's Solar pinhole (Patches/AbilityAI_Royalty.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Recast</workerClass>
  <recastThing>SolarPinhole</recastThing>
</li>
```

#### Zone

Colonists cast it on what a zone the player paints holds, or on colony pawns that want it.

| Field | Default | What it does |
|---|---|---|
| `zoneTarget` | | Required: what to cast at (below). |
| `hediff` | read | What the cast gives. Required for `LowMood`, `ColonistsWithout` and `LeechHosts`. |
| `sensitivityAbove` | any | Only casters whose psychic sensitivity is above this (VPE's Mend restores nothing at 100% or below). |

`unlessCasterHas` from the common fields keeps a caster from casting again while it has that hediff (Brain Leech keeps one leech per caster).

`zoneTarget` takes one of:

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

```xml
<!-- VPE's Mend (Patches/AbilityAI_VPE_Zones.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Zone</workerClass>
  <zoneTarget>Damaged</zoneTarget>
  <sensitivityAbove>1</sensitivityAbove>
</li>
```

#### Spot

Colonists cast it at markers the player places from the autocast menu, once the ground there is ready.

| Field | Default | What it does |
|---|---|---|
| `spotKind` | | Required. `Crops`: no crop within `spotRadius` and enough fertile open cells (Crop Burst). `Trees`: no crop and enough cells a wild tree of the biome can grow on (Tree Sprout). |
| `spotRadius` | 0 | Required: how far from the marker the cast reaches, the most any caster's radius can be. The marker draws it. |

```xml
<!-- VPE's Crop burst (Patches/AbilityAI_VPE_Zones.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Spot</workerClass>
  <spotKind>Crops</spotKind>
  <spotRadius>6</spotRadius>
</li>
```

## Group links

`AbilityAI.AbilityAIWorker_GroupLink` marks a psycast that links the caster to the pawns around it (VPE's Group Link). It is never cast for itself: the AI casts it just before a Word of psycast when the link would reach enough of that Word's targets, for other factions too, and Word of Trust's zone work does the same for the player. Only ability systems that tell Ability AI how to read their links in C# can use it; VPE's is built in. In or out of combat.

The Words themselves (Target hostile, Target friendly, Recovery, Calm and Zone rules) take two fields:

| Field | Default | What it does |
|---|---|---|
| `linkSafe` | Targets | Who besides the rule's own targets may take the Word through a link: `Targets` (nobody else), `Enemies` or `NonHostile`. |
| `linkFirst` | true | Let the link be cast first when it would reach enough targets. Turn it off for a Word priced per target. |

```xml
<!-- On VPE's Group link (Patches/AbilityAI_VPE.xml) -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_GroupLink</workerClass>
</li>

<!-- On VPE's Word of trust (Patches/AbilityAI_VPE_Zones.xml): anyone not hostile may hear it through the link -->
<li Class="AbilityAI.AbilityAIExtension">
  <workerClass>AbilityAI.AbilityAIWorker_Zone</workerClass>
  <zoneTarget>Prisoners</zoneTarget>
  <linkSafe>NonHostile</linkSafe>
</li>
```

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

`npcGate` puts a rule behind one of three player settings (all off by default) for other factions. The player's own pawns are never held back by it.

- `InstantKill`: the cast kills or takes a pawn for good (Mind Break, Reap, shamblerification). Every NPC caster, friendly ones too.
- `Maiming`: the cast destroys body parts for good without killing (Alpha Animals' Leg skip, Destroy eyes). Every NPC caster, friendly ones too.
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

## Resources

Abilities that spend from a pool (hemogen, The Force's Force points, a magic mod's mana) can get a floor: the player sets, per pawn, the share of the pool autocast leaves unspent, on the autocast limits gizmo beside the psyfocus one. A resource is an `AbilityAI.AIResourceDef`, so it needs no code. Ability AI ships two in `Defs/AbilityAI_Resources.xml`: hemogen (default floor 30%) and Force points (default none).

- `label`: the resource's name in the row's tooltip ("this pawn's hemogen").
- `limitLabel`: the row's label ("Min hemogen"). `description`, when set, is added to the tooltip.
- `barColor`, `defaultFloor` (0 to 1, a share of the pawn's maximum).
- `requiredMod`: a packageId; the def takes no part while that mod or DLC is not active, so it needs no patch.
- Where a pawn's amount is read, exactly one of: `comp` (`type`, a ThingComp's full name, with `current` and `max`, field or property names), `geneResource` (a `Gene_Resource` subclass's full name: its Value and Max), `need` (a NeedDef: CurLevel and MaxLevel). A pawn with a maximum of 0 has none of the resource.
- Where an ability's cost is read, one or more of, tried in this order: `costStat` (a StatDef on a vanilla AbilityDef's statBases), `costComp` (`type` and `field` on one of a vanilla AbilityDef's comp properties), `costExtensionField` (a float field of that name on any mod extension of the ability's def, any ability system).

A type or member that can't be found turns the resource off with one warning at startup. The row shows only for a pawn that has the resource and holds an ability on the autocast menu that costs some. Floors hold back autocast on the player's pawns only; casting by hand ignores them. An ability whose comp refuses the cast for lack of the resource (its gizmo greyed out) is never autocast, floor or not.

```xml
<AbilityAI.AIResourceDef>
  <defName>AAI_ForcePoints</defName>
  <label>Force points</label>
  <limitLabel>Min Force</limitLabel>
  <barColor>(0.18, 0.37, 0.62)</barColor>
  <defaultFloor>0</defaultFloor>
  <requiredMod>lee.theforce.standalone</requiredMod>
  <comp>
    <type>TheForce_Standalone.CompClass_ForceUser</type>
    <current>currentFP</current>
    <max>MaxFP</max>
  </comp>
  <costStat>Force_AbilityForcePoolCost</costStat>
</AbilityAI.AIResourceDef>
```

## Testing

Rule mistakes show at load as config errors naming the def: a missing required field, a field the worker doesn't read, a value out of range. Fix every one; most mean the rule never casts.

In the mod settings, General tab, turn on "Log ability AI decisions" (needs dev mode). The log then gets a line for each cast, and for each pawn that holds ready abilities but casts nothing, a line giving each ability's first reason (out of range, no target worth it, too few enemies, held by a setting). A pawn gets at most one line every 10 seconds, and the same line again only after about 40.

To watch a rule work, give a pawn the ability in dev mode, set it to autocast from its gizmo's right-click menu, and draft it near enemies; or spawn a raid with a pawn that has it.

## Rules that need code

Some things can't be said in XML: claiming or vetoing an ability per pawn, telling Ability AI the area of your own comp class, gear verbs that make their own jobs, abilities of another ability system, or a worker of your own. [csharp-api.md](csharp-api.md) covers them.
