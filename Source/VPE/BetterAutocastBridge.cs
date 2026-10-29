using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using VEF.Abilities;
using Verse;
using AbilityAI.Vef;
using VefAbility = VEF.Abilities.Ability;
using VefAbilityDef = VEF.Abilities.AbilityDef;

// Better Autocasting for VPE (dev.tobot.vpe.betterautocast) autocasts about forty VPE psycasts for colonists, each by
// a hand-written handler keyed by defName in PsycastingHandler.abilityHandlers. It has no toggle of its own: it forces
// VEF's CanAutoCast true for those defs so VEF's toggle shows, and its tick loop casts the ones whose raw autoCast flag
// is set. For the defs both mods have rules for, one of the two decides (AbilityAISettings.BetterAutocastOwns):
// - Ability AI (default): our CanAutoCast postfix runs after BA's and hides the toggle, and a prefix on HandleAbility
//   keeps BA's loop off them even while a flag from before is still set. HasHandler is left alone: it is a one-line
//   method the JIT may inline into BA's callers, and the CanAutoCast postfix already overrides what it decides.
// - Better Autocasting: PlayerAutocastElsewhere is true for colonists, so Ability AI offers no menu and casts nothing
//   for them. Mechs and animals stay ours (BA's loop only runs for colonists), and so do NPCs.
// Switching carries each colonist's on/off choice across (Carry). Nothing here names a BA type at compile time: it is
// found by name and patched by hand, only when BA is loaded.
namespace AbilityAI.Vpe
{
    internal static class BetterAutocastBridge
    {
        private const string PackageId = "dev.tobot.vpe.betterautocast";
        private static readonly HashSet<VefAbilityDef> handled = new HashSet<VefAbilityDef>();

        internal static void Init()
        {
            if (ModLister.GetActiveModWithIdentifier(PackageId, true) == null) return;
            Type handler = AccessTools.TypeByName("BetterAutocastVPE.PsycastingHandler");
            if (!(handler != null && AccessTools.Field(handler, "abilityHandlers")?.GetValue(null) is IDictionary handlers))
            {
                Log.Warning("[Ability AI] Better Autocasting is loaded, but its list of handled psycasts wasn't found. " +
                    "Ability AI keeps its own autocast for the psycasts both mods have rules for.");
                return;
            }
            foreach (object key in handlers.Keys)
                if (key is string name && DefDatabase<VefAbilityDef>.GetNamedSilentFail(name) is VefAbilityDef def) handled.Add(def);

            VefAIAbility.RegisterPlayerAutocaster(Ceded);
            // Spots cast with a psycast BA decides keep no marker and aren't recast while it does
            AbilityAIRecast.Ceded = def => AbilityAISettings.BetterAutocastOwns && def is VefAbilityDef d && handled.Contains(d);
            AbilityAIAutocast.OwnerChanged += OwnerChanged;
            VefAutocast.BeforeConvertAll += WorldReady;
            AbilityAISettings.BetterAutocastActive = true;

            MethodInfo handle = AccessTools.Method(handler, "HandleAbility");
            try
            {
                if (handle == null) throw new MissingMethodException(handler.FullName, "HandleAbility");
                new Harmony("saintwacko.abilityai.vpe").Patch(handle, prefix: new HarmonyMethod(typeof(BetterAutocastBridge), nameof(HandleAbilityPrefix)));
            }
            catch (Exception e)
            {
                Log.Error($"[Ability AI] Patching Better Autocasting's HandleAbility failed. Where Ability AI owns a psycast, " +
                    $"Better Autocasting may still cast it for a colonist whose VEF autocast flag was set before: {e}");
            }
        }

        // The setting first: it is false unless the player picked Better Autocasting
        private static bool Ceded(VefAbility ability) =>
            AbilityAISettings.BetterAutocastOwns && handled.Contains(ability.def) && ability.pawn != null && ability.pawn.IsColonist;

        // BA's loop only gets here for an ability whose raw flag is set. Where we own it, that is a flag from before
        // we took it over (or before a switch) that nothing has converted yet: convert it now instead of casting.
        // Skipping the original is the point; no other mod patches this internal method.
        private static bool HandleAbilityPrefix(VefAbility ability, ref bool __result)
        {
            if (ability == null || AbilityAISettings.BetterAutocastOwns || !VefAutocast.Owns(ability)) return true;
            VefAutocast.Convert(ability);
            __result = false;
            return false;
        }

        // From the settings page. At the main menu there is nothing to carry yet, and before the first tick the load
        // carry hasn't run: WorldReady does both, and checking the world's last owner keeps a switch and a switch
        // back from carrying twice.
        private static void OwnerChanged()
        {
            if (Current.ProgramState == ProgramState.Playing && Find.World != null && AbilityAIAutocast.Ready) WorldReady();
        }

        private static void WorldReady()
        {
            if (AbilityAIAutocast.WorldBetterAutocastOwned != AbilityAISettings.BetterAutocastOwns) Carry(AbilityAISettings.BetterAutocastOwns);
        }

        // To BA: every handled ability with one of our modes gets VEF's flag (the mode stays, unused, for a switch
        // back). To us: a set flag becomes a mode as Convert does, and a cleared one turns the mode off, since the
        // player switched it off while BA had it. Offers rather than Owns: a colonist in a break is carried too.
        private static void Carry(bool toBetterAutocast)
        {
            var pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (!p.IsColonist) continue;
                var list = p.GetComp<CompAbilities>()?.LearnedAbilities;
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    VefAbility a = list[j];
                    if (!handled.Contains(a.def) || !VefAIAbility.Supported(a.def)) continue;
                    if (toBetterAutocast)
                    {
                        if (AbilityAIAutocast.ModeFor(p, a.def) != AbilityAIAutocastMode.Off) a.autoCast = true;
                    }
                    else if (AbilityAIAutocast.Offers(VefAIAbility.Of(a)))
                    {
                        if (!a.autoCast) AbilityAIAutocast.SetMode(p, a.def, AbilityAIAutocastMode.Off);
                        else
                        {
                            if (AbilityAIAutocast.ModeFor(p, a.def) == AbilityAIAutocastMode.Off)
                                AbilityAIAutocast.SetMode(p, a.def, AbilityAIAutocastMode.Stationary);
                            a.autoCast = false;
                        }
                    }
                }
            }
            AbilityAIAutocast.WorldBetterAutocastOwned = toBetterAutocast;
        }
    }
}
