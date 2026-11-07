using System;

namespace Aero.BinocularADS
{
    // Read the Bindon fade settings from config every frame.
    internal struct AimSteadinessSettings
    {
        // Fully faded opacity (%), capped at the steady opacity.
        public float MovingOpacity;
        // Ignore turns below this apparent speed (view speed × zoom, in degrees per second).
        public float LookDeadZone;
        // Apparent turn speed that brings the sight picture down to the moving opacity.
        public float LookFullSpeed;
        // Once a turn starts fading the sight picture, keep fading until the speed drops below this percentage of the dead zone.
        public float DeadZoneRelease;
        // Seconds to reach the look-fade target at any turn speed.
        public float LookFadeSeconds;
        // Seconds for walking to fade the sight picture from steady to moving opacity.
        public float WalkFadeSeconds;
        // Seconds to restore steady opacity once the player stops turning and walking.
        public float RestoreSeconds;
        // Ground speed threshold for walking, in metres per second.
        public float WalkingSpeed;
    }

    // Magnified Sight Picture Opacity sets how opaque the optic is during a steady aim. Turning or walking fades the picture toward the moving opacity, and holding still brings it back.
    //
    // Measure turning from the player's view rotation, which recoil and sway leave alone. Scale it by zoom so the dead zone feels the same across optics. Track turning and walking separately and use the stronger fade. This code runs in tests without Unity.
    internal sealed class AimSteadiness
    {
        // Smooth increases in turn speed because mouse input can arrive in uneven steps between frames.
        private const float SpeedSmoothingSeconds = 0.02f;

        private float _lookFade;
        private float _walkFade;
        private float _speed;
        private float _lastRawSpeed;
        private bool _lookActive;
        private bool _hasPrevious;
        private float _previousYaw;
        private float _previousPitch;

        // Combined fade. 0 is steady, 1 is fully faded to the moving opacity.
        public float Fade => Math.Max(_lookFade, _walkFade);
        public float LookFade => _lookFade;
        public float WalkFade => _walkFade;
        // Smoothed turn speed as seen through the optic, in degrees per second.
        public float ApparentSpeed => _speed;
        public bool LookActive => _lookActive;

        // Reset so the next sample starts a fresh aim.
        public void Reset()
        {
            _lookFade = 0f;
            _walkFade = 0f;
            _speed = 0f;
            _lastRawSpeed = 0f;
            _lookActive = false;
            _hasPrevious = false;
        }

        // Advance one frame. Yaw and pitch are view angles in degrees. Clamp zoom to at least 1 and take ground speed in metres per second.
        public void Update(float deltaTime, float yaw, float pitch, float zoom, float groundSpeed, AimSteadinessSettings s)
        {
            if (deltaTime <= 0f)
                return;

            float rawSpeed = 0f;
            if (_hasPrevious)
            {
                float dYaw = DeltaAngle(_previousYaw, yaw);
                float dPitch = DeltaAngle(_previousPitch, pitch);
                rawSpeed = (float)Math.Sqrt(dYaw * dYaw + dPitch * dPitch) / deltaTime * Math.Max(1f, zoom);
            }
            _previousYaw = yaw;
            _previousPitch = pitch;
            _hasPrevious = true;
            // Smooth increases in turn speed and take decreases immediately. Letting a fast turn decay through the filter delayed restoration by up to 0.07 s. A mouse that polls slower than the frame rate can leave an empty frame mid-turn, so hold the last speed for the first still frame.
            bool pollingGap = rawSpeed == 0f && _lastRawSpeed > 0f;
            if (!pollingGap)
            {
                if (rawSpeed < _speed)
                    _speed = rawSpeed;
                else
                    _speed += (rawSpeed - _speed) * (1f - (float)Math.Exp(-deltaTime / SpeedSmoothingSeconds));
            }
            _lastRawSpeed = rawSpeed;

            // Start fading above the dead zone and stop below its release threshold. The gap keeps the picture from flickering when you track near the threshold.
            float deadZone = Math.Max(0f, s.LookDeadZone);
            float release = deadZone * Clamp01(s.DeadZoneRelease / 100f);
            if (_lookActive ? _speed < release : _speed <= deadZone)
                _lookActive = false;
            else
                _lookActive = true;

            float lookTarget = 0f;
            if (_lookActive)
            {
                float span = Math.Max(1e-3f, s.LookFullSpeed - release);
                lookTarget = SmoothStep(Clamp01((_speed - release) / span));
            }
            float walkTarget = groundSpeed > s.WalkingSpeed ? 1f : 0f;

            float restoreRate = 1f / Math.Max(1e-3f, s.RestoreSeconds);
            // Scale the rate with the target fade. Every turn reaches its target in LookFadeSeconds, with faster turns fading more deeply in the same time.
            _lookFade = Approach(_lookFade, lookTarget, lookTarget / Math.Max(1e-3f, s.LookFadeSeconds), restoreRate, deltaTime);
            _walkFade = Approach(_walkFade, walkTarget, 1f / Math.Max(1e-3f, s.WalkFadeSeconds), restoreRate, deltaTime);
        }

        // Move this frame's opacity from the steady value toward the moving value by the fade amount.
        public float Opacity(float steadyOpacity, AimSteadinessSettings s)
        {
            float moving = Math.Min(s.MovingOpacity, steadyOpacity);
            return steadyOpacity - (steadyOpacity - moving) * Fade;
        }

        // Move linearly toward the target. riseRate controls how fast the picture fades and fallRate controls how fast it returns. Both are in full fades per second.
        private static float Approach(float value, float target, float riseRate, float fallRate, float deltaTime)
        {
            if (value < target)
                return Math.Min(target, value + riseRate * deltaTime);
            return Math.Max(target, value - fallRate * deltaTime);
        }

        private static float DeltaAngle(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            else if (d < -180f) d += 360f;
            return d;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        private static float SmoothStep(float t) => t * t * (3f - 2f * t);
    }
}
