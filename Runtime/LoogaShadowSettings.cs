using System;
using UnityEngine;

namespace LoogaSoft.Shadows
{
    [Serializable]
    public struct LoogaShadowSettings
    {
        internal const int CurrentVersion = 2;

        [SerializeField, HideInInspector]
        private int _version;

        [SerializeField]
        [Tooltip("Selects the owned shadow-atlas resolution, clipmap count, and deterministic filter budget.")]
        private LoogaShadowQuality _quality;

        [SerializeField]
        [Tooltip("Allows Looga Shadows to render in the Scene view as well as Game cameras.")]
        private bool _renderSceneView;

        [SerializeField, Min(1f)]
        [Tooltip("Half-width in meters of the highest-detail clipmap around the camera.")]
        private float _nearClipmapRadius;

        [SerializeField, Min(10f)]
        [Tooltip("Maximum camera distance that receives realtime Looga Shadows.")]
        private float _shadowDistance;

        [SerializeField, Min(10f)]
        [Tooltip("World-space depth captured along the directional light.")]
        private float _depthRange;

        [SerializeField, Range(0.05f, 3f)]
        [Tooltip("Angular diameter of the directional light in degrees. The real sun is approximately 0.53 degrees.")]
        private float _sourceAngularDiameter;

        [SerializeField, Range(0f, 2f)]
        [Tooltip("Scales contact-hardening penumbrae without changing clipmap resolution.")]
        private float _softness;

        [SerializeField, Range(0.05f, 8f)]
        [Tooltip("Caps the world-space penumbra radius so distant blockers cannot wash out the scene.")]
        private float _maximumPenumbra;

        [SerializeField, Range(0f, 0.02f)]
        [Tooltip("World-space depth-bias floor used for caster capture and receiver comparison.")]
        private float _depthBias;

        [SerializeField, Range(0f, 0.1f)]
        [Tooltip("World-space normal-bias floor applied while rendering shadow casters.")]
        private float _normalBias;

        [SerializeField, Range(0f, 0.25f)]
        [Tooltip("Width of the overlap used to blend adjacent clipmap levels.")]
        private float _clipmapBlend;

        [SerializeField]
        [Tooltip("Selects the surface-normal source used by shadow reconstruction. G-Buffers is preferred for deferred rendering, Reconstruct From Depth avoids material normal maps, and Depth + Normals Pass requests URP's normals prepass.")]
        private LoogaShadowNormalsSource _normalsSource;

        [SerializeField]
        [Tooltip("Replaces the opaque camera result with a selected clipmap diagnostic.")]
        private LoogaShadowDebugView _debugView;

        public LoogaShadowQuality Quality => _quality;
        public bool RenderSceneView => _renderSceneView;
        public float NearClipmapRadius => _nearClipmapRadius;
        public float ShadowDistance => _shadowDistance;
        public float DepthRange => _depthRange;
        public float SourceAngularDiameter => _sourceAngularDiameter;
        public float Softness => _softness;
        public float MaximumPenumbra => _maximumPenumbra;
        public float DepthBias => _depthBias;
        public float NormalBias => _normalBias;
        public float ClipmapBlend => _clipmapBlend;
        public LoogaShadowNormalsSource NormalsSource => _normalsSource;
        public LoogaShadowDebugView DebugView => _debugView;
        internal bool IsInitialized => _version >= CurrentVersion;

        public static LoogaShadowSettings Default => Create(
            LoogaShadowQuality.High,
            true,
            1f,
            300f,
            500f,
            0.53f,
            1f,
            3f,
            0.0012f,
            0.002f,
            0.12f,
            LoogaShadowNormalsSource.GBuffer,
            LoogaShadowDebugView.Off);

        internal static LoogaShadowSettings Create(
            LoogaShadowQuality quality,
            bool renderSceneView,
            float nearClipmapRadius,
            float shadowDistance,
            float depthRange,
            float sourceAngularDiameter,
            float softness,
            float maximumPenumbra,
            float depthBias,
            float normalBias,
            float clipmapBlend,
            LoogaShadowNormalsSource normalsSource,
            LoogaShadowDebugView debugView)
        {
            LoogaShadowSettings settings = new()
            {
                _version = CurrentVersion,
                _quality = quality,
                _renderSceneView = renderSceneView,
                _nearClipmapRadius = nearClipmapRadius,
                _shadowDistance = shadowDistance,
                _depthRange = depthRange,
                _sourceAngularDiameter = sourceAngularDiameter,
                _softness = softness,
                _maximumPenumbra = maximumPenumbra,
                _depthBias = depthBias,
                _normalBias = normalBias,
                _clipmapBlend = clipmapBlend,
                _normalsSource = normalsSource,
                _debugView = debugView
            };
            settings.Validate();
            return settings;
        }

        internal void EnsureInitialized()
        {
            if (_version <= 0)
            {
                this = Default;
                return;
            }

            if (_version < 2)
            {
                _normalsSource = LoogaShadowNormalsSource.GBuffer;
                _version = 2;
                Validate();
            }
        }

        internal void Validate()
        {
            _version = CurrentVersion;
            _nearClipmapRadius = Mathf.Max(1f, _nearClipmapRadius);
            _shadowDistance = Mathf.Max(_nearClipmapRadius * 2f, _shadowDistance);
            _depthRange = Mathf.Max(10f, _depthRange);
            _sourceAngularDiameter = Mathf.Clamp(_sourceAngularDiameter, 0.05f, 3f);
            _softness = Mathf.Clamp(_softness, 0f, 2f);
            _maximumPenumbra = Mathf.Clamp(_maximumPenumbra, 0.05f, 8f);
            _depthBias = Mathf.Clamp(_depthBias, 0f, 0.02f);
            _normalBias = Mathf.Clamp(_normalBias, 0f, 0.1f);
            _clipmapBlend = Mathf.Clamp(_clipmapBlend, 0f, 0.25f);
            if (!Enum.IsDefined(typeof(LoogaShadowNormalsSource), _normalsSource))
                _normalsSource = LoogaShadowNormalsSource.GBuffer;
            if (!Enum.IsDefined(typeof(LoogaShadowDebugView), _debugView))
                _debugView = LoogaShadowDebugView.Off;
        }
    }
}
