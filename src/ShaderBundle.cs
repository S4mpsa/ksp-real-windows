// Loads the shaders (id, hull, probe, composite, and the optional window cut-out) from the platform asset bundle built by
// unity/Assets/Editor/BuildRealWindowsBundle.cs (Unity 2019.4.18f1).

using System;
using System.IO;
using UnityEngine;

namespace RealWindows
{
    public static class ShaderBundle
    {
        public const string IdShaderName = "Hidden/RealWindows/Id";
        public const string CompositeShaderName = "Hidden/RealWindows/Composite";
        public const string HullShaderName = "Hidden/RealWindows/Hull";
        public const string ProbeShaderName = "Hidden/RealWindows/Probe";
        public const string CoverShaderName = "Hidden/RealWindows/Cover";   // optional: seeThroughHoles

        public static Shader IdShader;
        public static Shader CompositeShader;
        public static Shader HullShader;
        public static Shader ProbeShader;
        public static Shader CoverShader;

        private static bool attempted;

        public static bool Ready
        {
            get { return IdShader != null && CompositeShader != null && HullShader != null && ProbeShader != null; }
        }

        public static bool Load()
        {
            if (attempted)
                return Ready;
            attempted = true;

            string suffix;
            switch (Application.platform)
            {
                case RuntimePlatform.OSXPlayer: suffix = "osx"; break;
                case RuntimePlatform.LinuxPlayer: suffix = "linux"; break;
                default: suffix = "windows"; break;
            }
            string path = Path.Combine(RWSettings.RootPath, "Shaders/realwindows-" + suffix + ".shaders");
            try
            {
                if (!File.Exists(path))
                {
                    RWLog.Error("shader bundle missing: {0} - build it with tools/build_shaders.py", path);
                    return false;
                }
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    RWLog.Error("shader bundle failed to load: {0}", path);
                    return false;
                }
                Shader[] shaders = bundle.LoadAllAssets<Shader>();
                for (int i = 0; i < shaders.Length; i++)
                {
                    if (shaders[i].name == IdShaderName) IdShader = shaders[i];
                    else if (shaders[i].name == CompositeShaderName) CompositeShader = shaders[i];
                    else if (shaders[i].name == HullShaderName) HullShader = shaders[i];
                    else if (shaders[i].name == ProbeShaderName) ProbeShader = shaders[i];
                    else if (shaders[i].name == CoverShaderName) CoverShader = shaders[i];
                }
                bundle.Unload(false);
                if (!Ready)
                {
                    RWLog.Error("shader bundle {0} is missing shaders (id={1}, composite={2}, hull={3}, probe={4}) - rebuild it",
                        path, IdShader != null, CompositeShader != null, HullShader != null, ProbeShader != null);
                    return false;
                }
                if (!IdShader.isSupported || !CompositeShader.isSupported || !HullShader.isSupported || !ProbeShader.isSupported)
                {
                    RWLog.Error("shaders not supported on {0} (id={1}, composite={2}, hull={3}, probe={4}) - bundle built without this graphics API?",
                        SystemInfo.graphicsDeviceType, IdShader.isSupported, CompositeShader.isSupported, HullShader.isSupported, ProbeShader.isSupported);
                    IdShader = CompositeShader = HullShader = ProbeShader = null;
                    return false;
                }
                if (CoverShader != null && !CoverShader.isSupported)
                    CoverShader = null;
                RWLog.Info("shaders loaded from {0} ({1}){2}", path, SystemInfo.graphicsDeviceType, CoverShader == null ? ", no window cut-outs" : "");
                return true;
            }
            catch (Exception e)
            {
                RWLog.Error("shader bundle load threw: {0}", e);
                return false;
            }
        }
    }
}
