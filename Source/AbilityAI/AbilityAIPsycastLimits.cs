using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

// Per-pawn limits on what autocast may spend on psycasts: a neural heat cap and a psyfocus floor. Set from a gizmo
// beside vanilla's psyfocus one (design: .circinus/mockups/psycast-limits-gizmo.html, variant A, but
// without its reset button: dragging the handles to the ends is the reset), stored in the
// AbilityAIAutocast world component, applied by AbilityAIDriver.PsycastWithinLimits. Only real Psycasts count; Entad's
// psycast copies are plain Abilities. NPCs get no limits: vanilla already makes them pay psyfocus and respects their
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
        private static readonly Texture2D EmptyBarTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.03f, 0.035f, 0.05f));
        private static readonly Texture2D HandleTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.91f, 0.83f, 0.42f));
        // Over the part of a bar autocast won't go into
        private static readonly Texture2D BlockedTex = SolidColorMaterials.NewSolidColorTexture(new Color(0f, 0f, 0f, 0.45f));

        private const float LabelWidth = 63f;
        private const float BarWidth = 100f;
        private const float BarHeight = 22f;
        // 5% steps, as vanilla's psyfocus target
        private const int Increments = 20;

        private static bool draggingHeat, draggingFocus;
        // One instance, re-pointed at the selected pawn: the gizmo is only shown for a single selected pawn
        private static readonly Gizmo_PsycastLimits shared = new Gizmo_PsycastLimits();

        // Whether the selected pawn has a psycast set to autocast, rebuilt when the modes change, the selection
        // changes, or after CacheFrames frames (abilities gained or lost)
        private const int CacheFrames = 60;
        private static Pawn cachedPawn;
        private static int cachedVersion = -1;
        private static int cachedUntilFrame;
        private static bool cachedAnswer;

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
            shared.pawn = null;
        }

        internal static Gizmo For(Pawn pawn)
        {
            shared.pawn = pawn;
            return shared;
        }

        /// <summary>A single selected player pawn with a psychic entropy tracker and at least one real psycast set to
        /// autocast. Cost per GetGizmos call: a few field reads and a dictionary lookup, and the ability walk once per
        /// CacheFrames frames or mode change.</summary>
        internal static bool ShouldShow(Pawn pawn)
        {
            if (!AbilityAISettings.Enabled || !AbilityAISettings.PlayerAutocast) return false;
            if (pawn.psychicEntropy == null || pawn.Faction == null || !pawn.Faction.IsPlayer || pawn.IsPrisoner) return false;
            if (Find.Selector.SingleSelectedThing != pawn || !AbilityAIAutocast.HasAny(pawn)) return false;
            int frame = Time.frameCount;
            if (pawn != cachedPawn || AbilityAIAutocast.Version != cachedVersion || frame >= cachedUntilFrame)
            {
                cachedPawn = pawn;
                cachedVersion = AbilityAIAutocast.Version;
                cachedUntilFrame = frame + CacheFrames;
                cachedAnswer = HasAutocastPsycast(pawn);
            }
            return cachedAnswer;
        }

        private static bool HasAutocastPsycast(Pawn pawn)
        {
            var abilities = pawn.abilities?.AllAbilitiesForReading;
            if (abilities == null) return false;
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i] is Psycast && AbilityAIAutocast.ModeFor(pawn, abilities[i].def) != AbilityAIAutocastMode.Off) return true;
            return false;
        }

        // Label and bar plus the 6 px margin each side (no reset button: dragging both handles to the ends is the reset)
        public override float GetWidth(float maxWidth) => LabelWidth + BarWidth + 12f;

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            var rect = new Rect(topLeft.x, topLeft.y, GetWidth(maxWidth), 75f);
            Rect inner = rect.ContractedBy(6f);
            Widgets.DrawWindowBackground(rect);
            var pe = pawn?.psychicEntropy;
            if (pe == null) return new GizmoResult(GizmoState.Clear);
            AbilityAIAutocast.TryGetLimits(pawn, out float heatCap, out float focusFloor);

            // Heat: the bar is the pawn's heat now, the handle the cap; shaded to the right of it
            var heatRow = new Rect(inner.x, inner.y + 6f, LabelWidth + BarWidth, BarHeight);
            Label(heatRow, "AAI_Limits_Heat".Translate());
            var heatBar = new Rect(inner.x + LabelWidth, inner.y + 6f, BarWidth, BarHeight);
            float newCap = heatCap;
            Widgets.DraggableBar(heatBar, HeatBarTex, HeatBarHighlightTex, EmptyBarTex, HandleTex, ref draggingHeat,
                pe.EntropyRelativeValue, ref newCap, null, Increments);
            Shade(heatBar, newCap, 1f);
            BarText(heatBar, newCap >= AbilityAIAutocast.NoHeatCap ? (string)"AAI_Limits_NoCap".Translate() : newCap.ToStringPercent());
            TooltipHandler.TipRegion(heatRow, () => "AAI_Limits_Heat_Tip".Translate(), 0x4A41C1);

            // Psyfocus: the bar is the pawn's psyfocus now, the handle the floor; shaded to the left of it
            var focusRow = new Rect(inner.x, inner.y + 38f, LabelWidth + BarWidth, BarHeight);
            Label(focusRow, "AAI_Limits_Focus".Translate());
            var focusBar = new Rect(inner.x + LabelWidth, inner.y + 38f, BarWidth, BarHeight);
            float newFloor = focusFloor;
            Widgets.DraggableBar(focusBar, FocusBarTex, FocusBarHighlightTex, EmptyBarTex, HandleTex, ref draggingFocus,
                pe.CurrentPsyfocus, ref newFloor, null, Increments);
            Shade(focusBar, 0f, newFloor);
            BarText(focusBar, newFloor <= AbilityAIAutocast.NoFocusFloor ? (string)"AAI_Limits_NoFloor".Translate() : newFloor.ToStringPercent());
            TooltipHandler.TipRegion(focusRow, () => "AAI_Limits_Focus_Tip".Translate(), 0x4A41C2);

            if (newCap != heatCap || newFloor != focusFloor) AbilityAIAutocast.SetLimits(pawn, newCap, newFloor);
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
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(bar, text);
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    /// <summary>Adds the psycast limits gizmo after the pawn's own. Pass-through: the original sequence is yielded
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
