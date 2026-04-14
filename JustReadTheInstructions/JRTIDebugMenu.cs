using UnityEngine;

namespace JustReadTheInstructions
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class JRTIDebugMenu : MonoBehaviour
    {
        private const int WindowId = 1901;
        private const float WindowWidth = 350;
        private const float WindowHeight = 400;

        private bool _isVisible;
        private Rect _windowRect;
        private bool _lastHotkeyState;
        private Vector2 _scrollPosition;

        public static bool EnableDeferred = true;
        public static bool EnableTUFX = true;
        public static bool EnableEVE = true;
        public static bool EnableParallax = false; // By default seems very heavy on performance, so start disabled
        public static bool EnableFirefly = true;
        public static bool EnableScatterer = true;

        private GUIStyle _labelStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _sectionHeaderStyle;
        private GUIStyle _smallLabelStyle;
        private GUIStyle _descriptionStyle;

        void Start()
        {
            _windowRect = new Rect(
                (Screen.width - WindowWidth) / 2,
                (Screen.height - WindowHeight) / 2,
                WindowWidth,
                WindowHeight
            );

            InitializeStyles();
            Debug.Log("[JRTI]: Debug menu initialized (Ctrl+Alt+F8)");
        }

        void Update()
        {
            bool hotkeyPressed =
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) &&
                Input.GetKey(KeyCode.F8);

            if (hotkeyPressed && !_lastHotkeyState)
            {
                _isVisible = !_isVisible;
                Debug.Log($"[JRTI]: Debug menu {(_isVisible ? "opened" : "closed")}");
            }

            _lastHotkeyState = hotkeyPressed;
        }

        void OnGUI()
        {
            if (_isVisible)
            {
                _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "JRTI Debug Menu");
                ClampToScreen();
            }
        }

        private void InitializeStyles()
        {
            _labelStyle = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };

            _toggleStyle = new GUIStyle(HighLogic.Skin.toggle)
            {
                fontSize = 11,
                margin = new RectOffset(5, 5, 5, 5)
            };

            _buttonStyle = new GUIStyle(HighLogic.Skin.button)
            {
                fontSize = 11
            };

            _sectionHeaderStyle = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.8f, 0.9f, 1.0f) }
            };

            _smallLabelStyle = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = 10,
                normal = { textColor = new Color(1f, 1f, 0.8f) }
            };

            _descriptionStyle = new GUIStyle(HighLogic.Skin.label)
            {
                fontSize = 10,
                normal = { textColor = Color.gray },
                wordWrap = true
            };
        }

        private void DrawWindow(int windowId)
        {
            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);
            GUILayout.BeginVertical();

            GUILayout.Label("Rendering & Post-Processing", _sectionHeaderStyle);
            GUILayout.Space(5);

            DrawModuleToggle(
                name: "Deferred Rendering",
                enabled: ref EnableDeferred,
                available: DeferredIntegration.IsAvailable,
                description: "Enables deferred shading pipeline"
            );

            GUILayout.Space(3);

            DrawModuleToggle(
                name: "TUFX Post-Processing",
                enabled: ref EnableTUFX,
                available: TUFXIntegration.IsAvailable,
                description: "Applies TUFX post-processing effects"
            );

            GUILayout.Space(3);

            DrawModuleToggle(
                name: "Scatterer (Ocean & Atmosphere)",
                enabled: ref EnableScatterer,
                available: ScattererIntegration.IsAvailable,
                description: "Scatterer ocean and atmospheric scattering. Disables MSAA on JRTI cameras"
            );

            GUILayout.Space(10);

            GUILayout.Label("Visual Effects", _sectionHeaderStyle);
            GUILayout.Space(5);

            DrawModuleToggle(
                name: "EVE (Clouds & Water)",
                enabled: ref EnableEVE,
                available: EVEIntegration.IsAvailable,
                description: "Environmental Visual Enhancements - clouds, water puddles, atmospheric effects"
            );

            GUILayout.Space(3);

            DrawModuleToggle(
                name: "Parallax (Terrain Scatter)",
                enabled: ref EnableParallax,
                available: ParallaxIntegration.IsAvailable,
                description: "Parallax-Continued - grass, rocks, trees, and terrain details (near camera only)"
            );

            GUILayout.Space(3);

            DrawModuleToggle(
                name: "Firefly (Re-entry Effects)",
                enabled: ref EnableFirefly,
                available: FireflyIntegration.IsAvailable,
                description: "Firefly - atmospheric re-entry plasma effects (near camera only)"
            );

            GUILayout.Space(10);

            GUILayout.Label("Status", _sectionHeaderStyle);
            GUILayout.Space(5);

            int openCameras = HullCameraManager.Instance?.GetOpenCameraCount() ?? 0;
            GUILayout.Label($"Open cameras: {openCameras}", _labelStyle);

            if (ParallaxIntegration.IsAvailable)
            {
                bool hasScatters = ParallaxIntegration.HasActiveScatters();
                GUILayout.Label($"Parallax scatters active: {hasScatters}", _labelStyle);
            }

            GUILayout.Space(10);

            GUILayout.Label("Changes apply immediately to all cameras.", _smallLabelStyle);
            GUILayout.Label("Settings are runtime-only (not saved).", _smallLabelStyle);
            GUILayout.Label("Diagnostics output to KSP.log.", _smallLabelStyle);
            GUI.color = Color.white;

            GUILayout.Space(10);

            if (GUILayout.Button("Get Diagnostics (prints to log)", _buttonStyle))
                PrintDiagnostics();

            GUILayout.Space(5);

            if (GUILayout.Button("Close", _buttonStyle))
                _isVisible = false;

            GUILayout.EndVertical();
            GUILayout.EndScrollView();

            GUI.DragWindow();
        }

        private void DrawModuleToggle(string name, ref bool enabled, bool available, string description)
        {
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();

            bool newValue = GUILayout.Toggle(enabled, name, _toggleStyle);

            if (newValue != enabled)
            {
                enabled = newValue;
                OnToggleChanged(name, enabled);
            }

            GUILayout.FlexibleSpace();

            if (available)
            {
                GUI.color = Color.green;
                GUILayout.Label("✓ Available", _labelStyle);
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = Color.red;
                GUILayout.Label("✗ Not Found", _labelStyle);
                GUI.color = Color.white;
            }

            GUILayout.EndHorizontal();

            GUILayout.Label(description, _descriptionStyle);

            GUILayout.EndVertical();
        }

        private void OnToggleChanged(string moduleName, bool enabled)
        {
            Debug.Log($"[JRTI-Debug]: {moduleName} {(enabled ? "enabled" : "disabled")} - applying to all cameras");
            HullCameraManager.Instance?.UpdateAllCameraVisualEffects();
        }

        private void PrintDiagnostics()
        {
            Debug.Log("[JRTI-Debug]: ===== Visual Mod Integration Diagnostics =====");
            Debug.Log($"[JRTI-Debug]: Deferred: Available={DeferredIntegration.IsAvailable}, Enabled={EnableDeferred}");
            Debug.Log($"[JRTI-Debug]: TUFX: Available={TUFXIntegration.IsAvailable}, Enabled={EnableTUFX}");
            Debug.Log($"[JRTI-Debug]: EVE: Available={EVEIntegration.IsAvailable}, Enabled={EnableEVE}");
            Debug.Log($"[JRTI-Debug]: Parallax: Available={ParallaxIntegration.IsAvailable}, Enabled={EnableParallax}");
            Debug.Log($"[JRTI-Debug]: Firefly: Available={FireflyIntegration.IsAvailable}, Enabled={EnableFirefly}");
            Debug.Log($"[JRTI-Debug]: Scatterer: Available={ScattererIntegration.IsAvailable}, Enabled={EnableScatterer}");

            if (ParallaxIntegration.IsAvailable)
                Debug.Log($"[JRTI-Debug]: Parallax has active scatters: {ParallaxIntegration.HasActiveScatters()}");

            int openCount = HullCameraManager.Instance?.GetOpenCameraCount() ?? 0;
            Debug.Log($"[JRTI-Debug]: Currently {openCount} cameras open");
            Debug.Log("[JRTI-Debug]: ========================================");
        }

        private void ClampToScreen()
        {
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Screen.width - _windowRect.width);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Screen.height - _windowRect.height);
        }
    }
}