using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aero.BinocularADS
{
    // Mirror the game's HBAO on the eye camera so the aimed weapon keeps the crevice and contact shadows it has at hipfire. EFT uses HBAO as an opaque image effect or HBAO_Integrated before lighting, with PPv2 AmbientOcclusion disabled. Both affect the main camera before the weapon composite.
    //
    // Use the main camera's HBAO type, shader and settings. Set them before OnEnable builds the material, and let the eye own its runtime textures. The eye only renders the weapon and arms. Save and restore the global _HBAOTex around the eye render so other cameras still see the main camera's AO.
    internal static class EyeAmbientOcclusion
    {
        private static readonly int HbaoTexId = Shader.PropertyToID("_HBAOTex");

        private static string _lastState;
        private static DepthTextureMode _requiredDepth = DepthTextureMode.None;
        private static Texture _savedHbaoTex;
        private static bool _bracketed;

        // Depth textures needed by the eye's HBAO and Cinematic AO. Returns None with AO off.
        internal static DepthTextureMode RequiredDepthMode => _requiredDepth | EyeCinematicOcclusion.RequiredDepthMode;

        // Sync the eye's HBAO with the setting and the main camera before each aimed render. Keep setup errors inside this method.
        internal static void Sync(Camera main, Camera eye)
        {
            try
            {
                SyncCore(main, eye);
            }
            catch (Exception e)
            {
                Report($"failed: {e.GetType().Name}: {e.Message}", warn: true);
                Disable(eye);
            }
            // Sync Cinematic AO (High Quality and above) even if the HBAO mirror failed.
            EyeCinematicOcclusion.Sync(main, eye);
        }

        private static void SyncCore(Camera main, Camera eye)
        {
            if (eye == null || main == null)
                return;

            if (Plugin.WeaponAmbientOcclusion.Value == WeaponShadingQuality.Off)
            {
                Disable(eye);
                Report("off (setting)");
                return;
            }

            HBAO_Core source = null;
            string seen = "";
            foreach (HBAO_Core candidate in main.GetComponents<HBAO_Core>())
            {
                seen += $"{candidate.GetType().Name}(enabled={candidate.isActiveAndEnabled}) ";
                if (source == null && candidate.isActiveAndEnabled && candidate.hbaoShader != null)
                    source = candidate;
            }
            if (source == null)
            {
                Disable(eye);
                Report($"off (no active HBAO on the main camera; found: {(seen.Length > 0 ? seen.Trim() : "none")})");
                return;
            }

            Type type = source.GetType();
            var target = eye.GetComponent(type) as HBAO_Core;
            if (target == null)
            {
                // Add the component while the host is inactive so OnEnable can use the correct shader and settings to build its material and request depth textures. If setup fails, remove the partial component and reactivate the host so the eye can still render.
                GameObject host = eye.gameObject;
                bool wasActive = host.activeSelf;
                host.SetActive(false);
                try
                {
                    target = (HBAO_Core)host.AddComponent(type);
                    CopyAssetsAndSettings(source, target);
                }
                catch
                {
                    if (target != null)
                        UnityEngine.Object.DestroyImmediate(target);
                    throw;
                }
                finally
                {
                    host.SetActive(wasActive);
                }
            }
            else
            {
                CopySettings(source, target);
                if (!target.enabled)
                    target.enabled = true;
            }

            _requiredDepth = main.depthTextureMode & (DepthTextureMode.Depth | DepthTextureMode.DepthNormals);
            if (_requiredDepth == DepthTextureMode.None)
                _requiredDepth = DepthTextureMode.Depth;

            HBAO_Core.AOSettings ao = source.aoSettings;
            Report($"on mirroring {type.Name} | radius={ao.radius:0.00} intensity={ao.intensity:0.00} " +
                   $"normals={ao.perPixelNormals} quality={source.generalSettings.quality} " +
                   $"mainResolution={source.generalSettings.resolution} eyeResolution={target.generalSettings.resolution} eyeDepth={_requiredDepth} | " +
                   $"mainResources quad={(source.quadMesh != null ? "ok" : "MISSING")} noise={(source.noiseTex != null ? "ok" : "MISSING")} " +
                   $"eyeOwnsQuad={(target.quadMesh != null && target.quadMesh != source.quadMesh)}");
        }

        private static void CopyAssetsAndSettings(HBAO_Core source, HBAO_Core target)
        {
            // Copy the shader and settings. Leave noiseTex and quadMesh null so HBAO builds its own. CreateMaterial can destroy the quad, noise regeneration can destroy the texture, and OnDisable destroys both. Sharing them would let the eye destroy the main camera's resources.
            target.hbaoShader = source.hbaoShader;
            target.m_Presets = source.m_Presets;
            target.m_GeneralSettings = EyeGeneralSettings(source);
            target.m_AOSettings = source.m_AOSettings;
            target.m_ColorBleedingSettings = source.m_ColorBleedingSettings;
            target.m_BlurSettings = source.m_BlurSettings;

            // Use the main camera's CameraEvent for the HBAO_Integrated pass.
            foreach (FieldInfo field in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(CameraEvent) && field.DeclaringType != typeof(HBAO_Core))
                    field.SetValue(target, field.GetValue(source));
            }
        }

        // Refresh through the settings properties each frame, as EFT's controls do.
        private static void CopySettings(HBAO_Core source, HBAO_Core target)
        {
            target.generalSettings = EyeGeneralSettings(source);
            target.aoSettings = source.aoSettings;
            target.colorBleedingSettings = source.colorBleedingSettings;
            target.blurSettings = source.blurSettings;
        }

        // Copy the main camera's GeneralSettings struct and set the eye resolution on the copy. Resolution has the biggest effect on eye-pass cost. The game's Full setting costs about 1.5–2 ms.
        private static HBAO_Core.GeneralSettings EyeGeneralSettings(HBAO_Core source)
        {
            HBAO_Core.GeneralSettings settings = source.generalSettings;
            settings.resolution = Plugin.WeaponAmbientOcclusion.Value == WeaponShadingQuality.Full ? HBAO_Core.Resolution.Full
                : Plugin.WeaponAmbientOcclusion.Value == WeaponShadingQuality.Quarter ? HBAO_Core.Resolution.Quarter
                : HBAO_Core.Resolution.Half;
            return settings;
        }

        // Save the main camera's global AO texture before rendering the eye.
        internal static void BeginEyeRender()
        {
            _savedHbaoTex = Shader.GetGlobalTexture(HbaoTexId);
            _bracketed = true;
            EyeCinematicOcclusion.BeginEyeRender();
        }

        // Restore the main camera's global AO texture after the eye render.
        internal static void EndEyeRender()
        {
            EyeCinematicOcclusion.EndEyeRender();
            if (!_bracketed)
                return;
            _bracketed = false;
            Shader.SetGlobalTexture(HbaoTexId, _savedHbaoTex);
            _savedHbaoTex = null;
        }

        private static void Disable(Camera eye)
        {
            _requiredDepth = DepthTextureMode.None;
            if (eye == null)
                return;
            foreach (HBAO_Core component in eye.GetComponents<HBAO_Core>())
                if (component.enabled)
                    component.enabled = false;
        }

        private static void Report(string state, bool warn = false)
        {
            if (state == _lastState)
                return;
            _lastState = state;
            string line = $"[BinoADS] eye ambient occlusion {state}";
            if (warn)
                Plugin.Log.LogWarning(line);
            else
                Plugin.Diagnostic(line);
        }

        internal static void Dispose()
        {
            EyeCinematicOcclusion.Dispose();
            _lastState = null;
            _requiredDepth = DepthTextureMode.None;
        }
    }
}
