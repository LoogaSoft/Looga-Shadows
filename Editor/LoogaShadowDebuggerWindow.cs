using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LoogaSoft.Shadows.Editor
{
    public sealed class LoogaShadowDebuggerWindow : EditorWindow
    {
        private const double RepaintIntervalSeconds = 0.25;
        private double _nextRepaintTime;

        [MenuItem("LoogaSoft/Shadows/Debugger")]
        public static void Open()
        {
            LoogaShadowDebuggerWindow window = GetWindow<LoogaShadowDebuggerWindow>();
            window.titleContent = new GUIContent("Looga Shadows");
            window.minSize = new Vector2(360f, 250f);
            window.Show();
        }

        private void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaintTime)
                return;

            _nextRepaintTime = EditorApplication.timeSinceStartup + RepaintIntervalSeconds;
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.Toggle("Rendering", LoogaShadowRuntimeDiagnostics.IsRendering);
                EditorGUILayout.TextField("Last Camera", LoogaShadowRuntimeDiagnostics.LastCameraName);
                EditorGUILayout.TextField("Main Light", LoogaShadowRuntimeDiagnostics.MainLightName);
                EditorGUILayout.TextField("Settings Source", LoogaShadowRuntimeDiagnostics.SettingsSource);
                EditorGUILayout.IntField("Clipmap Levels", LoogaShadowRuntimeDiagnostics.ClipmapCount);
                EditorGUILayout.IntField("Atlas Resolution", LoogaShadowRuntimeDiagnostics.AtlasResolution);
                EditorGUILayout.Vector2IntField(
                    "Output Size",
                    new Vector2Int(
                        LoogaShadowRuntimeDiagnostics.OutputWidth,
                        LoogaShadowRuntimeDiagnostics.OutputHeight));
                EditorGUILayout.IntField("Last Frame", LoogaShadowRuntimeDiagnostics.LastRenderedFrame);
                EditorGUILayout.EnumPopup("Debug View", LoogaShadowRuntimeDiagnostics.DebugView);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Pipeline", EditorStyles.boldLabel);
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            bool isUrp = pipeline is UniversalRenderPipelineAsset;
            EditorGUILayout.LabelField("Active Pipeline", pipeline != null ? pipeline.name : "Built-in");

            if (!isUrp)
                EditorGUILayout.HelpBox("Looga Shadows requires the Universal Render Pipeline.", MessageType.Error);
            else if (!LoogaShadowRuntimeDiagnostics.IsRendering)
                EditorGUILayout.HelpBox(
                    "Enter Play mode or enable Scene view rendering in the Looga Shadows renderer feature.",
                    MessageType.Info);

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "Debug visualizations use renderer-feature settings unless the active Looga Shadow Light has an override profile. The rendered atlas and resolve path are owned by Looga Shadows.",
                MessageType.None);
        }
    }
}
