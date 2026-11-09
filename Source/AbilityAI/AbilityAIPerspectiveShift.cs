using System;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AbilityAI
{
    /// <summary>Perspective Shift (ferny.PerspectiveShift), by reflection: no reference to its assembly, nothing
    /// patched. The pawn it puts under the player's direct control (PerspectiveShift.State.Avatar.pawn) is played by
    /// hand, so with the setting on Ability AI leaves it alone: no casts, no stand-down of vanilla's or VEF's own
    /// triggers, no autocast menu, badge or limits gizmo. Its saved modes and limits are kept and apply again as soon
    /// as the player lets go or moves to another pawn.
    ///
    /// Asked from Handles, which vanilla's stand-down calls for every ready aiCanUse ability, so without Perspective
    /// Shift it stops at one null check, and with it costs one compiled delegate call.</summary>
    [StaticConstructorOnStartup]
    internal static class AbilityAIPerspectiveShift
    {
        private static readonly Func<Pawn> avatarPawn;

        /// <summary>Perspective Shift is loaded and its controlled pawn was found: the setting is shown.</summary>
        internal static bool Loaded => avatarPawn != null;

        static AbilityAIPerspectiveShift()
        {
            Type state = GenTypes.GetTypeInAnyAssembly("PerspectiveShift.State", "PerspectiveShift");
            if (state == null) return;
            FieldInfo avatar = AccessTools.Field(state, "Avatar");
            FieldInfo pawn = avatar == null || !avatar.IsStatic ? null : AccessTools.Field(avatar.FieldType, "pawn");
            if (pawn == null || pawn.IsStatic || !typeof(Pawn).IsAssignableFrom(pawn.FieldType))
            {
                Log.Warning("[Ability AI] Perspective Shift is loaded but its controlled pawn wasn't found; Ability AI will keep autocasting for the pawn you control.");
                return;
            }
            try
            {
                var held = Expression.Field(null, avatar);
                var body = Expression.Condition(Expression.Equal(held, Expression.Constant(null, avatar.FieldType)),
                    Expression.Constant(null, typeof(Pawn)), Expression.Convert(Expression.Field(held, pawn), typeof(Pawn)));
                avatarPawn = Expression.Lambda<Func<Pawn>>(body).Compile();
            }
            catch (Exception e)
            {
                Log.Warning($"[Ability AI] Reading Perspective Shift's controlled pawn failed; Ability AI will keep autocasting for the pawn you control: {e}");
            }
        }

        /// <summary>The pawn is the one Perspective Shift has under direct control and the setting hands it over.</summary>
        internal static bool Controlled(Pawn pawn) =>
            avatarPawn != null && pawn != null && AbilityAISettings.PerspectiveShiftOff && avatarPawn() == pawn;
    }
}
