using UnityEngine;

namespace LoogaSoft.Shadows
{
    internal readonly struct LoogaShadowResolvedSettings
    {
        public readonly LoogaShadowQuality Quality;
        public readonly bool RenderSceneView;
        public readonly float NearClipmapRadius;
        public readonly float ShadowDistance;
        public readonly float DepthRange;
        public readonly float SourceAngularDiameter;
        public readonly float Softness;
        public readonly float MaximumPenumbra;
        public readonly float DepthBias;
        public readonly float NormalBias;
        public readonly float ClipmapBlend;
        public readonly LoogaShadowDebugView DebugView;
        public readonly int AtlasResolution;
        public readonly int ClipmapCount;
        public readonly int BlockerSampleCount;
        public readonly int FilterSampleCount;
        public readonly string SettingsSource;

        private LoogaShadowResolvedSettings(
            LoogaShadowSettings settings,
            LoogaShadowLight shadowLight,
            string settingsSource)
        {
            Quality = settings.Quality;
            RenderSceneView = settings.RenderSceneView;
            NearClipmapRadius = settings.NearClipmapRadius;
            ShadowDistance = settings.ShadowDistance;
            DepthRange = settings.DepthRange;
            SourceAngularDiameter = settings.SourceAngularDiameter;
            Softness = settings.Softness;
            MaximumPenumbra = settings.MaximumPenumbra;
            DepthBias = settings.DepthBias;
            NormalBias = settings.NormalBias;
            ClipmapBlend = settings.ClipmapBlend;
            DebugView = settings.DebugView;
            SettingsSource = settingsSource;

            (AtlasResolution, ClipmapCount, BlockerSampleCount, FilterSampleCount) = Quality switch
            {
                LoogaShadowQuality.Low => (2048, 3, 8, 16),
                LoogaShadowQuality.Medium => (4096, 4, 10, 24),
                LoogaShadowQuality.Ultra => (8192, 4, 16, 48),
                _ => (4096, 4, 12, 32)
            };

            if (shadowLight != null && shadowLight.OverrideSourceAngularDiameter)
                SourceAngularDiameter = shadowLight.SourceAngularDiameter;
        }

        public int TileResolution => AtlasResolution / 2;

        public static LoogaShadowResolvedSettings Resolve(
            LoogaShadowSettings rendererSettings,
            LoogaShadowLight shadowLight)
        {
            rendererSettings.EnsureInitialized();
            LoogaShadowProfile lightProfile = shadowLight != null ? shadowLight.ProfileOverride : null;
            LoogaShadowSettings settings;
            string source;

            if (lightProfile != null)
            {
                settings = lightProfile.Settings;
                source = lightProfile.name + " (Light Override)";
            }
            else
            {
                settings = rendererSettings;
                source = "Renderer Feature";
            }

            settings.Validate();
            return new LoogaShadowResolvedSettings(settings, shadowLight, source);
        }
    }
}
