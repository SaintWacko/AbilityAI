using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AbilityAI
{
    /// <summary>The grey ring on an ability gizmo whose autocast menu is available but whose mode is still Off, and
    /// the "right-click to set autocast" line in its tooltip.
    ///
    /// The ring is drawn every frame per ability gizmo shown, and Offers asks the host hooks (Entad's can look at
    /// traits and bonds), so the answer is cached per Ability object and re-asked once a second. A stale second is
    /// harmless: it only decides whether a grey ring shows. The cache is keyed by the Ability, which a pawn keeps for
    /// as long as it has the ability; it is dropped whole on a new world and when it grows past MaxEntries, so
    /// abilities that went away (a pawn died, an implant was removed) don't pile up.
    ///
    /// The ring texture is made in code (no asset to ship or miss): 32 px, an anti-aliased grey ring around a
    /// half-transparent dark disc, matching the badge's dark backing.</summary>
    [StaticConstructorOnStartup]
    internal static class AutocastMarker
    {
        private const int RefreshFrames = 60;
        private const int MaxEntries = 512;
        private const int TexSize = 32;

        private struct Entry
        {
            public int frame;
            public bool offers;
        }

        // Keyed by the ability object of whichever system it belongs to (RimWorld.Ability, VEF's Ability)
        private static readonly Dictionary<object, Entry> cache = new Dictionary<object, Entry>();
        private static readonly Texture2D RingTex = MakeRing();

        internal static void ClearCache() => cache.Clear();

        internal static bool Offers(Ability ability)
        {
            if (TryCached(ability, out bool offers)) return offers;
            return Store(ability, AbilityAIAutocast.Offers(ability));
        }

        internal static bool Offers(AIAbility ability)
        {
            if (TryCached(ability.Raw, out bool offers)) return offers;
            return Store(ability.Raw, AbilityAIAutocast.Offers(ability));
        }

        private static bool TryCached(object key, out bool offers)
        {
            int now = Time.frameCount;
            if (cache.TryGetValue(key, out Entry e) && now - e.frame < RefreshFrames && now >= e.frame)
            {
                offers = e.offers;
                return true;
            }
            offers = false;
            return false;
        }

        private static bool Store(object key, bool offers)
        {
            if (cache.Count >= MaxEntries) cache.Clear();
            cache[key] = new Entry { frame = Time.frameCount, offers = offers };
            return offers;
        }

        // Where the badge goes: full size on the second text line at the left (the hotkey has the first), shrunk at
        // the bottom left, or bottom right with rightEdge (AbilityAIAutocast.DrawBadge)
        internal static void Draw(Rect button, bool shrunk, bool rightEdge = false)
        {
            float size = shrunk ? 9f : 12f;
            Text.Font = GameFont.Tiny;
            float y = shrunk ? button.yMax - size - 2f : button.y + 3f + Text.LineHeight + 1f;
            Text.Font = GameFont.Small;
            float x = shrunk && rightEdge ? button.xMax - 4f - size : button.x + 4f;
            GUI.DrawTexture(new Rect(x, y, size, size), RingTex);
        }

        private static Texture2D MakeRing()
        {
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                name = "AbilityAI_AutocastMarker",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            float c = (TexSize - 1) / 2f;
            const float outer = 14.5f, inner = 10.5f;
            var ring = new Color(0.66f, 0.66f, 0.66f);
            var fill = new Color(0f, 0f, 0f, 0.5f);
            var pixels = new Color[TexSize * TexSize];
            for (int y = 0; y < TexSize; y++)
                for (int x = 0; x < TexSize; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    // One-pixel soft edges: outside the ring, on it, and the dark disc inside
                    float onRing = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
                    float inside = Mathf.Clamp01(inner - d + 0.5f);
                    Color px = Color.Lerp(new Color(0f, 0f, 0f, fill.a * inside), ring, onRing);
                    pixels[y * TexSize + x] = px;
                }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }

    /// <summary>Adds "Right-click to set autocast (now: ...)" to an ability gizmo's tooltip. Command builds the tooltip
    /// only while the mouse is over a button and appends DescPostfix to it; Command_Ability doesn't override
    /// DescPostfix, so this getter is the one it reads. Runs for any Command being hovered, and leaves at one type
    /// check for anything that isn't an ability gizmo.</summary>
    [HarmonyPatch(typeof(Command), nameof(Command.DescPostfix), MethodType.Getter)]
    internal static class Patch_Command_AutocastHint
    {
        static void Postfix(Command __instance, ref string __result)
        {
            if (!(__instance is Command_Ability command)) return;
            Ability ability = command.Ability;
            if (ability?.pawn == null || !AutocastMarker.Offers(ability)) return;
            string mode = AbilityAIAutocast.Label(AbilityAIAutocast.ModeFor(ability.pawn, ability.def), ability.def);
            __result += "\n\n" + "AAI_Autocast_Hint".Translate(mode).Colorize(ColoredText.SubtleGrayColor);
            string recast = AbilityAIRecast.Hint(ability.def);
            if (recast != null) __result += "\n" + recast.Colorize(ColoredText.SubtleGrayColor);
            // Zone casting is undrafted work: without undrafted autocast it never runs
            string zone = AbilityAISettings.UndraftedAutocast ? AbilityAIZones.Hint(ability.def, ability.pawn) : null;
            if (zone != null) __result += "\n" + zone.Colorize(ColoredText.SubtleGrayColor);
        }
    }
}
