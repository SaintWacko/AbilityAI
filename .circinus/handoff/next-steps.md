# Ability AI: next steps (handoff, written at build 0019)

State at handoff: player autocast plan steps 1–5 are done and tested in game (runs #27–#30 clean). Latest additions:
autocast marker ring + "right-click to set autocast" tooltip line (build 0018), reset button removed from the psycast
limits gizmo (build 0019). Mode labels are now "Stationary Autocast" / "Full Autocast" (operator's Keyed edit).
Still to do from the old plan: mark steps 3–5 done in `player-autocast-plan.md`.

## Agreed with the operator: build these next

### A. Slower check interval when undrafted and not in a fight (idea 2)
- Today the driver (postfix on `Pawn_JobTracker.DetermineNextConstantThinkTreeJob`) checks each pawn at most every 30 ticks.
- New: player pawns that are undrafted AND not fighting back AND not fleeing use a slow interval. Setting, default
  **250 ticks** (~4 s). Slider in settings under "Also while undrafted".
- Fighting back (`AbilityAIDriver.FightsUndrafted`) and fleeing keep the 30-tick check, so reactions stay quick.
- Drafted pawns and NPCs unchanged (30).
- Better Autocasting for VPE uses 30 drafted / 600 undrafted for comparison; operator chose 250.
- Keep it allocation-free: per-pawn next-check tick already lives in PawnState.

### B. Editable blocked-jobs list (idea 3)
- Keep the hard-coded rules (lying down, carrying, player-forced job, `casualInterruptible == false`): those aren't
  JobDefs.
- Add a list of JobDefs that undrafted autocast must not interrupt, edited from a sub-window opened on the settings
  page (enable/disable per def, reset to defaults, drop defs that no longer exist on load; store defNames as strings
  so a removed mod doesn't break settings).
- Default list: jobs that are interruptible but annoying to break off: Ingest, social relax / gatherings
  (SocialRelax, StandAndBeSociallyActive), SpectateCeremony, Lovin, plus anything else found by checking vanilla
  JobDefs with casualInterruptible true. Verify defNames with search_defs.
- Cache the list as a HashSet<JobDef> resolved after defs load; rebuild when the settings change.
- Mockup the sub-window first (`.circinus/mockups/blocked-jobs-window.html`).

## Deferred into the VPE project (ideas 1 and 4)
- **1. Per-ability target filters** (colonists, slaves, prisoners, visitors, colony animals, wild animals) as an
  AbilityAIExtension field plus settings. No real use in vanilla/Royalty; its users are VPE spells (Deathshield,
  Word of Serenity, Word of Immunity...). Design it against those.
- **4. Cast areas** for cell-targeted utility spells (player-painted Area, like Better Autocasting's Ice Crystal /
  Solar Pinhole / Runecircle areas). Only meaningful once a host adds such spells.

## VPE project: what we found
- VEF abilities are a separate system: `VEF.Abilities.AbilityDef : Def`, `VEF.Abilities.Ability` (abstract, own
  IExposable), `VEF.Abilities.Command_Ability : Command_Action`. None derive from vanilla's. Everything in Ability AI
  (workers, driver, menu/badge/marker patches on `RimWorld.Command_Ability`, `Ability.AICanTargetNow` stand-down,
  psycast limits on `Psycast`) targets `RimWorld.Ability`, so XML extensions alone do nothing for VPE.
- Needed: an adapter abstraction over "an ability" (range, cooldown/can-cast-now, queue cast job, heat/psyfocus cost,
  targeting params) used by the driver and workers; a compat assembly gated on VEF (LoadFolders `IfModActive`, same
  pattern as Entad/Bastle hosts); gizmo hooks on VEF's Command_Ability or reuse of VEF's own `autoCast` toggle;
  coordination with VEF's `Pawn.TryGetAttackVerb` postfix (`VanillaExpandedFramework_Pawn_TryGetAttackVerb_Patch`),
  which is how NPCs cast VEF abilities today (random weighted pick).
- Keep the adapter cheap: no per-tick allocation, cache per def. Do not change existing public API or assembly name.

### Better Autocasting for VPE (dev.tobot.vpe.betterautocast)
- Decompiled at `ModSource/dev.tobot.vpe.betterautocast@DECOMPILED/BetterAutocastVPE/` (1.7 build).
- Colonists only, utility-focused: ~40 VPE psycasts each with a hand-written handler in
  `PsycastingHandler.abilityHandlers` (Mend, Power, Words, constructs, Enthrall, area spells, self buffs).
- Opt-in via VEF's `ability.autoCast`; forces `CanAutoCast` true for handled abilities; global drafted/undrafted def
  lists; global MinFocusThreshold 0.5; checks on `Pawn.Tick`/`CompAbilities.CompTick` postfix with
  HashOffsetTicks staggering (30 / 600).
- **Unregisters VEF's TryGetAttackVerb postfix and installs its own** (Harmony id `dev.tobot.vpe-better-autocast`),
  excluding its handled abilities from combat autocast.
- Plan agreed: Ability AI adds what it lacks (NPC VPE casting with real targeting, drafted combat casting for
  colonists, per-pawn limits, walk-in/return) and **stands down on any ability Better Autocasting has a handler for**
  when that mod is active. Do not replicate its per-spell handler design.
- Check which mod's TryGetAttackVerb patch is live and make sure we don't double-cast with either.

## Suggested order
1. A (interval) and B (blocked jobs) in this mod; build, review, test.
2. VPE: adapter design first (short design note + operator OK), then the compat assembly, then patches per VPE
   path/addon, with 1 and 4 designed against real spells.
