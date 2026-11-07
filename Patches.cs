using System;
using EFT;
using EFT.Animations;
using HarmonyLib;
using FirearmController = EFT.Player.FirearmController;

namespace Aero.BinocularADS
{
    // ProceduralWeaponAnimation.OnAimOrPoseChanged runs when IsAiming changes and at other aim refresh points. Forward the current state to the controller for the local player only.
    [HarmonyPatch(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.OnAimOrPoseChanged))]
    public static class AimRefreshPatch
    {
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, FirearmController> GetFirearmController =
            GameFields.TryRef<ProceduralWeaponAnimation, FirearmController>("_firearmController");

        private static readonly AccessTools.FieldRef<FirearmController, Player> GetPlayer =
            GameFields.TryRef<FirearmController, Player>("_player");

        [HarmonyPostfix]
        public static void Postfix(ProceduralWeaponAnimation __instance)
        {
            if (__instance == null)
                return;

            // End the session even when the setting was just disabled, or the native weapon may stay hidden until the next End(). StereoRenderCoordinator also checks this in case aim refreshes stop entirely.
            if (!Plugin.Enabled.Value)
            {
                BinocularRenderer.End("config disabled");
                return;
            }

            try
            {
                if (GetFirearmController == null || GetPlayer == null)
                    return;

                var firearmController = GetFirearmController(__instance);
                if (firearmController == null)
                    return;

                var player = GetPlayer(firearmController);
                if (player == null || !player.IsYourPlayer)
                    return;

                BinocularController.OnAimRefresh(__instance, player, __instance.IsAiming);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[BinoADS] aim refresh postfix failed: {e}");
            }
        }
    }
}
