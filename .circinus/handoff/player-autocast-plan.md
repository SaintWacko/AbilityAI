# Player autocast, steps 3 to 5: agreed plan

Written at build 0011 (cb6ef76). Steps 1 and 2 (mode storage, right-click menu, badge, drafted Stationary) are done,
as are Entad's requests: `OthersStandDown`, hook vetoes on menu and badge, menu title from the gizmo label, group
menu only for `groupAbility` defs.

## Step 3: Full mode (agreed)

- Re-add Full to the `OpenMenu` mode list (a saved Full already loads and shows "auto+").
- Full = Stationary plus an approach walk, using the NPC approach logic (TryApproach / approachDistance), only
  drafted, in combat, ranged pawns, one walk at a time, same timeout. Only abilities set to Full walk.
- Start only from the hold/idle jobs `CanActNowPlayer` allows: never interrupt a player-ordered move. Our own
  approach job must be exempt from `CanActNowPlayer`'s "pather is moving" refusal.
- On starting a walk, record a non-saved per-pawn home cell plus whether it was on AttackStatic (and its target).
- At cast: queue the return walk, then the AttackStatic resume. Walk ends with nothing castable: go straight home.
- A new player order clears the queue: drop the home record at the same time (check `playerForced` on the new job).
- Setting "return to cover instead of the original spot" (default off): vanilla `CastPositionFinder` with the pawn's
  own weapon verb against the nearest enemy, `wantCoverFromTarget = true`, so it is a covered spot it can shoot from.
- Check first whether `JobDriver_CastAbility` already walks to range (`Toils_Combat.GotoCastPosition`).

## Step 4: undrafted rules (agreed)

For undrafted player pawns with a mode set:
- Any time: safe workers only (Recovery, Rescue, Calm, Firefighting): the existing `WorksUndrafted`.
- Fleeing (`IsFleeing`): Movement and Recovery too. Check vanilla's Flee job can be interrupted for this.
- Hostility response Attack (`playerSettings.UsesConfigurableHostilityResponse`, colonists and colony subhumans
  only) AND actually fighting (`ctx.InCombat` undrafted meaning: hostile target, or attacked/hurt in the last 10s):
  the drafted set. Not "any enemy on the map".
- Tame animals (no drafter, no hostility response): the drafted set while fighting (defending master, Release,
  being attacked: same `InCombat` check). Remove the `p.drafter != null` gate in `Offers` / `OpenMenu`.
- Gizmo visibility: no second menu route. A mode set while drafted persists; animal abilities need
  `displayGizmoWhileUndrafted` from the host (tell Bastle when it adopts Ability AI).
- Update `WorksUndrafted` / `HasWorker(def, undraftedOnly)` / `Handles` / `CanActNowPlayer` together, and the
  handoff docs' description of what undrafted pawns cast.

## Step 5: psycast limits (agreed: mockup A)

User wants it per pawn: a gizmo like vanilla's psyfocus gizmo with "max autocast heat" and "min autocast psyfocus".
Mockup: `.circinus/mockups/psycast-limits-gizmo.html` (A: own gizmo, recommended; B: handles on vanilla's gizmo).
- Per-pawn values in the same world component as the modes (saved). Pawns without an entry use the defaults:
  psyfocus floor 0%, heat cap unlimited (handle all the way right = no extra limit, vanilla's own heat-limit toggle
  decides overheat). Reset button returns a pawn to these. Only store an entry when it differs from the default.
- Gizmo shown only for a player pawn with a psychic entropy tracker and at least one real `Psycast` set to a non-Off
  mode.
- Rule: if the cast would push heat over the cap or psyfocus under the floor, don't cast it. Check beside
  `PsycastAffordable`: current heat + `EntropyGain` vs cap * MaxEntropy, current psyfocus - cost vs floor. Only real
  Psycasts; Entad's psycast copies are plain Abilities.
- Cost: the check runs only when a psycast candidate is evaluated (once per eval interval, not per tick), and is a
  dictionary lookup plus two compares, skipped entirely when the pawn has no entry. Cache the gizmo's "has an
  autocast psycast" answer per pawn for a short time instead of walking abilities every frame.
- NPCs: no setting. Vanilla already makes them pay psyfocus and respects their heat limit, and `PsycastAffordable`
  covers psyfocus; there is nothing for the player to manage on a raider.

## After all three

- Dubs Performance Analyzer with NPCs and player pawns both autocasting (earlier: 10 raiders,
  `Patch_AbilityAI_ConstantThinkTree` <= 0.003 ms/tick).
- Test checklist: Stationary casts while holding but not during an ordered move; right-click works during cooldown;
  group menu sets every pawn on a `groupAbility` button (ungrouped buttons set only their own pawn); badge
  placement full size and shrunk; undrafted casts; vetoed abilities show no menu or badge.
