using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aero.BinocularADS
{
    internal static partial class ProductionPipeline
    {
        // The eye camera only culls weapon geometry. In Unity's built-in pipeline that also means its sun shadow map has no walls or roofs, so the weapon would look sunlit indoors. EFT's global ambient, analytic lights and stencil volumes still work. Including the world in the cull fixes shadows but costs about three times as much.
        //
        // SunShadowSampler reads the main camera's shadow mask behind the weapon and scales the directional light during the eye render. The sample arrives a frame late and applies to the whole gun, so it cannot show shadow edges or dappled light. Local lights keep their normal strength. Restore the shared sun intensity in finally before another camera can see it.
        private static Light _sunLight;
        private static float _sunFactor = 1f;
        private static float _savedSunIntensity;
        private static bool _sunScoped;
        private static float _sunRescanAt;

        // Try RenderSettings.sun first because EFT's weather drives it. If the scene leaves it unset, look for the brightest directional light. Rate-limit that search so a map without a sun doesn't scan the scene on every aimed frame.
        private static Light ResolveSun()
        {
            if (_sunLight != null && _sunLight.isActiveAndEnabled)
                return _sunLight;

            Light candidate = RenderSettings.sun;
            if (candidate != null && candidate.isActiveAndEnabled &&
                candidate.type == LightType.Directional)
            {
                _sunLight = candidate;
                return _sunLight;
            }

            if (Time.unscaledTime < _sunRescanAt)
                return null;
            _sunRescanAt = Time.unscaledTime + 5f;

            Light best = null;
            float brightest = -1f;
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (light.type != LightType.Directional || !light.isActiveAndEnabled)
                    continue;
                if (light.intensity > brightest)
                {
                    brightest = light.intensity;
                    best = light;
                }
            }

            _sunLight = best;
            return _sunLight;
        }

        // Start-of-aim state for the shadow mask. See UpdateSunOcclusion.
        private const float ProvisionalMaxAgeSeconds = 20f;
        private const float ProvisionalMaxDistanceM = 3f;
        private static bool _maskPrimed;
        private static string _maskProvisional = "none";

        private const int AimEntryFrames = 30;
        private static int _aimEntryFrame;
        private static float _aimEntryIdleSeconds;
        private static float _aimEntryMaxMs;
        private static float _aimEntrySumMs;
        private static int _aimEntrySamples;
        private static bool _nativeAiming;
        private static int _aimEntryMaxFrame;
        private static bool _aimEntryBypass;
        private static bool _aimEntryReported = true;
        private static float _lastAimEndTime = float.NegativeInfinity;

        // With diagnostics enabled, Plugin.Update tracks the first aimed frames and logs them with the preceding idle time. Use this to tie an aim-entry hitch to pipeline work and time spent idle.
        internal static void TrackAimEntry(float deltaSeconds)
        {
            // Track native aim edges separately because bypassed sessions never reach End() and have no render-session timestamps.
            bool aiming = (Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.HandsController as EFT.Player.FirearmController)?.IsAiming ?? false;
            if (aiming && !_nativeAiming)
            {
                _aimEntryFrame = Time.frameCount;
                _aimEntryIdleSeconds = Time.unscaledTime - _lastAimEndTime;
                _aimEntryMaxMs = 0f;
                _aimEntrySumMs = 0f;
                _aimEntrySamples = 0;
                bool bypass = false;
                DeveloperMeasurementBypass(ref bypass);
                _aimEntryBypass = bypass;
                _aimEntryReported = false;
                AimReallocsAtStart = _targetReallocs;
            }
            else if (!aiming && _nativeAiming)
            {
                _lastAimEndTime = Time.unscaledTime;
                if (!_aimEntryReported && _aimEntrySamples > 0)
                {
                    _aimEntryReported = true;
                    Plugin.Log.LogInfo($"[BinoADS] aim entry | {(_aimEntryBypass ? "measurement bypass | " : "")}aim ended after {_aimEntrySamples} frames: max={_aimEntryMaxMs:0.0} ms (frame {_aimEntryMaxFrame}) mean={_aimEntrySumMs / _aimEntrySamples:0.0} ms | registry builds={AimBuilds} {AimBuildMs:0.0} ms, render resources {AimResourcesMs:0.0} ms with {_targetReallocs - AimReallocsAtStart} target realloc(s) | since last aim-out: prefix max {AimPrefixMaxMs:0.0} ms at frame {AimPrefixMaxFrame}, eye render max {AimEyeRenderMaxMs:0.0} ms at frame {AimEyeRenderMaxFrame}");
                }
                AimBuildMs = 0; AimResourcesMs = 0; AimBuilds = 0;
                AimPrefixMaxMs = 0; AimEyeRenderMaxMs = 0; AimPrefixMaxFrame = 0; AimEyeRenderMaxFrame = 0;
            }
            _nativeAiming = aiming;
            if (_aimEntryReported || !aiming)
                return;
            float ms = deltaSeconds * 1000f;
            if (ms > _aimEntryMaxMs) { _aimEntryMaxMs = ms; _aimEntryMaxFrame = Time.frameCount; }
            _aimEntrySumMs += ms;
            _aimEntrySamples++;
            if (_aimEntrySamples < AimEntryFrames)
                return;
            _aimEntryReported = true;
            Plugin.Log.LogInfo($"[BinoADS] aim entry | {(_aimEntryBypass ? "measurement bypass | " : "")}idle before={(float.IsInfinity(_aimEntryIdleSeconds) ? "first aim" : _aimEntryIdleSeconds.ToString("0.0") + " s")} " +
                               $"first {_aimEntrySamples} frames (from frame {_aimEntryFrame}): max={_aimEntryMaxMs:0.0} ms (frame {_aimEntryMaxFrame}) mean={_aimEntrySumMs / _aimEntrySamples:0.0} ms | registry builds={AimBuilds} {AimBuildMs:0.0} ms, render resources {AimResourcesMs:0.0} ms with {_targetReallocs - AimReallocsAtStart} target realloc(s) | since last aim-out: prefix max {AimPrefixMaxMs:0.0} ms at frame {AimPrefixMaxFrame}, eye render max {AimEyeRenderMaxMs:0.0} ms at frame {AimEyeRenderMaxFrame}");
        }

        // Move the sun multiplier toward the main camera's shadow-mask reading. Keep this per-frame work outside the render timer so cpuSubmitAvg measures camera work.
        private static void UpdateSunOcclusion(Camera main)
        {
            if (!Plugin.SunOcclusionEnabled.Value)
            {
                _sunFactor = 1f;
                SunShadowSampler.Detach("Block Sunlight Indoors is off");
                return;
            }

            Light sun = ResolveSun();
            if (sun == null)
            {
                _sunFactor = 1f;
                SunShadowSampler.Detach("no sun light");
                return;
            }

            float target;
            SunShadowSampler.Attach(sun);

            if (SunShadowSampler.HasValue)
            {
                // Main camera's resolved shadow behind the weapon. 0 is fully shaded and 1 is fully lit.
                target = SunShadowSampler.Visibility;
                if (!_maskPrimed)
                {
                    // Use the first actual reading immediately to replace the provisional value.
                    _maskPrimed = true;
                    _sunFactor = target;
                    Plugin.Diagnostic($"[BinoADS] sun mask primed: visibility={target:0.00} (provisional start was {_maskProvisional}) | {SunShadowSampler.Describe()}");
                    return;
                }
            }
            else
            {
                // Readback arrives a frame late. A raycast through roof gaps could light the first frame indoors and make the gun flash. Reuse the previous reading if it's recent and nearby. Otherwise start shaded until the first readback arrives.
                bool recent = Time.unscaledTime - SunShadowSampler.LastValueTime <= ProvisionalMaxAgeSeconds &&
                              (main.transform.position - SunShadowSampler.LastValuePosition).sqrMagnitude <= ProvisionalMaxDistanceM * ProvisionalMaxDistanceM;
                _sunFactor = recent ? SunShadowSampler.LastVisibility : 0f;
                _maskProvisional = recent ? $"last reading {SunShadowSampler.LastVisibility:0.00}" : "shaded";
                return;
            }
            float fade = Plugin.SunOcclusionFadeSeconds.Value;
            _sunFactor = fade <= 0f
                ? target
                : Mathf.MoveTowards(_sunFactor, target, Time.unscaledDeltaTime / fade);
        }

        private static void BeginSunScope()
        {
            if (_sunScoped || _sunLight == null || _sunFactor >= 1f)
                return;

            _savedSunIntensity = _sunLight.intensity;
            _sunLight.intensity = _savedSunIntensity * _sunFactor;
            _sunScoped = true;
        }

        private static void EndSunScope()
        {
            if (!_sunScoped)
                return;

            // The light can be destroyed or the raid can end before cleanup. Guard the restore so finally doesn't mask the original exception.
            if (_sunLight != null)
                _sunLight.intensity = _savedSunIntensity;
            _sunScoped = false;
        }

        // Render the dominant eye's colour and exact coverage, then premultiply and blur the weapon.
        private static bool CaptureEye(Camera main, EyePose pose)
        {
            ConfigureEyeCamera(main, pose, _eyeColorA);
            ConfigureCoverageCommand(pose.View, _eye.projectionMatrix);
            EyeReflectionIntensity.Prepare(main, _eye, _ambientOwner,
                Plugin.SsrReflections.Value != SsrReflectionMatch.AlwaysOff);

            TemporaryForce.Clear();
            try
            {
                _capturing = true;
                ApplyEyePhase();
                long eyeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                // Keep the eye's AO globals and reflection strength active through the diagnostic callbacks. Restore both in finally.
                try
                {
                    EyeReflectionIntensity.BeginEyeRender();
                    EyeAmbientOcclusion.BeginEyeRender();
                    try
                    {
                        DeveloperBeforeEyeRender();
                        EyeProbeLight.Sync();
                        _eye.Render();
                    }
                    finally
                    {
                        DeveloperAfterEyeRender();
                    }
                }
                finally
                {
                    EyeReflectionIntensity.EndEyeRender();
                    EyeAmbientOcclusion.EndEyeRender();
                    DeveloperAfterAoRestore();
                }
                double eyeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - eyeStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (eyeMs > AimEyeRenderMaxMs) { AimEyeRenderMaxMs = eyeMs; AimEyeRenderMaxFrame = Time.frameCount; }
                _eyeRenderCount++;
                return true;
            }
            finally
            {
                for (int i = TemporaryForce.Count - 1; i >= 0; i--)
                {
                    ForceSnapshot snapshot = TemporaryForce[i];
                    if (snapshot.Renderer != null)
                        snapshot.Renderer.forceRenderingOff = snapshot.ForceOff;
                }
                TemporaryForce.Clear();
                DisableEyeClones();
                _capturing = false;
            }
        }

        private static void ConfigureEyeCamera(Camera main, EyePose pose, RenderTexture target)
        {
            // Both eyes use the main camera's unjittered projection over the full frame.
            Matrix4x4 projection = main.nonJitteredProjectionMatrix;
            _eye.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            _eye.worldToCameraMatrix = pose.View;
            _eye.projectionMatrix = projection;
            _eye.nonJitteredProjectionMatrix = projection;
            // Cull weapon and arm layers plus AmbientLight's layer, which its mask check needs to fill both buffers. Keep the main camera's culling matrix so the projection stays the same. Culling the world costs about three times the weapon-only render. UpdateSunOcclusion supplies the sun shadow from the game's mask.
            _eye.cullingMatrix = main.cullingMatrix;
            int eyeMask = WeaponLayerMask(main) | AmbientGateBit();

            // Use a spherical distance limit on layer 8 to keep other characters out of the eye render. Allow AmbientLight's layer (0:Default) with a tiny positive radius to pass its gate without drawing the scene. Unity interprets zero as the far clip distance.
            int ambientLayer = AmbientGateLayer();
            var cullDistances = new float[32];
            for (int layer = 0; layer < 32; layer++)
                cullDistances[layer] = layer == ambientLayer && (WeaponLayerMask(main) & (1 << layer)) == 0
                    ? AmbientGateCullRadiusMeters
                    : EyeCullRadiusMeters;
            DeveloperEyeCulling(main, ref eyeMask, cullDistances);
            _eye.cullingMask = eyeMask;
            _eye.layerCullDistances = cullDistances;
            _eye.layerCullSpherical = true;
            _eye.renderingPath = main.actualRenderingPath;
            _eye.allowHDR = main.allowHDR;
            _eye.allowMSAA = main.allowMSAA;
            _eye.clearFlags = main.clearFlags;
            _eye.backgroundColor = main.backgroundColor;
            _eye.nearClipPlane = main.nearClipPlane;
            _eye.farClipPlane = main.farClipPlane;
            _eye.orthographic = main.orthographic;
            _eye.orthographicSize = main.orthographicSize;
            // The nearby weapon parts have no scene occluders in this pass, so occlusion queries would do extra work without culling anything.
            _eye.useOcclusionCulling = false;
            _eye.depthTextureMode = EyeAmbientOcclusion.RequiredDepthMode;
            _eye.usePhysicalProperties = main.usePhysicalProperties;
            _eye.sensorSize = main.sensorSize;
            _eye.lensShift = main.lensShift;
            _eye.focalLength = main.focalLength;
            _eye.gateFit = main.gateFit;
            _eye.rect = new Rect(0f, 0f, 1f, 1f);
            _eye.targetTexture = target;
        }

        private static bool _loggedEmptyMask;
        private static bool _loggedMaskFallback;

        // Print the layer names so the culling mask is readable in logs.
        private static string DescribeMask(int mask)
        {
            var names = new System.Text.StringBuilder();
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0)
                    continue;
                if (names.Length > 0)
                    names.Append(',');
                string name = LayerMask.LayerToName(layer);
                names.Append(layer).Append(':')
                     .Append(string.IsNullOrEmpty(name) ? "<unnamed>" : name);
            }
            return names.ToString();
        }

        // Intersect registered weapon and arm layers with the main camera's mask. With no renderers, draw nothing. If renderers exist but the result is zero, use the main mask and log the fallback.
        private static int WeaponLayerMask(Camera main)
        {
            int mask = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                Renderer source = Entries[i].Source;
                if (source != null)
                    mask |= 1 << source.gameObject.layer;
            }
            mask &= main.cullingMask;

            // The shared weapon layer also contains other characters and their gear. Bound the cull volume as well as the mask because their cost doesn't depend on render-target size.
            //
            // An empty registry is possible on a loading screen. Draw nothing in that case. Falling back to the main mask could render the entire scene through a second camera and stall for seconds.
            if (Entries.Count == 0)
            {
                if (!_loggedEmptyMask)
                {
                    _loggedEmptyMask = true;
                    Plugin.Diagnostic($"[BinoADS] no weapon renderers registered at frame {Time.frameCount}; the eye cameras draw nothing (no whole-world fallback)");
                }
                return 0;
            }
            if (mask == 0)
            {
                if (!_loggedMaskFallback)
                {
                    _loggedMaskFallback = true;
                    Plugin.Log.LogWarning(
                        "[BinoADS] weapon layer mask resolved to zero; falling back to the MAIN camera " +
                        "mask, so the eye cameras are drawing the entire world, which is expensive.");
                }
                return main.cullingMask;
            }
            return mask;
        }

        private static void ApplyEyePhase()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Source == null)
                    continue;

                if (entry.Kind == EntryKind.Ammunition)
                {
                    SetTemporaryForceOff(entry.Source);
                    continue;
                }

                if (entry.Kind == EntryKind.Mixed)
                {
                    SetTemporaryForceOff(entry.Source);
                    if (entry.OpaqueClone != null)
                        entry.OpaqueClone.enabled = entry.FrameVisible;
                }
            }

            foreach (Renderer sight in SightPicture)
                if (sight != null)
                    SetTemporaryForceOff(sight);
        }

        private static void SetTemporaryForceOff(Renderer renderer)
        {
            TemporaryForce.Add(new ForceSnapshot
            {
                Renderer = renderer,
                ForceOff = renderer.forceRenderingOff,
            });
            renderer.forceRenderingOff = true;
        }

        private static void ConfigureCoverageCommand(Matrix4x4 view, Matrix4x4 projection)
        {
            RenderTexture color = _eyeColorA, coverage = _coverageA, weapon = _weaponA;
            RefreshCoverageMaterials();
            _coverageCommand.Clear();
            _coverageCommand.BeginSample("Aero Weapon Coverage");
            _coverageCommand.SetRenderTarget(coverage);
            _coverageCommand.ClearRenderTarget(
                true, true, new Color(0f, _eye.farClipPlane, 0f, 0f));
            // Use renderIntoTexture: false at AfterEverything. The camera already accounted for the render-target flip, so true mirrors coverage against colour. OpenGL ignores the flag.
            _coverageCommand.SetViewProjectionMatrices(
                view, CoverageGpuProjection(projection));

            for (int i = 0; i < CoverageDraws.Count; i++)
            {
                CoverageDraw draw = CoverageDraws[i];
                if (draw.Renderer == null)
                    continue;
                Entry owner = draw.Owner;
                if (owner == null || !owner.FrameCoverage)
                    continue;
                _coverageCommand.DrawRenderer(
                    draw.Renderer, draw.CoverageMaterial, draw.Submesh, 0);
            }

            // Draw the shared [0,1] quad explicitly. Blit adjusts its UVs for _MainTex and exposes the flip in _MainTex_TexelSize.y, which would leave _CoverageDepth mirrored at the same UV. DrawMesh keeps this pass on the composite's orientation convention.
            _premultiply.SetTexture(MainTexId, color);
            _premultiply.SetTexture(CoverageDepthId, coverage);
            _premultiply.SetFloat(AeroFlipYId, FlipY);
            _coverageCommand.SetRenderTarget(weapon);
            _coverageCommand.SetViewport(new Rect(0f, 0f, weapon.width, weapon.height));
            _coverageCommand.DrawMesh(_quad, Matrix4x4.identity, _premultiply, 0, 0);
            _coverageCommand.EndSample("Aero Weapon Coverage");

            RecordDefocusBlur(_coverageCommand, weapon, _blurA);
        }

        // Draw eye B's exact coverage with its own view-projection and gather colour from eye A by depth. Use the same renderers, materials and SecondaryExcluded rules as ConfigureCoverageCommand. Graphics.ExecuteCommandBuffer runs the draws directly, and DrawRenderer bypasses culling, so only eye A needs Camera.Render. Return false on missing resources so the caller can restore native ADS.
        private static bool DeriveSecondEyeReprojected(EyePose a, EyePose b)
        {
            EnsureDeriveCommand();
            if (_eye == null || _reproject == null || _quad == null ||
                _weaponA == null || _weaponB == null ||
                _eyeColorA == null || _coverageA == null || _coverageB == null)
                return false;

            RefreshCoverageMaterials();

            Matrix4x4 projection = _eye.projectionMatrix;
            _deriveCommand.Clear();
            _deriveCommand.BeginSample("Aero Reprojected Second Image");

            // Each texel gets its real disparity from eye B's view-projection. Only the convergence plane has zero disparity. If the second image is missing or appears merged, inspect coverage, orientation, the colour gather, output alpha and the composite.
            _deriveCommand.SetRenderTarget(_coverageB);
            _deriveCommand.ClearRenderTarget(
                true, true, new Color(0f, _eye.farClipPlane, 0f, 0f));
            _deriveCommand.SetViewProjectionMatrices(
                b.View, CoverageGpuProjection(projection));

            for (int i = 0; i < CoverageDraws.Count; i++)
            {
                CoverageDraw draw = CoverageDraws[i];
                if (draw.Renderer == null)
                    continue;
                Entry owner = draw.Owner;
                if (owner == null || !owner.FrameCoverage || owner.SecondaryExcluded)
                    continue;
                _deriveCommand.DrawRenderer(
                    draw.Renderer, draw.CoverageMaterial, draw.Submesh, 0);
            }

            // Both eyes share a projection, so pass four terms and a B-to-A view transform. This avoids an inverse in the shader. With renderIntoTexture false, GetGPUProjectionMatrix leaves these X and Y terms alone, so use the original matrix the reconstruction was derived from.
            _reproject.SetVector(AeroProjTermsId, new Vector4(
                projection.m00, projection.m02, projection.m11, projection.m12));
            _reproject.SetMatrix(AeroBToAId, a.View * b.View.inverse);
            // z flips _coverageB at lookup if needed. The camera-attached pass was checked with renderIntoTexture false, but this standalone ExecuteCommandBuffer can store coverage differently. Flipping the lookup keeps NDC Y in the reconstruction unchanged. Set y to zero to disable the horizontal fill and isolate a fill artefact from one already in eye A's colour, without rebuilding the shader.
            _reproject.SetVector(AeroFillParamsId, new Vector4(
                ReprojectCoverageThreshold, ReprojectFillSpan,
                0f, ReprojectDepthTolerance));
            _reproject.SetTexture(MainTexId, _eyeColorA);
            _reproject.SetTexture(CoverageDepthId, _coverageB);
            _reproject.SetTexture(CoverageAId, _coverageA);
            // Keep the same convention as premultiply. Sample camera-space textures with untouched UVs and flip clip Y in the shader.
            _reproject.SetFloat(AeroFlipYId, FlipY);

            _deriveCommand.SetRenderTarget(_weaponB);
            _deriveCommand.SetViewport(new Rect(0f, 0f, _weaponB.width, _weaponB.height));
            _deriveCommand.DrawMesh(_quad, Matrix4x4.identity, _reproject, 0, 0);

            _deriveCommand.EndSample("Aero Reprojected Second Image");

            RecordDefocusBlur(_deriveCommand, _weaponB, _blurB);
            Graphics.ExecuteCommandBuffer(_deriveCommand);
            return true;
        }

        // Run two horizontal/vertical Gaussian pairs at half resolution. This costs 20 taps at a quarter of the pixels and stays smooth to about 25–30 px before the taps look like separate copies. See DefocusKernel and AeroWeaponDefocusBlur.shader.
        //
        // Only the ping-pong scratch is temporary. The main camera's SSAAPropagator reads the final texture after Unity has freed the eye camera's temporaries.
        private static void RecordDefocusBlur(
            CommandBuffer cmd, RenderTexture source, RenderTexture destination)
        {
            if (cmd == null || source == null || destination == null ||
                _blurPasses == null || _quad == null)
                return;

            cmd.BeginSample("Aero Weapon Defocus Blur");

            float radius = DefocusConfig.MaxRadiusPx.Value;
            if (!DefocusConfig.Enabled.Value || radius <= 0.001f)
            {
                // Clear the blur target even at zero defocus. The composite still evaluates both inputs to lerp, and a stale or uninitialised target can propagate NaN into a sharp frame.
                cmd.SetRenderTarget(destination);
                cmd.ClearRenderTarget(false, true, Color.clear);
                cmd.EndSample("Aero Weapon Defocus Blur");
                return;
            }

            // Convert the blur-target tap spacing from texels to UVs per axis for _AeroBlurStep.
            float stepTexels = DefocusKernel.StepTexels(radius);
            var horizontal = new Vector4(stepTexels / Mathf.Max(1, _blurWidth), 0f, 0f, 0f);
            var vertical = new Vector4(0f, stepTexels / Mathf.Max(1, _blurHeight), 0f, 0f);

            for (int i = 0; i < _blurPasses.Length; i++)
            {
                _blurPasses[i].SetVector(BlurStepId, (i & 1) == 0 ? horizontal : vertical);
                // Read premultiplied colour on the first pass and the blur shader's unpremultiplied output on later passes.
                _blurPasses[i].SetFloat(SourcePremultipliedId, i == 0 ? 1f : 0f);

                // Use identity because both textures belong to this pipeline and share an orientation. Premultiply handles the Camera.Render source, and the composite handles EFT's HDR destination with its renderIntoTexture projection. An even pass count can hide a wrong flip because the errors cancel until the count changes.
                _blurPasses[i].SetFloat(AeroFlipYId, 0f);
            }

            RenderTextureFormat format = destination.format;
            cmd.GetTemporaryRT(
                BlurScratch0Id, _blurWidth, _blurHeight, 0, FilterMode.Bilinear, format);
            cmd.GetTemporaryRT(
                BlurScratch1Id, _blurWidth, _blurHeight, 0, FilterMode.Bilinear, format);

            BlurPass(cmd, source, BlurScratch0Id, _blurPasses[0]);
            BlurPass(cmd, BlurScratch0Id, BlurScratch1Id, _blurPasses[1]);
            BlurPass(cmd, BlurScratch1Id, BlurScratch0Id, _blurPasses[2]);
            BlurPass(cmd, BlurScratch0Id, destination, _blurPasses[3]);

            cmd.ReleaseTemporaryRT(BlurScratch0Id);
            cmd.ReleaseTemporaryRT(BlurScratch1Id);
            cmd.EndSample("Aero Weapon Defocus Blur");
        }

        private static void BlurPass(
            CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier destination,
            Material material)
        {
            cmd.SetGlobalTexture(BlurSourceId, source);
            cmd.SetRenderTarget(destination);
            cmd.SetViewport(new Rect(0f, 0f, _blurWidth, _blurHeight));
            cmd.DrawMesh(_quad, Matrix4x4.identity, material, 0, 0);
        }

        private static void DisableEyeClones()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                DisableClone(Entries[i].OpaqueClone);
                DisableClone(Entries[i].GlassClone);
            }
        }

        private static void DisableClone(Renderer renderer)
        {
            if (renderer != null)
                renderer.enabled = false;
        }

        private static void RestoreCaptureMutations()
        {
            _capturing = false;
            DisableEyeClones();
        }

        private static void ApplyMainSuppression()
        {
            // Keep the native weapon visible while Takeover crossfades over it. Hide it when the composite reaches full opacity.
            if (BinocularController.Takeover < 1f)
                return;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Source == null)
                    continue;

                switch (entry.Kind)
                {
                    case EntryKind.Ordinary:
                        entry.Source.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                        break;
                    case EntryKind.Mixed:
                        entry.Source.forceRenderingOff = true;
                        if (entry.OpaqueClone != null)
                        {
                            entry.OpaqueClone.enabled = entry.FrameVisible;
                            entry.OpaqueClone.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                        }
                        if (entry.GlassClone != null)
                        {
                            entry.GlassClone.enabled = entry.FrameCoverage;
                            entry.GlassClone.shadowCastingMode = ShadowCastingMode.Off;
                        }
                        break;
                    case EntryKind.Ammunition:
                        entry.Source.forceRenderingOff = true;
                        break;
                }
            }

            // Layer 8 puts the optic lens in the main cull. Hide it with ShadowsOnly while sight-picture opacity is active so the pre-lens copy contains the scene behind the scope. EFT still draws it explicitly in OutputOptic, just as its depth prepass can draw suppressed weapon parts.
            if (_ssaaLens != null && MagnifiedOpacityInUse())
            {
                _lensFrameShadow = _ssaaLens.shadowCastingMode;
                _ssaaLens.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                _lensSuppressed = _ssaaLens;
                for (int g = 0; g < OpticGlass.Count; g++)
                {
                    Renderer glass = OpticGlass[g];
                    if (glass == null || glass.forceRenderingOff)
                        continue;
                    _glassSuppressed.Add(glass);
                    _glassFrameOff.Add(glass.forceRenderingOff);
                    glass.forceRenderingOff = true;
                }
                if (!_loggedGlass)
                {
                    _loggedGlass = true;
                    Plugin.Diagnostic($"[BinoADS] magnified sight opacity: {_glassSuppressed.Count} optic glass renderer(s) out of the main pass while aimed " +
                                       $"[{string.Join(", ", _glassSuppressed.Select(r => r.name).ToArray())}]");
                }
            }
        }

        private static Renderer _lensSuppressed;
        private static ShadowCastingMode _lensFrameShadow;
        // Hide this frame's CW FX/OpticLens transparent glass in the main pass.
        private static readonly List<Renderer> OpticGlass = new List<Renderer>();
        private static readonly List<Renderer> _glassSuppressed = new List<Renderer>();
        private static readonly List<bool> _glassFrameOff = new List<bool>();
        private static bool _loggedGlass;
    }
}
