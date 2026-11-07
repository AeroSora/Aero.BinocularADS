using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace Aero.BinocularADS
{
    internal static partial class ProductionPipeline
    {
        private static bool MagnifiedOpacityInUse()
        {
            bool showCopy = false;
            DeveloperShowPreLensCopy(ref showCopy);
            // Use the configured opacity for this frame, reduced by Bindon aiming when the player turns or walks.
            return (showCopy || BindonAiming.EffectiveOpacity < 99.95f) &&
                   ShaderAssets.LensBlend != null && ShaderAssets.LensBlend.isSupported && _lensHookInstalled;
        }

        internal static bool _lensHookInstalled;

        private static string _sightLine = "not computed";
        private static bool _nativeAnchor;

        // Project EFT's live front-sight marker through the current eye. Return false if this scope has no active marker pair.
        private static bool UpdateNativeMarkerAnchor(EyePose pose)
        {
            _anchorFrameValid = false;
            Transform weapon = BinocularController.CurrentWeaponRoot;
            Transform marker = NativeFrontMarker.Front(BinocularController.CurrentWeaponAnimation);
            if (weapon == null || marker == null)
                return false;
            _sightLine = $"native marker '{marker.name}'";
            _anchorFrameValid = NativeFrontMarker.Project(marker.position, pose.View, _eye.projectionMatrix,
                out _anchorFrameUV, out _anchorFrameDepth);
            return true;
        }
        private static bool _anchorFrameValid;
        private static Vector2 _anchorFrameUV;
        private static float _anchorFrameDepth;

        // Resolve and project the front-post anchor after the dominant-eye capture, when the eye's projection and the rendered weapon and aim-bone poses are ready.
        private static void UpdateIronSightAnchor(EyePose pose)
        {
            bool wasNative = _nativeAnchor;
            _nativeAnchor = UpdateNativeMarkerAnchor(pose);
            if (_nativeAnchor != wasNative)
                Plugin.Diagnostic($"[BinoADS] iron-sight anchor: {(_nativeAnchor ? "game front-sight marker" : "none (no marker in use)")} | {NativeFrontMarker.Describe()}");
            if (!_nativeAnchor)
                _sightLine = "no game front-sight marker: " + NativeFrontMarker.Status;
        }

        // Bump this on teardown so an attachment change forces a new front-sight part lookup even if the renderer count stays the same.
        private static int _registryGeneration;

        private static bool AnchorFresh() => _nativeAnchor && _anchorFrameValid;

        private static string AnchorDescribe() => NativeFrontMarker.Describe();

        private static void ConfigureCompositeMaterial()
        {
            float fade = BinocularController.Fade;
            float overlap = Mathf.Clamp01(Plugin.OverlapOpacity.Value / 100f);
            float single = Mathf.Clamp01(Plugin.SingleEyeOpacity.Value / 100f);
            float share = Mathf.Clamp(Plugin.DominantEyeShare.Value / 100f, 0.5f, 0.9f);
            float focus = ResolveFocusDepth(_main);
            bool defocus = DefocusConfig.Enabled.Value && DefocusConfig.MaxRadiusPx.Value > 0.001f;
            float defocusFloor = DefocusConfig.UniformFloorForCurrentSight();

            _composite.SetTexture(WeaponAId, _weaponA);
            _composite.SetTexture(WeaponBId, _weaponB);
            _composite.SetTexture(BlurAId, _blurA);
            _composite.SetTexture(BlurBId, _blurB);
            _composite.SetTexture(CoverageAId, _coverageA);
            _composite.SetTexture(CoverageBId, _coverageB);

            // Weight the overlap and the two single-eye crescents independently. See AeroBinocularComposite.cginc.
            _composite.SetFloat(OverlapOpacityId, overlap);
            _composite.SetFloat(SingleOpacityId, single);
            _composite.SetFloat(ShareAId, share);
            _composite.SetFloat(ShareBId, 1f - share);
            _composite.SetFloat(FadeTId, fade);
            _composite.SetFloat(TakeoverId, BinocularController.Takeover);
            _composite.SetFloat(AeroFlipYId, FlipY);
            _composite.SetFloat(DefocusEnabledId, defocus ? 1f : 0f);
            _composite.SetFloat(FocusDepthId, focus);
            _composite.SetFloat(ClearBandId, DefocusConfig.ClearBandM.Value);
            _composite.SetFloat(FalloffId, DefocusConfig.FalloffM.Value);
            // The blur pass sets the radius through its tap spacing. The composite blends the sharp and fully blurred images using DefocusFraction in AeroBinocularComposite.cginc.
            _composite.SetFloat(OutlineSoftnessId, DefocusConfig.OutlineSoftness.Value);
            // Keep weapon pixels within the anchor's 3D sharp radius in focus, then increase blur over the fade distance. Always blur the second image fully. AnchorFresh means the front marker projects onto the weapon this frame. Log when this focus mode turns on or off.
            bool spotEligible = defocus && defocusFloor < 0.5f && DefocusConfig.IronSightFocus.Value;
            bool spot = spotEligible && AnchorFresh();
            // If irons have no usable anchor, blur the weapon uniformly. The depth band would leave whole surfaces near the farthest renderer sharp, including the AKM top rail, a strip of SVD barrel or nearly all of a sawn-off Mosin. Keep the band for optics, collimators and when iron-sight focus is disabled.
            bool unanchoredIron = spotEligible && !spot && IsIronSightClass(BinocularController.CurrentSight);
            if (unanchoredIron)
                defocusFloor = 1f;
            if (unanchoredIron != _unanchoredIronLatched)
            {
                _unanchoredIronLatched = unanchoredIron;
                if (unanchoredIron)
                    Plugin.Diagnostic($"[BinoADS] iron-sight focus: no anchor, dominant image uniformly blurred (depth band not used) at frame {Time.frameCount} | sight={BinocularController.CurrentSight} {AnchorDescribe()}");
            }
            _composite.SetFloat(DefocusFloorId, defocusFloor);
            if (spot != _spotLatched)
            {
                _spotLatched = spot;
                Plugin.Diagnostic($"[BinoADS] iron-sight focus {(spot ? (_spotEverActive ? "recovered" : "on") : "off")} at frame {Time.frameCount} fade={fade:0.00} | eligible={spotEligible} sightLine={_sightLine} {AnchorDescribe()}");
                _spotEverActive |= spot;
            }
            if (spot)
            {
                // Use this frame's marker projection so the sharp area follows the weapon.
                Vector2 anchorUV = _anchorFrameUV;
                float anchorDepth = _anchorFrameDepth;
                focus = anchorDepth;
                _composite.SetVector(IronSightAnchorId, new Vector4(
                    anchorUV.x, anchorUV.y, anchorDepth, 0f));
                _composite.SetVector(IronSightRadiiId, new Vector4(
                    DefocusConfig.IronSightSharpRadiusMm.Value / 1000f,
                    DefocusConfig.IronSightFadeDistanceMm.Value / 1000f,
                    DefocusConfig.IronSightBlurCurve.Value,
                    0f));
                // The eye camera uses this same projection, so its terms reconstruct eye-space positions from the composite's NDC.
                Matrix4x4 p = _main.nonJitteredProjectionMatrix;
                _composite.SetVector(ViewFromNdcId, new Vector4(1f / p[0, 0], 1f / p[1, 1], p[0, 2], p[1, 2]));
            }
            else
            {
                _composite.SetVector(IronSightRadiiId, Vector4.zero);
            }
            // Log the actual focus result once per aim, one second after its first composite.
            if (_spotAimStart <= 0f)
                _spotAimStart = Time.unscaledTime;
            if (!_loggedSpot && Time.unscaledTime - _spotAimStart > 1f && Plugin.Debug.Value)
            {
                _loggedSpot = true;
                Plugin.Diagnostic($"[BinoADS] {NativeFrontMarker.DescribeAimPoints(BinocularController.CurrentWeaponAnimation)}");
                DeveloperAimSettled(spot);
                Plugin.Diagnostic($"[BinoADS] iron-sight focus | active={spot} uniformBlurNoAnchor={unanchoredIron} curve={DefocusConfig.IronSightBlurCurve.Value:0.00} floor={defocusFloor:0} sharp={DefocusConfig.IronSightSharpRadiusMm.Value:0.0}mm fade={DefocusConfig.IronSightFadeDistanceMm.Value:0.0}mm parts={PartNames.Count} sightLine={_sightLine} projectedAnchor={_anchorFrameValid} uv=({_anchorFrameUV.x:0.000},{_anchorFrameUV.y:0.000}) depth={_anchorFrameDepth:0.000}m weaponLength={WeaponLengthM():0.000}m eyeTarget={_targetWidth}x{_targetHeight} | {AnchorDescribe()}");
            }
            _composite.SetFloat(ForceDefocusBId,
                spot || DefocusConfig.BlurSecondImageFully.Value ? 1f : 0f);

            // Build this only with diagnostics enabled. This path runs every aimed frame.
            if (!Plugin.Debug.Value)
                return;
            string description =
                $"enabled={defocus} focus={focus:0.000}m " +
                $"offset={DefocusConfig.FocusOffsetM.Value:+0.000;-0.000;0.000}m " +
                $"clear={DefocusConfig.ClearBandM.Value:0.000}m " +
                $"falloff={DefocusConfig.FalloffM.Value:0.000}m " +
                $"radius={DefocusConfig.MaxRadiusPx.Value:0.0}px floor={defocusFloor:0} " +
                $"sight={BinocularController.CurrentSight}";
            if (description != _lastDefocusLog)
            {
                _lastDefocusLog = description;
                Plugin.Log.LogInfo($"[BinoADS] defocus ownership | {description}");
            }
        }

        // Reject focus anchors closer than this. HandsContainer.Fireport can report 0.06–0.09 m, near the eye. With a 0.1 m clear band and 0.1 m falloff, that can fully blur everything beyond roughly 0.27 m and make the depth controls look broken.
        private const float MinPlausibleFocusM = 0.12f;

        private static bool _loggedFocusAnchor;

        private static float CameraDepth(Camera main, Vector3 world)
        {
            // Negate camera-space Z to get forward distance in Unity.
            return -main.worldToCameraMatrix.MultiplyPoint3x4(world).z;
        }

        // Use the SSAA renderer's live world bounds for the depth of the sight picture. The sight picture and transparent slots write no coverage, so sampling coverage there can return the far-plane clear value in G.
        private static bool TrySightDepth(Camera main, out float depth)
        {
            depth = 0f;
            Renderer sight = _ssaaCollimator != null ? _ssaaCollimator : _ssaaLens;
            if (sight == null || !sight.gameObject.activeInHierarchy)
                return false;
            depth = CameraDepth(main, sight.bounds.center);
            return depth >= MinPlausibleFocusM;
        }

        // Use the farthest registered weapon surface as a muzzle-end estimate. Iron sights have no separate sight-picture renderer, and the front post is near that end. Skip skinned meshes because they are the player's arms.
        private static bool TryForwardWeaponDepth(Camera main, out float depth)
        {
            float best = 0f;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Kind == EntryKind.Ammunition || entry.Source == null ||
                    entry.Source is SkinnedMeshRenderer ||
                    !entry.Source.enabled || !entry.Source.gameObject.activeInHierarchy)
                    continue;
                float d = CameraDepth(main, entry.Source.bounds.center);
                if (d > best)
                    best = d;
            }
            depth = best;
            return best >= MinPlausibleFocusM;
        }

        private static float _weaponLengthM = -1f;

        // Measure the largest dimension of the non-skinned weapon meshes in metres. Transform each mesh's local bounds into weapon-root space so world rotation does not affect the result. Cache it until aim-out.
        private static float WeaponLengthM()
        {
            if (_weaponLengthM >= 0f)
                return _weaponLengthM;
            Transform root = _weaponRoot;
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (root == null || entry.Kind == EntryKind.Ammunition || !(entry.Source is MeshRenderer) ||
                    !entry.Source.enabled || !entry.Source.gameObject.activeInHierarchy)
                    continue;
                Mesh mesh = entry.Source.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null)
                    continue;
                Bounds local = mesh.bounds;
                Transform t = entry.Source.transform;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    Vector3 p = root.InverseTransformPoint(t.TransformPoint(corner));
                    if (!any) { min = max = p; any = true; }
                    else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                }
            }
            Vector3 size = (max - min) * (root != null ? root.lossyScale.x : 1f);
            _weaponLengthM = any ? Mathf.Max(size.x, Mathf.Max(size.y, size.z)) : 0f;
            return _weaponLengthM;
        }

        // Focus distance used when neither the sight nor the weapon's far end gives a plausible anchor.
        private const float FallbackFocusM = 0.7f;

        private static float ResolveFocusDepth(Camera main)
        {
            float offset = DefocusConfig.FocusOffsetM.Value;
            if (main == null)
                return Mathf.Max(0.01f, FallbackFocusM + offset);

            bool haveSight = TrySightDepth(main, out float sightDepth);
            bool haveForward = TryForwardWeaponDepth(main, out float forwardDepth);

            // Try the sight picture first, then the farthest weapon surface, then a fixed distance. That last fallback keeps a bad anchor from pinning the blur ramp.
            float depth;
            string picked;
            if (haveSight) { depth = sightDepth; picked = "sight-renderer"; }
            else if (haveForward) { depth = forwardDepth; picked = "forward-weapon"; }
            else { depth = FallbackFocusM; picked = "fallback (no plausible anchor)"; }

            if (!_loggedFocusAnchor && Plugin.Debug.Value)
            {
                _loggedFocusAnchor = true;
                Plugin.Log.LogInfo(
                    $"[BinoADS] focus anchor | picked={picked} resolved={depth:0.000}m "+
                    $"sightClass={BinocularController.CurrentSight} uniformFloor={DefocusConfig.UniformFloorForCurrentSight():0} " +
                    $"sight={(haveSight ? sightDepth.ToString("0.000") + "m" : "<implausible>")} " +
                    $"forward={(haveForward ? forwardDepth.ToString("0.000") + "m" : "<implausible>")} " +
                    $"offset={offset:+0.000;-0.000;0.000}m minPlausible={MinPlausibleFocusM:0.00}m");
            }

            return Mathf.Max(main.nearClipPlane + 0.001f, depth + offset);
        }

        private static void LogFrameOwnership(Camera camera, int serial)
        {
            if (!_loggedPipeline)
            {
                _loggedPipeline = true;
                string upscaler = SsaaOwnerField?.GetValue(_ssaa)?.GetType().Name ?? "<unknown>";
                Plugin.Diagnostic(
                    $"[BinoADS] pipeline | camera='{camera.name}' internal={camera.pixelWidth}x{camera.pixelHeight} " +
                    $"output={Screen.width}x{Screen.height} upscaler={upscaler} " +
                    "stage=post-upscale/pre-native-optic manualEyeRenders=1");
            }

            if (Plugin.Debug.Value && Time.unscaledTime >= _nextPerfLog)
            {
                FrameTimingManager.CaptureFrameTimings();
                uint timingCount = FrameTimingManager.GetLatestTimings(1, LatestTiming);
                string frameTiming = timingCount == 0
                    ? "frameTiming=unavailable"
                    : $"cpuFrame={LatestTiming[0].cpuFrameTime:0.000}ms " +
                      $"gpuFrame={LatestTiming[0].gpuFrameTime:0.000}ms";
                double average = _eyePerfFrames == 0 ? 0 : _eyeTotalMs / _eyePerfFrames;
                float fraction =
                    2f * _targetWidth * _targetHeight /
                    Mathf.Max(1f, camera.pixelWidth * camera.pixelHeight);
                Plugin.Log.LogInfo(
                    $"[BinoADS] cost | build={serial} " +
                    $"eyeRenders/frame=1 secondImage=reprojected totalEyeRenders={_eyeRenderCount} " +
                    $"cpuSubmitAvg={average:0.000}ms peak={_eyePeakMs:0.000}ms " +
                    $"{frameTiming} " +
                    $"eyeRT={_targetWidth}x{_targetHeight} rtReallocs/window={_targetReallocs} " +
                    $"twoEyePixelRatio={fraction:0.000} " +
                    // Matching eyeMask and mainMask means WeaponLayerMask fell back after finding no weapon layers. The eye camera is drawing the whole world.
                    $"eyeMask=0x{(_eye != null ? _eye.cullingMask : 0):X8} " +
                    $"mainMask=0x{camera.cullingMask:X8} " +
                    $"maskIsWeaponOnly={(_eye != null && _eye.cullingMask != camera.cullingMask)} " +
                    $"eyeLayers=[{DescribeMask(_eye != null ? _eye.cullingMask : 0)}] " +
                    $"sunOcclusion={Plugin.SunOcclusionEnabled.Value} " +
                    $"sunFactor={_sunFactor:0.00} {SunShadowSampler.Describe()} " +
                    $"sunLight={(_sunLight != null ? _sunLight.name : "NONE")} " +
                    $"gameShadowDist={QualitySettings.shadowDistance:0.#}m " +
                    $"gameCascades={QualitySettings.shadowCascades} " +
                    $"eyeOcclusionCulling={(_eye != null && _eye.useOcclusionCulling)} " +
                    $"eyeCullRadius={EyeCullRadiusMeters:0.#}m " +
                    $"eyeCullSpherical={(_eye != null && _eye.layerCullSpherical)} " +
                    $"entries={Entries.Count}");
                // Keep GPU work out of this callback. It runs inside the main camera's command-buffer build, where immediate draws can disturb the target EFT expects.
                DeveloperCostWindow();
                ResetCostAccumulators();
                _nextPerfLog = Time.unscaledTime + 10f;
            }

            LogAppearanceDelta(camera);
            EnsureMainHook(camera);
        }

        // Log once per aim to help explain why the weapon can brighten at takeover. At Fade = 0, the composite already draws the full dominant image and the native weapon is hidden.
        //
        // Exposure can settle after about a second because the weapon drops out of PPv2 metering. The eye has no PostProcessLayer, so its AO comes from its own HBAO and Cinematic AO, subject to the setting and what's active on the main camera. BeforeLighting decal injectors also lose access to the weapon because it leaves the main G-buffer. Weapon camo or stickers can therefore stay missing for the entire aim.
        private static void LogAppearanceDelta(Camera main)
        {
            if (_loggedAppearanceDelta || Plugin.Debug == null || !Plugin.Debug.Value || _eye == null)
                return;
            _loggedAppearanceDelta = true;

            var decals = new System.Text.StringBuilder();
            CommandBuffer[] beforeLighting = main.GetCommandBuffers(CameraEvent.BeforeLighting);
            for (int i = 0; i < beforeLighting.Length; i++)
            {
                if (beforeLighting[i] == null)
                    continue;
                if (decals.Length > 0)
                    decals.Append(", ");
                decals.Append(beforeLighting[i].name);
            }

            bool eyeHasPostProcess = _eye.GetComponent(typeof(PostProcessLayer)) != null;
            HBAO_Core eyeHbao = _eye.GetComponent<HBAO_Core>();
            Plugin.Log.LogInfo(
                "[BinoADS] appearance delta | " +
                $"mainDepthMode={main.depthTextureMode} eyeDepthMode={_eye.depthTextureMode} " +
                $"eyePostProcessLayer={eyeHasPostProcess} " +
                $"eyeHBAO={(eyeHbao == null ? "none" : eyeHbao.isActiveAndEnabled ? "active" : "inactive")} " +
                $"ambientOwner={(_ambientOwner != null ? _ambientOwner.name : "<none>")} " +
                $"mainBeforeLighting=[{decals}] " +
                "| the aimed weapon is not drawn into the gameplay camera's buffers listed above; its ambient occlusion " +
                "comes from the eye camera's own HBAO and Cinematic AO.");
        }

        private static void EnsureMainHook(Camera camera)
        {
            if (_hook != null && _hook.gameObject == camera.gameObject)
                return;
            _hook = camera.GetComponent<MainCameraHook>() ?? camera.gameObject.AddComponent<MainCameraHook>();
        }
    }
}
