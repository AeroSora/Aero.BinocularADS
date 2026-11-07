using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Aero.BinocularADS
{
    // Use the game's SSR-off cubemap strength for the aimed weapon. With SSR on, AmbientLight's Custom Ambient pass sets _ReflectionIntensity to 1. The SSR composite subtracts that cubemap contribution and adds back what the screen-space trace finds. The eye runs Custom Ambient without SSR, so leaving the full cubemap in place makes the gun look like it glows.
    //
    // Set the shared property block to the rain wetting curve value times ReflectionIntensity. Re-record the eye's ambient buffer and restore the original value in finally. The gameplay camera keeps the buffer it recorded in AmbientLight.LateUpdate. This adds no GPU pass, and the weapon gets the game's SSR-off look without traced scene reflections.
    //
    // Run this only when PPv2's ScreenSpaceReflections.IsEnabledAndSupported says the gameplay camera will draw SSR. That also respects mods that suppress SSR through the same check.
    internal static class EyeReflectionIntensity
    {
        private static readonly FieldInfo AmbientCamerasField = AccessTools.Field(typeof(AmbientLight), "_cameras");
        private static readonly FieldInfo AmbientBlockField = AccessTools.Field(typeof(AmbientLight), "_screenAmbientBlock");
        private static readonly FieldInfo ReflectionIntensityIdField = AccessTools.Field(typeof(AmbientLight), "_reflectionIntensityID");
        private static readonly FieldInfo ReflectionIntensityField = AccessTools.Field(typeof(AmbientLight), "ReflectionIntensity");
        private static readonly FieldInfo WettingCurveField = AccessTools.Field(typeof(AmbientLight), "_reflectionWettingFunc");
        private static readonly MethodInfo InitAmbientBufferMethod = AccessTools.Method(typeof(AmbientLight), "InitAmbientBuffer");
        private static readonly PropertyInfo WettingProperty = AccessTools.Property(typeof(RainController), "Wetting");

        private static FieldInfo _layerContextField;
        private static bool _layerContextResolved;

        private static AmbientLight _pendingOwner;
        private static Camera _pendingEye;
        private static float _pendingValue;

        private static MaterialPropertyBlock _appliedBlock;
        private static int _appliedId;
        private static float _savedValue;

        // After a failure, skip this override for the current ambient owner and eye camera. Warn once and try again when either is replaced.
        private static AmbientLight _failedOwner;
        private static Camera _failedEye;

        private static string _lastState;

        // True if the override is ready for this eye render.
        internal static bool Active => _pendingOwner != null;

        // Decide before BeginEyeRender whether to use the SSR-off strength. Keep lookup failures out of the render path.
        internal static void Prepare(Camera main, Camera eye, AmbientLight owner, bool enabled)
        {
            _pendingOwner = null;
            _pendingEye = null;
            try
            {
                if (main == null || eye == null)
                    return;
                if (!enabled)
                {
                    Report("off (Match Screen Space Reflections is Always Off)");
                    return;
                }
                if (!GameSsrRenders(main, out string reason))
                {
                    Report($"off ({reason})");
                    return;
                }
                if (owner == null)
                {
                    Report("off (no ambient owner for the eye camera)");
                    return;
                }
                if (owner == _failedOwner && eye == _failedEye)
                    return;
                if (AmbientCamerasField == null || AmbientBlockField == null || ReflectionIntensityIdField == null ||
                    ReflectionIntensityField == null || WettingCurveField == null || InitAmbientBufferMethod == null ||
                    WettingProperty == null)
                {
                    Report("off (AmbientLight or RainController members missing in this game version)", warn: true);
                    return;
                }

                var curve = WettingCurveField.GetValue(owner) as AnimationCurve;
                float wetting = (float)WettingProperty.GetValue(null, null);
                float intensity = (float)ReflectionIntensityField.GetValue(owner);
                _pendingValue = (curve != null ? curve.Evaluate(wetting) : 1f) * intensity;
                _pendingOwner = owner;
                _pendingEye = eye;
                Report($"on ({reason}) | eye reflection intensity {_pendingValue:0.00} (ReflectionIntensity {intensity:0.00} " +
                       $"x wetting curve {(curve != null ? curve.Evaluate(wetting) : 1f):0.00} at wetting {wetting:0.##}); " +
                       $"gameplay camera keeps 1");
            }
            catch (Exception e)
            {
                _pendingOwner = null;
                _pendingEye = null;
                _failedOwner = owner;
                _failedEye = eye;
                Report($"failed: {e.GetType().Name}: {e.Message}", warn: true);
            }
        }

        // Set the SSR-off strength and re-record the eye's ambient buffer just before drawing.
        internal static void BeginEyeRender()
        {
            if (_pendingOwner == null)
                return;
            MaterialPropertyBlock block = null;
            int id = 0;
            try
            {
                block = AmbientBlockField.GetValue(_pendingOwner) as MaterialPropertyBlock;
                id = (int)ReflectionIntensityIdField.GetValue(null);
                var cameras = AmbientCamerasField.GetValue(_pendingOwner) as IDictionary;
                object settings = cameras != null && cameras.Contains(_pendingEye) ? cameras[_pendingEye] : null;
                if (block == null || settings == null)
                {
                    Suspend();
                    Report(block == null ? "off (ambient property block missing)" : "off (eye camera not registered with the ambient owner)",
                           warn: true);
                    return;
                }
                _savedValue = block.GetFloat(id);
                _appliedBlock = block;
                _appliedId = id;
                block.SetFloat(id, _pendingValue);
                InitAmbientBufferMethod.Invoke(_pendingOwner, new[] { settings, _pendingEye });
            }
            catch (Exception e)
            {
                Restore();
                Suspend();
                Exception cause = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                Report($"failed: {cause.GetType().Name}: {cause.Message}", warn: true);
            }
        }

        private static void Suspend()
        {
            _failedOwner = _pendingOwner;
            _failedEye = _pendingEye;
            _pendingOwner = null;
            _pendingEye = null;
        }

        // Restore the gameplay camera's value from the eye render's finally block.
        internal static void EndEyeRender()
        {
            Restore();
        }

        private static void Restore()
        {
            if (_appliedBlock == null)
                return;
            try
            {
                _appliedBlock.SetFloat(_appliedId, _savedValue);
            }
            finally
            {
                _appliedBlock = null;
            }
        }

        // Use PPv2's own ScreenSpaceReflections.IsEnabledAndSupported check and render context to tell whether gameplay SSR will draw. This sees mods that patch the check to suppress SSR. Without a context, skip the override and log why.
        internal static bool GameSsrRenders(Camera main, out string reason)
        {
            var layer = main == null ? null : main.GetComponent<PostProcessLayer>();
            if (layer == null || !layer.enabled)
            {
                reason = "no enabled post-process layer";
                return false;
            }
            var settings = layer.GetSettings<ScreenSpaceReflections>();
            if (settings == null || !settings.active || !settings.enabled.value)
            {
                reason = "game SSR off";
                return false;
            }
            if (!_layerContextResolved)
            {
                _layerContextResolved = true;
                _layerContextField = AccessTools.Field(typeof(PostProcessLayer), "m_CurrentContext");
                if (_layerContextField == null)
                    Plugin.Log.LogWarning("[BinoADS] eye reflection intensity: PostProcessLayer.m_CurrentContext not found");
            }
            var context = _layerContextField?.GetValue(layer) as PostProcessRenderContext;
            if (context == null || context.camera == null)
            {
                reason = "game SSR on but its render context is unavailable, so suppression cannot be checked";
                return false;
            }
            if (!settings.IsEnabledAndSupported(context))
            {
                reason = "game SSR on but not rendered: unsupported, or suppressed by another mod";
                return false;
            }
            reason = "game SSR rendering";
            return true;
        }

        private static void Report(string state, bool warn = false)
        {
            // Log changes only. Always emit warnings, and emit other states when diagnostics are enabled.
            if (state == _lastState)
                return;
            _lastState = state;
            string line = $"[BinoADS] eye reflection intensity {state}";
            if (warn)
                Plugin.Log.LogWarning(line);
            else
                Plugin.Diagnostic(line);
        }

        internal static void Dispose()
        {
            Restore();
            _pendingOwner = null;
            _pendingEye = null;
            _failedOwner = null;
            _failedEye = null;
            _lastState = null;
        }
    }
}
