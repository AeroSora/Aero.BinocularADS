using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Copy Shaders/ to Assets/Shaders in the Unity project and place this script under Assets/Editor. Run Aero > Build Shader Bundle, then copy Unity's Build/aero_binocular.bundle to bundles/aero_binocular.bundle in this repository.
public static class AeroBundleBuilder
{
    private const string ShaderFolder = "Assets/Shaders";
    private const string BundleName = "aero_binocular.bundle";

    [MenuItem("Aero/Build Shader Bundle")]
    public static void Build()
    {
        string[] shaders = Directory.GetFiles(ShaderFolder, "*.shader")
            .Select(path => path.Replace('\\', '/'))
            .OrderBy(path => path)
            .ToArray();
        if (shaders.Length == 0)
            throw new FileNotFoundException("No shaders found in " + ShaderFolder);

        string output = Path.Combine(Directory.GetCurrentDirectory(), "Build");
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild { assetBundleName = BundleName, assetNames = shaders };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
            throw new IOException("BuildAssetBundles failed; see the Console for shader errors.");

        Debug.Log($"Built {Path.Combine(output, BundleName)} from {shaders.Length} shaders.");
    }
}
