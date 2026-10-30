using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// Per-pawn limits on what autocast may spend: a neural heat cap and a psyfocus floor for psycasts, and a hemogen floor
// for any ability with a hemogen cost. Set from a gizmo beside vanilla's psyfocus one (design:
// .circinus/mockups/psycast-limits-gizmo.html, variant A, but without its reset button: dragging the handles to the
// ends is the reset; the hemogen row: psycast-limits-hemogen.html), which shows only the rows that apply. Stored in the
// AbilityAIAutocast world component, applied by AbilityAIDriver.PsycastWithinLimits. Only abilities their own system
// calls psycasts count (AIAbility.IsPsycast: vanilla's Psycast, VPE's psycasts through the VEF and VPE compat); Entad's
// psycast copies are plain Abilities. VPE replaces vanilla's psyfocus gizmo with its own at the same Order (-100), so
// this one follows either. NPCs get no limits: vanilla already makes them pay psyfocus and respects their
// heat limit, and there is nothing for the player to manage on a raider.
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
        // Near the game's hemogen gizmo red
        private static readonly Texture2D HemogenBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.54f, 0.16f, 0.16f));
        private static readonly Texture2D HemogenBarHighlightTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.66f, 0.22f, 0.22f));
        private static readonly Texture2D EmptyBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.03f, 0.035f, 0.05f));
        private static readonly Texture2D HandleTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.91f, 0.83f, 0.42f));
        // Over the part of a bar autocast won't go into
        private static readonly Texture2D BlockedTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0f, 0f, 0.45f));

        private const float LabelWidth = 63f;
        private const float BarWidth = 100f;
        private const float BarHeight = 22f;
        // With all three rows: 3 × 19 plus two 3 px gaps fill the 63 px inside the margin
        private const float CompactBarHeight = 19f;
        // 5% steps, as vanilla's psyfocus target
        private const int Increments = 20;

        private static bool draggingHeat, draggingFocus, draggingHemogen;
        // One instance, re-pointed at the selected pawn: the gizmo is only shown for a single selected pawn
        private static readonly Gizmo_PsycastLimits shared = new Gizmo_PsycastLimits();

        // Which rows the selected pawn gets, rebuilt when the modes change, the selection changes, or after
        // CacheFrames frames (abilities or genes gained or lost)
        private const int CacheFrames = 60;
        private static Pawn cachedPawn;
        private static int cachedVersion = -1;
        private static int cachedUntilFrame;
        private static bool showPsycast, showHemogen;
        private static Gene_Hemogen cachedGene;

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
            cachedGene = null;
            shared.pawn = null;
        }

        internal static Gizmo For(Pawn pawn)
        {
            shared.pawn = pawn;
            return shared;
        }

        /// <summary>A single selected player pawn with a row to show: the heat and focus rows with a psychic entropy
        /// tracker and a real psycast set to autocast; the hemogen row with a hemogen gene and any ability with a
        /// hemogen cost, on autocast or not, so the floor can be set before turning it on. Cost per GetGizmos call: a
        /// few field reads, and the ability walk once per CacheFrames frames or mode change.</summary>
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
            return showPsycast || showHemogen;
        }

        private static readonly List<AIAbility> collected = new List<AIAbility>();

        // Every system's abilities with an extension, since only those can have a mode
        private static void Refresh(Pawn pawn)
        {
            bool psy = pawn.psychicEntropy != null && AbilityAIAutocast.HasAny(pawn);
            cachedGene = pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>();
            showPsycast = showHemogen = false;
            if (!psy && cachedGene == null) return;
            try
            {
                AIAbilitySources.Collect(pawn, collected);
                for (int i = 0; i < collected.Count; i++)
                {
                    AIAbility a = collected[i];
                    if (a.PlayerAutocastElsewhere) continue;
                    if (psy && !showPsycast && a.IsPsycast && AbilityAIAutocast.ModeFor(pawn, a.Def) != AbilityAIAutocastMode.Off)
                        showPsycast = true;
                    if (cachedGene != null && !showHemogen && a.HemogenCost > 0f) showHemogen = true;
                }
            }
            finally { collected.Clear(); }
        }

        // Label and bar plus the 6 px margin each side (no reset button: dragging the heat and focus handles to the
        // ends is their reset; the hemogen floor's default is 30%, shown on its tooltip)
        public override float GetWidth(float maxWidth) => LabelWidth + BarWidth + 12f;

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            var rect = new Rect(topLeft.x, topLeft.y, GetWidth(maxWidth), 75f);
            Rect inner = rect.ContractedBy(6f);
            Widgets.DrawWindowBackground(rect);
            var pe = pawn?.psychicEntropy;
            bool psy = showPsycast && pe != null;
            Gene_Hemogen gene = showHemogen ? cachedGene : null;
            if (!psy && gene == null) return new GizmoResult(GizmoState.Clear);
            AbilityAIAutocast.TryGetLimits(pawn, out float heatCap, out float focusFloor, out float hemogenFloor);
            float newCap = heatCap, newFloor = focusFloor, newHemogen = hemogenFloor;

            // Row tops: the usual two rows; three compact ones; or a title above the hemogen row, so its slider
            // sits where the second row always does
            float h = psy && gene != null ? CompactBarHeight : BarHeight;
            float heatY, focusY, hemogenY;
            if (gene == null) { heatY = inner.y + 6f; focusY = inner.y + 38f; hemogenY = 0f; }
            else if (!psy) { heatY = focusY = 0f; hemogenY = inner.y + 38f; }
            else { heatY = inner.y; focusY = inner.y + 22f; hemogenY = inner.y + 44f; }

            if (psy)
            {
                // Heat: the bar is the pawn's heat now, the handle the cap; shaded to the right of it
                var heatRow = new Rect(inner.x, heatY, LabelWidth + BarWidth, h);
                Label(heatRow, "AAI_Limits_Heat".Translate());
                var heatBar = new Rect(inner.x + LabelWidth, heatY, BarWidth, h);
                Widgets.DraggableBar(heatBar, HeatBarTex, HeatBarHighlightTex, EmptyBarTex, HandleTex, ref draggingHeat,
                    pe.EntropyRelativeValue, ref newCap, null, Increments);
                Shade(heatBar, newCap, 1f);
                BarText(heatBar, newCap >= AbilityAIAutocast.NoHeatCap ? (string)"AAI_Limits_NoCap".Translate() : newCap.ToStringPercent());
                TooltipHandler.TipRegion(heatRow, () => "AAI_Limits_Heat_Tip".Translate(), 0x4A41C1);

                // Psyfocus: the bar is the pawn's psyfocus now, the handle the floor; shaded to the left of it
                var focusRow = new Rect(inner.x, focusY, LabelWidth + BarWidth, h);
                Label(focusRow, "AAI_Limits_Focus".Translate());
                var focusBar = new Rect(inner.x + LabelWidth, focusY, BarWidth, h);
                Widgets.DraggableBar(focusBar, FocusBarTex, FocusBarHighlightTex, EmptyBarTex, HandleTex, ref draggingFocus,
                    pe.CurrentPsyfocus, ref newFloor, null, Increments);
                Shade(focusBar, 0f, newFloor);
                BarText(focusBar, newFloor <= AbilityAIAutocast.NoFocusFloor ? (string)"AAI_Limits_NoFloor".Translate() : newFloor.ToStringPercent());
                TooltipHandler.TipRegion(focusRow, () => "AAI_Limits_Focus_Tip".Translate(), 0x4A41C2);
            }
            else
            {
                Text.Font = GameFont.Small;
                GUI.color = ColoredText.SubtleGrayColor;
                Widgets.Label(new Rect(inner.x, inner.y + 6f, inner.width, BarHeight), "AAI_Limits_Title".Translate());
                GUI.color = Color.white;
            }

            if (gene != null)
            {
                // Hemogen: the bar is the pawn's hemogen now, the handle the floor; shaded to the left of it
                var hemogenRow = new Rect(inner.x, hemogenY, LabelWidth + BarWidth, h);
                Label(hemogenRow, "AAI_Limits_Hemogen".Translate());
                var hemogenBar = new Rect(inner.x + LabelWidth, hemogenY, BarWidth, h);
                Widgets.DraggableBar(hemogenBar, HemogenBarTex, HemogenBarHighlightTex, EmptyBarTex, HandleTex, ref draggingHemogen,
                    gene.ValuePercent, ref newHemogen, null, Increments);
                Shade(hemogenBar, 0f, newHemogen);
                BarText(hemogenBar, newHemogen <= 0.001f ? (string)"AAI_Limits_NoFloor".Translate() : newHemogen.ToStringPercent());
                TooltipHandler.TipRegion(hemogenRow, () => "AAI_Limits_Hemogen_Tip".Translate(), 0x4A41C3);
            }

            if (newCap != heatCap || newFloor != focusFloor || newHemogen != hemogenFloor)
                AbilityAIAutocast.SetLimits(pawn, newCap, newFloor, newHemogen);
            return new GizmoResult(GizmoState.Clear);
        }

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
