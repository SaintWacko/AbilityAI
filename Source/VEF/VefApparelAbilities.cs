using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using VEF.Abilities;
using Verse;
using VefAbility = VEF.Abilities.Ability;
using VefAbilityDef = VEF.Abilities.AbilityDef;

// VEF abilities granted by worn apparel (CompAbilitiesApparel: VFE Pirates' aerial shoulders and the like). VEF keeps
// them on the item, not in the wearer's LearnedAbilities, so the VEF source never sees them; they are handed to it
// through AIHeldAbilities, as Entad's held abilities are. VEF points them at the wearer only when it first draws the
// worn gizmos, which never happens for an NPC: the same re-pointing is done here, and the comp is told, so VEF's
// gizmo code doesn't Init the ability again in the middle of a cast.
namespace AbilityAI.Vef
{
    internal static class VefApparelAbilities
    {
        private static readonly HashSet<ThingDef> apparelDefs = new HashSet<ThingDef>();
        private static AccessTools.FieldRef<CompAbilitiesApparel, Pawn> compPawn;

        internal static void Init()
        {
            var defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                if (defs[i].apparel == null || defs[i].comps == null) continue;
                var props = defs[i].GetCompProperties<CompProperties_AbilitiesApparel>();
                if (props?.abilities == null) continue;
                for (int j = 0; j < props.abilities.Count; j++)
                    if (VefAIAbility.Supported(props.abilities[j])) { apparelDefs.Add(defs[i]); break; }
            }
            if (apparelDefs.Count == 0) return;
            compPawn = AccessTools.FieldRefAccess<CompAbilitiesApparel, Pawn>("pawn");
            AIHeldAbilities.Register(HasAny, Collect);
        }

        private static bool HasAny(Pawn pawn)
        {
            var worn = pawn.apparel?.WornApparel;
            if (worn == null) return false;
            for (int i = 0; i < worn.Count; i++)
                if (apparelDefs.Contains(worn[i].def)) return true;
            return false;
        }

        private static void Collect(Pawn pawn, List<object> into)
        {
            var worn = pawn.apparel?.WornApparel;
            if (worn == null) return;
            for (int i = 0; i < worn.Count; i++)
            {
                if (!apparelDefs.Contains(worn[i].def)) continue;
                var comp = worn[i].GetComp<CompAbilitiesApparel>();
                if (comp == null) continue;
                var given = comp.GivenAbilities;
                bool repoint = compPawn(comp) != pawn;
                for (int j = 0; j < given.Count; j++)
                {
                    VefAbility a = given[j];
                    if (repoint || a.pawn != pawn)
                    {
                        a.pawn = pawn;
                        a.Init();
                    }
                    into.Add(a);
                }
                if (repoint) compPawn(comp) = pawn;
            }
        }

        // VFE Pirates' Power jump spends PowerJumpExtension.fuelConsumption charges of the shoulders' reloadable comp
        // and only its gizmo checks there is that much
        internal static void RegisterPowerJumpFuel()
        {
            Type abilityType = AccessTools.TypeByName("VFEPirates.Ability_PowerJump");
            Type extType = AccessTools.TypeByName("VFEPirates.PowerJumpExtension");
            var fuelField = extType != null ? AccessTools.Field(extType, "fuelConsumption") : null;
            if (abilityType == null || fuelField == null) return;
            var fuelByDef = new Dictionary<VefAbilityDef, float>();
            VefAIAbility.RegisterReady(abilityType, a =>
            {
                if (!fuelByDef.TryGetValue(a.def, out float fuel))
                {
                    fuel = 0f;
                    var exts = a.def.modExtensions;
                    if (exts != null)
                        for (int i = 0; i < exts.Count; i++)
                            if (extType.IsInstanceOfType(exts[i])) { fuel = Convert.ToSingle(fuelField.GetValue(exts[i])); break; }
                    fuelByDef[a.def] = fuel;
                }
                var tank = a.holder?.TryGetComp<CompApparelReloadable>();
                return tank == null || tank.RemainingCharges >= fuel;
            });
        }
    }
}
