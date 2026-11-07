using UnityEngine;

namespace Aero.BinocularADS
{
    // Advance the fade and check session preconditions every frame. Config disable, death, raid exit, camera recreation and weapon swaps can all happen without another aim refresh. ProductionPipeline renders during PPv2's command-buffer build.
    public class StereoRenderCoordinator : MonoBehaviour
    {
        public static StereoRenderCoordinator Instance;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            BinocularRenderer.End();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            BinocularController.UpdateFade(Time.deltaTime);
            ProductionPipeline.Watchdog();

            bool thermal = BinocularController.ThermalGogglesOn;
            if (thermal != _thermalLatched)
            {
                _thermalLatched = thermal;
                Plugin.Diagnostic(thermal
                    ? "[BinoADS] thermal goggles on: effect stands down, the game's weapon is shown"
                    : "[BinoADS] thermal goggles off: effect resumes");
                if (!thermal)
                    BinocularController.ResumeAim();
            }

            if (!BinocularRenderer.IsActive)
            {
                if (!thermal)
                    WarmUpWhenIdle();
                return;
            }

            if (thermal)
            {
                BinocularRenderer.End("thermal goggles on");
                return;
            }

            // A completed capture pays the first-use cost for this weapon, whether it came from a real aim or the warm-up.
            if (ProductionPipeline.CapturedThisSession)
                _warmedRootId = ProductionPipeline.WeaponRootId;

            // Keep the session alive through aim-out so the second image fades away before teardown.
            if (!BinocularController.IsAiming && BinocularController.FadeFinished)
            {
                BinocularRenderer.End();
                return;
            }

            if (!Plugin.Enabled.Value)
            {
                BinocularRenderer.End("config disabled");
                return;
            }

            if (!BinocularController.LocalPlayerAlive)
            {
                BinocularRenderer.End("local player dead or gone");
                return;
            }

            if (EFT.CameraControl.CameraManager.Instance?.Camera == null)
            {
                BinocularRenderer.End("main camera lost");
                return;
            }

            if (!BinocularController.WeaponRootMatches(BinocularRenderer.WeaponRootId))
                BinocularRenderer.End("weapon root changed");
        }

        // Track the weapon that has captured, the last weapon we tried to warm and when this weapon appeared. Wait for the draw and try once per weapon, including when warm-up fails or is bypassed.
        private static int _warmedRootId;
        private static bool _thermalLatched;
        private static int _attemptedRootId;
        private static int _pendingRootId;
        private static float _pendingSince;
        private const float WarmUpDelaySeconds = 2f;

        // Run a hidden session to pay the setup cost before the first aim, which can cost roughly ten times a later one. Takeover stays at 0, so the native weapon draws and the composite contributes nothing. The normal idle check ends the session next frame.
        private static void WarmUpWhenIdle()
        {
            if (!Plugin.Enabled.Value ||
                BinocularController.IsAiming || !BinocularController.FadeFinished)
                return;
            EFT.Player player = Comfort.Common.Singleton<EFT.GameWorld>.Instantiated
                ? Comfort.Common.Singleton<EFT.GameWorld>.Instance.MainPlayer
                : null;
            EFT.Animations.ProceduralWeaponAnimation pwa = player?.ProceduralWeaponAnimation;
            Transform root = pwa?.HandsContainer?.Weapon;
            if (player == null || pwa == null || root == null || !player.HealthController.IsAlive ||
                EFT.CameraControl.CameraManager.Instance?.Camera == null)
                return;
            int id = root.GetInstanceID();
            if (id == _warmedRootId || id == _attemptedRootId)
                return;
            if (id != _pendingRootId)
            {
                _pendingRootId = id;
                _pendingSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _pendingSince < WarmUpDelaySeconds)
                return;
            _attemptedRootId = id;
            Plugin.Diagnostic($"[BinoADS] warm-up session for weapon root '{root.name}' at frame {Time.frameCount}");
            BinocularRenderer.Begin(pwa);
        }
    }
}
