using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Aero.BinocularADS
{
    // Loads the mod's shaders from its asset bundle.
    public static class ShaderAssets
    {
        public const string ProductionCompositeName = "Aero/BinocularProductionComposite";
        public const string NoDrawName = "Aero/NoDraw";
        public const string CoverageOpaqueName = "Aero/CoverageDepthOpaque";
        public const string CoverageCutoutName = "Aero/CoverageDepthCutout";
        public const string PremultiplyName = "Aero/WeaponPremultiply";
        public const string DefocusBlurName = "Aero/WeaponDefocusBlur";
        public const string ReprojectName = "Aero/WeaponReproject";
        public const string LensBlendName = "Aero/LensBlend";
        private const string BundleFile = "aero_binocular.bundle";

        private static bool _tried;

        public static Shader ProductionComposite { get; private set; }
        public static Shader NoDraw { get; private set; }
        public static Shader CoverageOpaque { get; private set; }
        public static Shader CoverageCutout { get; private set; }
        public static Shader Premultiply { get; private set; }

        // Separable weapon defocus. Run the Gaussian pass four times per eye.
        public static Shader DefocusBlur { get; private set; }

        // Fill eye B's exact silhouette by gathering colour from eye A with depth checks. This takes the place of premultiply for the derived eye.
        public static Shader Reproject { get; private set; }

        // Blend the saved pre-lens scene over EFT's magnified sight picture.
        public static Shader LensBlend { get; private set; }

        public static void Load()
        {
            if (_tried)
                return;
            _tried = true;

            try
            {
                string dir = Path.GetDirectoryName(typeof(ShaderAssets).Assembly.Location) ?? "";
                string[] candidates =
                {
                    Path.Combine(dir, "bundles", BundleFile),
                    Path.Combine(dir, BundleFile),
                };

                string path = candidates.FirstOrDefault(File.Exists);
                if (path == null)
                {
                    Plugin.Log.LogWarning(
                        $"[BinoADS] shader bundle not found ({BundleFile}) - production will retain the native viewmodel.");
                    return;
                }

                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Plugin.Log.LogError($"[BinoADS] failed to load shader bundle: {path}");
                    return;
                }

                var shaders = bundle.LoadAllAssets<Shader>();
                ProductionComposite = shaders?.FirstOrDefault(
                    s => s != null && s.name == ProductionCompositeName);
                NoDraw = shaders?.FirstOrDefault(s => s != null && s.name == NoDrawName);
                CoverageOpaque = shaders?.FirstOrDefault(s => s != null && s.name == CoverageOpaqueName);
                CoverageCutout = shaders?.FirstOrDefault(s => s != null && s.name == CoverageCutoutName);
                Premultiply = shaders?.FirstOrDefault(s => s != null && s.name == PremultiplyName);
                DefocusBlur = shaders?.FirstOrDefault(s => s != null && s.name == DefocusBlurName);
                Reproject = shaders?.FirstOrDefault(s => s != null && s.name == ReprojectName);
                LensBlend = shaders?.FirstOrDefault(s => s != null && s.name == LensBlendName);

                Plugin.Diagnostic(
                    $"[BinoADS] shader bundle | " +
                    $"production={Describe(ProductionComposite)} " +
                    $"noDraw={Describe(NoDraw)} coverageOpaque={Describe(CoverageOpaque)} " +
                    $"coverageCutout={Describe(CoverageCutout)} premultiply={Describe(Premultiply)} " +
                    $"defocusBlur={Describe(DefocusBlur)} reproject={Describe(Reproject)} lensBlend={Describe(LensBlend)} " +
                    $"bundle={IdentifyBundle(path)}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[BinoADS] exception loading shader bundle: {e}");
            }
        }

        private static string Describe(Shader s)
        {
            return s == null ? "MISSING" : (s.isSupported ? "OK" : "UNSUPPORTED");
        }

        // Log the size and hash of the loaded bundle so the log identifies the shaders. The DLL and bundle come from separate toolchains and can get out of sync, and the bundle has no version field.
        private static string IdentifyBundle(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(bytes);
                    var text = new System.Text.StringBuilder(16);
                    for (int i = 0; i < 8; i++)
                    {
                        text.Append(hash[i].ToString("x2"));
                    }

                    return $"{bytes.Length}B/sha256:{text}";
                }
            }
            catch (Exception e)
            {
                return $"unreadable({e.GetType().Name})";
            }
        }
    }
}
