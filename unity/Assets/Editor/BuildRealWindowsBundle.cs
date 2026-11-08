// Builds the RealWindows shader bundle(s). Run from tools/build_shaders.py:
//
//   Unity.exe -batchmode -quit -projectPath <window-portals\unity>
//     -executeMethod BuildRealWindowsBundle.BuildFromCommandLine
//     -rwOut <dir> [-rwTargets windows,osx,linux] -logFile <log>
//
// or interactively via the menu RealWindows/Build Shader Bundles.
// Must be Unity 2019.4.18f1 (KSP 1.12.x's Unity version) - shaders compiled
// by other Unity versions may not load in KSP.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildRealWindowsBundle
{
    private static readonly string[] ShaderAssets =
    {
        "Assets/RealWindows/Shaders/RW_Id.shader",
        "Assets/RealWindows/Shaders/RW_Composite.shader",
        "Assets/RealWindows/Shaders/RW_Hull.shader",
        "Assets/RealWindows/Shaders/RW_Probe.shader",
    };

    [MenuItem("RealWindows/Build Shader Bundles")]
    public static void BuildFromMenu()
    {
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../GameData/RealWindows/Shaders"));
        Build(outDir, new[] { "windows" });
    }

    public static void BuildFromCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../GameData/RealWindows/Shaders"));
        string[] targets = { "windows" };
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-rwOut") outDir = args[i + 1];
            if (args[i] == "-rwTargets") targets = args[i + 1].Split(',');
        }
        try
        {
            Build(outDir, targets);
        }
        catch (Exception e)
        {
            Debug.LogError("[RealWindows] bundle build failed: " + e);
            EditorApplication.Exit(1);
        }
    }

    private static void Build(string outDir, string[] targets)
    {
        Directory.CreateDirectory(outDir);
        foreach (string raw in targets)
        {
            string t = raw.Trim().ToLowerInvariant();
            BuildTarget target;
            GraphicsDeviceType[] apis;
            switch (t)
            {
                case "windows":
                    target = BuildTarget.StandaloneWindows64;
                    // D3D11 is KSP's default; GLCore covers -force-glcore users.
                    apis = new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore };
                    break;
                case "osx":
                    target = BuildTarget.StandaloneOSX;
                    apis = new[] { GraphicsDeviceType.Metal, GraphicsDeviceType.OpenGLCore };
                    break;
                case "linux":
                    target = BuildTarget.StandaloneLinux64;
                    apis = new[] { GraphicsDeviceType.OpenGLCore, GraphicsDeviceType.Vulkan };
                    break;
                default:
                    Debug.LogWarning("[RealWindows] unknown target " + raw);
                    continue;
            }
            PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
            PlayerSettings.SetGraphicsAPIs(target, apis);

            string tmp = Path.Combine(Path.GetTempPath(), "rw-bundle-" + t);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            AssetBundleBuild b = new AssetBundleBuild
            {
                assetBundleName = "realwindows-" + t + ".shaders",
                assetNames = ShaderAssets
            };
            AssetBundleManifest m = BuildPipeline.BuildAssetBundles(tmp, new[] { b },
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression, target);
            if (m == null)
                throw new Exception("BuildAssetBundles returned null for " + t);

            string src = Path.Combine(tmp, b.assetBundleName);
            string dst = Path.Combine(outDir, b.assetBundleName);
            File.Copy(src, dst, true);
            Debug.Log("[RealWindows] built " + dst + " (" + new FileInfo(dst).Length + " bytes)");
        }
    }
}
