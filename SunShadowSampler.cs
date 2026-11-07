using UnityEngine;
using UnityEngine.Rendering;

namespace Aero.BinocularADS
{
    // Approximate sun visibility using the gameplay camera's shadow mask behind the weapon. The eye cull contains only the weapon, so walls and roofs never enter its shadow map. EFT also hides the gun from sun shadows via _HideGunShadow.
    //
    // SPT 4.1's ShadowMaskExtractor exposes the full mask at LightEvent.AfterScreenspaceMask. The optic camera can overwrite its _SnowShadowMask global, so use our own light command buffer at the same event. Camera.onPreCull fills it for the gameplay camera and clears it for other cameras.
    //
    // With the native weapon hidden, the sampled screen region contains the world behind it. Read back the mean red channel asynchronously, one frame late. Red is Unity's shadow attenuation, with 1 fully lit.
    internal static partial class SunShadowSampler
    {
        // SunShadowSampler.Developer.cs implements the mask dump. The compiler drops these calls from release builds.
        static partial void DeveloperBeforeRegionCopy(Camera cam);
        static partial void DeveloperAfterGameplayRender();
        static partial void DeveloperDetach();

        private const int Size = 16;

        private static Light _light;
        private static CommandBuffer _buffer;
        private static RenderTexture _target;
        // Centre half of the frame, where the aimed weapon sits.
        private static readonly Rect _region = new Rect(0.25f, 0.25f, 0.5f, 0.5f);
        private static bool _filledThisFrame;
        private static bool _pending;
        // Bump the generation on Detach so callbacks from earlier readbacks cannot publish a value after release.
        private static int _generation;

        internal static bool HasValue { get; private set; }
        internal static float Visibility { get; private set; } = 1f;

        // Keep the latest reading across aim-out so the next aim can use it while waiting for its first readback.
        internal static float LastVisibility { get; private set; } = 1f;
        internal static float LastValueTime { get; private set; } = float.NegativeInfinity;
        internal static Vector3 LastValuePosition { get; private set; }
        internal static int Readbacks { get; private set; }
        internal static int Failures { get; private set; }
        internal static int Fills { get; private set; }

        internal static void Attach(Light sun)
        {
            if (sun == null || (_light == sun && _buffer != null))
                return;
            Detach("re-attach");
            _light = sun;
            _buffer = new CommandBuffer { name = "Aero.BinocularADS sun shadow copy" };
            _target = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32)
            {
                name = "Aero.BinocularADS.SunShadowCopy",
                filterMode = FilterMode.Bilinear
            };
            _target.Create();
            _light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, _buffer);
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            Plugin.Diagnostic($"[BinoADS] sun shadow copy attached to '{sun.name}' at AfterScreenspaceMask");
        }

        internal static void Detach(string reason)
        {
            bool wasAttached = _buffer != null;
            _generation++;
            _pending = false;
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            if (_light != null && _buffer != null)
                _light.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, _buffer);
            _buffer?.Release();
            _buffer = null;
            if (_target != null)
            {
                _target.Release();
                UnityEngine.Object.Destroy(_target);
                _target = null;
            }
            DeveloperDetach();
            _light = null;
            _filledThisFrame = false;
            HasValue = false;
            Visibility = 1f;
            if (wasAttached)
                Plugin.Diagnostic($"[BinoADS] sun shadow copy detached ({reason}) | {Describe()}");
        }

        // Log every channel mean to check where EFT stores the attenuation. Visibility uses Unity's red channel.
        private static Vector4 _channelMeans;

        internal static string Describe() =>
            $"sunMask={(HasValue ? Visibility.ToString("0.00") : "none")} rgba=({_channelMeans.x:0.00},{_channelMeans.y:0.00},{_channelMeans.z:0.00},{_channelMeans.w:0.00}) " +
            $"region=({_region.x:0.00},{_region.y:0.00},{_region.width:0.00},{_region.height:0.00}) fills={Fills} readbacks={Readbacks} readbackFailures={Failures}";

        private static Camera Gameplay => EFT.CameraControl.CameraManager.Instance?.Camera;

        private static void OnPreCull(Camera cam)
        {
            if (_buffer == null)
                return;
            _buffer.Clear();
            // Only the gameplay camera writes this copy. Skip the optic and private eye cameras.
            if (cam == null || cam != Gameplay)
                return;
            // Blit samples source UV × scale + offset to copy the region. Queue the debug copy first because Blit makes its destination active. A later CurrentActive copy would read the 16×16 region.
            DeveloperBeforeRegionCopy(cam);
            _buffer.Blit(BuiltinRenderTextureType.CurrentActive, _target,
                new Vector2(_region.width, _region.height), new Vector2(_region.x, _region.y));
            _filledThisFrame = true;
        }

        private static void OnPostRender(Camera cam)
        {
            if (!_filledThisFrame || cam == null || cam != Gameplay)
                return;
            _filledThisFrame = false;
            Fills++;
            DeveloperAfterGameplayRender();
            if (_pending || _target == null)
                return;
            _pending = true;
            int generation = _generation;
            AsyncGPUReadback.Request(_target, 0, r => OnReadback(r, generation));
        }

        private static void OnReadback(AsyncGPUReadbackRequest request, int generation)
        {
            if (generation != _generation)
                return; // Detach may already have released this readback's texture.
            _pending = false;
            if (request.hasError)
            {
                Failures++;
                return;
            }
            var data = request.GetData<Color32>();
            if (data.Length == 0)
            {
                Failures++;
                return;
            }
            float sum = 0f;
            Vector4 channels = Vector4.zero;
            for (int i = 0; i < data.Length; i++)
            {
                sum += data[i].r;
                channels += new Vector4(data[i].r, data[i].g, data[i].b, data[i].a);
            }
            _channelMeans = channels / (255f * data.Length);
            Visibility = sum / (255f * data.Length);
            HasValue = true;
            Readbacks++;
            LastVisibility = Visibility;
            LastValueTime = Time.unscaledTime;
            Camera gameplay = Gameplay;
            if (gameplay != null)
                LastValuePosition = gameplay.transform.position;
        }
    }
}
