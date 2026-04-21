using System.Reflection;
using UnityEngine;

namespace JustReadTheInstructions
{
    public class CanvasFix : MonoBehaviour
    {
        private static readonly FieldInfo CanvasField = typeof(Canvas).GetField(
            "willRenderCanvases",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        private object _storedCanvasValue;

        void OnPreRender()
        {
            if (CanvasField == null) return;

            _storedCanvasValue = CanvasField.GetValue(null);
            CanvasField.SetValue(null, null);
        }

        void OnPostRender()
        {
            if (CanvasField == null) return;

            CanvasField.SetValue(null, _storedCanvasValue);
        }
    }
}