using UnityEngine;

namespace LoogaSoft.Shadows
{
    /// <summary>
    /// Supplies optional scene-authored overrides for a directional light. Renderer-feature settings
    /// remain authoritative when the active light does not reference an override profile.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    [AddComponentMenu("LoogaSoft/Shadows/Looga Shadow Light")]
    public sealed class LoogaShadowLight : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Optionally replaces the renderer feature settings while this light is URP's active main light.")]
        private LoogaShadowProfile _profileOverride;

        [SerializeField]
        [Tooltip("Overrides only the physical source angle without requiring another profile asset.")]
        private bool _overrideSourceAngularDiameter;

        [SerializeField]
        [Range(0.05f, 3f)]
        [Tooltip("Angular diameter of this directional light in degrees.")]
        private float _sourceAngularDiameter = 0.53f;

        private Light _light;

        public Light Light => ResolveLight();
        public LoogaShadowProfile ProfileOverride => _profileOverride;
        internal bool OverrideSourceAngularDiameter => _overrideSourceAngularDiameter;
        internal float SourceAngularDiameter => _sourceAngularDiameter;

        private void OnEnable()
        {
            LoogaShadowLightRegistry.Register(this);
        }

        private void OnDisable()
        {
            LoogaShadowLightRegistry.Unregister(this);
        }

        private void OnValidate()
        {
            _sourceAngularDiameter = Mathf.Clamp(_sourceAngularDiameter, 0.05f, 3f);
            if (isActiveAndEnabled)
                LoogaShadowLightRegistry.Register(this);
        }

        private Light ResolveLight()
        {
            if (_light == null)
                _light = GetComponent<Light>();

            return _light;
        }
    }
}
