using BepInEx.Configuration;

namespace Aero.BinocularADS
{
    internal static class DefocusConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> FocusOffsetM;
        public static ConfigEntry<float> ClearBandM;
        public static ConfigEntry<float> FalloffM;
        public static ConfigEntry<float> MaxRadiusPx;
        public static ConfigEntry<bool> BlurSecondImageFully;
        public static ConfigEntry<bool> IronSightFocus;
        public static ConfigEntry<float> IronSightSharpRadiusMm;
        public static ConfigEntry<float> IronSightFadeDistanceMm;
        public static ConfigEntry<float> IronSightBlurCurve;
        public static ConfigEntry<bool> ClearPlaneIronSightsOnly;
        public static ConfigEntry<float> OutlineSoftness;

        // Set the minimum blur fraction for the active sight. With an optic, the player focuses on the reticle, which EFT draws sharp after the composite, so a floor of 1 blurs the whole weapon. With irons, use a floor of 0 so the depth ramp can leave the front post sharp.
        public static float UniformFloorForCurrentSight()
        {
            if (ClearPlaneIronSightsOnly == null || !ClearPlaneIronSightsOnly.Value)
                return 0f;

            BinocularController.SightClass sight = BinocularController.CurrentSight;
            bool hasClearPlane =
                sight == BinocularController.SightClass.IronSight ||
                sight == BinocularController.SightClass.BuiltinScope;
            return hasClearPlane ? 0f : 1f;
        }

        public static void EnsureBound()
        {
            if (Enabled != null || Plugin.Instance == null)
                return;

            var config = Plugin.Instance.Config;
            Enabled = config.Bind(
                "Defocus",
                "Enabled",
                true,
                "Blurs the gun (never the world or the scope picture), the way a gun near your face is out of " +
                "focus while you look past it.");

            FocusOffsetM = config.Bind(
                "Defocus",
                "Focus Offset (m)",
                0.05f,
                new ConfigDescription(
                    "Moves the sharp band nearer or farther.",
                    new AcceptableValueRange<float>(-1f, 1f)));

            ClearBandM = config.Bind(
                "Defocus",
                "Clear Depth Band (m)",
                0.10f,
                new ConfigDescription(
                    "How deep the sharp band is.",
                    new AcceptableValueRange<float>(0f, 0.5f)));

            FalloffM = config.Bind(
                "Defocus",
                "Falloff Distance (m)",
                0.10f,
                new ConfigDescription(
                    "How quickly the gun blurs out beyond the sharp band.",
                    new AcceptableValueRange<float>(0.05f, 1.5f)));

            ClearPlaneIronSightsOnly = config.Bind(
                "Defocus",
                "Clear Plane On Iron Sights Only",
                true,
                "On: with scopes and red dots the whole gun is blurred, because your eye is on the reticle. " +
                "Off: scopes and red dots also get a sharp band on the gun. Note: Focus Offset, Clear Depth Band " +
                "and Falloff Distance never affect iron sights while Iron Sight Focus is on; they work with optics " +
                "when this is off, and with iron sights when Iron Sight Focus is off.");

            OutlineSoftness = config.Bind(
                "Defocus",
                "Outline Softness",
                1f,
                new ConfigDescription(
                    "Whether the gun's outline blurs too (1) or stays crisp around a blurred inside (0).",
                    new AcceptableValueRange<float>(0f, 1f)));

            BlurSecondImageFully = config.Bind(
                "Defocus",
                "Blur The Second Image Fully",
                true,
                "Blurs the whole ghost image evenly; your non-aiming eye never focuses on the gun. Note: with " +
                "iron-sight focus active, the ghost image is always fully blurred whatever this says, so it only " +
                "matters with scopes/red dots or with iron-sight focus off.");

            IronSightFocus = config.Bind(
                "Defocus",
                "Iron Sight Focus",
                true,
                "With iron sights, only the front sight's tip is sharp and the rest of the gun is blurred. Note: " +
                "needs the game's front-sight marker for that weapon; if it can't be found, the whole gun is " +
                "blurred evenly instead. Iron Sight Sharp Radius, Fade Distance and Blur Curve only work while " +
                "the front sight is found.");

            IronSightSharpRadiusMm = config.Bind(
                "Defocus",
                "Iron Sight Sharp Radius (mm)",
                3f,
                new ConfigDescription(
                    "Size of the fully sharp spot around the front sight's tip.",
                    new AcceptableValueRange<float>(0f, 30f)));

            IronSightFadeDistanceMm = config.Bind(
                "Defocus",
                "Iron Sight Fade Distance (mm)",
                3f,
                new ConfigDescription(
                    "How far beyond the sharp spot the gun takes to reach full blur.",
                    new AcceptableValueRange<float>(0.5f, 50f)));

            IronSightBlurCurve = config.Bind(
                "Defocus",
                "Iron Sight Blur Curve",
                1f,
                new ConfigDescription(
                    "Shape of that fade: 1 is even; above 1 stays sharp longer then drops; below 1 blurs fast " +
                    "then levels off.",
                    new AcceptableValueRange<float>(0.25f, 4f)));

            // Radius in full-resolution pixels. The separable Gaussian runs at half resolution. Beyond roughly 25–30 pixels, the taps stop overlapping and the blur starts to look like offset copies.
            MaxRadiusPx = config.Bind(
                "Defocus",
                "Maximum Radius (pixels)",
                15f,
                new ConfigDescription(
                    "How strong the blur is. Around 15 makes markings unreadable while the gun's shape stays " +
                    "clear; past about 25-30 it starts looking like copies instead of blur.",
                    new AcceptableValueRange<float>(0f, 30f)));
        }
    }
}
