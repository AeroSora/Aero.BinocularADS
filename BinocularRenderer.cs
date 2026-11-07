using EFT.Animations;
using UnityEngine;

namespace Aero.BinocularADS
{
    // The aim hook and lifecycle watchdog use this to start and end sessions. ProductionPipeline owns rendering and the registry. Log the reason here so a dropout can be traced to the guard that ended the session.
    public static class BinocularRenderer
    {
        public static bool IsActive => ProductionPipeline.SessionRequested;

        // Weapon root instance ID for this session, or 0 while idle.
        public static int WeaponRootId => ProductionPipeline.WeaponRootId;

        // Sight-mode fingerprint captured at session start.
        public static int SightSignature => ProductionPipeline.SightSignature;

        // Start the session if one is not already active.
        public static void Begin(ProceduralWeaponAnimation pwa)
        {
            ProductionPipeline.Begin(pwa);
        }

        // Restore the native viewmodel and end the active session.
        public static void End(string reason = null)
        {
            // Log the guard that ended an active session. The reload-in-ADS dropout was hard to reproduce by hand, so keep the reason in the log when it happens.
            if (!ProductionPipeline.SessionRequested)
            {
                ProductionPipeline.End(reason);
                return;
            }

            if (Plugin.Debug != null && Plugin.Debug.Value)
            {
                Plugin.Log.LogInfo(
                    $"[BinoADS] DISENGAGE | reason='{reason ?? "un-aimed / fade finished"}' " +
                    $"aiming={BinocularController.IsAiming} " +
                    $"weaponRootMatch={BinocularController.WeaponRootMatches(WeaponRootId)} " +
                    $"t={Time.time:0.00}");
            }

            ProductionPipeline.End(reason);
        }
    }
}
