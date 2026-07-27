using UnityEngine;

namespace LoogaSoft.Shadows
{
    public enum LoogaShadowQuality
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

    public enum LoogaShadowNormalsSource
    {
        [InspectorName("G-Buffers")]
        GBuffer = 0,

        [InspectorName("Reconstruct From Depth")]
        ReconstructFromDepth = 1,

        [InspectorName("Depth + Normals Pass")]
        DepthNormalsPass = 2
    }

    public enum LoogaShadowDebugView
    {
        Off = 0,
        FinalVisibility = 1,
        RawVisibility = 2,
        Penumbra = 3,
        ClipmapLevels = 4,
        VirtualTexels = 5,
        LinearDepth = 6,
        WorldNormals = 7
    }

    [CreateAssetMenu(fileName = "Looga Shadow Profile", menuName = "LoogaSoft/Shadows/Shadow Profile")]
    public sealed class LoogaShadowProfile : ScriptableObject
    {
        [SerializeField]
        private LoogaShadowSettings _settings = LoogaShadowSettings.Default;

        [SerializeField, HideInInspector]
        private int _serializedVersion;

        // Retained for one-way migration of profiles authored before settings were grouped.
        [SerializeField, HideInInspector] private LoogaShadowQuality _quality = LoogaShadowQuality.High;
        [SerializeField, HideInInspector] private bool _renderSceneView = true;
        [SerializeField, HideInInspector] private float _nearClipmapRadius = 1f;
        [SerializeField, HideInInspector] private float _shadowDistance = 300f;
        [SerializeField, HideInInspector] private float _depthRange = 500f;
        [SerializeField, HideInInspector] private float _sourceAngularDiameter = 0.53f;
        [SerializeField, HideInInspector] private float _softness = 1f;
        [SerializeField, HideInInspector] private float _maximumPenumbra = 3f;
        [SerializeField, HideInInspector] private float _depthBias = 0.0012f;
        [SerializeField, HideInInspector] private float _normalBias = 0.002f;
        [SerializeField, HideInInspector] private float _clipmapBlend = 0.12f;
        [SerializeField, HideInInspector] private LoogaShadowDebugView _debugView;

        public LoogaShadowSettings Settings
        {
            get
            {
                MigrateIfNeeded();
                return _settings;
            }
        }

        public LoogaShadowQuality Quality => Settings.Quality;
        public bool RenderSceneView => Settings.RenderSceneView;
        public float NearClipmapRadius => Settings.NearClipmapRadius;
        public float ShadowDistance => Settings.ShadowDistance;
        public float DepthRange => Settings.DepthRange;
        public float SourceAngularDiameter => Settings.SourceAngularDiameter;
        public float Softness => Settings.Softness;
        public float MaximumPenumbra => Settings.MaximumPenumbra;
        public float DepthBias => Settings.DepthBias;
        public float NormalBias => Settings.NormalBias;
        public float ClipmapBlend => Settings.ClipmapBlend;
        public LoogaShadowNormalsSource NormalsSource => Settings.NormalsSource;
        public LoogaShadowDebugView DebugView => Settings.DebugView;

        private void OnEnable()
        {
            MigrateIfNeeded();
        }

        private void OnValidate()
        {
            MigrateIfNeeded();
            _settings.Validate();
        }

        private void MigrateIfNeeded()
        {
            if (_serializedVersion >= LoogaShadowSettings.CurrentVersion && _settings.IsInitialized)
                return;

            if (_serializedVersion <= 0)
            {
                _settings = LoogaShadowSettings.Create(
                    _quality,
                    _renderSceneView,
                    _nearClipmapRadius,
                    _shadowDistance,
                    _depthRange,
                    _sourceAngularDiameter,
                    _softness,
                    _maximumPenumbra,
                    _depthBias,
                    _normalBias,
                    _clipmapBlend,
                    LoogaShadowNormalsSource.GBuffer,
                    _debugView);
            }
            else
            {
                _settings.EnsureInitialized();
            }

            _serializedVersion = LoogaShadowSettings.CurrentVersion;
        }
    }
}
