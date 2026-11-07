using UnityEngine;

namespace Aero.BinocularADS
{
    // Sample the local player once in Plugin.Update so the lens blend, glass suppression and active check all use the same opacity this frame.
    //
    // In SPT 4.1, look input goes through MoveInputTranslator.TranslateAxes and Player.Rotate to MovementContext.Rotation (yaw and pitch in degrees). Recoil, sway and breathing stay in ProceduralWeaponAnimation, so changes in Rotation come from look input alone. Get zoom from the active scope's SightComponent.GetCurrentOpticZoom() (0 without a zoom table) and walking speed from MovementContext.ActualLinearVelocity in m/s.
    internal static class BindonAiming
    {
        private static readonly AimSteadiness State = new AimSteadiness();
        private static bool _tracking;
        private static float _lastZoom;

        // This frame's magnified-sight opacity (%), reduced while the player moves.
        internal static float EffectiveOpacity { get; private set; } = 100f;

        internal static float SteadyOpacity => Mathf.Clamp(Plugin.MagnifiedSightOpacity.Value, 10f, 100f);

        internal static AimSteadinessSettings Settings() => new AimSteadinessSettings
        {
            MovingOpacity = Plugin.BindonMovingOpacity.Value,
            LookDeadZone = Plugin.BindonLookDeadZone.Value,
            LookFullSpeed = Plugin.BindonLookFullSpeed.Value,
            DeadZoneRelease = Plugin.BindonDeadZoneRelease.Value,
            LookFadeSeconds = Plugin.BindonLookFadeSeconds.Value,
            WalkFadeSeconds = Plugin.BindonWalkFadeSeconds.Value,
            RestoreSeconds = Plugin.BindonRestoreSeconds.Value,
            WalkingSpeed = Plugin.BindonWalkingSpeed.Value,
        };

        internal static void Tick(float deltaTime)
        {
            float steady = SteadyOpacity;
            var player = BinocularController.LocalPlayer;
            var context = player != null ? player.MovementContext : null;
            bool active = Plugin.BindonEnabled.Value && BinocularController.IsAiming &&
                          BinocularController.CurrentSight == BinocularController.SightClass.OpticPiP && context != null;
            if (!active)
            {
                if (_tracking)
                {
                    _tracking = false;
                    Plugin.Diagnostic("[BinoADS] bindon aiming: stopped (aim ended, not a magnified optic, or switched off)");
                }
                State.Reset();
                EffectiveOpacity = steady;
                return;
            }

            float zoom = 1f;
            var scope = BinocularController.CurrentScopeOrNull(BinocularController.CurrentWeaponAnimation);
            if (scope != null && scope.Mod != null)
                zoom = Mathf.Max(1f, scope.Mod.GetCurrentOpticZoom());

            var settings = Settings();
            Vector2 rotation = context.Rotation;
            bool wasLook = State.LookActive;
            bool wasWalk = State.WalkFade > 0f;
            State.Update(deltaTime, rotation.x, rotation.y, zoom, context.ActualLinearVelocity, settings);
            EffectiveOpacity = State.Opacity(steady, settings);

            if (!_tracking || Mathf.Abs(zoom - _lastZoom) > 0.01f)
            {
                _tracking = true;
                _lastZoom = zoom;
                Plugin.Diagnostic(
                    $"[BinoADS] bindon aiming: tracking | zoom={zoom:0.##}x steady={steady:0}% moving={Mathf.Min(settings.MovingOpacity, steady):0}% " +
                    $"deadZone={settings.LookDeadZone:0}deg/s release={settings.DeadZoneRelease:0}% full={settings.LookFullSpeed:0}deg/s " +
                    $"lookFade={settings.LookFadeSeconds:0.##}s walkFade={settings.WalkFadeSeconds:0.##}s restore={settings.RestoreSeconds:0.##}s walking>{settings.WalkingSpeed:0.##}m/s");
            }
            // With diagnostics enabled, log once per state change.
            if (State.LookActive != wasLook)
            {
                Plugin.Diagnostic($"[BinoADS] bindon aiming: look fade {(State.LookActive ? "start" : "release")} | apparentSpeed={State.ApparentSpeed:0}deg/s opacity={EffectiveOpacity:0}%");
            }
            bool isWalk = State.WalkFade > 0f;
            if (isWalk != wasWalk)
            {
                Plugin.Diagnostic($"[BinoADS] bindon aiming: walk fade {(isWalk ? "start" : "restored")} | groundSpeed={context.ActualLinearVelocity:0.00}m/s opacity={EffectiveOpacity:0}%");
            }
        }
    }
}
