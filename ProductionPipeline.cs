using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace Aero.BinocularADS
{
    // PPv2's BuildCommandBuffers prefix renders the lit dominant-eye weapon and derives the second image from it. It then hides the ordinary main-scene viewmodel, with a restore path if the frame fails. The injected SSAA callback records the premultiplied composite after upscale and before EFT draws the native optic. Keep renderer state changes in the prefix and restore paths.
    internal static partial class ProductionPipeline
    {
        // ProductionPipeline.Developer.cs implements the debug views, timing bypass, censuses and depth-order controls. In release builds the compiler drops the calls, leaving each ref argument at its caller's production default.
        static partial void DeveloperMeasurementBypass(ref bool bypass);
        static partial void DeveloperShowPreLensCopy(ref bool show);
        static partial void DeveloperCompositeMaterial(ref Material composite);
        static partial void DeveloperAimBegin();
        static partial void DeveloperAimSettled(bool ironSightFocus);
        static partial void DeveloperAimEnd();
        static partial void DeveloperCostWindow();
        static partial void DeveloperDispose();
        static partial void DeveloperEyeCulling(Camera main, ref int mask, float[] cullDistances);
        static partial void DeveloperPreLensCopied(Renderer lens);
        static partial void DeveloperReleaseTargets();
        static partial void DeveloperFaultAllocation(ref bool forceReallocate);
        static partial void DeveloperFaultAllocated();
        static partial void DeveloperFaultCallback();
        static partial void DeveloperBeforeEyeRender();
        static partial void DeveloperAfterEyeRender();
        static partial void DeveloperAfterAoRestore();
        static partial void DeveloperBeforeHighlightFill(AmbientHighlight owner);
        static partial void DeveloperAfterHighlightFill(AmbientHighlight owner);

        private enum SlotClass
        {
            Opaque,
            Cutout,
            Transparent,
        }

        private enum EntryKind
        {
            Ordinary,
            Mixed,
            Ammunition,
        }

        private sealed class CoverageDraw
        {
            public Entry Owner;
            public Renderer Renderer;
            public Renderer PropertySource;
            public int Submesh;
            public Material SourceMaterial;
            public Material CoverageMaterial;
            public bool Cutout;
            public int TextureId;
            public int CutoffId;
            public int TintId;
            public int StId;
        }

        private sealed class Entry
        {
            public Renderer Source;
            public Material[] SourceMaterials;
            public SlotClass[] Slots;
            public EntryKind Kind;
            public bool SecondaryExcluded;
            public bool SessionOriginalForceOff;
            public ShadowCastingMode SessionOriginalShadow;
            public bool FrameForceOff;
            public ShadowCastingMode FrameShadow;
            public bool FrameVisible;
            // Use FrameVisible and the native shadow-only flag to tell whether a surface draws colour. Include those silhouettes in coverage and allow their glass clones in the main pass.
            public bool FrameCoverage;
            public GameObject OpaqueObject;
            public Renderer OpaqueClone;
            public GameObject GlassObject;
            public Renderer GlassClone;
            public readonly List<Material> OwnedNoDraw = new List<Material>();
        }

        private struct EyePose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Matrix4x4 View;
        }

        private struct ForceSnapshot
        {
            public Renderer Renderer;
            public bool ForceOff;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly List<CoverageDraw> CoverageDraws = new List<CoverageDraw>();
        private static readonly List<Renderer> Scan = new List<Renderer>();
        private static readonly List<ForceSnapshot> TemporaryForce = new List<ForceSnapshot>(16);
        private static readonly HashSet<Renderer> SightPicture = new HashSet<Renderer>();
        private static readonly Stopwatch EyeTimer = new Stopwatch();
        private static readonly MaterialPropertyBlock CoverageBlock = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock CloneBlock = new MaterialPropertyBlock();
        private static readonly FrameTiming[] LatestTiming = new FrameTiming[1];

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        private static readonly int AlphaCutoffId = Shader.PropertyToID("_AlphaCutoff");
        private static readonly int CutoffValueId = Shader.PropertyToID("_CutoffValue");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int CullId = Shader.PropertyToID("_Cull");
        private static readonly int CullModeId = Shader.PropertyToID("_CullMode");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int CoverageDepthId = Shader.PropertyToID("_CoverageDepth");
        private static readonly int WeaponAId = Shader.PropertyToID("_WeaponA");
        private static readonly int WeaponBId = Shader.PropertyToID("_WeaponB");
        private static readonly int BlurAId = Shader.PropertyToID("_BlurA");
        private static readonly int BlurBId = Shader.PropertyToID("_BlurB");
        private static readonly int CoverageAId = Shader.PropertyToID("_CoverageA");
        private static readonly int CoverageBId = Shader.PropertyToID("_CoverageB");
        private static readonly int OverlapOpacityId = Shader.PropertyToID("_OverlapOpacity");
        private static readonly int SingleOpacityId = Shader.PropertyToID("_SingleOpacity");
        private static readonly int ShareAId = Shader.PropertyToID("_ShareA");
        private static readonly int ShareBId = Shader.PropertyToID("_ShareB");
        private static readonly int FadeTId = Shader.PropertyToID("_FadeT");
        private static readonly int TakeoverId = Shader.PropertyToID("_Takeover");
        private static readonly int BlurStepId = Shader.PropertyToID("_AeroBlurStep");
        private static readonly int SourcePremultipliedId =
            Shader.PropertyToID("_SourceIsPremultiplied");
        private static readonly int BlurScratch0Id = Shader.PropertyToID("_AeroBlurScratch0");
        private static readonly int BlurScratch1Id = Shader.PropertyToID("_AeroBlurScratch1");
        // Each blur pass uses SetGlobalTexture to read the previous temporary target. Bind it under a private sampler name so EFT's later shaders cannot pick up a stale _MainTex.
        private static readonly int BlurSourceId = Shader.PropertyToID("_AeroBlurSource");
        private static readonly int AeroFlipYId = Shader.PropertyToID("_AeroFlipY");

        // Use the same flip rule as PPv2's ApplyDefaultFlip. A raw clip-space quad drawn into a top-down render texture needs its Y negated. Share this constant across those passes so orientation does not depend on a sampler's _TexelSize sign.
        private static float FlipY => SystemInfo.graphicsUVStartsAtTop ? 1f : 0f;
        private static readonly int DefocusEnabledId = Shader.PropertyToID("_DefocusEnabled");
        private static readonly int FocusDepthId = Shader.PropertyToID("_FocusDepth");
        private static readonly int ClearBandId = Shader.PropertyToID("_DefocusClearBand");
        private static readonly int FalloffId = Shader.PropertyToID("_DefocusFalloff");
        private static readonly int ForceDefocusBId = Shader.PropertyToID("_ForceDefocusB");
        private static readonly int DefocusFloorId = Shader.PropertyToID("_DefocusFloor");
        private static readonly int IronSightAnchorId = Shader.PropertyToID("_IronSightAnchor");
        private static readonly int IronSightRadiiId = Shader.PropertyToID("_IronSightRadii");
        private static readonly int ViewFromNdcId = Shader.PropertyToID("_ViewFromNdc");
        private static readonly int PartIdId = Shader.PropertyToID("_PartId");
        // Look up renderer names by coverage part ID in the iron-sight focus log. Index with ID minus one.
        private static readonly List<string> PartNames = new List<string>();
        private static bool _loggedSpot;
        private static bool _spotLatched, _spotEverActive, _unanchoredIronLatched;

        // Coverage tests raw depth with ZTest LEqual. On reversed-Z devices such as D3D11, the GPU projection would let the farther surface win in either draw order. A suppressor could then overwrite the depth of both iron sights inside its silhouette. Flip the depth row with z' = w - z so nearer surfaces win again. The stored coverage depth is linear view depth and is unaffected.
        private static Matrix4x4 CoverageGpuProjection(Matrix4x4 projection)
        {
            Matrix4x4 gpu = GL.GetGPUProjectionMatrix(projection, false);
            if (SystemInfo.usesReversedZBuffer)
                for (int col = 0; col < 4; col++)
                    gpu[2, col] = gpu[3, col] - gpu[2, col];
            return gpu;
        }

        // Both removable and built-in irons use iron-sight focus.
        private static bool IsIronSightClass(BinocularController.SightClass sight) =>
            sight == BinocularController.SightClass.IronSight || sight == BinocularController.SightClass.BuiltinScope;
        private static float _spotAimStart;
        private static readonly int OutlineSoftnessId = Shader.PropertyToID("_OutlineSoftness");

        private static readonly FieldInfo SsaaLensField = AccessTools.Field(typeof(SSAAPropagator), "_opticLensRenderer");
        private static readonly FieldInfo SsaaCollimatorField = AccessTools.Field(typeof(SSAAPropagator), "_collimatorRenderer");
        private static readonly FieldInfo SsaaTargetsField = AccessTools.Field(typeof(SSAAPropagator), "_resampledColorTargetHDR");
        private static readonly FieldInfo SsaaCommandField = AccessTools.Field(typeof(SSAAPropagator), "_cmdBuf");
        private static readonly FieldInfo SsaaOwnerField = AccessTools.Field(typeof(SSAAPropagator), "m_ssaa");
        private static readonly FieldInfo PpCameraField = AccessTools.Field(typeof(PostProcessLayer), "m_Camera");
        private static readonly FieldInfo AmbientCameraOwnersField =
            AccessTools.Field(typeof(AmbientLight), "_cameras"); // Cameras registered with AmbientLight.

        private static ProceduralWeaponAnimation _pwa;
        private static Transform _weaponRoot;
        private static int _weaponRootId;
        private static int _sightSignature;
        private static int _sightKey;
        private static bool _requested;
        private static bool _registryReady;

        // Cap the number of rebuilds from mid-aim material changes so a broken weapon eventually falls back to the native view.
        private const int MaxSourceArrayRebuilds = 4;
        private static int _sourceArrayRebuilds;
        private static bool _sessionFailed;
        private static bool _permanentlyIncompatible;
        private static string _permanentReason;

        private static bool _buildHookPatched;
        private static bool _fillHookPatched;
        private static int _buildSerial;
        private static int _activeBuildSerial;
        private static int _compositeRecordedSerial;
        private static int _captureFrame = -1;
        private static int _suppressedFrame = -1;
        private static int _restoreFrame = -1;
        private static bool _suppressed;
        private static bool _capturing;

        private static Camera _main;
        private static Camera _eye;
        private static CommandBuffer _coverageCommand;
        private static CommandBuffer _deriveCommand;
        private static Material _reproject;

        private static readonly int AeroProjTermsId = Shader.PropertyToID("_AeroProjTerms");
        private static readonly int AeroBToAId = Shader.PropertyToID("_AeroBToA");
        private static readonly int AeroFillParamsId = Shader.PropertyToID("_AeroFillParams");

        // Below this eye-A coverage, treat the point as hidden from eye A and recover a sample instead of using its colour.
        private const float ReprojectCoverageThreshold = 0.05f;

        // Search this far left and right in eye-A UVs to recover hidden pixels. A 4% span in either direction covers disocclusion on a weapon at arm's length without visibly smearing the gather. Keep the tap count fixed so the shader can unroll the loop. Texture fetches in a uniform-bounded loop have no gradients.
        private const float ReprojectFillSpan = 0.04f;

        // Maximum difference in metres between the observed eye-A depth and the projected depth of the surface we're looking for. A tight threshold sends valid grazing-angle samples, such as a barrel seen edge-on, through slower and blurrier recovery. A loose one can accept a nearer occluder. At viewmodel distances, 15 mm covers the depth slope across a texel while separating parts a few centimetres apart, such as a sight and receiver. Keep this a geometry constant because tuning errors look like subtle shading defects.
        private const float ReprojectDepthTolerance = 0.015f;
        private static Material _premultiply;
        private static Material _composite;
        private static Mesh _quad;
        private static MainCameraHook _hook;
        private static SSAAPropagator _ssaa;
        private static AmbientLight _ambientOwner;
        private const string HighlightBufferName = "Aero Ambient Highlight";
        private static CommandBuffer _highlightCommand;
        private static AmbientHighlight _highlightOwner;
        private static AmbientHighlight _highlightOwnerCache;
        private static int _highlightSearchedWorld;
        private static int _prewarmWorld, _prewarmAttempts;
        private static float _prewarmNextAt = -1f;
        private static bool _prewarmDone;

        // Schedule the scene-wide lookups five seconds after GameWorld appears so their cost lands during raid startup. The old Resources.FindObjectsOfTypeAll lookup measured 136 ms for AmbientLight. Plugin.Update drives the prewarm each frame.
        internal static void PrewarmLookups()
        {
            // Use Object.FindObjectsOfType because both searches only need active, enabled scene objects. AmbientLight registers the camera once the raid renders, so wait for the local player and retry every two seconds, up to five times.
            EFT.GameWorld gameWorld = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            int world = gameWorld?.GetInstanceID() ?? 0;
            if (world != _prewarmWorld)
            {
                _prewarmWorld = world;
                _prewarmAttempts = 0;
                _prewarmNextAt = -1f;
                _prewarmDone = false;
            }
            if (world == 0 || _prewarmDone || gameWorld.MainPlayer == null)
                return;
            if (_prewarmNextAt < 0f)
                _prewarmNextAt = Time.unscaledTime + 3f;
            Camera main = EFT.CameraControl.CameraManager.Instance?.Camera;
            // Wait for AmbientLight to attach its buffer before doing the scene search. This check saves a 60–130 ms search before AmbientLight is ready.
            if (main == null || Time.unscaledTime < _prewarmNextAt ||
                !HasCommandBuffer(main, CameraEvent.AfterLighting, "Custom Ambient"))
                return;
            _prewarmAttempts++;
            _prewarmNextAt = Time.unscaledTime + 2f;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ambient = FindAmbientOwner(main) != null;
            bool highlight = FindHighlightOwner() != null;
            _prewarmDone = ambient || _prewarmAttempts >= 5;
            Plugin.Diagnostic($"[BinoADS] owner lookups prewarm attempt {_prewarmAttempts} in {SinceMs(start):0.0} ms | ambientOwner={ambient} highlightOwner={highlight}");
        }
        private static Renderer _ssaaLens;
        private static Renderer _ssaaCollimator;

        private static RenderTexture _eyeColorA;
        private static RenderTexture _coverageA;
        private static RenderTexture _coverageB;
        private static RenderTexture _weaponA;
        private static RenderTexture _weaponB;

        // Keep the final blurred, unpremultiplied eye colour in persistent textures. The eye writes it at AfterEverything, and the main camera reads it later in SSAAPropagator. Unity releases GetTemporaryRT allocations when the owning camera finishes. Only the ping-pong scratch can be temporary. Resize and release the final textures with _weaponA and _weaponB.
        private static RenderTexture _blurA;
        private static RenderTexture _blurB;
        private static Material[] _blurPasses;
        private static int _targetWidth;
        private static int _targetHeight;

        // Apply a spherical cull radius in metres to all eye layers. Even a long rifle and both hands fit within about 1.5 m. This gives roughly three times that room and bounds layer 8, which the weapon shares with every character in the raid.
        private const float EyeCullRadiusMeters = 5f;

        private static int _blurWidth;
        private static int _blurHeight;
        private static bool _loggedStaleCollimator;

        // Camera space looks down -Z and a Transform points along +Z. Include the Z flip when deriving worldToCameraMatrix from the transform.
        private static readonly Matrix4x4 ViewFlipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

        private static double _eyeTotalMs;
        private static double _eyePeakMs;
        private static int _eyePerfFrames;

        // Restart the ten-second timing window when the config key changes. Otherwise a window could average samples from the old setting and label them with the new one. Dropping the partial window keeps each reported average tied to the configuration and sample count that produced it.
        private static string _costKey;

        private static int _eyeRenderCount;
        private static float _nextPerfLog;
        private static float _nextLifecycleLog;
        private static string _lastDefocusLog;
        private static int _coverageRefreshFrame = -1;
        private static bool _loggedPipeline;
        private static bool _loggedLighting;

        // Cache the resolved owner before registration. The culling gate needs its layer before ManualOnRenderObject runs and sets _ambientOwner.
        private static AmbientLight _ambientOwnerResolved;

        private static bool _loggedAppearanceDelta;

        public static bool SessionRequested => _requested;
        public static int WeaponRootId => _weaponRootId;
        // The first completed eye capture marks the session's setup work as done.
        public static bool CapturedThisSession => _captureFrame >= 0;
        public static int SightSignature => _sightSignature;
        public static bool IsCapturing => _capturing;

        public static void MarkBuildHookPatched()
        {
            _buildHookPatched = true;
        }

        public static void MarkFillHookPatched()
        {
            _fillHookPatched = true;
        }

        public static void MarkPatchIncompatible(string reason)
        {
            SetPermanentFailure(reason);
        }

        public static void AfterPatchInstallation()
        {
            bool fields = SsaaLensField != null && SsaaCollimatorField != null &&
                          SsaaTargetsField != null && SsaaCommandField != null &&
                          PpCameraField != null && AmbientCameraOwnersField != null;
            bool shaders = Supported(ShaderAssets.ProductionComposite) &&
                           Supported(ShaderAssets.NoDraw) &&
                           Supported(ShaderAssets.CoverageOpaque) &&
                           Supported(ShaderAssets.CoverageCutout) &&
                           Supported(ShaderAssets.Premultiply) &&
                           Supported(ShaderAssets.DefocusBlur) &&
                           Supported(ShaderAssets.Reproject);

            if (!_buildHookPatched || !_fillHookPatched || !fields || !shaders)
            {
                SetPermanentFailure(
                    $"startup preflight failed | buildHook={_buildHookPatched} fillHook={_fillHookPatched} " +
                    $"fields={fields} shaders={shaders}");
                return;
            }

            Plugin.Diagnostic(
                "[BinoADS] production preflight PASS | PPv2 BuildCommandBuffers owner and exact " +
                "post-upscale/pre-native-optic insertion installed");
        }

        private static bool Supported(Shader shader)
        {
            return shader != null && shader.isSupported;
        }

        // Track aim even when the render pipeline is bypassed. The frame census needs to count those native control frames as aimed, or the timing comparison would treat them as hipfire.
        public static bool AimActive { get; private set; }

        public static void Begin(ProceduralWeaponAnimation pwa)
        {
            if (_requested || pwa == null)
                return;

            AimActive = true;


            // Developer timing control. Keep native ADS and skip eye renders, suppression and compositing. Always false in release builds.
            bool bypass = false;
            DeveloperMeasurementBypass(ref bypass);
            if (bypass)
            {
                Plugin.Log.LogInfo("[BinoADS] measurement bypass active | aiming natively, pipeline stood down");
                return;
            }

            // Reset the rebuild budget for each aim so four material changes spread across a raid cannot disable the effect.
            _sourceArrayRebuilds = 0;
            _loggedFocusAnchor = false;
            _loggedAppearanceDelta = false;

            Transform root = pwa.HandsContainer?.Weapon;
            if (root == null)
            {
                Plugin.Log.LogWarning("[BinoADS] production begin rejected | weapon root is null");
                return;
            }

            _pwa = pwa;
            _weaponRoot = root;
            _weaponRootId = root.GetInstanceID();
            _sightSignature = BinocularController.SightSignature(pwa);
            _requested = true;
            _sessionFailed = false;
            _registryReady = false;
            _sightKey = 0;
            DeveloperAimBegin();

            if (_permanentlyIncompatible)
            {
                _sessionFailed = true;
                Plugin.Log.LogWarning(
                    $"[BinoADS] production unavailable; native viewmodel retained | {_permanentReason}");
            }
        }

        public static void End(string reason = null)
        {
            // Clear the aimed flag before the early return. Measurement bypass sets the flag without creating any of the render state below.
            AimActive = false;
            // Restore the sun here as a fallback. Capture normally restores it in finally, but a missed restore would dim the player's entire view.
            EndSunScope();
            // Release the light command buffer, camera callbacks and readbacks before the early return so the shadow sampler stays out of native timing windows.
            SunShadowSampler.Detach("session end");
            _anchorFrameValid = false;
            _sightLine = "not computed";
            _loggedSpot = false;
            _loggedLens = false;
            _loggedLensDraw = false;
            _spotAimStart = 0f;
            _spotLatched = false;
            _spotEverActive = false;
            _unanchoredIronLatched = false;
            _weaponLengthM = -1f;
            _maskPrimed = false;

            if (!_requested && !_registryReady && !_suppressed)
                return;

            DeveloperAimEnd();
            RestoreSuppression("session end");
            RestoreAmmunition();
            DestroyRegistry();
            _requested = false;
            _sessionFailed = false;
            _pwa = null;
            _weaponRoot = null;
            _weaponRootId = 0;
            _sightSignature = 0;
            _sightKey = 0;
            _captureFrame = -1;
            _suppressedFrame = -1;

            if (Plugin.Debug != null && Plugin.Debug.Value)
                Plugin.Log.LogInfo(
                    "[BinoADS] production END" + (string.IsNullOrEmpty(reason) ? "" : $" | reason={reason}"));
        }

        public static void Watchdog()
        {
            if (_suppressed && Time.frameCount != _suppressedFrame)
                RestoreSuppression("frame-advance watchdog");
        }

        // Keep the worst CPU time and frame number for the prefix and a single eye render since the last aim-out. Use them to trace an aim-entry hitch.
        internal static double AimPrefixMaxMs, AimEyeRenderMaxMs;
        internal static int AimPrefixMaxFrame, AimEyeRenderMaxFrame;

        public static int BuildPrefix(PostProcessLayer layer)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            System.Array.Clear(_stageMarks, 0, _stageMarks.Length);
            _stageMarks[0] = start;
            try { return BuildPrefixCore(layer); }
            finally
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms > AimPrefixMaxMs) { AimPrefixMaxMs = ms; AimPrefixMaxFrame = Time.frameCount; }
                if (ms > 20.0)
                    LogStageBreakdown(ms);
            }
        }

        // Timestamps at stage boundaries of BuildPrefixCore (0 = entry); a stage not reached stays 0.
        private static readonly long[] _stageMarks = new long[7];
        private static readonly string[] StageNames = { "setup+registry+eyes", "camera+ambient", "frame snapshot", "pre-capture+sun", "capture+derive", "after capture" };

        private static void Mark(int index) => _stageMarks[index] = System.Diagnostics.Stopwatch.GetTimestamp();

        private static void LogStageBreakdown(double totalMs)
        {
            var parts = new System.Text.StringBuilder();
            long previous = _stageMarks[0];
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 1; i <= StageNames.Length; i++)
            {
                long mark = i < StageNames.Length ? _stageMarks[i] : end;
                if (mark == 0) { parts.Append(' ').Append(StageNames[i - 1]).Append("=not reached"); continue; }
                parts.Append(' ').Append(StageNames[i - 1]).Append('=').Append(((mark - previous) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0"));
                previous = mark;
            }
            Plugin.Diagnostic($"[BinoADS] slow prefix frame {Time.frameCount}: {totalMs:0.0} ms |{parts} | camera+ambient detail: prime={_subPrimeMs:0.0} configure={_subConfigureMs:0.0} ambient-ManualOnRenderObject={_subManualRenderMs:0.0} recreate-eye-camera={_subRecreateMs:0.0} highlight={_subHighlightMs:0.0}");
        }

        private static double _subPrimeMs, _subConfigureMs, _subManualRenderMs, _subRecreateMs, _subHighlightMs;

        private static double SinceMs(long start) => (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static int BuildPrefixCore(PostProcessLayer layer)
        {
            MarkBuildHookPatched();
            if (_capturing || _permanentlyIncompatible)
                return 0;

            Camera camera = PpCameraField?.GetValue(layer) as Camera;
            Camera gameplay = EFT.CameraControl.CameraManager.Instance?.Camera;
            if (camera == null || gameplay == null || camera != gameplay)
                return 0;

            int serial = ++_buildSerial;
            _activeBuildSerial = serial;
            _compositeRecordedSerial = 0;

            if (!_requested || _sessionFailed || !Plugin.Enabled.Value)
                return serial;

            // PPv2 can rebuild the buffer for another stereo eye. Reuse this frame's images and leave the existing suppression in place to avoid repeating the manual render.
            if (_captureFrame == Time.frameCount && _registryReady &&
                _weaponA != null && _weaponB != null &&
                _coverageA != null && _coverageB != null)
            {
                SSAAPropagator liveSsaa = layer.GetComponent<SSAAPropagator>();
                Renderer liveLens = liveSsaa == null
                    ? null
                    : SsaaLensField.GetValue(liveSsaa) as Renderer;
                Renderer liveCollimator = liveSsaa == null
                    ? null
                    : SsaaCollimatorField.GetValue(liveSsaa) as Renderer;
                int liveKey = (liveLens == null ? 0 : liveLens.GetInstanceID()) * 397 ^
                              (liveCollimator == null ? 0 : liveCollimator.GetInstanceID());
                if (liveSsaa == _ssaa && liveKey == _sightKey &&
                    ValidateSightOwnership(liveLens, liveCollimator))
                {
                    if (!_suppressed)
                    {
                        SnapshotFrameState();
                        ApplyMainSuppression();
                        _suppressed = true;
                        _suppressedFrame = Time.frameCount;
                    }
                    return serial;
                }
            }

            if (_suppressed)
                RestoreSuppression("new PPv2 build");

            try
            {
                _main = camera;
                _ssaa = layer.GetComponent<SSAAPropagator>();
                if (_ssaa == null)
                {
                    SetPermanentFailure("gameplay PostProcessLayer has no SSAAPropagator");
                    return serial;
                }

                Renderer lens = SsaaLensField.GetValue(_ssaa) as Renderer;
                Renderer collimator = SsaaCollimatorField.GetValue(_ssaa) as Renderer;
                if (!ValidateSightOwnership(lens, collimator))
                {
                    FailSession("live SSAA sight ownership disagreed with the active EFT sight");
                    return serial;
                }

                int sightKey = (lens == null ? 0 : lens.GetInstanceID()) * 397 ^
                               (collimator == null ? 0 : collimator.GetInstanceID());
                if (!_registryReady || sightKey != _sightKey)
                {
                    DestroyRegistry();
                    _ssaaLens = lens;
                    _ssaaCollimator = collimator;
                    _sightKey = sightKey;
                    if (!BuildRegistry())
                        return serial;
                }

                RefreshAmmunition();
                if (!ValidateSourceArrays())
                {
                    // Rebuild in the same frame when firing or a camo, sticker or material mod changes the weapon's material array. Otherwise an ordinary change such as an ADS reload can drop the second image for the rest of the aim. Limit retries so a genuinely broken weapon falls back to native ADS.
                    if (_sourceArrayRebuilds < MaxSourceArrayRebuilds)
                    {
                        _sourceArrayRebuilds++;
                        if (Plugin.Debug.Value)
                            Plugin.Log.LogInfo(
                                "[BinoADS] source material array changed - rebuilding registry " +
                                $"({_sourceArrayRebuilds}/{MaxSourceArrayRebuilds})");

                        DestroyRegistry();
                        if (!BuildRegistry() || !ValidateSourceArrays())
                        {
                            FailSession("registry rebuild after material change did not validate");
                            return serial;
                        }
                    }
                    else
                    {
                        FailSession("source material array keeps changing after registry rebuilds");
                        return serial;
                    }
                }

                EyePose a;
                EyePose b;
                ResolveEyes(camera, out a, out b);
                Mark(1);

                EyeTargetSize(camera, out int targetWidth, out int targetHeight);
                var resourcesWatch = System.Diagnostics.Stopwatch.StartNew();
                bool resourcesReady = EnsureRenderResources(camera, targetWidth, targetHeight);
                AimResourcesMs += resourcesWatch.Elapsed.TotalMilliseconds;
                if (!resourcesReady)
                    return serial;
                // Resolve AmbientLight before ConfigureEyeCamera so its layer is in the culling mask. Doing it later loses ambient lighting on the first aimed frame. LateUpdate fixes the next frame, but the missed lighting is visible and the one-shot verification log would describe the wrong state. See PrimeAmbientOwner.
                long subStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PrimeAmbientOwner(camera);
                _subPrimeMs = SinceMs(subStart); subStart = System.Diagnostics.Stopwatch.GetTimestamp();
                ConfigureEyeCamera(camera, a, _eyeColorA);
                _subConfigureMs = SinceMs(subStart);
                _subManualRenderMs = 0; _subRecreateMs = 0; _subHighlightMs = 0;
                if (!EnsureAmbientOwnership(camera))
                    return serial;
                EyeAmbientOcclusion.Sync(camera, _eye);

                Mark(2);
                SnapshotFrameState();
                Mark(3);

                // Exclude this from the timer. See UpdateSunOcclusion.
                UpdateSunOcclusion(camera);
                Mark(4);

                EyeTimer.Restart();
                bool pair;
                try
                {
                    // Time the sun override with the eye render because it changes the render's cost.
                    BeginSunScope();
                    pair = CaptureEye(camera, a);
                    if (DefocusConfig.IronSightFocus.Value && DefocusConfig.UniformFloorForCurrentSight() < 0.5f)
                        UpdateIronSightAnchor(a);

                    // Draw eye B's silhouette with its own view-projection matrix, then gather colour from eye A using depth. Only eye A needs a camera render.
                    if (pair)
                        pair = DeriveSecondEyeReprojected(a, b);
                }
                finally
                {
                    EndSunScope();
                    EyeTimer.Stop();
                    Mark(5);
                }

                if (!pair)
                {
                    RestoreCaptureMutations();
                    FailSession("eye pair did not complete");
                    return serial;
                }

                if (_captureFrame < 0 && Plugin.Debug.Value)
                    LogChurn();
                _captureFrame = Time.frameCount;
                ApplyMainSuppression();
                _suppressed = true;
                _suppressedFrame = Time.frameCount;

                double elapsedMs = EyeTimer.Elapsed.TotalMilliseconds;

                // Reset the timing window before adding the first sample under the new config.
                string costKey = CurrentCostKey();
                if (_costKey != costKey)
                {
                    ResetCostAccumulators();
                    _costKey = costKey;
                }

                _eyeTotalMs += elapsedMs;
                _eyePeakMs = Math.Max(_eyePeakMs, elapsedMs);
                _eyePerfFrames++;
                LogFrameOwnership(camera, serial);
            }
            catch (Exception e)
            {
                RestoreCaptureMutations();
                RestoreSuppression("capture exception");
                FailSession($"capture exception: {e.GetType().Name}: {e.Message}");
            }

            return serial;
        }

        public static void BuildPostfix(int serial)
        {
            if (serial == 0 || !_suppressed || _activeBuildSerial != serial)
                return;

            if (_compositeRecordedSerial != serial)
            {
                RestoreSuppression("composite callback missing");
                SetPermanentFailure(
                    $"post-upscale callback was not recorded for PPv2 build {serial}; restored before cull");
            }
        }

        // If the injected callback throws, restore the native viewmodel before culling and disable the effect for the rest of the game session. Catch the exception here so EFT can finish its own post-processing build.
        public static void InjectedCallbackFailed(string callback, Exception exception)
        {
            _lensCopySerial = 0;
            RestoreSuppression(callback + " exception");
            SetPermanentFailure($"{callback} callback threw {exception.GetType().Name}: {exception.Message}");
            Plugin.Log.LogError($"[BinoADS] {callback} callback exception: {exception}");
        }

        public static Exception BuildFinalizer(Exception exception, int serial)
        {
            if (serial != 0 && exception != null)
            {
                RestoreSuppression("PPv2 finalizer");
                SetPermanentFailure(
                    $"PostProcessLayer.BuildCommandBuffers threw {exception.GetType().Name}: {exception.Message}");
            }
            return exception;
        }

        public static void Append(SSAAPropagator instance, CommandBuffer externalCommandBuffer)
        {
            if (_permanentlyIncompatible ||
                !_suppressed || instance == null || instance != _ssaa)
                return;

            int serial = _activeBuildSerial;
            if (serial == 0 || _compositeRecordedSerial == serial)
                return;

            if (_captureFrame != Time.frameCount || _suppressedFrame != Time.frameCount ||
                _weaponA == null || _weaponB == null || _coverageA == null || _coverageB == null)
            {
                RestoreSuppression("record-time frame mismatch");
                return;
            }

            CommandBuffer command = externalCommandBuffer ?? SsaaCommandField.GetValue(instance) as CommandBuffer;
            RenderTexture[] targets = SsaaTargetsField.GetValue(instance) as RenderTexture[];
            RenderTexture output = targets != null && targets.Length > 0 ? targets[0] : null;
            if (command == null || output == null || _composite == null || _quad == null)
            {
                RestoreSuppression("record-time target missing");
                SetPermanentFailure("post-upscale command buffer or HDR output target was unavailable");
                return;
            }

            ConfigureCompositeMaterial();

            // Copy the scene while the ordinary weapon is suppressed, before the weapon composite. A later copy would include the scope housing and darken the lens blend.
            RecordPreLensCopy(instance, command, output, serial);

            command.BeginSample("Aero Binocular PostUpscale Composite");
            command.SetRenderTarget(output);
            command.SetViewport(new Rect(0f, 0f, output.width, output.height));
            try
            {
                DeveloperFaultCallback();
                // The development view can draw a single term of the composite here for inspection.
                Material composite = _composite;
                DeveloperCompositeMaterial(ref composite);
                command.DrawMesh(_quad, Matrix4x4.identity, composite, 0, 0);
            }
            catch
            {
                // Balance EFT's command buffer before the injected wrapper disables the effect.
                command.EndSample("Aero Binocular PostUpscale Composite");
                throw;
            }
            command.SetRenderTarget(output);
            command.SetViewport(new Rect(0f, 0f, output.width, output.height));
            command.EndSample("Aero Binocular PostUpscale Composite");

            _compositeRecordedSerial = serial;
            if (Plugin.Debug.Value && Time.unscaledTime >= _nextLifecycleLog)
            {
                _nextLifecycleLog = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo(
                    $"[BinoADS] lifecycle | build={serial} captureFrame={_captureFrame} " +
                    $"suppressedFrame={_suppressedFrame} postUpscaleRecordFrame={Time.frameCount} " +
                    $"lastRestoreFrame={_restoreFrame}");
            }
        }

        private static RenderTexture _preLens;
        private static Material _lensBlendMaterial;
        private static Renderer _lensCopyLens;
        private static int _lensCopySerial;
        private static bool _loggedLens;
        private static bool _loggedLensDraw;
        private static readonly int PreLensId = Shader.PropertyToID("_AeroPreLens");
        private static readonly int LensAlphaId = Shader.PropertyToID("_AeroLensAlpha");

        // Save the scene behind the weapon for Magnified Sight Picture Opacity. Copy EFT's HDR target before the weapon composite so AfterOpticLens can blend it over the magnified picture.
        private static void RecordPreLensCopy(SSAAPropagator instance, CommandBuffer command, RenderTexture output, int serial)
        {
            _lensCopySerial = 0;
            Renderer lens = SsaaLensField.GetValue(instance) as Renderer;
            float opacity = Mathf.Clamp(BindonAiming.EffectiveOpacity, 10f, 100f);
            bool showCopy = false;
            DeveloperShowPreLensCopy(ref showCopy);
            if (lens == null || (!showCopy && opacity >= 99.95f))
                return;
            if (ShaderAssets.LensBlend == null || !ShaderAssets.LensBlend.isSupported)
            {
                if (!_loggedLens)
                {
                    _loggedLens = true;
                    Plugin.Log.LogWarning("[BinoADS] magnified sight opacity unavailable: lens blend shader missing from the bundle");
                }
                return;
            }

            if (_preLens == null || _preLens.width != output.width || _preLens.height != output.height ||
                _preLens.format != output.format)
            {
                Release(ref _preLens);
                _preLens = new RenderTexture(output.width, output.height, 0, output.format)
                {
                    name = "Aero Pre-Lens Frame",
                    filterMode = FilterMode.Point,
                };
                _preLens.Create();
            }
            if (_lensBlendMaterial == null)
                _lensBlendMaterial = new Material(ShaderAssets.LensBlend) { hideFlags = HideFlags.HideAndDontSave };

            if (SystemInfo.copyTextureSupport != CopyTextureSupport.None && output.antiAliasing <= 1)
                command.CopyTexture(output, _preLens);
            else
                command.Blit(output, _preLens);
            _lensBlendMaterial.SetTexture(PreLensId, _preLens);
            _lensBlendMaterial.SetFloat(LensAlphaId, showCopy ? 1f : 1f - opacity / 100f);
            _lensCopyLens = lens;
            _lensCopySerial = serial;
            DeveloperPreLensCopied(lens);

            if (!_loggedLens)
            {
                _loggedLens = true;
                int layer = lens.gameObject.layer;
                bool inMain = _main != null && (_main.cullingMask & (1 << layer)) != 0;
                Plugin.Diagnostic(
                    $"[BinoADS] magnified sight opacity | opacity={opacity:0}% showPreLensCopy={showCopy} lens='{lens.name}' " +
                    $"layer={layer} inMainCullingMask={inMain} enabled={lens.enabled} active={lens.gameObject.activeInHierarchy} " +
                    $"forceRenderingOff={lens.forceRenderingOff} mainPassLensHidden={_lensSuppressed == lens} " +
                    $"target={output.width}x{output.height} {output.format} " +
                    $"copy={(SystemInfo.copyTextureSupport != CopyTextureSupport.None && output.antiAliasing <= 1 ? "CopyTexture" : "Blit")} " +
                    $"lensScreenRect={LensScreenRect(lens)}");
            }
        }

        // Project the lens renderer's world bounds into a pixel rect with Y measured from the top, like a screenshot. Record the camera dimensions too. This gives opacity tests a lens region independent of the images they're evaluating.
        private static string LensScreenRect(Renderer lens)
        {
            if (_main == null)
                return "unavailable(no camera)";
            Bounds b = lens.bounds;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 s = _main.WorldToScreenPoint(corner);
                if (s.z <= 0f)
                    return "unavailable(behind camera)";
                x0 = Mathf.Min(x0, s.x); x1 = Mathf.Max(x1, s.x);
                y0 = Mathf.Min(y0, s.y); y1 = Mathf.Max(y1, s.y);
            }
            int h = _main.pixelHeight;
            return $"{x0:0},{h - y1:0},{x1:0},{h - y0:0} camera={_main.pixelWidth}x{h}";
        }

        // The injected SSAAPropagator.FillCommandBuffer callback runs right after DrawRenderer(_opticLensRenderer, _opticLensMaterial), while EFT's HDR and depth targets are still bound. Redraw the lens with the blend material so the pre-lens copy shows through only the visible lens.
        public static void AfterOpticLens(SSAAPropagator instance, CommandBuffer externalCommandBuffer)
        {
            string skip =
                _lensCopySerial == 0 ? null :
                _lensCopySerial != _activeBuildSerial ? $"copy serial {_lensCopySerial} != build {_activeBuildSerial}" :
                instance == null || instance != _ssaa ? "different SSAAPropagator" :
                _lensBlendMaterial == null ? "no blend material" : "";
            if (skip == null)
                return;
            _lensCopySerial = 0;
            CommandBuffer command = externalCommandBuffer ?? SsaaCommandField.GetValue(instance) as CommandBuffer;
            Renderer lens = SsaaLensField.GetValue(instance) as Renderer;
            if (skip.Length == 0 && (command == null || lens == null || lens != _lensCopyLens))
                skip = command == null ? "no command buffer" : lens == null ? "no lens" : "lens changed since the copy";
            if (skip.Length > 0)
            {
                if (!_loggedLensDraw)
                {
                    _loggedLensDraw = true;
                    Plugin.Diagnostic($"[BinoADS] magnified sight opacity: lens redraw skipped ({skip})");
                }
                return;
            }
            command.BeginSample("Aero Magnified Sight Opacity");
            try
            {
                command.DrawRenderer(lens, _lensBlendMaterial, 0, 0);
            }
            finally
            {
                command.EndSample("Aero Magnified Sight Opacity");
            }
            if (!_loggedLensDraw)
            {
                _loggedLensDraw = true;
                Plugin.Diagnostic(
                    $"[BinoADS] magnified sight opacity: lens redraw recorded | preLensShare={_lensBlendMaterial.GetFloat(LensAlphaId):0.00} " +
                    $"lens='{lens.name}' shadowMode={lens.shadowCastingMode} subMeshes={(lens.GetComponent<MeshFilter>()?.sharedMesh?.subMeshCount ?? -1)} " +
                    $"materials={lens.sharedMaterials.Length} external={(externalCommandBuffer != null)}");
            }
        }

        public static void OnPostRender(Camera camera)
        {
            if (camera != null && camera == _main)
                RestoreSuppression("OnPostRender");
        }

        public static void RestoreSuppression(string owner)
        {
            if (!_suppressed)
                return;

            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Source != null)
                {
                    entry.Source.forceRenderingOff = entry.Kind == EntryKind.Ammunition
                        ? true
                        : entry.FrameForceOff;
                    entry.Source.shadowCastingMode = entry.FrameShadow;
                }

                DisableClone(entry.OpaqueClone);
                DisableClone(entry.GlassClone);
            }

            if (_lensSuppressed != null)
            {
                _lensSuppressed.shadowCastingMode = _lensFrameShadow;
                _lensSuppressed = null;
            }
            for (int g = 0; g < _glassSuppressed.Count; g++)
                if (_glassSuppressed[g] != null)
                    _glassSuppressed[g].forceRenderingOff = _glassFrameOff[g];
            _glassSuppressed.Clear();
            _glassFrameOff.Clear();

            _suppressed = false;
            _restoreFrame = Time.frameCount;

            if (Plugin.Debug != null && Plugin.Debug.Value &&
                owner != "OnPostRender")
            {
                Plugin.Log.LogWarning(
                    $"[BinoADS] suppression restored by {owner} | frame={Time.frameCount}");
            }
        }

        private static void SetPermanentFailure(string reason)
        {
            if (_permanentlyIncompatible)
                return;
            _permanentlyIncompatible = true;
            _permanentReason = reason;
            RestoreSuppression("permanent failure");
            _sessionFailed = true;
            Plugin.Log.LogError(
                $"[BinoADS] PRODUCTION INCOMPATIBLE; native viewmodel retained | {reason}");
        }

        private static void FailSession(string reason)
        {
            if (_sessionFailed)
                return;
            RestoreSuppression("session failure");
            RestoreAmmunition();
            _sessionFailed = true;
            Plugin.Log.LogError(
                $"[BinoADS] ADS SESSION FAILED CLOSED; native viewmodel retained | {reason}");
        }

        private static bool _loggedInactiveManagerLens;

        private static bool ValidateSightOwnership(Renderer lens, Renderer collimator)
        {
            var currentOptic = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
            Renderer currentLens = currentOptic == null ? null : currentOptic.LensRenderer;

            // SPT 4.1 can retain the previous lens after switching to irons. We saw ssaaLens=null and opticManagerLens='linza_mode_000' active=False, which rejected every iron-sight session. Treat disabled lenses as absent and still reject disagreements about active lenses.
            if (currentLens != null && !(currentLens.enabled && currentLens.gameObject.activeInHierarchy))
            {
                if (!_loggedInactiveManagerLens)
                {
                    _loggedInactiveManagerLens = true;
                    Plugin.Diagnostic($"[BinoADS] optic manager lens {Describe(currentLens)} is inactive; treated as no lens");
                }
                currentLens = null;
            }

            // Treat a disabled SSAA lens as absent too. Both sides can retain the same old lens.
            if (lens != null && !(lens.enabled && lens.gameObject.activeInHierarchy))
            {
                lens = null;
            }

            if (currentLens != lens)
            {
                // Unity's overloaded equality treats destroyed objects as null. Two absent references agree.
                if (currentLens != null || lens != null)
                {
                    Plugin.Log.LogError(
                        $"[BinoADS] sight ownership mismatch | ssaaLens={Describe(lens)} " +
                        $"opticManagerLens={Describe(currentLens)}");
                    return false;
                }
            }

            Renderer currentCollimator = EFT.CameraControl.CameraManager.Instance?.SSAA?.GetCollimatorRenderer();
            if (currentCollimator != collimator)
            {
                // Use EFT.CameraControl.CameraManager.SSAA as the active collimator source. CameraManager forwards the renderer with an object-ID guard. SSAAPropagator can retain an old field after switching to irons, such as propagatorCollimator='linza' with cameraSsaaCollimator=<null>. Trust the camera-side result so the stale field doesn't reject iron sights.
                if (currentCollimator == null)
                {
                    if (!_loggedStaleCollimator)
                    {
                        _loggedStaleCollimator = true;
                        Plugin.Diagnostic(
                            "[BinoADS] stale propagator collimator ignored | " +
                            $"propagatorCollimator={Describe(collimator)} cameraSsaaCollimator=<null> | " +
                            "CameraManager.SSAA is authoritative; the active sight is treated as having no collimator");
                    }
                }
                else
                {
                    Plugin.Log.LogError(
                        $"[BinoADS] sight ownership mismatch | propagatorCollimator={Describe(collimator)} " +
                        $"cameraSsaaCollimator={Describe(currentCollimator)}");
                    return false;
                }
            }

            return true;
        }

        private static string Describe(Renderer renderer)
        {
            return renderer == null ? "<null>" :
                $"'{renderer.name}'#{renderer.GetInstanceID()} active={renderer.gameObject.activeSelf}";
        }

        // Time registry construction and render-resource checks for this aim.
        internal static double AimBuildMs, AimResourcesMs;
        internal static int AimBuilds, AimReallocsAtStart;
    }
}
