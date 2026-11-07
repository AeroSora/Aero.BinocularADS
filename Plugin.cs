using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;

namespace Aero.BinocularADS
{
    // Turn weapon AO off or choose its render resolution.
    public enum WeaponShadingQuality
    {
        Off,
        Quarter,
        Half,
        Full,
    }

    // Auto matches the aimed weapon's cubemap strength while the game's SSR draws. AlwaysOff skips the adjustment.
    public enum SsrReflectionMatch
    {
        Auto,
        AlwaysOff,
    }

    // The gameplay camera stays at the physical eye the player aims with. This setting chooses that eye.
    public enum Eye
    {
        Right,
        Left,
    }

    [BepInPlugin(GUID, NAME, VERSION)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string GUID = "aero.binocularads";
        public const string NAME = "Aero.BinocularADS";
        // Keep this in sync with <Version> in the project file. tools/release_check.py checks it, and BepInEx reports it at startup so the log identifies the loaded build.
        public const string VERSION = "0.1.0";

        public static Plugin Instance;
        public static ManualLogSource Log;

        // Enable Diagnostic Logging adds routine detail. Startup messages, actionable errors and compatibility warnings are always logged.
        internal static void Diagnostic(string message)
        {
            if (Debug != null && Debug.Value)
                Log.LogInfo(message);
        }

        // --- General ---
        public static ConfigEntry<bool> Enabled;

        // --- Binocular Offset ---
        public static ConfigEntry<float> EyeSeparationCm;
        public static ConfigEntry<Eye> DominantEye;
        public static ConfigEntry<float> ConvergenceDistanceM;

        // --- Appearance ---
        public static ConfigEntry<float> OverlapOpacity;
        public static ConfigEntry<float> SingleEyeOpacity;
        public static ConfigEntry<float> MagnifiedSightOpacity;
        public static ConfigEntry<float> DominantEyeShare;
        public static ConfigEntry<float> FadeTimeSeconds;
        public static ConfigEntry<bool> TransparentArms;
        public static ConfigEntry<bool> DoubleArms;

        // --- Bindon Aiming ---
        public static ConfigEntry<bool> BindonEnabled;
        public static ConfigEntry<float> BindonMovingOpacity;
        public static ConfigEntry<float> BindonLookDeadZone;
        public static ConfigEntry<float> BindonLookFullSpeed;
        public static ConfigEntry<float> BindonDeadZoneRelease;
        public static ConfigEntry<float> BindonLookFadeSeconds;
        public static ConfigEntry<float> BindonWalkFadeSeconds;
        public static ConfigEntry<float> BindonRestoreSeconds;
        public static ConfigEntry<float> BindonWalkingSpeed;

        // --- Rendering ---
        public static ConfigEntry<WeaponShadingQuality> WeaponAmbientOcclusion;
        public static ConfigEntry<bool> SunOcclusionEnabled;
        public static ConfigEntry<SsrReflectionMatch> SsrReflections;
        public static ConfigEntry<float> SunOcclusionFadeSeconds;
        public static ConfigEntry<float> EyeBufferScale;

        // --- Debug ---
        public static ConfigEntry<bool> Debug;

        // Plugin.Developer.cs implements the settings and frame probes in development builds. The compiler drops both calls from release builds.
        partial void BindDeveloperSettings();
        partial void DeveloperUpdate();

        private bool _installed;

        private void Update()
        {
            if (!_installed)
                return;

            // Prewarm the AmbientLight lookups during raid startup so they're ready for the first aim, regardless of the effect or logging settings.
            ProductionPipeline.PrewarmLookups();
            // Update opacity before rendering so every pass reads the same value this frame.
            BindonAiming.Tick(Time.deltaTime);
            if (Debug.Value)
                ProductionPipeline.TrackAimEntry(Time.unscaledDeltaTime);
            DeveloperUpdate();
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Config.Bind(
                "Contract", "Schema Version", 2,
                new ConfigDescription(
                    "Internal settings version used for upgrades. Not shown to players.",
                    null,
                    new ConfigurationManagerAttributes { Browsable = false }));

            Enabled = Config.Bind(
                "General", "Enabled", true,
                "Turns the whole binocular aiming effect on or off.");

            EyeSeparationCm = Config.Bind(
                "Binocular Offset", "Eye Separation (cm)", 5f,
                new ConfigDescription(
                    "How far apart your eyes are. Bigger pushes the two gun images further apart; 0 merges them into one.",
                    new AcceptableValueRange<float>(0f, 12f)));

            DominantEye = Config.Bind(
                "Binocular Offset", "Dominant Eye", Eye.Right,
                "The eye you aim with. Your sight picture stays lined up with it; the ghost gun image belongs to the other eye.");

            ConvergenceDistanceM = Config.Bind(
                "Binocular Offset", "Convergence Distance (m)", 1f,
                new ConfigDescription(
                    "How far away your eyes are looking. Close values swing the ghost image at an angle around that point; far values just slide it sideways.",
                    new AcceptableValueRange<float>(0.2f, 100f)));

            MagnifiedSightOpacity = Config.Bind(
                "Appearance", "Magnified Sight Picture Opacity (%)", 60f,
                new ConfigDescription(
                    "How solid the zoomed picture inside a scope is. Below 100 you see the world around the scope through it, like aiming with both eyes open.",
                    new AcceptableValueRange<float>(10f, 100f)));

            OverlapOpacity = Config.Bind(
                "Appearance", "Overlap Opacity (%)", 100f,
                new ConfigDescription(
                    "How solid the gun is where both eyes see it.",
                    new AcceptableValueRange<float>(50f, 100f)));

            SingleEyeOpacity = Config.Bind(
                "Appearance", "Single Eye Opacity (%)", 70f,
                new ConfigDescription(
                    "How solid the gun is where only one eye sees it: the see-through edges on each side.",
                    new AcceptableValueRange<float>(20f, 100f)));

            DominantEyeShare = Config.Bind(
                "Appearance", "Dominant Eye Share (%)", 60f,
                new ConfigDescription(
                    "Where the two images overlap, how much comes from your aiming eye, so you can tell which image is yours. Doesn't change how solid the overlap is.",
                    new AcceptableValueRange<float>(50f, 90f)));

            FadeTimeSeconds = Config.Bind(
                "Appearance", "Fade Time (s)", 0.2f,
                new ConfigDescription(
                    "How long the effect takes to fade in when you aim and out when you stop.",
                    new AcceptableValueRange<float>(0f, 0.6f)));

            TransparentArms = Config.Bind(
                "Appearance", "Transparent Arms", true,
                "Your hands become see-through along with the gun instead of staying solid.");

            DoubleArms = Config.Bind(
                "Appearance", "Double Arms", true,
                "The ghost image gets hands too.");

            BindonEnabled = Config.Bind(
                "Bindon Aiming", "Enabled", true,
                "Adds to Magnified Sight Picture Opacity: while you turn or walk, the zoomed picture becomes more " +
                "see-through so you can follow the world around the scope, and it returns to that opacity when your aim " +
                "is steady. Off keeps the picture at Magnified Sight Picture Opacity at all times.");

            BindonMovingOpacity = Config.Bind(
                "Bindon Aiming", "Moving Opacity (%)", 30f,
                new ConfigDescription(
                    "How solid the zoomed picture gets at most fade, while walking or turning fast. Never more solid than " +
                    "Magnified Sight Picture Opacity.",
                    new AcceptableValueRange<float>(10f, 100f)));

            BindonLookDeadZone = Config.Bind(
                "Bindon Aiming", "Turn Dead Zone (deg/s)", 40f,
                new ConfigDescription(
                    "Turning slower than this does not fade the picture, so following a moving target keeps it solid. " +
                    "Measured as the speed the view moves through the scope (turn speed times zoom), so it means the same on every scope.",
                    new AcceptableValueRange<float>(0f, 400f)));

            BindonLookFullSpeed = Config.Bind(
                "Bindon Aiming", "Turn Speed For Full Fade (deg/s)", 200f,
                new ConfigDescription(
                    "Turning at this speed through the scope (turn speed times zoom) fades the picture all the way to Moving Opacity; " +
                    "between the dead zone and this, the fade grows with speed.",
                    new AcceptableValueRange<float>(20f, 2000f)));

            BindonDeadZoneRelease = Config.Bind(
                "Bindon Aiming", "Dead Zone Release (%)", 70f,
                new ConfigDescription(
                    "Once turning has started a fade, it keeps fading until the speed falls below this share of the dead zone, " +
                    "so a turn hovering around the dead zone does not flicker.",
                    new AcceptableValueRange<float>(0f, 100f)));

            BindonLookFadeSeconds = Config.Bind(
                "Bindon Aiming", "Turn Fade Time (s)", 0.15f,
                new ConfigDescription(
                    "How long a turn takes to reach its fade. A faster turn fades deeper in the same time, so it fades faster.",
                    new AcceptableValueRange<float>(0.02f, 1f)));

            BindonWalkFadeSeconds = Config.Bind(
                "Bindon Aiming", "Walk Fade Time (s)", 1f,
                new ConfigDescription(
                    "How long walking takes to fade the picture to Moving Opacity.",
                    new AcceptableValueRange<float>(0.1f, 3f)));

            BindonRestoreSeconds = Config.Bind(
                "Bindon Aiming", "Restore Time (s)", 0.2f,
                new ConfigDescription(
                    "How quickly the picture returns to Magnified Sight Picture Opacity once your aim and feet are still.",
                    new AcceptableValueRange<float>(0.02f, 1f)));

            BindonWalkingSpeed = Config.Bind(
                "Bindon Aiming", "Walking Speed Threshold (m/s)", 0.3f,
                new ConfigDescription(
                    "Moving faster than this counts as walking.",
                    new AcceptableValueRange<float>(0.05f, 2f)));

            WeaponAmbientOcclusion = Config.Bind(
                "Rendering", "Weapon Ambient Occlusion", WeaponShadingQuality.Half,
                "Gives the aimed gun the game's crevice shading, so it doesn't look flatter and brighter than at the hip. " +
                "Off turns it off; Quarter, Half and Full set its quality. Half looks almost the same as Full for less cost.");

            SunOcclusionEnabled = Config.Bind(
                "Rendering", "Block Sunlight Indoors", true,
                "Stops the aimed gun being lit by the sun when you're indoors or in shadow.");

            SunOcclusionFadeSeconds = Config.Bind(
                "Rendering", "Sunlight Blend Time (s)", 0.15f,
                new ConfigDescription(
                    "How quickly the gun changes between sunlit and shaded when you walk through a doorway.",
                    new AcceptableValueRange<float>(0f, 1f)));

            SsrReflections = Config.Bind(
                "Rendering", "Match Screen Space Reflections", SsrReflectionMatch.Auto,
                "With the game's Screen Space Reflections on, stops the aimed gun shining brighter than at the hip. Auto does " +
                "this only while the game's SSR is on and no other mod has replaced it with its own reflections; Always Off never " +
                "does. Costs nothing noticeable.");

            EyeBufferScale = Config.Bind(
                "Rendering", "Eye Buffer Scale (%)", 100f,
                new ConfigDescription(
                    "Resolution of the gun images. Lower is cheaper and slightly softer.",
                    new AcceptableValueRange<float>(50f, 100f)));

            Debug = Config.Bind(
                "Debug", "Enable Diagnostic Logging", false,
                "Writes extra detail to the log for bug reports.");

            BindDeveloperSettings();

            // Bind defocus settings at startup so ConfigurationManager shows them before the first aim. Allocate the render targets on demand.
            DefocusConfig.EnsureBound();

            ShaderAssets.Load();

            // Roll back all patches if PatchAll fails. A missing target after a game update can leave earlier patches installed, and those patches need the rest of the mod to work.
            var harmony = new Harmony(GUID);
            try
            {
                harmony.PatchAll();
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Log.LogError($"[BinoADS] patch installation failed; every patch was removed and the effect stays off: {e}");
                return;
            }
            ProductionPipeline.AfterPatchInstallation();
            _installed = true;

            // Give the watchdog its own component so Unity drives its lifecycle.
            gameObject.AddComponent<StereoRenderCoordinator>();

            Log.LogInfo($"{NAME} v{VERSION} loaded.");
        }

        private void OnDestroy()
        {
            BinocularRenderer.End("plugin destroyed");
            ProductionPipeline.Dispose();
        }
    }

    // ConfigurationManager finds this type by name and reads its display hints by reflection. The plugin can build without referencing its assembly.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
    }
}
