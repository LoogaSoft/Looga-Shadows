using System;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Shadows.Editor
{
    internal abstract class LoogaShadowEditorBase : UnityEditor.Editor
    {
        private static GUIStyle _headerStyle;
        private static GUIStyle _boxStyle;

        protected static void DrawLoogaSoftHeader()
        {
            GUIStyle titleStyle = new()
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
            };

            EditorGUILayout.Space(3);
            GUILayout.Label("-  LoogaSoft  -", titleStyle);
            EditorGUILayout.Space(3);
        }

        internal static void DrawSection(
            string title,
            string prefKey,
            bool defaultShow,
            Action content)
        {
            DrawSectionHeader(title, prefKey, defaultShow, null, out bool show);
            if (show)
            {
                EditorGUILayout.Space(2);
                content();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        internal static void DrawToggleSection(
            string title,
            string prefKey,
            bool defaultShow,
            SerializedProperty enabled,
            Action content)
        {
            DrawSectionHeader(title, prefKey, defaultShow, enabled, out bool show);
            if (enabled.boolValue && show)
            {
                EditorGUILayout.Space(2);
                content();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawSectionHeader(
            string title,
            string prefKey,
            bool defaultShow,
            SerializedProperty enabled,
            out bool show)
        {
            EnsureStyles();
            show = EditorPrefs.GetBool(prefKey, defaultShow);

            EditorGUILayout.BeginVertical(_boxStyle);
            Rect full = GUILayoutUtility.GetRect(GUIContent.none, _headerStyle);
            full.height += 4f;
            full.y -= 2f;
            full.width += 8f;
            full.x -= 4f;

            bool hasToggle = enabled != null;
            bool isEnabled = !hasToggle || enabled.boolValue;
            Rect toggle = new(full.x + 3f, full.y + 1f, 18f, full.height);
            Rect text = new(full.x + (hasToggle ? 24f : 4f), full.y + 1f, full.width - 44f, full.height);
            Rect arrow = new(full.xMax - 10f, full.y, 15f, full.height);

            if (full.Contains(Event.current.mousePosition))
                EditorGUI.DrawRect(full, new Color(1f, 1f, 1f, 0.05f));

            if (hasToggle)
            {
                EditorGUI.BeginChangeCheck();
                bool value = EditorGUI.Toggle(toggle, enabled.boolValue);
                if (EditorGUI.EndChangeCheck())
                {
                    enabled.boolValue = value;
                    isEnabled = value;
                    if (value)
                    {
                        show = true;
                        EditorPrefs.SetBool(prefKey, true);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(!isEnabled))
            {
                GUI.Label(text, title, _headerStyle);
                bool newShow = isEnabled
                    ? EditorGUI.Foldout(arrow, show, GUIContent.none)
                    : false;

                if (Event.current.type == EventType.MouseDown &&
                    Event.current.button == 0 &&
                    full.Contains(Event.current.mousePosition) &&
                    (!hasToggle || !toggle.Contains(Event.current.mousePosition)) &&
                    isEnabled)
                {
                    newShow = !show;
                    Event.current.Use();
                }

                if (newShow != show && isEnabled)
                {
                    show = newShow;
                    EditorPrefs.SetBool(prefKey, show);
                }
            }
        }

        private static void EnsureStyles()
        {
            if (_headerStyle != null)
                return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                padding = new RectOffset(0, 0, 0, 4)
            };
            _boxStyle = new GUIStyle("HelpBox")
            {
                padding = new RectOffset(8, 8, 6, 6)
            };
        }
    }
}
