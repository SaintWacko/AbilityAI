using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI.Group;

// Staged fights for measuring the driver with Dubs Performance Analyzer. The last measurement (run 27) was a 7 vs 7
// psycaster fight; since then worn gear made every raider with a smokepop belt or a pack a caster, so the raid sizes
// here are what a plain vanilla raid can bring.
namespace AbilityAI
{
    internal static class AbilityAIPerfDebug
    {
        // Vanilla and DLC gear with rules that every faction may use. Left out: the orbital targeters (BaseWrecking,
        // off for NPCs by default, and a colonist would level the test map) and the biomutation lance (InstantKill).
        private static readonly string[] GearNames =
        {
            "Apparel_SmokepopBelt", "Apparel_FirefoampopPack", "Apparel_PackJump", "Apparel_PackBroadshield",
            "Apparel_PackTox", "Apparel_PackTurret", "Apparel_PackHunter", "Apparel_DisruptorFlarePack",
            "Apparel_DeadlifePack", "Apparel_ArmorMarineGrenadier", "Apparel_ArmorCataphractPhoenix",
            "Apparel_ArmorLocust", "Apparel_PsychicShockLance", "Apparel_PsychicInsanityLance",
        };

        private const int MaxRuledItems = 2;

        private static readonly int[] RaidSizes = { 10, 30, 60 };
        private static readonly int[] SquadSizes = { 5, 10, 20 };

        [DebugAction("Ability AI", "Perf: gear raid", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> GearRaid()
        {
            var nodes = new List<DebugActionNode>();
            foreach (int size in RaidSizes)
            {
                int count = size;
                nodes.Add(new DebugActionNode($"{count} raiders", DebugActionType.ToolMap, () =>
                {
                    Faction enemy = EnemyFaction();
                    if (enemy == null) return;
                    Map map = Find.CurrentMap;
                    var pawns = SpawnFighters(map, UI.MouseCell(), enemy, enemy, count);
                    int geared = pawns.Sum(GiveGear);
                    LordMaker.MakeNewLord(enemy, new LordJob_AssaultColony(enemy, canKidnap: false, canTimeoutOrFlee: false,
                        canSteal: false), map, pawns);
                    Messages.Message($"Perf: {pawns.Count} {enemy.Name} raiders, {geared} ruled items", pawns.FirstOrDefault(),
                        MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            return nodes;
        }

        // The same pawn kinds as the raid, so the two sides carry the same weapons
        [DebugAction("Ability AI", "Perf: geared colonists", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> GearedColonists()
        {
            var nodes = new List<DebugActionNode>();
            foreach (int size in SquadSizes)
            {
                int count = size;
                nodes.Add(new DebugActionNode($"{count} colonists", DebugActionType.ToolMap, () =>
                {
                    Faction enemy = EnemyFaction();
                    if (enemy == null) return;
                    var pawns = SpawnFighters(Find.CurrentMap, UI.MouseCell(), Faction.OfPlayer, enemy, count);
                    int geared = pawns.Sum(GiveGear);
                    int modes = pawns.Sum(AutocastAll);
                    Messages.Message($"Perf: {pawns.Count} drafted colonists, {geared} ruled items, {modes} set to autocast",
                        pawns.FirstOrDefault(), MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            return nodes;
        }

        private static Faction EnemyFaction()
        {
            Faction f = Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false, minTechLevel: TechLevel.Industrial)
                ?? Find.FactionManager.RandomEnemyFaction(allowNonHumanlike: false);
            if (f == null) Messages.Message("Perf: no humanlike enemy faction on this world", MessageTypeDefOf.RejectInput, false);
            return f;
        }

        /// <summary>Fighting pawns of kinds from kindsFrom's raid groups, spawned near the cell for faction. Player pawns
        /// come drafted, so they hold where they stand and shoot.</summary>
        internal static List<Pawn> SpawnFighters(Map map, IntVec3 near, Faction faction, Faction kindsFrom, int count)
        {
            var pawns = new List<Pawn>();
            bool player = faction.IsPlayer;
            for (int i = 0; i < count; i++)
            {
                var request = new PawnGenerationRequest(kindsFrom.RandomPawnKind(), faction,
                    player ? PawnGenerationContext.PlayerStarter : PawnGenerationContext.NonPlayer,
                    mustBeCapableOfViolence: true);
                Pawn p = PawnGenerator.GeneratePawn(request);
                GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(near, map, 6), map);
                if (player && p.drafter != null) p.drafter.Drafted = true;
                pawns.Add(p);
            }
            return pawns;
        }

        /// <summary>Puts up to two ruled items that fit together on the pawn, in a random order; returns how many.</summary>
        private static int GiveGear(Pawn p)
        {
            if (p.apparel == null) return 0;
            var pool = GearNames.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n))
                .Where(d => d != null && ApparelAIAbility.RuleDefs.Contains(d)).InRandomOrder().ToList();
            var given = new List<ThingDef>();
            foreach (ThingDef def in pool)
            {
                if (given.Count >= MaxRuledItems) break;
                if (!ApparelUtility.HasPartsToWear(p, def) || given.Any(g => !ApparelUtility.CanWearTogether(g, def, p.RaceProps.body)))
                    continue;
                var apparel = (Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
                // A fresh pawn's replaced clothes aren't worth a pile on the floor
                p.apparel.Wear(apparel, dropReplacedApparel: false);
                given.Add(def);
            }
            return given.Count;
        }

        /// <summary>Every ability and ruled item of a player pawn the menu would offer, set to the walking mode where the
        /// pawn can be drafted (as the menu does); returns how many.</summary>
        internal static int AutocastAll(Pawn p)
        {
            var abilities = new List<AIAbility>();
            AIAbilitySources.Collect(p, abilities);
            int set = 0;
            var mode = p.drafter != null ? AbilityAIAutocastMode.Full : AbilityAIAutocastMode.Stationary;
            foreach (AIAbility a in abilities)
            {
                if (!AbilityAIAutocast.Offers(a)) continue;
                AbilityAIAutocast.SetMode(p, a.Def, mode);
                set++;
            }
            return set;
        }
    }
}
