using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Shadows.Editor
{
    internal static class LoogaShadowSettingsEditorUtility
    {
        public static void Draw(
            SerializedObject serializedObject,
            string rootPath,
            string preferencePrefix)
        {
            DrawSection("Quality", preferencePrefix + ".Quality", true, () =>
            {
                DrawProperty(serializedObject, rootPath, "_quality", "Quality");
                DrawProperty(serializedObject, rootPath, "_renderSceneView", "Render Scene View");
            });

            DrawSection("Soft Shadows", preferencePrefix + ".SoftShadows", true, () =>
            {
                DrawProperty(serializedObject, rootPath, "_nearClipmapRadius", "Near Clipmap Radius");
                DrawProperty(serializedObject, rootPath, "_shadowDistance", "Shadow Distance");
                DrawProperty(serializedObject, rootPath, "_depthRange", "Depth Range");
                EditorGUILayout.Space(3);
                DrawProperty(serializedObject, rootPath, "_sourceAngularDiameter", "Source Angular Diameter");
                DrawProperty(serializedObject, rootPath, "_softness", "Softness");
                DrawProperty(serializedObject, rootPath, "_maximumPenumbra", "Maximum Penumbra");
                EditorGUILayout.Space(3);
                DrawProperty(serializedObject, rootPath, "_depthBias", "Depth Bias");
                DrawProperty(serializedObject, rootPath, "_normalBias", "Normal Bias");
                DrawProperty(serializedObject, rootPath, "_clipmapBlend", "Clipmap Blend");
                EditorGUILayout.Space(3);
                DrawProperty(serializedObject, rootPath, "_normalsSource", "Normals Source");
            });

            DrawSection("Debugging", preferencePrefix + ".Debugging", false, () =>
            {
                DrawProperty(serializedObject, rootPath, "_debugView", "Debug View");
            });
        }

        private static void DrawSection(string title, string key, bool open, System.Action content)
        {
            LoogaShadowEditorBase.DrawSection(title, key, open, content);
        }

        private static void DrawProperty(
            SerializedObject serializedObject,
            string rootPath,
            string propertyName,
            string label)
        {
            SerializedProperty property = Find(serializedObject, rootPath, propertyName);
            if (property != null)
                EditorGUILayout.PropertyField(property, new GUIContent(label));
        }

        private static SerializedProperty Find(
            SerializedObject serializedObject,
            string rootPath,
            string propertyName)
        {
            return serializedObject.FindProperty(rootPath + "." + propertyName);
        }
    }
}
