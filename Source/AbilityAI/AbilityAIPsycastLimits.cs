using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// Per-pawn limits on what autocast may spend: a neural heat cap and a psyfocus floor for psycasts, and a floor for each
// resource in play (AIResources: hemogen, Force points) for any ability that costs some. Set from a gizmo beside
// vanilla's psyfocus one (design: .circinus/mockups/psycast-limits-gizmo.html, variant A, but without its reset button:
// dragging the handles to the ends is the reset; the resource rows: resource-limits.html), which shows only the rows
// that apply. Stored in the AbilityAIAutocast world component, applied by AbilityAIDriver.PsycastWithinLimits. Only
// abilities their own system calls psycasts count for heat and focus (AIAbility.IsPsycast: vanilla's Psycast, VPE's
// psycasts through the VEF and VPE compat); Entad's psycast copies are plain Abilities. VPE replaces vanilla's psyfocus
// gizmo with its own at the same Order (-100), so this one follows either. NPCs get no limits: vanilla already makes
// them pay psyfocus and respects their heat limit, and there is nothing for the player to manage on a raider.
namespace AbilityAI
{
    [StaticConstructorOnStartup]
    internal class Gizmo_PsycastLimits : Gizmo
    {
        // Vanilla's psyfocus gizmo colours, so the two read as a pair
        private static readonly Texture2D HeatBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.46f, 0.34f, 0.35f));
        private static readonly Texture2D HeatBarHighlightTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.56f, 0.42f, 0.43f));
        private static readonly Texture2D FocusBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.34f, 0.42f, 0.43f));
        private static readonly Texture2D FocusBarHighlightTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.43f, 0.54f, 0.55f));
        private static readonly Texture2D EmptyBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.03f, 0.035f, 0.05f));
        private static readonly Texture2D HandleTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.91f, 0.83f, 0.42f));
        // Over the part of a bar autocast won't go into
        private static readonly Texture2D BlockedTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0f, 0f, 0.45f));

        private const float LabelWidth = 63f;
        private const float BarWidth = 100f;
        private const float BarHeight = 22f;
        // With three rows: 3 × 19 plus two 3 px gaps fill the 63 px inside the margin
        private const float CompactBarHeight = 19f;
        // Between columns once there are four rows or more
        private const float ColumnGap = 6f;
        // 5% steps, as vanilla's psyfocus target
        private const int Increments = 20;

        // Per row, in draw order
        private static bool[] dragging = new bool[4];
        // One instance, re-pointed at the selected pawn: the gizmo is only shown for a single selected pawn
        private static readonly Gizmo_PsycastLimits shared = new Gizmo_PsycastLimits();

        // Which rows the selected pawn gets, rebuilt when the modes change, the selection changes, or after
        // CacheFrames frames (abilities or genes gained or lost)
        private const int CacheFrames = 60;
        private static Pawn cachedPawn;
        private static int cachedVersion = -1;
        private static int cachedUntilFrame;
        private static bool showPsycast;
        private static readonly List<AIResourceDef> shownResources = new List<AIResourceDef>();

        private Pawn pawn;

        private Gizmo_PsycastLimits()
        {
            // Right after vanilla's psyfocus gizmo (-100)
            Order = -99f;
        }

        internal static void ClearCache()
        {
            cachedPawn = null;
            cachedVersion = -1;
            shownResources.Clear();
            shared.pawn = null;
        }

        internal static Gizmo For(Pawn pawn)
        {
            shared.pawn = pawn;
            return shared;
        }

        /// <summary>A single selected player pawn with a row to show: the heat and focus rows with a psylink and a
        /// psycast the autocast menu offers; a resource's row when the pawn has that resource and the menu offers an
        /// ability that costs some. On autocast or not, so the limits can be set before turning anything on. Cost per
        /// GetGizmos call: a few field reads, and the ability walk once per CacheFrames frames or mode change.</summary>
        internal static bool ShouldShow(Pawn pawn)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) return false;
            if (pawn.Faction == null || !pawn.Faction.IsPlayer || pawn.IsPrisoner) return false;
            if (Find.Selector.SingleSelectedThing != pawn) return false;
            int frame = Time.frameCount;
            if (pawn != cachedPawn || AbilityAIAutocast.Version != cachedVersion || frame >= cachedUntilFrame)
            {
                cachedPawn = pawn;
                cachedVersion = AbilityAIAutocast.Version;
                cachedUntilFrame = frame + CacheFrames;
                Refresh(pawn);
            }
            return showPsycast || shownResources.Count > 0;
        }

        private static readonly List<AIAbility> collected = new List<AIAbility>();
        private static readonly List<AIResourceDef> held = new List<AIResourceDef>();

        // Every system's abilities with an extension, since only those can have a mode
        private static void Refresh(Pawn pawn)
        {
            bool psy = pawn.HasPsylink;
            showPsycast = false;
            shownResources.Clear();
            var resources = AIResources.Active;
            for (int i = 0; i < resources.Count; i++)
                if (resources[i].TryRead(pawn, out _, out _)) held.Add(resources[i]);
            try
            {
                if (!psy && held.Count == 0) return;
                AIAbilitySources.Collect(pawn, collected);
                for (int i = 0; i < collected.Count; i++)
                {
                    AIAbility a = collected[i];
                    if (a.PlayerAutocastElsewhere || !AbilityAIAutocast.Offers(a)) continue;
                    if (psy && a.IsPsycast) showPsycast = true;
                    for (int j = held.Count - 1; j >= 0; j--)
                        if (a.ResourceCost(held[j]) > 0f)
                        {
                            shownResources.Add(held[j]);
                            held.RemoveAt(j);
                        }
                }
                // Def order, whichever ability turned each up first
                shownResources.SortBy(r => r.index);
            }
            finally
            {
                collected.Clear();
                held.Clear();
            }
        }

        private static int RowCount => (showPsycast ? 2 : 0) + shownResources.Count;

        // Up to three rows in one column; from four, columns of two at full height
        private static int Columns(int rows) => rows <= 3 ? 1 : (rows + 1) / 2;

        // Label and bar per column plus the 6 px margin each side (no reset button: dragging the heat and focus
        // handles to the ends is their reset; a resource floor's default is on its tooltip)
        public override float GetWidth(float maxWidth)
        {
            int cols = Columns(RowCount);
            return cols * (LabelWidth + BarWidth) + (cols - 1) * ColumnGap + 12f;
        }

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            var rect = new Rect(topLeft.x, topLeft.y, GetWidth(maxWidth), 75f);
            Rect inner = rect.ContractedBy(6f);
            Widgets.DrawWindowBackground(rect);
            var pe = pawn?.psychicEntropy;
            bool psy = showPsycast && pe != null;
            int rows = (psy ? 2 : 0) + shownResources.Count;
            if (rows == 0) return new GizmoResult(GizmoState.Clear);
            if (dragging.Length < rows) System.Array.Resize(ref dragging, rows);

            if (rows == 1)
            {
                // A title fills the top row, so the one slider sits where the second row always does
                Text.Font = GameFont.Small;
                GUI.color = ColoredText.SubtleGrayColor;
                Widgets.Label(new Rect(inner.x, inner.y + 6f, inner.width, BarHeight), "AAI_Limits_Title".Translate());
                GUI.color = Color.white;
            }

            int row = 0;
            if (psy)
            {
                AbilityAIAutocast.TryGetLimits(pawn, out float heatCap, out float focusFloor);
                float newCap = heatCap, newFloor = focusFloor;

                // Heat: the bar is the pawn's heat now, the handle the cap; shaded to the right of it
                Rect heatRow = RowRect(inner, row, rows);
                Label(heatRow, "AAI_Limits_Heat".Translate());
                Rect heatBar = BarRect(heatRow);
                Widgets.DraggableBar(heatBar, HeatBarTex, HeatBarHighlightTex, EmptyBarTex, HandleTex, ref dragging[row],
                    pe.EntropyRelativeValue, ref newCap, null, Increments);
                Shade(heatBar, newCap, 1f);
                BarText(heatBar, newCap >= AbilityAIAutocast.NoHeatCap ? (string)"AAI_Limits_NoCap".Translate() : newCap.ToStringPercent());
                TooltipHandler.TipRegion(heatRow, () => "AAI_Limits_Heat_Tip".Translate(), 0x4A41C1);
                row++;

                // Psyfocus: the bar is the pawn's psyfocus now, the handle the floor; shaded to the left of it
                Rect focusRow = RowRect(inner, row, rows);
                Label(focusRow, "AAI_Limits_Focus".Translate());
                Rect focusBar = BarRect(focusRow);
                Widgets.DraggableBar(focusBar, FocusBarTex, FocusBarHighlightTex, EmptyBarTex, HandleTex, ref dragging[row],
                    pe.CurrentPsyfocus, ref newFloor, null, Increments);
                Shade(focusBar, 0f, newFloor);
                BarText(focusBar, newFloor <= AbilityAIAutocast.NoFocusFloor ? (string)"AAI_Limits_NoFloor".Translate() : newFloor.ToStringPercent());
                TooltipHandler.TipRegion(focusRow, () => "AAI_Limits_Focus_Tip".Translate(), 0x4A41C2);
                row++;

                if (newCap != heatCap || newFloor != focusFloor) AbilityAIAutocast.SetLimits(pawn, newCap, newFloor);
            }

            for (int i = 0; i < shownResources.Count; i++, row++)
            {
                // The bar is the pawn's amount now, the handle the floor; shaded to the left of it
                AIResourceDef r = shownResources[i];
                r.TryRead(pawn, out float value, out float max);
                float floor = AbilityAIAutocast.ResourceFloor(pawn, r), newFloor = floor;
                Rect resRow = RowRect(inner, row, rows);
                Label(resRow, r.limitLabel);
                Rect resBar = BarRect(resRow);
                Widgets.DraggableBar(resBar, r.BarTex, r.BarHighlightTex, EmptyBarTex, HandleTex, ref dragging[row],
                    max > 0f ? value / max : 0f, ref newFloor, null, Increments);
                Shade(resBar, 0f, newFloor);
                BarText(resBar, newFloor <= 0.001f ? (string)"AAI_Limits_NoFloor".Translate() : newFloor.ToStringPercent());
                TooltipHandler.TipRegion(resRow, new TipSignal(r.LimitTip, 0x4A41C3 + r.index));
                if (newFloor != floor) AbilityAIAutocast.SetResourceFloor(pawn, r, newFloor);
            }
            return new GizmoResult(GizmoState.Clear);
        }

        // Row tops: one row under a title; two full rows; three compact ones; from four, columns of two full rows
        private static Rect RowRect(Rect inner, int row, int rows)
        {
            if (rows == 1) return new Rect(inner.x, inner.y + 38f, LabelWidth + BarWidth, BarHeight);
            if (rows == 3) return new Rect(inner.x, inner.y + 22f * row, LabelWidth + BarWidth, CompactBarHeight);
            float x = inner.x + (row / 2) * (LabelWidth + BarWidth + ColumnGap);
            return new Rect(x, inner.y + 6f + 32f * (row % 2), LabelWidth + BarWidth, BarHeight);
        }

        private static Rect BarRect(Rect row) => new Rect(row.x + LabelWidth, row.y, BarWidth, row.height);

        // The row label, in the tiny font when a translation doesn't fit the small one
        private static void Label(Rect row, string text)
        {
            Text.Font = GameFont.Small;
            if (Text.CalcSize(text).x > LabelWidth - 3f) Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x, row.y, LabelWidth - 3f, row.height), text.Truncate(LabelWidth - 3f));
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        // Fractions of the bar's fill area, which Widgets.FillableBar insets by 3 px on each side
        private static void Shade(Rect bar, float from, float to)
        {
            if (to - from <= 0.001f) return;
            Rect fill = bar.ContractedBy(3f);
            GUI.DrawTexture(new Rect(fill.x + fill.width * from, fill.y, fill.width * (to - from), fill.height), BlockedTex);
        }

        private static void BarText(Rect bar, string text)
        {
            Text.Font = bar.height < BarHeight ? GameFont.Tiny : GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(bar, text);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }
    }

    /// <summary>Adds the autocast limits gizmo after the pawn's own. Pass-through: the original sequence is yielded
    /// unchanged and unbuffered.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_Pawn_PsycastLimitsGizmo
    {
        static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Pawn __instance)
        {
            foreach (var g in values) yield return g;
            if (Gizmo_PsycastLimits.ShouldShow(__instance)) yield return Gizmo_PsycastLimits.For(__instance);
        }
    }
}
