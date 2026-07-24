using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Shadows.Editor
{
    [CustomEditor(typeof(LoogaShadowLight))]
    internal sealed class LoogaShadowLightEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            LoogaShadowLight shadowLight = (LoogaShadowLight)target;
            Light light = shadowLight.Light;
            if (light == null)
                return;

            if (light.type != LightType.Directional)
            {
                EditorGUILayout.HelpBox(
                    "Looga Shadow Light overrides currently apply only when this is URP's active directional main light.",
                    MessageType.Warning);
            }
            else if (light.shadows == LightShadows.None)
            {
                EditorGUILayout.HelpBox(
                    "Enable shadows on this Light so Looga Shadows can render its ShadowCaster geometry into the package-owned clipmaps.",
                    MessageType.Info);
            }
        }
    }
}
