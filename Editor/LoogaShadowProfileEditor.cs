using UnityEditor;

namespace LoogaSoft.Shadows.Editor
{
    [CustomEditor(typeof(LoogaShadowProfile))]
    internal sealed class LoogaShadowProfileEditor : LoogaShadowEditorBase
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawLoogaSoftHeader();
            LoogaShadowSettingsEditorUtility.Draw(
                serializedObject,
                "_settings",
                "LoogaShadowProfile");
            serializedObject.ApplyModifiedProperties();
        }
    }
}
