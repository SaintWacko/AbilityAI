using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

// Jobs undrafted autocast won't break off. The hard rules in AbilityAIDriver.CanActNowPlayer (lying down, carrying,
// a job the player ordered, a job that can't be casually interrupted) always apply; this list adds the jobs that
// could be interrupted but that a player would rather see finished: meals, recreation, gatherings, childcare.
//
// Saved as a difference from the defaults (defNames added, defNames removed) rather than as the whole list, so:
// - a default added in a later version reaches players who already saved their settings;
// - a defName from a mod or DLC that isn't loaded right now is simply kept and ignored, and comes back into force
//   when that mod does, instead of being dropped the first time the settings are written without it.
// Reset clears both lists.
namespace AbilityAI
{
    public static class AbilityAIBlockedJobs
    {
        /// <summary>The default list. Every one is casualInterruptible in vanilla (checked against the 1.6 defs):
        /// jobs that can't be interrupted are protected anyway and have no business here.</summary>
        public static readonly HashSet<string> Defaults = new HashSet<string>
        {
            // Core
            "Ingest", "Meditate", "Pray", "Skygaze", "GoForWalk", "ViewArt", "VisitGrave", "BuildSnowman", "SocialRelax",
            "Play_Horseshoes", "Play_Hoopstone", "Play_GameOfUr", "Play_Chess", "Play_Billiards", "Play_Poker",
            "WatchTelevision", "UseTelescope", "Play_MusicalInstrument", "SpectateCeremony", "StandAndBeSociallyActive",
            // Ideology
            "Dance", "GotoAndBeSociallyActive", "EatAtCannibalPlatter", "MeditatePray",
            // Royalty
            "Reign", "BestowingCeremony",
            // Biotech
            "Breastfeed", "BottleFeedBaby", "PlayStatic", "PlayWalking", "PlayToys", "Skydreaming", "NatureRunning",
            "Radiotalking", "Floordrawing", "Workwatching", "Lessontaking", "Lessongiving", "TryRomance",
            // Anomaly
            "GoldenCubePlay",
        };

        private static List<string> added = new List<string>();
        private static List<string> removed = new List<string>();

        // Resolved on first use after defs load, and again after any change: one array read per check in play
        private static bool[] blocked;
        private static int count;

        /// <summary>Whether undrafted autocast must leave this job alone. One array read once resolved.</summary>
        public static bool IsBlocked(JobDef def)
        {
            if (blocked == null) Resolve();
            int i = def.index;
            return i < blocked.Length && blocked[i];
        }

        /// <summary>How many loaded JobDefs are on the list.</summary>
        public static int Count
        {
            get
            {
                if (blocked == null) Resolve();
                return count;
            }
        }

        public static void Set(JobDef def, bool on)
        {
            string name = def.defName;
            bool isDefault = Defaults.Contains(name);
            added.Remove(name);
            removed.Remove(name);
            if (on && !isDefault) added.Add(name);
            else if (!on && isDefault) removed.Add(name);
            blocked = null;
        }

        public static void Reset()
        {
            added.Clear();
            removed.Clear();
            blocked = null;
        }

        public static void ExposeData()
        {
            Scribe_Collections.Look(ref added, "abilityAI_blockedJobsAdded", LookMode.Value);
            Scribe_Collections.Look(ref removed, "abilityAI_blockedJobsRemoved", LookMode.Value);
            if (added == null) added = new List<string>();
            if (removed == null) removed = new List<string>();
            blocked = null;
        }

        private static void Resolve()
        {
            var arr = new bool[DefDatabase<JobDef>.DefCount];
            foreach (string name in Defaults) Mark(arr, name, true);
            for (int i = 0; i < removed.Count; i++) Mark(arr, removed[i], false);
            for (int i = 0; i < added.Count; i++) Mark(arr, added[i], true);
            int n = 0;
            for (int i = 0; i < arr.Length; i++) if (arr[i]) n++;
            count = n;
            blocked = arr;
        }

        private static void Mark(bool[] arr, string name, bool on)
        {
            var def = DefDatabase<JobDef>.GetNamedSilentFail(name);
            if (def != null && def.index < arr.Length) arr[def.index] = on;
        }
    }

    /// <summary>Edits AbilityAIBlockedJobs, opened from the settings page. Lists every JobDef that can be casually
    /// interrupted (the others are protected anyway), less the fight jobs undrafted casting treats as its own.
    /// Checked jobs first. Rows keep their place while the window is open, so a row doesn't jump away from the
    /// cursor when it is ticked; the order is rebuilt when the search, the "checked only" toggle or reset changes.
    /// Only the visible rows are drawn.</summary>
    public class Dialog_BlockedJobs : Window
    {
        private const float RowHeight = 24f;
        private const float PackWidth = 110f;

        private struct Row
        {
            public JobDef def;     // null for a group header
            public string label;   // the header text for a header
            public string pack;
        }

        private readonly List<Row> all = new List<Row>();
        private readonly List<Row> shown = new List<Row>();
        private readonly QuickSearchWidget search = new QuickSearchWidget();
        private bool checkedOnly;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(620f, 680f);

        public Dialog_BlockedJobs()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            var defs = DefDatabase<JobDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                JobDef d = defs[i];
                if (!d.casualInterruptible || d == JobDefOf.AttackStatic || d == JobDefOf.AttackMelee) continue;
                all.Add(new Row { def = d, label = LabelOf(d), pack = d.modContentPack?.Name ?? "" });
            }
            all.SortBy(r => r.label);
            Rebuild();
        }

        // Most JobDefs have no label; their report string ("eating TargetA.") reads well enough without its targets
        private static string LabelOf(JobDef d)
        {
            if (!d.label.NullOrEmpty()) return d.label;
            string s = d.reportString;
            if (s.NullOrEmpty()) return d.defName;
            s = s.Replace(" TargetA", "").Replace(" TargetB", "").Replace(" TargetC", "").Replace("TargetA", "")
                .Replace("TargetB", "").Replace("TargetC", "").Trim().TrimEnd('.').Trim();
            return s.NullOrEmpty() ? d.defName : s;
        }

        private void Rebuild()
        {
            shown.Clear();
            int on = 0, off = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantOn = pass == 0;
                if (!wantOn && checkedOnly) break;
                int header = shown.Count;
                shown.Add(default);
                int n = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    Row r = all[i];
                    if (AbilityAIBlockedJobs.IsBlocked(r.def) != wantOn) continue;
                    if (search.filter.Active && !search.filter.Matches(r.label) && !search.filter.Matches(r.def.defName)) continue;
                    shown.Add(r);
                    n++;
                }
                if (wantOn) on = n; else off = n;
                // No counts on the headers: rows stay in their group when ticked until the next rebuild, so a count
                // here would go stale. The description line above carries the live count.
                shown[header] = new Row
                {
                    label = wantOn ? "AAI_BlockedJobs_GroupOn".Translate() : "AAI_BlockedJobs_GroupOff".Translate(),
                };
            }
            search.noResultsMatched = on + off == 0;
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = inRect.y;
            Text.Font = GameFont.Medium;
            float titleH = Text.CalcHeight("AAI_BlockedJobs_Title".Translate(), inRect.width);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, titleH), "AAI_BlockedJobs_Title".Translate());
            y += titleH + 4f;
            Text.Font = GameFont.Small;
            string desc = "AAI_BlockedJobs_Desc".Translate(AbilityAIBlockedJobs.Count);
            float descH = Text.CalcHeight(desc, inRect.width);
            GUI.color = ColoredText.SubtleGrayColor;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, descH), desc);
            GUI.color = Color.white;
            y += descH + 8f;

            // Search, and the "checked only" toggle to its right
            string onlyLabel = "AAI_BlockedJobs_CheckedOnly".Translate();
            float onlyW = Text.CalcSize(onlyLabel).x + 40f;
            search.OnGUI(new Rect(inRect.x, y, inRect.width - onlyW - 10f, QuickSearchWidget.WidgetHeight), Rebuild);
            bool was = checkedOnly;
            Widgets.CheckboxLabeled(new Rect(inRect.xMax - onlyW, y, onlyW, QuickSearchWidget.WidgetHeight), onlyLabel, ref checkedOnly);
            if (was != checkedOnly) Rebuild();
            y += QuickSearchWidget.WidgetHeight + 6f;

            // The list: only the rows in view are drawn
            float bottom = inRect.yMax - 40f;
            var outer = new Rect(inRect.x, y, inRect.width, bottom - y - 6f);
            var view = new Rect(0f, 0f, outer.width - 16f, shown.Count * RowHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight));
            int last = Mathf.Min(shown.Count - 1, Mathf.CeilToInt((scroll.y + outer.height) / RowHeight));
            Text.Anchor = TextAnchor.MiddleLeft;
            for (int i = first; i <= last; i++)
            {
                Row r = shown[i];
                var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
                if (r.def == null)
                {
                    GUI.color = ColoredText.SubtleGrayColor;
                    Widgets.Label(row, r.label);
                    GUI.color = Color.white;
                    Widgets.DrawLineHorizontal(row.x, row.yMax - 2f, row.width);
                    continue;
                }
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                Widgets.DrawHighlightIfMouseover(row);
                bool on = AbilityAIBlockedJobs.IsBlocked(r.def);
                bool now = on;
                Widgets.Checkbox(new Vector2(row.x + 2f, row.y), ref now, RowHeight);
                var labelRect = new Rect(row.x + RowHeight + 8f, row.y, row.width - RowHeight - 8f - PackWidth, RowHeight);
                // Truncate the plain label only: truncating a string with a colour tag in it can cut the closing tag
                string dn = "  " + r.def.defName;
                float labelW = Mathf.Max(0f, labelRect.width - Text.CalcSize(dn).x);
                Widgets.Label(labelRect, r.label.CapitalizeFirst().Truncate(labelW) + dn.Colorize(ColoredText.SubtleGrayColor));
                GUI.color = ColoredText.SubtleGrayColor;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(row.xMax - PackWidth, row.y, PackWidth - 4f, RowHeight), r.pack.Truncate(PackWidth - 4f));
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(row, r.def.defName + (r.pack.NullOrEmpty() ? "" : " (" + r.pack + ")"));
                // A click anywhere on the row toggles it, not only on the box
                if (Widgets.ButtonInvisible(new Rect(labelRect.x, row.y, row.width - labelRect.x, RowHeight))) now = !on;
                if (now != on) AbilityAIBlockedJobs.Set(r.def, now);
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.EndScrollView();

            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 34f, 180f, 34f), "AAI_BlockedJobs_Reset".Translate()))
            {
                AbilityAIBlockedJobs.Reset();
                Rebuild();
            }
            if (Widgets.ButtonText(new Rect(inRect.xMax - 140f, inRect.yMax - 34f, 140f, 34f), "CloseButton".Translate()))
                Close();
        }
    }
}
