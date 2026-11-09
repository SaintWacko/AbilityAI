using System.Collections.Generic;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using VanillaPsycastsExpanded;
using VEF.Abilities;
using Verse;
using Verse.AI.Group;
using VefAbilityDef = VEF.Abilities.AbilityDef;

namespace AbilityAI.Vpe
{
    // VPE's Psycasts tab, and its dev mode, only show on player pawns, so there is no way in the game to hand a raider
    // the psycasts that have Ability AI rules, or to see which of them VPE would refuse it. These do both on any
    // humanlike, and leave the pawn in its lord.
    internal static class VpeDebug
    {
        private static readonly string[] RuledPsycasts =
        {
            "VPE_Stun", "VPE_Bolt", "VPE_Overshield", "VPE_PowerLeap", "VPE_Stabilize", "VPE_WordofSerenity",
        };

        // Level 6 for the heat room: VPE's heat limit grows by 5 a level, and Overshield alone is 50 heat
        private const int TestLevel = 6;

        [DebugAction("Ability AI", "Give VPE test psycasts", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void GiveTestPsycasts(Pawn pawn) =>
            Give(pawn, RuledPsycasts.Select(n => DefDatabase<VefAbilityDef>.GetNamedSilentFail(n)).Where(d => d != null));

        // Every psycast with a zone or marker rule, from whichever paths they are on, to try zone casting on one colonist
        [DebugAction("Ability AI", "Give VPE zone psycasts", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void GiveZonePsycasts(Pawn pawn) =>
            Give(pawn, AbilityAIZones.RuleDefs.Concat(AbilityAISpots.RuleDefs).OfType<VefAbilityDef>());

        // Group Link and every Word with a rule, to try linking first on one pawn
        [DebugAction("Ability AI", "Give VPE group link and words", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void GiveLinkPsycasts(Pawn pawn) =>
            Give(pawn, DefDatabase<VefAbilityDef>.AllDefsListForReading.Where(d => d.HasModExtension<AbilityAIExtension>()
                && d.abilityClass != null && (typeof(Ability_WordOf).IsAssignableFrom(d.abilityClass) || typeof(Ability_GroupLink).IsAssignableFrom(d.abilityClass))));

        // One entry per VPE path, giving every psycast of it that has an Ability AI rule, to try a path's rules on
        // raiders without a psycaster of that path turning up
        [DebugAction("Ability AI", "Give VPE path psycasts", allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static List<DebugActionNode> GivePathPsycasts()
        {
            var nodes = new List<DebugActionNode>();
            var ruled = DefDatabase<VefAbilityDef>.AllDefsListForReading
                .Where(d => d.HasModExtension<AbilityAIExtension>() && d.GetModExtension<AbilityExtension_Psycast>()?.path != null)
                .GroupBy(d => d.GetModExtension<AbilityExtension_Psycast>().path)
                .OrderBy(g => g.Key.label);
            foreach (var group in ruled)
            {
                var defs = group.ToList();
                nodes.Add(new DebugActionNode($"{group.Key.LabelCap} ({defs.Count})", DebugActionType.ToolMapForPawns,
                    pawnAction: p => Give(p, defs)));
            }
            return nodes;
        }

        private static readonly int[] SummonerSquadSizes = { 3, 5, 7 };

        // Drafted colonists holding Alpha Animals' Herald of the Black Hive path, every psycast set to autocast, for
        // measuring the Deploy worker (36 cells, each counting enemies in sight) beside a "Perf: gear raid". Colonists,
        // because the summons are off for NPCs: Ability_Summon always makes player-faction creatures.
        [DebugAction("Ability AI", "Perf: Black Hive summoners", allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static List<DebugActionNode> BlackHiveSummoners()
        {
            var nodes = new List<DebugActionNode>();
            foreach (int size in SummonerSquadSizes)
            {
                int count = size;
                nodes.Add(new DebugActionNode($"{count} summoners", DebugActionType.ToolMap, () =>
                {
                    var path = DefDatabase<VefAbilityDef>.GetNamedSilentFail("AAVPE_SummonBlackScarab")
                        ?.GetModExtension<AbilityExtension_Psycast>()?.path;
                    if (path == null)
                    {
                        Messages.Message("Perf: Black Hive summoners need Alpha Animals", MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Faction enemy = AbilityAIPerfDebug.EnemyFaction();
                    if (enemy == null) return;
                    var defs = DefDatabase<VefAbilityDef>.AllDefsListForReading
                        .Where(d => d.HasModExtension<AbilityAIExtension>() && d.GetModExtension<AbilityExtension_Psycast>()?.path == path)
                        .ToList();
                    var pawns = AbilityAIPerfDebug.SpawnFighters(Find.CurrentMap, UI.MouseCell(), Faction.OfPlayer, enemy, count);
                    int modes = 0;
                    foreach (Pawn p in pawns)
                    {
                        Give(p, defs, quiet: true);
                        modes += AbilityAIPerfDebug.AutocastAll(p);
                    }
                    Messages.Message($"Perf: {pawns.Count} drafted Black Hive summoners, {defs.Count} psycasts each, {modes} set to autocast",
                        pawns.FirstOrDefault(), MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            return nodes;
        }

        private static readonly int[] PsycasterSizes = { 3, 5, 7 };
        // Fixed so an off/on pair of fights deals the same psycasts to the same places in the squad; pawn kinds and
        // gear still differ between spawns
        private const int DealSeed = 4711;

        // Psycasters on both sides for measuring the driver in a run-27 sized fight: every psycast with a rule the
        // driver casts (zone and marker rules are work, not combat), dealt round the squad, so each side holds all of
        // them between its pawns and most pawns hold several value rules near each other in priority
        [DebugAction("Ability AI", "Perf: VPE psycaster raid", allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static List<DebugActionNode> PsycasterRaid() => PsycasterNodes(player: false);

        [DebugAction("Ability AI", "Perf: VPE psycaster colonists", allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static List<DebugActionNode> PsycasterColonists() => PsycasterNodes(player: true);

        private static List<DebugActionNode> PsycasterNodes(bool player)
        {
            var nodes = new List<DebugActionNode>();
            foreach (int size in PsycasterSizes)
            {
                int count = size;
                nodes.Add(new DebugActionNode(player ? $"{count} colonists" : $"{count} raiders", DebugActionType.ToolMap, () =>
                {
                    Faction enemy = AbilityAIPerfDebug.EnemyFaction();
                    if (enemy == null) return;
                    Map map = Find.CurrentMap;
                    var pawns = AbilityAIPerfDebug.SpawnFighters(map, UI.MouseCell(), player ? Faction.OfPlayer : enemy, enemy, count)
                        .Where(p => p.RaceProps.Humanlike && p.GetComp<CompAbilities>() != null).ToList();
                    if (pawns.Count == 0) return;
                    var work = new HashSet<Def>(AbilityAIZones.RuleDefs.Concat(AbilityAISpots.RuleDefs));
                    var defs = DefDatabase<VefAbilityDef>.AllDefsListForReading
                        .Where(d => d.HasModExtension<AbilityAIExtension>() && d.GetModExtension<AbilityExtension_Psycast>() != null
                            && !work.Contains(d))
                        .OrderBy(d => d.defName).ToList();
                    Rand.PushState(DealSeed);
                    try { defs = defs.InRandomOrder().ToList(); }
                    finally { Rand.PopState(); }
                    var hands = pawns.Select(_ => new List<VefAbilityDef>()).ToList();
                    for (int i = 0; i < defs.Count; i++) hands[i % pawns.Count].Add(defs[i]);
                    int modes = 0;
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Give(pawns[i], hands[i], quiet: true);
                        if (player) modes += AbilityAIPerfDebug.AutocastAll(pawns[i]);
                    }
                    if (!player)
                        LordMaker.MakeNewLord(enemy, new LordJob_AssaultColony(enemy, canKidnap: false, canTimeoutOrFlee: false,
                            canSteal: false), map, pawns);
                    Messages.Message(player
                            ? $"Perf: {pawns.Count} drafted psycasters, {defs.Count} psycasts dealt, {modes} set to autocast"
                            : $"Perf: {pawns.Count} {enemy.Name} psycasters, {defs.Count} psycasts dealt",
                        pawns[0], MessageTypeDefOf.TaskCompletion, false);
                }));
            }
            return nodes;
        }

        private static void Give(Pawn pawn, IEnumerable<VefAbilityDef> defs, bool quiet = false)
        {
            var comp = pawn.GetComp<CompAbilities>();
            if (comp == null || !pawn.RaceProps.Humanlike)
            {
                Messages.Message($"{pawn.LabelShort} can't hold VPE psycasts", pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }
            // Adding a psylink is what makes VPE add its own psycast hediff (a postfix on Hediff_Psylink.PostAdd)
            if (pawn.Psycasts() == null)
                pawn.health.AddHediff(HediffDefOf.PsychicAmplifier, pawn.health.hediffSet.GetBrain());
            var psycasts = pawn.Psycasts();
            if (psycasts == null)
            {
                Messages.Message($"VPE gave {pawn.LabelShort} no psycast hediff", pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (psycasts.level < TestLevel) psycasts.ChangeLevel(TestLevel - psycasts.level, sendLetter: false);

            var given = defs.ToList();
            foreach (var def in given) comp.GiveAbility(def);
            pawn.psychicEntropy?.OffsetPsyfocusDirectly(1f);

            if (quiet) return;
            Messages.Message($"{pawn.LabelShort}: psylink {psycasts.level}, {given.Count} psycasts, full psyfocus",
                pawn, MessageTypeDefOf.TaskCompletion, false);
            // GiveAbility adds a psycast whose path the pawn can't unlock (backstory, meme, gene) all the same
            ShowStatus(pawn);
        }

        // 3x5 glyphs for the Workshop preview, top row first
        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['A'] = new[] { ".#.", "#.#", "###", "#.#", "#.#" },
            ['B'] = new[] { "##.", "#.#", "##.", "#.#", "##." },
            ['I'] = new[] { "###", ".#.", ".#.", ".#.", "###" },
            ['L'] = new[] { "#..", "#..", "#..", "#..", "###" },
            ['T'] = new[] { "###", ".#.", ".#.", ".#.", ".#." },
            ['Y'] = new[] { "#.#", "#.#", ".#.", ".#.", ".#." },
        };

        // A pinhole is drawn 2 tiles wide, so dots closer than 2 run together into a blob
        private const int Pitch = 2;

        // Stamps "ABILITY" in solar pinholes with "AI" in ice crystals under it, centred on the clicked cell, and
        // moves the clock to 22:00 so the glow shows
        [DebugAction("Ability AI", "Stage preview scene", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void StagePreviewScene()
        {
            var map = Find.CurrentMap;
            var centre = UI.MouseCell();
            var pinhole = DefDatabase<ThingDef>.GetNamedSilentFail("SolarPinhole");
            var crystal = DefDatabase<ThingDef>.GetNamedSilentFail("VPE_IceCrystal");
            if (pinhole == null || crystal == null)
            {
                Messages.Message("Preview scene needs Royalty's solar pinhole and VPE's ice crystal", MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!centre.InBounds(map)) return;

            // Line height 5 dots, a 2 dot gap, then the second line
            int top = centre.z + (5 + 2 + 5) * Pitch / 2;
            int placed = Stamp(map, "ABILITY", pinhole, centre.x, top, fillDot: false)
                + Stamp(map, "AI", crystal, centre.x, top - 7 * Pitch, fillDot: true);

            float hour = GenLocalDate.HourFloat(map);
            int toNight = (int)((22f - hour + 24f) % 24f * GenDate.TicksPerHour);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + toNight);
            Messages.Message($"Preview scene: {placed} things placed", MessageTypeDefOf.TaskCompletion, false);
        }

        private static int Stamp(Map map, string word, ThingDef def, int centreX, int top, bool fillDot)
        {
            int cols = word.Length * 4 - 1;
            int left = centreX - cols * Pitch / 2;
            int placed = 0;
            for (int i = 0; i < word.Length; i++)
            {
                var glyph = Glyphs[word[i]];
                for (int row = 0; row < glyph.Length; row++)
                for (int col = 0; col < 3; col++)
                {
                    if (glyph[row][col] != '#') continue;
                    var dot = new IntVec3(left + (i * 4 + col) * Pitch, 0, top - row * Pitch);
                    // A 1x1 crystal on a 2 tile pitch reads as dotted, so crystals fill the whole dot
                    int size = fillDot ? Pitch : 1;
                    for (int dx = 0; dx < size; dx++)
                    for (int dz = 0; dz < size; dz++)
                    {
                        var cell = dot + new IntVec3(dx, 0, -dz);
                        // The crystal is an edifice, so spawning it would vanish whatever building is already there
                        if (!cell.InBounds(map) || cell.Impassable(map)
                            || (def.category == ThingCategory.Building && cell.GetEdifice(map) != null)) continue;
                        var thing = ThingMaker.MakeThing(def);
                        if (def.category == ThingCategory.Building) thing.SetFactionDirect(Faction.OfPlayer);
                        GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
                        placed++;
                    }
                }
            }
            return placed;
        }

        [DebugAction("Ability AI", "Show VPE psycast status", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap, requiresRoyalty = true)]
        private static void ShowStatus(Pawn pawn)
        {
            var learned = pawn.GetComp<CompAbilities>()?.LearnedAbilities;
            if (learned == null) return;
            var sb = new StringBuilder();
            var pe = pawn.psychicEntropy;
            sb.Append(pawn.LabelShort);
            if (pe != null) sb.Append($": heat {pe.EntropyValue:F0} / {pe.MaxEntropy:F0}, psyfocus {pe.CurrentPsyfocus:P0}");
            sb.AppendLine();
            foreach (var ability in learned)
            {
                if (!ability.def.HasModExtension<AbilityAIExtension>()) continue;
                var psycast = ability.def.GetModExtension<AbilityExtension_Psycast>();
                string cost = psycast == null ? ""
                    : $" ({psycast.GetPsyfocusUsedByPawn(pawn):P0} psyfocus, {psycast.GetEntropyUsedByPawn(pawn):F0} heat)";
                string state = ability.IsEnabledForPawn(out string reason) ? "ready" : reason.NullOrEmpty() ? "refused" : reason;
                sb.AppendLine($"{ability.def.LabelCap}{cost}: {state}");
            }
            Find.WindowStack.Add(new Dialog_MessageBox(sb.ToString().TrimEnd()));
        }
    }
}
