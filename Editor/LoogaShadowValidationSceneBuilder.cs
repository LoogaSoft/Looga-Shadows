using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Shadows.Editor
{
    internal static class LoogaShadowValidationSceneBuilder
    {
        private static readonly Vector3 ValidationGroundScale = new(60f, 1f, 60f);

        [MenuItem("LoogaSoft/Shadows/Create Validation Scene")]
        private static void CreateValidationScene()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Looga Shadows Validation Scene",
                "Looga Shadows Validation",
                "unity",
                "Choose where to create the validation scene.");

            if (string.IsNullOrWhiteSpace(path))
                return;

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateCamera();
            CreateSun();
            CreateGeometry();
            EditorSceneManager.SaveScene(scene, path);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
        }

        [MenuItem("LoogaSoft/Shadows/Expand Validation Ground Coverage")]
        private static void ExpandValidationGroundCoverage()
        {
            GameObject ground = GameObject.Find("Ground Receiver");
            if (ground == null)
            {
                Debug.LogWarning(
                    "[Looga Shadows] The active scene does not contain a Ground Receiver.");
                return;
            }

            Undo.RecordObject(ground.transform, "Expand Validation Ground Coverage");
            ground.transform.localScale = ValidationGroundScale;
            EditorSceneManager.MarkSceneDirty(ground.scene);
            Selection.activeGameObject = ground;
        }

        private static void CreateCamera()
        {
            GameObject cameraObject = new("Validation Camera", typeof(Camera));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(
                new Vector3(0f, 6f, -18f),
                Quaternion.Euler(12f, 0f, 0f));

            Camera camera = cameraObject.GetComponent<Camera>();
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 180f;

            Transform waypoints = CreateGroup("Camera Stability Waypoints");
            CreateMarker("Start", new Vector3(0f, 6f, -18f), waypoints);
            CreateMarker("Strafe Left", new Vector3(-4f, 6f, -18f), waypoints);
            CreateMarker("Strafe Right", new Vector3(4f, 6f, -18f), waypoints);
            CreateMarker("Forward", new Vector3(0f, 6f, -8f), waypoints);
        }

        private static void CreateSun()
        {
            GameObject lightObject = new("Validation Sun", typeof(Light), typeof(LoogaShadowLight));
            Light light = lightObject.GetComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = 1.2f;
            light.shadowStrength = 1f;
            lightObject.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        }

        private static void CreateGeometry()
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground Receiver";
            ground.transform.localScale = ValidationGroundScale;

            Transform stability = CreateGroup("01 Camera Stability");
            CreatePrimitive(PrimitiveType.Cube, "Fixed Reference Cube", new Vector3(-5f, 1f, 0f), new Vector3(1.5f, 2f, 1.5f), stability);
            CreatePrimitive(PrimitiveType.Sphere, "Curved Caster", new Vector3(0f, 1.1f, 0f), Vector3.one * 2.2f, stability);
            CreatePrimitive(PrimitiveType.Capsule, "Character Proxy", new Vector3(5f, 1f, 0f), Vector3.one, stability);
            CreatePrimitive(PrimitiveType.Cube, "Vertical Receiver", new Vector3(9f, 4f, 6f), new Vector3(0.2f, 8f, 14f), stability);

            Transform receiverGaps = CreateGroup("02 Receiver Gaps");
            float[] gaps = { 0f, 0.025f, 0.1f, 0.4f, 1f };
            for (int index = 0; index < gaps.Length; index++)
            {
                float gap = gaps[index];
                CreatePrimitive(
                    PrimitiveType.Cube,
                    $"Receiver Gap {gap:0.###}m",
                    new Vector3(-6f + index * 2.2f, 0.5f + gap, 6f),
                    Vector3.one,
                    receiverGaps);
            }

            Transform penumbra = CreateGroup("03 Penumbra Growth");
            float[] heights = { 0.5f, 1.5f, 3f, 6f };
            for (int index = 0; index < heights.Length; index++)
            {
                float height = heights[index];
                CreatePrimitive(
                    PrimitiveType.Sphere,
                    $"Caster Height {height:0.#}m",
                    new Vector3(-4.5f + index * 3f, 1f + height, 12f),
                    Vector3.one * 1.5f,
                    penumbra);
            }

            Transform thinGeometry = CreateGroup("04 Thin Geometry");
            for (int index = 0; index < 7; index++)
            {
                CreatePrimitive(
                    PrimitiveType.Cube,
                    $"Thin Post {index + 1}",
                    new Vector3(-6f + index * 2f, 2f, 20f),
                    new Vector3(0.08f, 4f, 0.08f),
                    thinGeometry);
            }

            CreatePrimitive(
                PrimitiveType.Cube,
                "Thin Horizontal Bar",
                new Vector3(0f, 3.25f, 20f),
                new Vector3(12f, 0.08f, 0.08f),
                thinGeometry);

            Transform distance = CreateGroup("05 Distance And Clipmaps");
            float[] distances = { 28f, 52f, 82f };
            for (int index = 0; index < distances.Length; index++)
            {
                CreatePrimitive(
                    PrimitiveType.Cube,
                    $"Distance Caster {distances[index]:0}m",
                    new Vector3(0f, 3f, distances[index]),
                    new Vector3(4f, 6f, 4f),
                    distance);
            }

        }

        private static Transform CreateGroup(string name)
        {
            return new GameObject(name).transform;
        }

        private static void CreateMarker(string name, Vector3 position, Transform parent)
        {
            Transform marker = new GameObject(name).transform;
            marker.SetParent(parent);
            marker.position = position;
        }

        private static void CreatePrimitive(
            PrimitiveType type,
            string name,
            Vector3 position,
            Vector3 scale,
            Transform parent)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent);
            primitive.transform.position = position;
            primitive.transform.localScale = scale;
        }
    }
}
