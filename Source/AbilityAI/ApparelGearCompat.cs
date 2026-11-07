using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

// Ready checks for other mods' gear verbs, by reflection so neither mod is referenced. Each is registered only when
// its type is loaded.
namespace AbilityAI
{
    internal static class ApparelGearCompat
    {
        internal static void Register()
        {
            RegisterApexMechamancerPack();
            RegisterAnomaliesExpectedBeam();
        }

        // Apex Mechanoids' mechamancer pack keeps at most DefModExtension_MechPack.maxNum satellites alive per pack;
        // past that its TryCastShot posts a message and fails after the warmup, and Available() doesn't say so
        private static void RegisterApexMechamancerPack()
        {
            Type verbType = AccessTools.TypeByName("ApexMechanoids.Verb_SpawnMech");
            Type extType = AccessTools.TypeByName("ApexMechanoids.DefModExtension_MechPack");
            if (verbType == null || extType == null) return;
            FieldInfo spawnedField = AccessTools.Field(verbType, "spawnedThing");
            FieldInfo maxField = AccessTools.Field(extType, "maxNum");
            if (spawnedField == null || maxField == null || !typeof(List<Pawn>).IsAssignableFrom(spawnedField.FieldType))
            {
                Log.Warning("[Ability AI] Apex Mechanoids' mechamancer pack has changed shape; its satellite limit isn't checked.");
                return;
            }
            var spawned = AccessTools.FieldRefAccess<List<Pawn>>(verbType, "spawnedThing");
            var maxByDef = new Dictionary<ThingDef, int>();
            ApparelVerbJobs.RegisterReady(verbType, verb =>
            {
                ThingDef def = verb.EquipmentSource?.def;
                if (def == null) return true;
                if (!maxByDef.TryGetValue(def, out int max))
                {
                    max = int.MaxValue;
                    var exts = def.modExtensions;
                    if (exts != null)
                        for (int i = 0; i < exts.Count; i++)
                            if (extType.IsInstanceOfType(exts[i])) { max = (int)maxField.GetValue(exts[i]); break; }
                    maxByDef[def] = max;
                }
                var list = spawned(verb);
                if (list == null) return true;
                int alive = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && !list[i].Dead && !list[i].Destroyed) alive++;
                return alive < max;
            });
        }

        // Anomalies Expected's precise orbital beam needs a beam target building of the player's while its setting
        // asks for one (the default), and its Available() posts a message every 500 ticks while it is missing
        private static void RegisterAnomaliesExpectedBeam()
        {
            Type verbType = AccessTools.TypeByName("AnomaliesExpected.Verb_AEPowerBeam");
            Type modType = AccessTools.TypeByName("AnomaliesExpected.AEMod");
            Type settingsType = AccessTools.TypeByName("AnomaliesExpected.AESettings");
            if (verbType == null || modType == null || settingsType == null) return;
            MethodInfo settingsGetter = AccessTools.PropertyGetter(modType, "Settings");
            FieldInfo requires = AccessTools.Field(settingsType, "PoweBeamRequireBeamTarget");
            MethodInfo targetGetter = AccessTools.PropertyGetter(verbType, "BeamTarget");
            if (settingsGetter == null || requires == null || targetGetter == null)
            {
                Log.Warning("[Ability AI] Anomalies Expected's orbital beam has changed shape; its beam target isn't checked.");
                return;
            }
            ApparelVerbJobs.RegisterReady(verbType, verb =>
            {
                object settings = settingsGetter.Invoke(null, null);
                if (settings == null || !(bool)requires.GetValue(settings)) return true;
                // Its BeamTarget reads Find.AnyPlayerHomeMap unguarded; with no home map there is no beam target
                return Find.AnyPlayerHomeMap != null && targetGetter.Invoke(verb, null) != null;
            });
        }
    }
}
