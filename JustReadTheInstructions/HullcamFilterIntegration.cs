using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace JustReadTheInstructions
{
    public static class HullcamFilterIntegration
    {
        private static bool? _isAvailable;
        private static Type _hullCamType;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable.HasValue)
                    return _isAvailable.Value;

                try
                {
                    var assembly = AssemblyLoader.loadedAssemblies
                        .FirstOrDefault(a =>
                            a.name.Equals("HullcamVDS", StringComparison.OrdinalIgnoreCase) ||
                            a.name.Equals("HullcamVDSContinued", StringComparison.OrdinalIgnoreCase))
                        ?.assembly;

                    if (assembly == null)
                    {
                        Debug.Log("[JRTI-HullcamFilter]: HullcamVDS not found");
                        _isAvailable = false;
                        return false;
                    }

                    _hullCamType = assembly.GetType("MuMechModuleHullCamera")
                                ?? assembly.GetType("HullcamVDS.MuMechModuleHullCamera");

                    if (_hullCamType == null)
                    {
                        Debug.LogWarning("[JRTI-HullcamFilter]: MuMechModuleHullCamera not found");
                        _isAvailable = false;
                        return false;
                    }

                    Debug.Log($"[JRTI-HullcamFilter]: HullcamVDS detected ({_hullCamType.FullName}). Filter hook not yet configured.");
                    _isAvailable = false;
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[JRTI-HullcamFilter]: Error checking availability: {ex.Message}");
                    _isAvailable = false;
                    return false;
                }
            }
        }

        public static string GetDiagnosticInfo()
        {
            bool avail = IsAvailable;
            return $"HullcamFilter: Assembly={(_hullCamType != null ? "found" : "not found")}, " +
                   $"Enabled={JRTISettings.EnableHullcamFilter}\n";
        }
    }
}
