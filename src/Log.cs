using System;
using UnityEngine;

namespace RealWindows
{
    public static class RWLog
    {
        public const string TAG = "RealWindows";

        public static void Info(string msg, params object[] args)
        {
            try { Debug.Log("[" + TAG + "] " + Format(msg, args)); } catch (Exception) { }
        }

        public static void Verbose(string msg, params object[] args)
        {
            if (RWSettings.verboseLogging)
                Info(msg, args);
        }

        public static void Warn(string msg, params object[] args)
        {
            try { Debug.LogWarning("[" + TAG + "] " + Format(msg, args)); } catch (Exception) { }
        }

        public static void Error(string msg, params object[] args)
        {
            try { Debug.LogError("[" + TAG + "] " + Format(msg, args)); } catch (Exception) { }
        }

        private static string Format(string msg, object[] args)
        {
            try { return args != null && args.Length > 0 ? string.Format(msg, args) : msg; }
            catch (Exception) { return msg; }
        }
    }
}
