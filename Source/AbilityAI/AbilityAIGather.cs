using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// waitForGather: an area attack at a fixed spot (a Runesmith Invoke at a runecircle) holds while enemies are still
// walking into it. Where a pawn will be is read from the path it is already following, so nothing is pathfound: the
// cell it reaches after so many ticks at its cardinal move speed. Terrain cost and diagonals make real pawns a little
// slower than that, and a pawn that changes its mind makes the guess wrong, so this is a best guess for a few seconds.
namespace AbilityAI
{
    public static class AbilityAIGather
    {
        /// <summary>How far past the landing the AI looks for enemies still coming.</summary>
        public const int LookAheadTicks = 180;
        /// <summary>The longest a spot is held, so a steady trickle of enemies can't keep it waiting forever.</summary>
        public const int MaxHoldTicks = 300;
        private const int SampleStep = 30;
        private const int Samples = LookAheadTicks / SampleStep + 1;
        // A hold not renewed for this long is over. Only casters checked every 30 ticks hold (a slow-checked one is
        // never held: AbilityAIContext.SlowCheck), but a caster that casts at another circle meanwhile isn't checked
        // again for the cast gap (MinGapSeconds), which mustn't restart the cap
        private static int LapseTicks => Mathf.Max(120, Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * 60f) + 60);

        private struct Hold { public int first, last; }
        // Keyed by spot and ability def, so two casters watching one circle share its hold and its cap
        private static readonly Dictionary<long, Hold> holds = new Dictionary<long, Hold>();
        private static readonly List<long> lapsed = new List<long>();

        /// <summary>Some ability has a waitForGather rule: the settings are shown.</summary>
        public static bool Any { get; private set; }

        /// <summary>Called at startup for every ability def of every system, beside AbilityAIRecast.Note.</summary>
        public static void Note(Def abilityDef)
        {
            if (Any) return;
            var exts = abilityDef?.modExtensions;
            if (exts == null) return;
            for (int i = 0; i < exts.Count; i++)
                if (exts[i] is AbilityAIExtension ext && ext.enabled && ext.waitForGather) { Any = true; return; }
        }

        public static bool On(AbilityAIContext ctx, AbilityAIExtension ext)
        {
            if (!ext.waitForGather) return false;
            Faction f = ctx.pawn.Faction;
            return f != null && f.IsPlayer ? AbilityAISettings.GatherWaitPlayer : AbilityAISettings.GatherWaitNpcs;
        }

        /// <summary>Ticks from now to each sample: the landing, then every SampleStep up to LookAheadTicks past it.</summary>
        public static int SampleTicks(int landing, int sample) => landing + sample * SampleStep;

        public static int SampleCount => Samples;

        /// <summary>Appends the pawn's cell at each sample to into. A pawn not walking stays where it is. Cost: one
        /// move speed read and one path read per sample.</summary>
        public static void Predict(Pawn p, int landing, List<IntVec3> into)
        {
            var pather = p.pather;
            PawnPath path = pather?.curPath;
            if (pather == null || !pather.Moving || path == null || !path.Found || path.NodesLeftCount <= 0)
            {
                for (int k = 0; k < Samples; k++) into.Add(p.Position);
                return;
            }
            float perCell = Mathf.Max(1f, p.TicksPerMoveCardinal);
            // ConsumeNextNode decrements past the node it returns, so Peek(0) is nextCell itself and Peek(n) the cell n
            // steps after it; Peek(NodesLeftCount - 1) is the destination
            int last = path.NodesLeftCount - 1;
            for (int k = 0; k < Samples; k++)
            {
                float t = SampleTicks(landing, k) - pather.nextCellCostLeft;
                if (t < 0f) into.Add(p.Position);
                else into.Add(path.Peek(Mathf.Min((int)(t / perCell), last)));
            }
        }

        /// <summary>The pawn's cell at the landing alone.</summary>
        public static IntVec3 CellAt(Pawn p, int landing)
        {
            var pather = p.pather;
            PawnPath path = pather?.curPath;
            if (pather == null || !pather.Moving || path == null || !path.Found || path.NodesLeftCount <= 0) return p.Position;
            float t = landing - pather.nextCellCostLeft;
            if (t < 0f) return p.Position;
            return path.Peek(Mathf.Min((int)(t / Mathf.Max(1f, p.TicksPerMoveCardinal)), path.NodesLeftCount - 1));
        }

        /// <summary>One of the pawns, walking, will be within r2 of c at the landing. Pawns standing still are left to
        /// the ordinary check of where they are now.</summary>
        public static bool AnyArriving(List<Pawn> pawns, IntVec3 c, float r2, int landing, AbilityAITargetKinds kinds, Pawn skip)
        {
            bool all = (kinds & AbilityAITargetKinds.All) == AbilityAITargetKinds.All;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == skip || p.pather == null || !p.pather.Moving) continue;
                if ((CellAt(p, landing) - c).LengthHorizontalSquared > r2) continue;
                if (all || AbilityAIUtility.IsOfKind(kinds, p)) return true;
            }
            return false;
        }

        /// <summary>Whether this spot may be held for this ability now: true until it has been held MaxHoldTicks in a
        /// row. Renews the hold.</summary>
        public static bool MayHold(Thing spot, Def abilityDef, int tick)
        {
            long key = ((long)spot.thingIDNumber << 16) | abilityDef.shortHash;
            if (!holds.TryGetValue(key, out Hold h) || tick - h.last > LapseTicks || tick < h.last) h.first = tick;
            h.last = tick;
            holds[key] = h;
            if (holds.Count > 64) Prune(tick);
            return tick - h.first < MaxHoldTicks;
        }

        private static void Prune(int tick)
        {
            foreach (var kv in holds)
                if (tick - kv.Value.last > LapseTicks || tick < kv.Value.last) lapsed.Add(kv.Key);
            for (int i = 0; i < lapsed.Count; i++) holds.Remove(lapsed[i]);
            lapsed.Clear();
        }
    }
}
