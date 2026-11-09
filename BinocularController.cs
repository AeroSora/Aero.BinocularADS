using EFT;
using EFT.Animations;
using UnityEngine;

namespace Aero.BinocularADS
{
    // Track local ADS, identify the active sight and manage the render session.
    public static class BinocularController
    {
        private static bool _wasAiming;
        private static string _lastSight = "";
        private static ProceduralWeaponAnimation _pwa;
        private static Player _player;

        // The watchdog checks this against the weapon root the session was built for.
        public static Transform CurrentWeaponRoot => _pwa == null ? null : _pwa.HandsContainer?.Weapon;

        // Resolve configurable defocus anchors from the local weapon animation.
        internal static ProceduralWeaponAnimation CurrentWeaponAnimation => _pwa;

        // The local player's arms live under PlayerBody, outside the weapon hierarchy.
        public static Player LocalPlayer => _player;

        // The local aim state at the last aim refresh.
        public static bool IsAiming => _wasAiming;

        // Thermal goggles process the main camera before the upscale. Compositing there would put a normally coloured gun over the thermal view, so show the native weapon and let the game's thermal pass handle it. Thermal scopes use a separate optic camera.
        public static bool ThermalGogglesOn
        {
            get
            {
                var manager = EFT.CameraControl.CameraManager.Instance;
                ThermalVision vision = manager != null ? manager.ThermalVision : null;
                return vision != null && vision.On;
            }
        }

        // Resume the effect after a temporary suspension during the same aim.
        public static void ResumeAim()
        {
            if (_wasAiming && _pwa != null && !ThermalGogglesOn)
                BinocularRenderer.Begin(_pwa);
        }

        // The compositor uses the sight type from the last refresh to decide how to handle PiP.
        public static SightClass CurrentSight { get; private set; }

        // 0 is the dominant eye alone and 1 is the full binocular effect. Interpolating the eye weights keeps the dominant image opaque at the start and brings in the second image as the weapon comes up. Start this phase once Takeover reaches 1.
        public static float Fade => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_fadeLinear - 1f));

        // Crossfade from the native weapon (0) to the dominant-eye render (1) over one Fade Time. Do this before Fade on aim-in and after it on aim-out.
        //
        // Auto-exposure, AO and decals can differ between the two renders, so an instant swap can make the gun look brighter. Keep the native weapon in the main pass during the crossfade. The dominant eye shares the main camera's pose, so the images line up and only the lighting changes.
        public static float Takeover => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_fadeLinear));

        // Tear down the registry once the fade reaches zero.
        public static bool FadeFinished => _fadeLinear <= 0f;

        // 0..2: 0-1 is the takeover phase, 1-2 the binocular fade.
        private static float _fadeLinear;

        // The eye the pipeline renders from. When the wanted eye changes mid-aim, fade the second image out, switch eyes, then fade it back in. The dominant image sits at the main camera's pose, so the switch itself can't be seen. When the second image isn't showing, switch at once.
        public static Eye ActiveDominantEye => _eyeInitialised ? _activeEye : Plugin.EffectiveDominantEye(_pwa);
        private static Eye _activeEye = Eye.Right;
        private static bool _eyeInitialised;
        private static bool _eyeSwitchLogged;

        // Advance the linear ramp once per frame. Fade applies smoothstep so the second image eases in and out.
        public static void UpdateFade(float deltaTime)
        {
            Eye wanted = Plugin.EffectiveDominantEye(_pwa);
            if (!_eyeInitialised)
            {
                _activeEye = wanted;
                _eyeInitialised = true;
            }
            // Hold the eye until the fade-out completes.
            bool switching = wanted != _activeEye;
            if (switching && _fadeLinear > 1f && !_eyeSwitchLogged)
            {
                _eyeSwitchLogged = true;
                Plugin.Diagnostic($"[BinoADS] eye switch {_activeEye} -> {wanted} requested at frame {Time.frameCount}: fading the second image out first (Fade {Fade:0.00})");
            }

            float target = !_wasAiming ? 0f : switching ? 1f : 2f;
            float seconds = Mathf.Max(0.001f, Plugin.FadeTimeSeconds.Value);
            _fadeLinear = Mathf.MoveTowards(_fadeLinear, target, deltaTime / seconds);

            if (switching && _fadeLinear <= 1f)
            {
                if (_eyeSwitchLogged)
                    Plugin.Diagnostic($"[BinoADS] eye switch {_activeEye} -> {wanted} done at frame {Time.frameCount}: second image fading back in");
                _activeEye = wanted;
                _eyeSwitchLogged = false;
            }
        }

        // Check player liveness here because death and raid teardown can happen without an aim refresh.
        public static bool LocalPlayerAlive =>
            _player != null && _player.HealthController != null && _player.HealthController.IsAlive;

        // Check that the registry still belongs to the current weapon root.
        public static bool WeaponRootMatches(int rootId)
        {
            var root = CurrentWeaponRoot;
            return root != null && root.GetInstanceID() == rootId;
        }

        // Fingerprint the active sight mode without allocations or a hierarchy walk. Flipping a magnifier can change the sight type while the player stays aimed.
        //
        // Use the same scope selection as ProceduralWeaponAnimation.CurrentScope, with guards for an empty ScopeAimTransforms list or negative AimIndex. The idle warm-up can hit either case, where the native getter would throw ArgumentOutOfRangeException.
        internal static ProceduralWeaponAnimation.SightNBone CurrentScopeOrNull(ProceduralWeaponAnimation pwa)
        {
            var list = pwa == null ? null : pwa.ScopeAimTransforms;
            if (list == null || list.Count == 0) return null;
            int index = pwa.AimIndex % list.Count;
            return index < 0 ? null : list[index];
        }

        public static int SightSignature(ProceduralWeaponAnimation pwa)
        {
            var scope = CurrentScopeOrNull(pwa);
            var optic = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;

            int h = 17;
            h = h * 31 + (int)Classify(pwa);
            h = h * 31 + (scope == null ? 0 : (scope.IsOptic ? 1 : 2));
            h = h * 31 + (optic == null ? 0 : optic.GetInstanceID());
            return h;
        }


        // These sight types control which parts to leave out of the second image. The classifier uses ScopePrefabCache (HasOptics and HasCollimators) and SightNBone.IsOptic.
        public enum SightClass
        {
            None,         // No scope resolved.
            OpticPiP,     // Magnified mode. OpticCamera renders the picture into the lens texture.
            Collimator,   // Red dot, reflex or holo. The main camera draws the dot on the collimator mesh.
            IronSight,    // Removable iron sight with a SightComponent and no ScopePrefabCache.
            BuiltinScope, // Built-in sights without a separate aiming mod, such as the PPSh.
        }

        public static void OnAimRefresh(ProceduralWeaponAnimation pwa, Player player, bool aiming)
        {
            _pwa = pwa;
            _player = player;
            var sight = Classify(pwa);

            // Log changes only so repeated aim refreshes stay out of Player.log.
            bool aimChanged = aiming != _wasAiming;
            bool sightChanged = aiming && sight.ToString() != _lastSight;

            if ((aimChanged || sightChanged) && Plugin.Debug.Value)
            {
                if (aiming)
                    Plugin.Log.LogInfo($"[BinoADS] AIM ON  | sight={sight} | {Discriminators(pwa)} | separation={Plugin.EyeSeparationCm.Value}cm dominant={Plugin.EffectiveDominantEye(pwa)} leftShoulder={pwa != null && pwa.LeftStance} | overlap={Plugin.OverlapOpacity.Value}% share={Plugin.DominantEyeShare.Value}%");
                else
                    Plugin.Log.LogInfo("[BinoADS] AIM OFF");
            }

            _wasAiming = aiming;
            _lastSight = aiming ? sight.ToString() : "";
            CurrentSight = sight;

            // Let the coordinator keep the effect alive through aim-out and call End() when the fade finishes.
            if (!aiming)
                return;

            // Drop the old registry before calling Begin() again. Begin() returns early while active, so a flipped magnifier would otherwise keep the old renderer list and sight-picture exclusion.
            if (BinocularRenderer.IsActive)
            {
                if (!WeaponRootMatches(BinocularRenderer.WeaponRootId))
                    BinocularRenderer.End("weapon root changed");
                else if (SightSignature(pwa) != BinocularRenderer.SightSignature)
                    BinocularRenderer.End("sight mode changed");
            }

            if (ThermalGogglesOn)
                return;

            // Begin() and End() can safely receive repeated aim refreshes.
            BinocularRenderer.Begin(pwa);
        }

        // Check IsOptic for the active magnified mode, then HasCollimators for a reflex sight. For irons, the presence of an aiming mod distinguishes a removable sight from a built-in one.
        private static SightClass Classify(ProceduralWeaponAnimation pwa)
        {
            var scope = CurrentScopeOrNull(pwa);
            if (scope == null)
                return SightClass.None;

            // The active mode uses the PiP camera.
            if (scope.IsOptic)
                return SightClass.OpticPiP;

            // A prefab with collimators is a red dot or holo sight.
            var cache = scope.ScopePrefabCache;
            if (cache != null && cache.HasCollimators)
                return SightClass.Collimator;

            // Without a prefab cache, a SightComponent mod marks a removable iron sight. Otherwise the sights are built in.
            if (pwa.CurrentAimingMod == null)
                return SightClass.BuiltinScope;

            return SightClass.IronSight;
        }

        // Log the raw fields alongside the result so the sight classification is easy to check.
        private static string Discriminators(ProceduralWeaponAnimation pwa)
        {
            var scope = CurrentScopeOrNull(pwa);
            if (scope == null)
                return "scope=null";

            var cache = scope.ScopePrefabCache;
            bool hasOptics = cache != null && cache.HasOptics;
            bool hasColl = cache != null && cache.HasCollimators;
            return $"opt={scope.IsOptic} cache={(cache != null)} hasOpt={hasOptics} hasColl={hasColl} mod={(pwa.CurrentAimingMod != null)}";
        }
    }
}
