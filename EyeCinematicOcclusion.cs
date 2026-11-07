using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aero.BinocularADS
{
    // Mirror EFT's Cinematic AO (UnityStandardAssets.CinematicEffects.AmbientOcclusion) so the aimed weapon keeps the same darkening as hipfire. CameraManager.SetSSAO enables it at High Quality HBAO and above.
    //
    // OnEnable sets SSAOMASKUSAGE for the MainCamera tag. The effect then reads _SSAOMask, which EFT's SSAOMask draws over the viewmodel. Copy the main camera's shader, quad, settings and keyword to the eye, and record the commands from SSAOMask.SetupBuffer into an eye-owned buffer. Attaching SSAOMask itself would overwrite the global at Start and on resize.
    //
    // Bind the eye mask in BeginEyeRender and restore the previous mask in EndEyeRender, called from finally. Resolve the type and its private members by reflection because the project has no Assembly-CSharp-firstpass reference. Skip Cinematic AO if the lookup fails.
    internal static class EyeCinematicOcclusion
    {
        private const string KeywordMaskUsage = "SSAOMASKUSAGE";
        private const string MaskBufferName = "Aero Eye SSAO Mask";

        private static readonly int SsaoMaskId = Shader.PropertyToID("_SSAOMask");

        private static readonly Type CinematicType =
            AccessTools.TypeByName("UnityStandardAssets.CinematicEffects.AmbientOcclusion");
        private static readonly FieldInfo SettingsField = Field("settings");
        private static readonly FieldInfo TriangleBlitField = Field("useTriangleBlit");
        private static readonly FieldInfo ShaderField = Field("_aoShader");
        private static readonly FieldInfo QuadField = Field("_quadMesh");
        private static readonly FieldInfo MaterialField = Field("_aoMaterial");
        private static readonly FieldInfo OcclusionSourceField =
            SettingsField == null ? null : AccessTools.Field(SettingsField.FieldType, "occlusionSource");

        private static readonly FieldInfo MaskShaderField = AccessTools.Field(typeof(SSAOMask), "_maskShader");
        private static readonly FieldInfo MaskEventField = AccessTools.Field(typeof(SSAOMask), "_cameraEvent");

        private static string _lastState;
        private static DepthTextureMode _requiredDepth = DepthTextureMode.None;

        // This class owns the mask and everything used to draw it.
        private static Camera _maskCamera;
        private static CameraEvent _maskEvent;
        private static CommandBuffer _maskBuffer;
        private static RenderTexture _maskTexture;
        private static Material _maskMaterial;
        private static Mesh _maskQuad;

        private static bool _publishing;
        private static Texture _savedMask;

        private static FieldInfo Field(string name) => CinematicType == null ? null : AccessTools.Field(CinematicType, name);

        // Depth textures the eye camera's Cinematic AO needs; None while it is off.
        internal static DepthTextureMode RequiredDepthMode => _requiredDepth;

        // Run once per aimed frame before rendering the eye. Keep setup failures out of the render path.
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
            if (CinematicType == null || SettingsField == null || ShaderField == null || QuadField == null || MaterialField == null)
            {
                Disable(eye);
                Report("off (Cinematic AO type or fields not found in this game build)");
                return;
            }

            var source = main.GetComponent(CinematicType) as Behaviour;
            if (source == null || !source.isActiveAndEnabled)
            {
                Disable(eye);
                Report(source == null ? "off (no Cinematic AO on the main camera)" : "off (main camera Cinematic AO disabled)");
                return;
            }

            var target = eye.GetComponent(CinematicType) as Behaviour;
            if (target == null)
            {
                // Set the shader, quad and settings on an inactive host before OnEnable runs. Remove the partial component if setup fails, and restore the host's activation either way.
                GameObject host = eye.gameObject;
                bool wasActive = host.activeSelf;
                host.SetActive(false);
                try
                {
                    target = (Behaviour)host.AddComponent(CinematicType);
                    ShaderField.SetValue(target, ShaderField.GetValue(source));
                    QuadField.SetValue(target, QuadField.GetValue(source));
                    TriangleBlitField?.SetValue(target, TriangleBlitField.GetValue(source));
                    SettingsField.SetValue(target, SettingsField.GetValue(source));
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
                // Settings is a struct, so this copies it. The component rebuilds itself in Update when settings change.
                SettingsField.SetValue(target, SettingsField.GetValue(source));
                if (!target.enabled)
                    target.enabled = true;
            }

            // Match the keyword every frame. OnEnable clears it on cameras without the MainCamera tag and can run again after a settings or size change.
            var sourceMaterial = MaterialField.GetValue(source) as Material;
            var targetMaterial = MaterialField.GetValue(target) as Material;
            bool maskUsage = sourceMaterial != null && sourceMaterial.IsKeywordEnabled(KeywordMaskUsage);
            if (targetMaterial != null && targetMaterial.IsKeywordEnabled(KeywordMaskUsage) != maskUsage)
            {
                if (maskUsage)
                    targetMaterial.EnableKeyword(KeywordMaskUsage);
                else
                    targetMaterial.DisableKeyword(KeywordMaskUsage);
            }

            string mask = "not used";
            if (maskUsage)
                mask = EnsureMask(main, eye);
            else
                ReleaseMask();

            int occlusionSource = OcclusionSourceField == null ? 0 : Convert.ToInt32(OcclusionSourceField.GetValue(SettingsField.GetValue(source)));
            // Include the depth flags OnEnable would request because the pipeline sets the eye's depth mode every frame. DepthTexture needs Depth and DepthNormals. DepthNormals needs DepthNormals. GBuffer needs neither.
            _requiredDepth = occlusionSource == 0 ? DepthTextureMode.Depth | DepthTextureMode.DepthNormals
                : occlusionSource == 1 ? DepthTextureMode.DepthNormals
                : DepthTextureMode.None;

            Report($"on mirroring Cinematic AO | occlusionSource={occlusionSource} maskUsage={maskUsage} mask={mask} " +
                   $"eyeMaterial={(targetMaterial != null ? "ok" : "not yet created")} eyeDepth={_requiredDepth}");
        }

        // Recreate SSAOMask.SetupBuffer at the same camera event. Clear an R8 target to white and draw the mask quad against the eye camera's depth and stencil.
        private static string EnsureMask(Camera main, Camera eye)
        {
            var nativeMask = main.GetComponent<SSAOMask>();
            var shader = nativeMask == null || MaskShaderField == null ? null : MaskShaderField.GetValue(nativeMask) as Shader;
            if (shader == null)
            {
                ReleaseMask();
                return "unavailable (no SSAOMask shader on the main camera)";
            }
            CameraEvent evt = MaskEventField == null ? CameraEvent.BeforeLighting : (CameraEvent)MaskEventField.GetValue(nativeMask);
            int width = Mathf.Max(1, eye.pixelWidth), height = Mathf.Max(1, eye.pixelHeight);

            bool rebuild = _maskBuffer == null || _maskCamera != eye || _maskEvent != evt || _maskTexture == null
                           || _maskTexture.width != width || _maskTexture.height != height
                           || _maskMaterial == null || _maskMaterial.shader != shader;
            if (!rebuild)
                return $"{width}x{height} at {evt}";

            ReleaseMask();
            _maskCamera = eye;
            _maskEvent = evt;
            _maskMaterial = new Material(shader) { name = "Aero Eye SSAO Mask", hideFlags = HideFlags.DontSave };
            _maskQuad = BuildQuad(0.1f);
            _maskTexture = new RenderTexture(width, height, 0, RenderTextureFormat.R8)
            {
                name = "Aero Eye SSAO Mask",
                hideFlags = HideFlags.DontSave,
            };
            _maskTexture.Create();
            _maskBuffer = new CommandBuffer { name = MaskBufferName };
            _maskBuffer.SetRenderTarget(_maskTexture, BuiltinRenderTextureType.CurrentActive);
            _maskBuffer.ClearRenderTarget(false, true, Color.white);
            _maskBuffer.DrawMesh(_maskQuad, Matrix4x4.identity, _maskMaterial);
            eye.AddCommandBuffer(evt, _maskBuffer);
            return $"{width}x{height} at {evt} (built)";
        }

        // Match SSAOMask.GetQuadMesh's index order and four corners at (+/-1, +/-1, z).
        private static Mesh BuildQuad(float z)
        {
            var mesh = new Mesh
            {
                name = "Aero Eye SSAO Mask Quad",
                hideFlags = HideFlags.DontSave,
                vertices = new[] { new Vector3(-1f, -1f, z), new Vector3(1f, -1f, z), new Vector3(-1f, 1f, z), new Vector3(1f, 1f, z) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) },
                triangles = new[] { 0, 1, 3, 3, 2, 0 },
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        // Bind the eye mask for the duration of the eye render.
        internal static void BeginEyeRender()
        {
            if (_maskTexture == null || _maskBuffer == null)
                return;
            _savedMask = Shader.GetGlobalTexture(SsaoMaskId);
            Shader.SetGlobalTexture(SsaoMaskId, _maskTexture);
            _publishing = true;
        }

        // Restore the previous mask in the eye render's finally block.
        internal static void EndEyeRender()
        {
            if (!_publishing)
                return;
            _publishing = false;
            Shader.SetGlobalTexture(SsaoMaskId, _savedMask);
            _savedMask = null;
        }

        private static void Disable(Camera eye)
        {
            _requiredDepth = DepthTextureMode.None;
            ReleaseMask();
            if (eye == null || CinematicType == null)
                return;
            if (eye.GetComponent(CinematicType) is Behaviour component && component.enabled)
                component.enabled = false;
        }

        // Detach the buffer and release the mask resources. Keep going if a cleanup step throws so the remaining resources still get released.
        private static void ReleaseMask()
        {
            if (_publishing)
                EndEyeRender();
            try
            {
                if (_maskCamera != null && _maskBuffer != null)
                    _maskCamera.RemoveCommandBuffer(_maskEvent, _maskBuffer);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[BinoADS] eye SSAO mask: buffer removal failed: {e.Message}"); }
            try { _maskBuffer?.Release(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[BinoADS] eye SSAO mask: buffer release failed: {e.Message}"); }
            _maskBuffer = null;
            _maskCamera = null;
            Destroy(ref _maskTexture);
            Destroy(ref _maskMaterial);
            Destroy(ref _maskQuad);
        }

        private static void Destroy<T>(ref T obj) where T : UnityEngine.Object
        {
            try
            {
                if (obj is RenderTexture texture)
                    texture.Release();
                if (obj != null)
                    UnityEngine.Object.Destroy(obj);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[BinoADS] eye SSAO mask: destroy failed: {e.Message}"); }
            obj = null;
        }

        private static void Report(string state, bool warn = false)
        {
            if (state == _lastState)
                return;
            _lastState = state;
            string line = $"[BinoADS] eye cinematic ambient occlusion {state}";
            if (warn)
                Plugin.Log.LogWarning(line);
            else
                Plugin.Diagnostic(line);
        }

        internal static void Dispose()
        {
            ReleaseMask();
            _lastState = null;
            _requiredDepth = DepthTextureMode.None;
        }
    }
}
