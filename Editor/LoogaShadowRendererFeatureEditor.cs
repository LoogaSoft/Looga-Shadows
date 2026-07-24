using UnityEditor;

namespace LoogaSoft.Shadows.Editor
{
    [CustomEditor(typeof(LoogaShadowRendererFeature))]
    internal sealed class LoogaShadowRendererFeatureEditor : LoogaShadowEditorBase
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawLoogaSoftHeader();

            LoogaShadowSettingsEditorUtility.Draw(
                serializedObject,
                "_settings",
                "LoogaShadowRendererFeature");

            serializedObject.ApplyModifiedProperties();
        }
    }
}
