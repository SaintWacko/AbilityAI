using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI.Group;

// Staged fights for The Force's rules (Patches/AbilityAI_TheForce.xml): Force users on both sides, every ruled power
// dealt out among them, so one fight shows most rules from the player's autocast and the NPC driver at once. The Force
// is reached by reflection: Ability AI has no reference to it.
namespace AbilityAI
{
    internal static class AbilityAIForceDebug
    {
        private const string ForcePackageId = "lee.theforce.standalone";
        private const string SensitivityTrait = "Force_NeutralSensitivity";
        // Major sensitivity; level 20 is 120 Force points before Force_FPMax, enough for several casts of the dearest
        private const int SensitivityDegree = 2;
        private const int ForceLevel = 20;

        private static readonly int[] RaidSizes = { 3, 6, 10 };
        private static readonly int[] SquadSizes = { 3, 5, 8 };

        [DebugAction("Ability AI", "Force: Force user raid", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> ForceRaid()
        {
            var nodes = new List<DebugActionNode>();
            foreach (int size in RaidSizes)
            {
                int count = size;
                nodes.Add(new DebugActionNode($"{count} raiders", DebugActionType.ToolMap, () =>
                {
                    if (!Ready()) return;
                    Faction enemy = AbilityAIPerfDebug.EnemyFaction();
                    if (enemy == null) return;
                    Map map = Find.CurrentMap;
                    var pawns = SpawnForceUsers(map, UI.MouseCell(), enemy, enemy, count, out int powers);
                    if (pawns.Count == 0) return;
                    LordMaker.MakeNewLord(enemy, new LordJob_AssaultColony(enemy, canKidnap: false, canTimeoutOrFlee: false,
                        canSteal: false), map, pawns);
                    Messages.Message($"Force: {pawns.Count} {enemy.Name} Force users, {powers} powers dealt", pawns[0],
                        MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            return nodes;
        }

        [DebugAction("Ability AI", "Force: Force user colonists", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> ForceColonists()
        {
            var nodes = new List<DebugActionNode>();
            foreach (int size in SquadSizes)
            {
                int count = size;
                nodes.Add(new DebugActionNode($"{count} colonists", DebugActionType.ToolMap, () =>
                {
                    if (!Ready()) return;
                    Faction enemy = AbilityAIPerfDebug.EnemyFaction();
                    if (enemy == null) return;
                    var pawns = SpawnForceUsers(Find.CurrentMap, UI.MouseCell(), Faction.OfPlayer, enemy, count, out int powers);
                    if (pawns.Count == 0) return;
                    int modes = pawns.Sum(AbilityAIPerfDebug.AutocastAll);
                    Messages.Message($"Force: {pawns.Count} drafted Force users, {powers} powers dealt, {modes} set to autocast",
                        pawns[0], MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            return nodes;
        }

        private static Type compType;
        private static FieldInfo levelField;
        private static MethodInfo recalcMax, recover, clearCache;
        private static PropertyInfo maxFP;
        private static bool resolved, usable;

        private static bool Ready()
        {
            if (!ModsConfig.IsActive(ForcePackageId))
            {
                Messages.Message("Force: The Force Standalone is not active", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (!resolved)
            {
                resolved = true;
                compType = AccessTools.TypeByName("TheForce_Standalone.CompClass_ForceUser");
                levelField = compType == null ? null : AccessTools.Field(compType, "forceLevel");
                recalcMax = compType == null ? null : AccessTools.Method(compType, "RecalculateMaxFP");
                recover = compType == null ? null : AccessTools.Method(compType, "RecoverFP");
                maxFP = compType == null ? null : AccessTools.Property(compType, "MaxFP");
                clearCache = AccessTools.Method("TheForce_Standalone.ForceSensitivityUtils:ClearCacheForPawn");
                usable = levelField != null && recalcMax != null && recover != null && maxFP != null && clearCache != null
                    && DefDatabase<TraitDef>.GetNamedSilentFail(SensitivityTrait) != null;
            }
            if (!usable) Messages.Message("Force: The Force's types or trait have changed; see the code", MessageTypeDefOf.RejectInput, false);
            return usable;
        }

        // Every Force power with an Ability AI rule, shuffled and dealt round, so each side holds all of them between
        // its pawns; a pawn of a raid of 3 gets about 14
        private static List<Pawn> SpawnForceUsers(Map map, IntVec3 near, Faction faction, Faction kindsFrom, int count, out int powers)
        {
            powers = 0;
            var pawns = new List<Pawn>();
            if (!near.InBounds(map)) return pawns;
            var defs = DefDatabase<AbilityDef>.AllDefsListForReading
                .Where(d => d.modContentPack?.PackageId == ForcePackageId && d.HasModExtension<AbilityAIExtension>())
                .InRandomOrder().ToList();
            if (defs.Count == 0)
            {
                Messages.Message("Force: no Force power has an Ability AI rule", MessageTypeDefOf.RejectInput, false);
                return pawns;
            }
            TraitDef trait = DefDatabase<TraitDef>.GetNamed(SensitivityTrait);
            bool player = faction.IsPlayer;
            for (int i = 0; i < count; i++)
            {
                var request = new PawnGenerationRequest(kindsFrom.RandomPawnKind(), faction,
                    player ? PawnGenerationContext.PlayerStarter : PawnGenerationContext.NonPlayer,
                    mustBeCapableOfViolence: true);
                Pawn p = PawnGenerator.GeneratePawn(request);
                if (p.story?.traits == null || p.abilities == null) continue;
                // The trait conflicts with psychic sensitivity, and the Force's own check caches a pawn's answer: the
                // trait goes on before the spawn, where the Force comp sets itself up for a valid user
                foreach (Trait t in p.story.traits.allTraits.Where(t => t.def == trait || trait.ConflictsWith(t)).ToList())
                    p.story.traits.RemoveTrait(t);
                p.story.traits.GainTrait(new Trait(trait, SensitivityDegree, forced: true));
                clearCache.Invoke(null, new object[] { p });
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(near, map, 6), map);
                ThingComp comp = p.AllComps.FirstOrDefault(c => compType.IsInstanceOfType(c));
                if (comp != null)
                {
                    levelField.SetValue(comp, ForceLevel);
                    recalcMax.Invoke(comp, null);
                    recover.Invoke(comp, new object[] { (float)maxFP.GetValue(comp) });
                }
                if (player && p.drafter != null) p.drafter.Drafted = true;
                pawns.Add(p);
            }
            // Not through the Force window: abilities given outright are never hidden by its presets (ShouldHideGizmo
            // asks only about unlocked ones), and its UpdatePawnAbilities only ever adds
            for (int i = 0; i < defs.Count && pawns.Count > 0; i++)
            {
                pawns[i % pawns.Count].abilities.GainAbility(defs[i]);
                powers++;
            }
            return pawns;
        }
    }
}
