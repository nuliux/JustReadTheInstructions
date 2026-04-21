using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace JustReadTheInstructions
{
    public class ScattererCameraSwap : MonoBehaviour
    {
        private Camera _camera;

        private static bool _initialized;
        private static object _scattererInstance;
        private static FieldInfo _nearCameraField;
        private static Camera _mainCamera;

        void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        void OnEnable()
        {
            if (!_initialized)
                Initialize();
        }

        private static void Initialize()
        {
            _initialized = true;

            try
            {
                var assembly = AssemblyLoader.loadedAssemblies
                    .FirstOrDefault(a => a.name == "Scatterer")?.assembly;

                if (assembly == null)
                    return;

                var scattererType = assembly.GetType("Scatterer.Scatterer");
                if (scattererType == null)
                {
                    Debug.LogWarning("[JRTI-CameraSwap]: Scatterer.Scatterer type not found");
                    return;
                }

                var instanceProp = scattererType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);

                _scattererInstance = instanceProp?.GetValue(null);

                if (_scattererInstance == null)
                {
                    Debug.LogWarning("[JRTI-CameraSwap]: Scatterer.Instance is null");
                    return;
                }

                _nearCameraField = scattererType.GetField("nearCamera",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_nearCameraField == null)
                {
                    Debug.LogWarning("[JRTI-CameraSwap]: nearCamera field not found");
                    return;
                }

                _mainCamera = Camera.allCameras.FirstOrDefault(c => c.name == "Camera 00");
                Debug.Log("[JRTI-CameraSwap]: Ready - will swap Scatterer.Instance.nearCamera in OnPreCull");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[JRTI-CameraSwap]: Init failed: {ex.Message}");
            }
        }

        void OnPreCull()
        {
            if (_scattererInstance == null || _nearCameraField == null)
                return;

            _nearCameraField.SetValue(_scattererInstance, _camera);
        }

        void OnPostRender()
        {
            if (_scattererInstance == null || _nearCameraField == null)
                return;

            if (_mainCamera != null)
                _nearCameraField.SetValue(_scattererInstance, _mainCamera);
        }
    }
}