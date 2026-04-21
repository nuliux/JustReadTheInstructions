using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace JustReadTheInstructions
{
    public static class TUFXIntegration
    {
        private static bool? _isAvailable;
        private static Type _postProcessLayerType;
        private static Type _postProcessVolumeType;
        private static Type _texturesUnlimitedFXLoaderType;
        private static MethodInfo _addOrGetComponentMethod;
        private static MethodInfo _initMethod;
        private static PropertyInfo _resourcesProperty;
        private static FieldInfo _volumeLayerField;
        private static FieldInfo _isGlobalField;
        private static FieldInfo _priorityField;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable.HasValue)
                    return _isAvailable.Value;

                try
                {
                    var tufxAssembly = AssemblyLoader.loadedAssemblies
                        .FirstOrDefault(a => a.name == "TUFX")?.assembly;

                    if (tufxAssembly == null)
                    {
                        Debug.Log("[JRTI-TUFX]: TUFX not found - post-processing disabled");
                        _isAvailable = false;
                        return false;
                    }

                    _postProcessLayerType = tufxAssembly.GetType("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
                    _postProcessVolumeType = tufxAssembly.GetType("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                    _texturesUnlimitedFXLoaderType = tufxAssembly.GetType("TUFX.TexturesUnlimitedFXLoader");

                    if (_postProcessLayerType == null || _postProcessVolumeType == null || _texturesUnlimitedFXLoaderType == null)
                    {
                        Debug.LogWarning("[JRTI-TUFX]: TUFX types not found - incompatible version?");
                        _isAvailable = false;
                        return false;
                    }

                    _resourcesProperty = _texturesUnlimitedFXLoaderType.GetProperty("Resources",
                        BindingFlags.Public | BindingFlags.Static);
                    _initMethod = _postProcessLayerType.GetMethod("Init",
                        BindingFlags.Public | BindingFlags.Instance);
                    _volumeLayerField = _postProcessLayerType.GetField("volumeLayer",
                        BindingFlags.Public | BindingFlags.Instance);
                    _isGlobalField = _postProcessVolumeType.GetField("isGlobal",
                        BindingFlags.Public | BindingFlags.Instance);
                    _priorityField = _postProcessVolumeType.GetField("priority",
                        BindingFlags.Public | BindingFlags.Instance);

                    var extensionsType = typeof(GameObject).Assembly.GetType("UnityEngine.GameObjectExtensions")
                        ?? typeof(GameObject);
                    _addOrGetComponentMethod = extensionsType.GetMethod("AddOrGetComponent",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(GameObject), typeof(Type) },
                        null);

                    if (_addOrGetComponentMethod == null)
                    {
                        Debug.Log("[JRTI-TUFX]: Using fallback AddOrGetComponent");
                    }

                    _isAvailable = true;
                    Debug.Log("[JRTI-TUFX]: Integration enabled");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[JRTI-TUFX]: Error checking availability: {ex.Message}");
                    _isAvailable = false;
                    return false;
                }
            }
        }

        public static void ApplyToCamera(Camera camera)
        {
            if (!IsAvailable || camera == null)
                return;

            try
            {
                Component layer = AddOrGetComponent(camera.gameObject, _postProcessLayerType);

                if (layer == null)
                {
                    Debug.LogWarning($"[JRTI-TUFX]: Failed to add PostProcessLayer to {camera.name}");
                    return;
                }

                var resources = _resourcesProperty?.GetValue(null);
                if (resources != null && _initMethod != null)
                {
                    _initMethod.Invoke(layer, new[] { resources });
                }
                else
                {
                    Debug.LogWarning($"[JRTI-TUFX]: Resources not found - removing PostProcessLayer from {camera.name}");
                    UnityEngine.Object.Destroy(layer);
                    return;
                }

                if (_volumeLayerField != null)
                {
                    LayerMask allLayers = ~0;
                    _volumeLayerField.SetValue(layer, allLayers);
                }

                Component volume = AddOrGetComponent(camera.gameObject, _postProcessVolumeType);

                if (volume == null)
                {
                    Debug.LogWarning($"[JRTI-TUFX]: Failed to add PostProcessVolume - removing PostProcessLayer from {camera.name}");
                    UnityEngine.Object.Destroy(layer);
                    return;
                }

                if (_isGlobalField != null)
                {
                    _isGlobalField.SetValue(volume, true);
                }

                if (_priorityField != null)
                {
                    _priorityField.SetValue(volume, 100);
                }

                Debug.Log($"[JRTI-TUFX]: Applied post-processing to {camera.name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-TUFX]: Failed to apply to {camera.name}: {ex.Message}\n{ex.StackTrace}");

                try
                {
                    var layer = camera.gameObject.GetComponent(_postProcessLayerType);
                    if (layer != null) UnityEngine.Object.Destroy(layer);

                    var volume = camera.gameObject.GetComponent(_postProcessVolumeType);
                    if (volume != null) UnityEngine.Object.Destroy(volume);
                }
                catch { }
            }
        }

        public static void RemoveFromCamera(Camera camera)
        {
            if (camera == null)
                return;

            try
            {
                if (_postProcessLayerType != null)
                {
                    var layer = camera.gameObject.GetComponent(_postProcessLayerType);
                    if (layer != null)
                    {
                        UnityEngine.Object.Destroy(layer);
                    }
                }

                if (_postProcessVolumeType != null)
                {
                    var volume = camera.gameObject.GetComponent(_postProcessVolumeType);
                    if (volume != null)
                    {
                        UnityEngine.Object.Destroy(volume);
                    }
                }

                Debug.Log($"[JRTI-TUFX]: Removed from {camera.name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-TUFX]: Error removing from {camera.name}: {ex.Message}");
            }
        }

        private static Component AddOrGetComponent(GameObject gameObject, Type componentType)
        {
            if (_addOrGetComponentMethod != null)
            {
                try
                {
                    return (Component)_addOrGetComponentMethod.Invoke(null, new object[] { gameObject, componentType });
                }
                catch
                {
                    // Ignore, fallback handles that
                }
            }

            // Manual fallback
            var existing = gameObject.GetComponent(componentType);
            if (existing != null)
                return existing;

            return gameObject.AddComponent(componentType);
        }

        public static string GetDiagnosticInfo(Camera camera)
        {
            if (!IsAvailable || camera == null)
                return "TUFX not available";

            var info = $"TUFX Integration for {camera.name}:\n";

            if (_postProcessLayerType != null)
            {
                var layer = camera.GetComponent(_postProcessLayerType);
                info += layer != null
                    ? "- PostProcessLayer: Present\n"
                    : "- PostProcessLayer: Missing\n";
            }

            if (_postProcessVolumeType != null)
            {
                var volume = camera.GetComponent(_postProcessVolumeType);
                info += volume != null
                    ? "- PostProcessVolume: Present\n"
                    : "- PostProcessVolume: Missing\n";
            }

            return info;
        }
    }
}
