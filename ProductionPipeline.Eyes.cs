using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Aero.BinocularADS
{
    internal static partial class ProductionPipeline
    {
        // The eye the last ResolveEyes used, so a switch (left shoulder, or a settings change) is logged once.
        private static Eye _lastDominantEye = Eye.Right;

        private static void ResolveEyes(Camera main, out EyePose a, out EyePose b)
        {
            Transform transform = main.transform;
            a = new EyePose
            {
                Position = transform.position,
                Rotation = transform.rotation,
                View = main.worldToCameraMatrix,
            };

            float separation = Plugin.EyeSeparationCm.Value / 100f;
            if (separation <= 0.000001f)
            {
                b = a;
                return;
            }

            Eye dominantEye = BinocularController.ActiveDominantEye;
            if (dominantEye != _lastDominantEye)
            {
                Plugin.Diagnostic($"[BinoADS] dominant eye {_lastDominantEye} -> {dominantEye} at frame {Time.frameCount} (configured {Plugin.DominantEye.Value})");
                _lastDominantEye = dominantEye;
            }
            float sign = dominantEye == Eye.Right ? 1f : -1f;
            Vector3 position = transform.position - sign * transform.right * separation;
            Vector3 convergence =
                transform.position + transform.forward * Mathf.Max(0.05f, Plugin.ConvergenceDistanceM.Value);
            Quaternion rotation = Quaternion.LookRotation(convergence - position, transform.up);

            // Convert the transform's +Z forward to Unity camera space's -Z. Without the Z flip, eye B gets negative clip W in front of the camera and clips the entire weapon. Eye A uses main.worldToCameraMatrix directly.
            b = new EyePose
            {
                Position = position,
                Rotation = rotation,
                View = ViewFlipZ * Matrix4x4.TRS(position, rotation, Vector3.one).inverse,
            };
        }

        // Size the eye targets to the full frame at Eye Buffer Scale. The size only changes with the screen or the setting, so aim entry reuses the existing targets.
        private static void EyeTargetSize(Camera main, out int width, out int height)
        {
            float scale = Mathf.Clamp(Plugin.EyeBufferScale.Value / 100f, 0.5f, 1f);
            width = Mathf.Max(1, Mathf.CeilToInt(main.pixelWidth * scale));
            height = Mathf.Max(1, Mathf.CeilToInt(main.pixelHeight * scale));
        }

        private static bool EnsureRenderResources(Camera main, int width, int height)
        {
            EnsureEyeCamera();
            if (_premultiply == null)
                _premultiply = NewMaterial(ShaderAssets.Premultiply, "Aero Weapon Premultiply");
            if (_reproject == null)
                _reproject = NewMaterial(ShaderAssets.Reproject, "Aero Weapon Reproject");
            if (_composite == null)
                _composite = NewMaterial(
                    ShaderAssets.ProductionComposite, "Aero Production Composite");
            if (_quad == null)
                _quad = CreateUnitQuad();
            if (_blurPasses == null)
            {
                // Give each blur pass its own material. Command buffers store a reference, so sharing one would make all four passes use the last _AeroBlurStep and _SourceIsPremultiplied values.
                var passes = new Material[4];
                for (int i = 0; i < passes.Length; i++)
                    passes[i] = NewMaterial(ShaderAssets.DefocusBlur, "Aero Defocus Blur " + i);
                if (passes[0] != null && passes[1] != null && passes[2] != null && passes[3] != null)
                    _blurPasses = passes;
            }
            if (_eye == null || _premultiply == null || _reproject == null || _composite == null ||
                _quad == null || _blurPasses == null)
            {
                SetPermanentFailure("production render resources could not be created");
                return false;
            }

            bool forceReallocate = false;
            DeveloperFaultAllocation(ref forceReallocate);
            if (!forceReallocate && _targetWidth == width && _targetHeight == height && TargetsComplete())
                return true;

            // Count resizes in the cost log because every dimension change recreates seven render textures.
            _targetReallocs++;
            Plugin.Diagnostic($"[BinoADS] eye targets reallocated at frame {Time.frameCount} | {_targetWidth}x{_targetHeight} -> {width}x{height}");

            ReleaseTargets();
            _targetWidth = width;
            _targetHeight = height;
            RenderTextureFormat color = main.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            // Only the dominant eye needs a colour target because eye B is derived.
            _eyeColorA = NewTarget(width, height, color, 24, FilterMode.Bilinear, "Aero Eye Color A");
            _coverageA = NewTarget(width, height, RenderTextureFormat.ARGBHalf, 24, FilterMode.Point, "Aero Coverage A");
            _coverageB = NewTarget(width, height, RenderTextureFormat.ARGBHalf, 24, FilterMode.Point, "Aero Coverage B");
            _weaponA = NewTarget(width, height, color, 0, FilterMode.Bilinear, "Aero Weapon A");
            _weaponB = NewTarget(width, height, color, 0, FilterMode.Bilinear, "Aero Weapon B");

            // Keep the blur at half resolution and vary the tap spacing. Downsampling further at larger radii visibly softened the weapon.
            _blurWidth = Mathf.Max(1, width / DefocusKernel.Downsample);
            _blurHeight = Mathf.Max(1, height / DefocusKernel.Downsample);
            _blurA = NewTarget(_blurWidth, _blurHeight, color, 0, FilterMode.Bilinear, "Aero Blur A");
            _blurB = NewTarget(_blurWidth, _blurHeight, color, 0, FilterMode.Bilinear, "Aero Blur B");

            DeveloperFaultAllocated();
            bool ok = TargetsComplete();
            if (!ok)
            {
                // Release the partial set and forget its size so the next aim allocates all the targets again.
                ReleaseTargets();
                FailSession($"RT allocation failed at {width}x{height}");
            }
            return ok;
        }

        // All seven per-session render targets exist and are created on the GPU.
        private static bool TargetsComplete()
        {
            return Created(_eyeColorA) && Created(_coverageA) && Created(_coverageB) &&
                   Created(_weaponA) && Created(_weaponB) && Created(_blurA) && Created(_blurB);
        }

        private static bool Created(RenderTexture texture) => texture != null && texture.IsCreated();

        private static Material NewMaterial(Shader shader, string name)
        {
            if (!Supported(shader))
                return null;
            return new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };
        }

        private static RenderTexture NewTarget(
            int width, int height, RenderTextureFormat format, int depth, FilterMode filter, string name)
        {
            var target = new RenderTexture(width, height, depth, format)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
            };
            target.Create();
            return target;
        }

        private static void EnsureEyeCamera()
        {
            if (_eye != null)
                return;
            var host = new GameObject("Aero Production Eye Camera")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Object.DontDestroyOnLoad(host);
            _eye = host.AddComponent<Camera>();
            _eye.enabled = false;
            _coverageCommand = new CommandBuffer { name = "Aero Weapon Coverage + Premultiply" };
            _eye.AddCommandBuffer(CameraEvent.AfterEverything, _coverageCommand);
            EyeProbeLight.Attach(_eye);
        }

        // Submit this buffer with Graphics.ExecuteCommandBuffer after the dominant eye renders. Eye B has no Camera.Render event, and sharing _coverageCommand would replay the derive on the next eye render.
        //
        // Create it separately from EnsureEyeCamera because that method returns early for an existing camera. Missing the one creation frame could leave the second image frozen without an error.
        private static void EnsureDeriveCommand()
        {
            if (_deriveCommand == null)
                _deriveCommand = new CommandBuffer { name = "Aero Derived Second Image" };
        }

        private static Mesh CreateUnitQuad()
        {
            var mesh = new Mesh { name = "Aero Unit Quad", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.UploadMeshData(true);
            return mesh;
        }

        private static bool EnsureAmbientOwnership(Camera main)
        {
            bool mainHasStencil = HasCommandBuffer(main, CameraEvent.BeforeLighting, "Stencil Shadow");
            bool mainHasAmbient = HasCommandBuffer(main, CameraEvent.AfterLighting, "Custom Ambient");
            bool needsAmbient = mainHasStencil || mainHasAmbient;
            if (!needsAmbient)
            {
                if (HasCommandBuffer(_eye, CameraEvent.BeforeLighting, "Stencil Shadow") ||
                    HasCommandBuffer(_eye, CameraEvent.AfterLighting, "Custom Ambient"))
                {
                    long recreateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    RecreateEyeCamera(main);
                    _subRecreateMs += SinceMs(recreateStart);
                    _ambientOwner = null;
                }
                return true;
            }

            AmbientLight owner = FindAmbientOwner(main);
            if (owner == null)
            {
                FailSession("main camera has EFT ambient buffers but the owning AmbientLight was not found");
                return false;
            }

            if (_ambientOwner != null && _ambientOwner != owner)
            {
                long recreateStart = System.Diagnostics.Stopwatch.GetTimestamp();
                RecreateEyeCamera(main);
                _subRecreateMs += SinceMs(recreateStart);
            }

            if (_ambientOwner != owner ||
                !HasCommandBuffer(_eye, CameraEvent.BeforeLighting, "Stencil Shadow") ||
                !HasCommandBuffer(_eye, CameraEvent.AfterLighting, "Custom Ambient"))
            {
                long manualStart = System.Diagnostics.Stopwatch.GetTimestamp();
                owner.ManualOnRenderObject(_eye);
                _subManualRenderMs += SinceMs(manualStart);
                _ambientOwner = owner;
            }

            bool eyeStencil = HasCommandBuffer(_eye, CameraEvent.BeforeLighting, "Stencil Shadow");
            bool eyeAmbient = HasCommandBuffer(_eye, CameraEvent.AfterLighting, "Custom Ambient");
            if (!eyeStencil || !eyeAmbient)
            {
                FailSession(
                    $"eye ambient registration incomplete | main={mainHasStencil}/{mainHasAmbient} " +
                    $"eye={eyeStencil}/{eyeAmbient}");
                return false;
            }

            long highlightStart = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureAmbientHighlight();
            _subHighlightMs = SinceMs(highlightStart);

            if (!_loggedLighting)
            {
                _loggedLighting = true;

                // AmbientLight checks its own layer against the camera mask before recording stencil shadow and custom ambient. Both named buffers can exist and be empty, so HasCommandBuffer alone cannot verify lighting. ConfigureEyeCamera includes the AmbientLight layer and logs the layer and gate result once per session.
                int ambientLayer = owner.gameObject.layer;
                int ambientBit = 1 << ambientLayer;
                bool eyeGate = (_eye.cullingMask | ambientBit) == _eye.cullingMask;
                bool mainGate = (main.cullingMask | ambientBit) == main.cullingMask;
                Plugin.Diagnostic(
                    $"[BinoADS] ambient gate | ambientOwner={owner.name} " +
                    $"layer={ambientLayer}:{LayerMask.LayerToName(ambientLayer)} bit=0x{ambientBit:X8} " +
                    $"eyePasses={eyeGate} mainPasses={mainGate} " +
                    $"note=buffers-exist-but-are-empty-when-eyePasses-is-False");

                Plugin.Diagnostic(
                    $"[BinoADS] lighting ownership | mainMask=0x{main.cullingMask:X8} " +
                    $"eyeMask=0x{_eye.cullingMask:X8} cullingMatrix=main-broad " +
                    $"mainBuffers={CommandInventory(main)} eyeBuffers={CommandInventory(_eye)} " +
                    $"highlightOwner={(_highlightOwner == null ? "MISSING" : _highlightOwner.name)} " +
                    "sourceLayers=original worldLeakage=structurally-zero-after-premultiply");
            }
            return true;
        }

        // Mirror EFT's Ambient Highlight pass so the weapon remains visible in shade. It adds a fullscreen quad through StencilType.Hands, supplying indirect light to the viewmodel. AmbientHighlight.ManualOnRenderObject only accepts the gameplay camera, but the public fill routine takes any camera.
        //
        // Use an eye-owned buffer and let EFT populate it. This keeps the private camera out of EFT's per-camera registry. Refill every aimed frame because the intensity curve follows sun elevation and would otherwise freeze at aim-in.
        private static void EnsureAmbientHighlight()
        {
            // The mod leaves the hands highlight out of the game's pass, so there is nothing for the weapon to mirror.
            AmbientHighlight owner = AmbientHighlightHands.Removed ? null : FindHighlightOwner();
            if (owner == null)
            {
                // Clear the attached buffer when the source is inactive so it stops replaying the last highlight.
                _highlightCommand?.Clear();
                _highlightOwner = null;
                return;
            }

            if (_highlightCommand == null)
                _highlightCommand = new CommandBuffer { name = HighlightBufferName };

            if (!HasCommandBuffer(_eye, CameraEvent.AfterLighting, HighlightBufferName))
                _eye.AddCommandBuffer(CameraEvent.AfterLighting, _highlightCommand);

            // Clear before the availability check. UpdateAmbientBuffer only clears after that guard, so skipping the call can leave last frame's commands attached.
            _highlightCommand.Clear();
            DeveloperBeforeHighlightFill(owner);
            try { owner.UpdateAmbientBuffer(_highlightCommand, _eye); }
            finally { DeveloperAfterHighlightFill(owner); }
            _highlightOwner = owner;
        }

        private static AmbientHighlight FindHighlightOwner()
        {
            if (_highlightOwnerCache != null && _highlightOwnerCache.isActiveAndEnabled)
                return _highlightOwnerCache;

            // Cache misses for the current GameWorld. A search on maps without AmbientHighlight takes tens of milliseconds and would hitch aim-in. Search again in the next raid. A source enabled later in the same raid is missed.
            int world = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.GetInstanceID() ?? 0;
            if (world == 0 || world == _highlightSearchedWorld)
                return null;

            _highlightSearchedWorld = world;
            _highlightOwnerCache = null;
            foreach (AmbientHighlight candidate in Object.FindObjectsOfType<AmbientHighlight>())
            {
                if (candidate == null || !candidate.isActiveAndEnabled)
                    continue;
                _highlightOwnerCache = candidate;
                break;
            }

            return _highlightOwnerCache;
        }

        private static AmbientLight _ambientOwnerCache;
        private static Camera _ambientOwnerCacheCamera;
        // Cache misses until the camera or scene changes. The hideout has no owner, and retrying the 45–80 ms scene search would cause repeated hitches.
        private static Camera _ambientMissCamera;
        private static int _ambientMissScene;
        private static int _ambientMisses;

        // Cache the main camera's AmbientLight owner and revalidate by reference. The scene search is too expensive per frame. Unity's null comparison also invalidates a destroyed owner on raid exit.
        private static AmbientLight FindAmbientOwner(Camera main)
        {
            if (_ambientOwnerCache != null && _ambientOwnerCacheCamera == main &&
                _ambientOwnerCache.isActiveAndEnabled)
                return _ambientOwnerCache;

            _ambientOwnerCache = null;
            _ambientOwnerCacheCamera = null;

            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (_ambientMissCamera == main && _ambientMissScene == scene)
                return null;

            foreach (AmbientLight candidate in Object.FindObjectsOfType<AmbientLight>())
            {
                if (candidate == null || !candidate.isActiveAndEnabled)
                    continue;
                IDictionary cameras = AmbientCameraOwnersField.GetValue(candidate) as IDictionary;
                if (cameras != null && cameras.Contains(main))
                {
                    _ambientOwnerCache = candidate;
                    _ambientOwnerCacheCamera = main;
                    _ambientMissCamera = null;
                    return candidate;
                }
            }
            _ambientMissCamera = main;
            _ambientMissScene = scene;
            if (_ambientMisses++ < 3)
                Plugin.Diagnostic($"[BinoADS] ambient owner: no AmbientLight owns camera {main?.name ?? "null"}; " +
                                   "ambient gate off until the camera or scene changes");
            return null;
        }

        private static void RecreateEyeCamera(Camera main)
        {
            if (_eye != null)
            {
                if (_coverageCommand != null)
                    _eye.RemoveCommandBuffer(CameraEvent.AfterEverything, _coverageCommand);
                if (_highlightCommand != null)
                    _eye.RemoveCommandBuffer(CameraEvent.AfterLighting, _highlightCommand);
                Object.Destroy(_eye.gameObject);
            }
            _coverageCommand?.Release();
            _coverageCommand = null;
            _deriveCommand?.Release();
            _deriveCommand = null;
            _highlightCommand?.Release();
            _highlightCommand = null;
            _highlightOwner = null;
            _highlightOwnerCache = null;
            _eye = null;
            EnsureEyeCamera();
            ResolveEyes(main, out EyePose dominant, out _);
            ConfigureEyeCamera(main, dominant, _eyeColorA);
        }

        // This runs four times per aimed frame. GetCommandBuffers allocates an array already, so skip the extra LINQ closure.
        private static bool HasCommandBuffer(Camera camera, CameraEvent evt, string name)
        {
            if (camera == null)
                return false;
            CommandBuffer[] buffers = camera.GetCommandBuffers(evt);
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null && string.Equals(buffers[i].name, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string CommandInventory(Camera camera)
        {
            string before = string.Join(",", camera.GetCommandBuffers(CameraEvent.BeforeGBuffer).Select(b => b.name));
            string lighting = string.Join(",", camera.GetCommandBuffers(CameraEvent.BeforeLighting).Select(b => b.name));
            string after = string.Join(",", camera.GetCommandBuffers(CameraEvent.AfterLighting).Select(b => b.name));
            return $"BeforeGBuffer=[{before}] BeforeLighting=[{lighting}] AfterLighting=[{after}]";
        }

        private static void SnapshotFrameState()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Source == null)
                    continue;
                entry.FrameForceOff = entry.Kind == EntryKind.Ammunition
                    ? entry.SessionOriginalForceOff
                    : entry.Source.forceRenderingOff;
                entry.FrameShadow = entry.Source.shadowCastingMode;
                entry.FrameVisible = entry.Source.enabled &&
                                     !entry.FrameForceOff &&
                                     entry.Source.gameObject.activeInHierarchy;
                // Read the restored FrameShadow outside suppression. A native ShadowsOnly renderer contributes no colour, so leave it out of coverage too.
                entry.FrameCoverage = entry.FrameVisible && entry.FrameShadow != ShadowCastingMode.ShadowsOnly;
                SyncCloneTransform(entry);
            }
        }

        private static void SyncCloneTransform(Entry entry)
        {
            int layer = entry.Source.gameObject.layer;
            if (entry.OpaqueObject != null)
                entry.OpaqueObject.layer = layer;
            if (entry.GlassObject != null)
                entry.GlassObject.layer = layer;
            if (entry.OpaqueClone != null)
            {
                CopyRendererState(entry.Source, entry.OpaqueClone);
                CopyPropertyBlocks(
                    entry.Source, entry.OpaqueClone, entry.SourceMaterials.Length);
                entry.OpaqueClone.enabled = false;
            }
            if (entry.GlassClone != null)
            {
                CopyRendererState(entry.Source, entry.GlassClone);
                CopyPropertyBlocks(
                    entry.Source, entry.GlassClone, entry.SourceMaterials.Length);
                entry.GlassClone.shadowCastingMode = ShadowCastingMode.Off;
                entry.GlassClone.enabled = false;
            }
        }

        // Use a tiny positive cull distance for the ambient gate layer. Zero means the far clip plane in Unity and would admit the entire layer.
        private const float AmbientGateCullRadiusMeters = 0.01f;

        // Layer of the EFT AmbientLight component, usually 0:Default. PrimeAmbientOwner runs before ConfigureEyeCamera, so -1 means the layer is still unknown after resolution.
        private static int AmbientGateLayer()
        {
            if (_ambientOwnerResolved == null) return -1;
            return _ambientOwnerResolved.gameObject.layer;
        }

        // Resolve AmbientLight before configuring the eye so the first aimed frame passes its layer check. AmbientLight.LateUpdate clears and records every registered camera buffer using the current mask, so later changes take effect on the next refresh.
        //
        // Getting the order wrong causes a one-frame ambient flash and permanently latches eyePasses=False in the one-shot _loggedLighting check, even though the camera passes from frame two.
        private static void PrimeAmbientOwner(Camera main)
        {
            if (_ambientOwnerResolved == null)
                _ambientOwnerResolved = FindAmbientOwner(main);
        }

        private static int AmbientGateBit()
        {
            int layer = AmbientGateLayer();
            return layer < 0 ? 0 : 1 << layer;
        }

        // Key the timing window on every setting that changes the eye workload. Clear it when the key changes so the samples stay comparable.
        private static string CurrentCostKey()
        {
            return Plugin.SunOcclusionEnabled.Value ? "sunOcclusion" : "noSunOcclusion";
        }

        // Reset all populations at the end of a ten-second window or when the config changes mid-window.
        private static void ResetCostAccumulators()
        {
            _eyeTotalMs = 0;
            _eyePeakMs = 0;
            _eyePerfFrames = 0;
            _targetReallocs = 0;
        }

        private static int _targetReallocs;
    }
}
