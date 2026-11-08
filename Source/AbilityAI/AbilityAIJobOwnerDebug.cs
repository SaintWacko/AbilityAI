using System.Collections.Generic;
using LudeonTK;
using Verse;
using Verse.AI;

// A stand-in job owner for testing the job-owner API without the mod it was written for. It owns every pawn while on.
// "Start the first" plays an arbiter sitting in the constant tree: it takes the first candidate that starts and hands
// the job back with no think node as its source, so IsOwnCast has to recognise it by Start's record, as it would for an
// owner's own node, and a save made mid-cast shows whether a source-less cast loads cleanly. Not saved.
namespace AbilityAI
{
    internal sealed class DevJobOwner : IAbilityAIJobOwner
    {
        internal enum Mode { Off, StartNothing, StartFirst }

        internal static Mode mode;
        private static bool registered;
        private static readonly DevJobOwner instance = new DevJobOwner();
        private static readonly List<AbilityAICandidate> offered = new List<AbilityAICandidate>();
        private static int offers, starts, refused;

        public bool Owns(Pawn pawn) => mode != Mode.Off;

        // From the constant-tree postfix, for a pawn the owners own and nothing else has a job for
        internal static void Give(Pawn pawn, ref ThinkResult result)
        {
            if (mode == Mode.Off || result.IsValid) return;
            AbilityAIDriver.GetCandidates(pawn, offered, 3);
            if (offered.Count == 0) return;
            offers++;
            if (AbilityAISettings.DebugLog)
            {
                var text = new System.Text.StringBuilder();
                for (int i = 0; i < offered.Count; i++)
                {
                    var c = offered[i];
                    if (i > 0) text.Append("; ");
                    text.Append(c.kind).Append(' ').Append(c.ability?.Label ?? "-").Append(" (").Append(c.category)
                        .Append(", priority ").Append(c.priority).Append(')');
                }
                Log.Message($"[Ability AI] dev job owner: {pawn.LabelShort} is offered {text}");
            }
            try
            {
                if (mode != Mode.StartFirst) return;
                for (int i = 0; i < offered.Count; i++)
                {
                    Job job = AbilityAIDriver.Start(pawn, offered[i]);
                    if (job == null) { refused++; continue; }
                    starts++;
                    result = new ThinkResult(job, null);
                    return;
                }
            }
            finally { offered.Clear(); }
        }

        [DebugAction("Ability AI", "Job owner test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static List<DebugActionNode> SetMode()
        {
            var nodes = new List<DebugActionNode>();
            foreach (Mode m in new[] { Mode.Off, Mode.StartNothing, Mode.StartFirst })
            {
                Mode chosen = m;
                string label = chosen == Mode.Off ? "Off" : chosen == Mode.StartNothing ? "Own every pawn, start nothing" : "Own every pawn, start the first";
                nodes.Add(new DebugActionNode(label, DebugActionType.Action, () =>
                {
                    if (!registered)
                    {
                        AbilityAIHooks.RegisterJobOwner(instance);
                        registered = true;
                    }
                    Log.Message($"[Ability AI] dev job owner: {mode} -> {chosen}; so far {offers} offers, {starts} started, {refused} refused at Start");
                    mode = chosen;
                    offers = starts = refused = 0;
                }));
            }
            return nodes;
        }
    }
}
