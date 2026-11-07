using System;
using HarmonyLib;

namespace Aero.BinocularADS
{
    // Resolve private game fields separately so a renamed field only disables the feature that needs it.
    //
    // AccessTools.FieldRefAccess throws for a missing field. In a static initializer that turns into a TypeInitializationException on every subsequent game call into the patch. TryRef logs once and returns null so callers can skip the unavailable field.
    internal static class GameFields
    {
        internal static AccessTools.FieldRef<TObject, TField> TryRef<TObject, TField>(string name)
        {
            try
            {
                return AccessTools.FieldRefAccess<TObject, TField>(name);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError(
                    $"[BinoADS] game field {typeof(TObject).Name}.{name} not found ({e.GetType().Name}: {e.Message}); " +
                    "the feature that reads it stays off");
                return null;
            }
        }
    }
}
