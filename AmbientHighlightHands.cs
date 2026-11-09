using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Aero.BinocularADS
{
    // Leave the game's hands highlight out while the mod is enabled. It only lifts the brightness of the arms and weapon, and the aimed image mirrors it, so removing it keeps hipfire and aim consistent. The Static and Characters entries still draw.
    //
    // UpdateAmbientBuffer fills the highlight buffer for the gameplay camera, and for the eye camera's mirror. Swap in a copy of the settings without the Hands entries for the call, then put the original back.
    [HarmonyPatch(typeof(AmbientHighlight), nameof(AmbientHighlight.UpdateAmbientBuffer))]
    internal static class AmbientHighlightHands
    {
        private static readonly FieldInfo SettingsField = AccessTools.Field(typeof(AmbientHighlight), "_highlightSettings");
        private static readonly Dictionary<Array, Array> Filtered = new Dictionary<Array, Array>();
        private static readonly HashSet<int> Logged = new HashSet<int>();

        // True while the mod removes the hands highlight; the eye camera skips its mirror then.
        internal static bool Removed => Plugin.Enabled != null && Plugin.Enabled.Value && SettingsField != null;

        [HarmonyPrefix]
        private static void Prefix(AmbientHighlight __instance, ref Array __state)
        {
            __state = null;
            if (!Removed || __instance == null)
                return;
            try
            {
                if (!(SettingsField.GetValue(__instance) is Array original) || original.Length == 0)
                    return;
                if (!Filtered.TryGetValue(original, out Array withoutHands))
                {
                    withoutHands = WithoutHands(original);
                    Filtered[original] = withoutHands;
                }
                if (withoutHands.Length == original.Length)
                    return;
                if (Logged.Add(__instance.GetInstanceID()))
                    Plugin.Diagnostic($"[BinoADS] ambient highlight: hands entries left out ({original.Length - withoutHands.Length} of {original.Length} settings) on '{__instance.name}'");
                SettingsField.SetValue(__instance, withoutHands);
                __state = original;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[BinoADS] ambient highlight: could not leave out the hands entries, the game's highlight is unchanged ({e.GetType().Name}: {e.Message})");
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer(AmbientHighlight __instance, Array __state)
        {
            if (__state != null && __instance != null)
                SettingsField.SetValue(__instance, __state);
        }

        private static Array WithoutHands(Array original)
        {
            Type element = original.GetType().GetElementType();
            FieldInfo stencil = AccessTools.Field(element, "StencilTypeToUse");
            var kept = new List<object>();
            foreach (object setting in original)
            {
                if (setting != null && stencil != null && Convert.ToInt32(stencil.GetValue(setting)) == (int)AmbientHighlight.StencilType.Hands)
                    continue;
                kept.Add(setting);
            }
            Array result = Array.CreateInstance(element, kept.Count);
            for (int i = 0; i < kept.Count; i++)
                result.SetValue(kept[i], i);
            return result;
        }
    }
}
