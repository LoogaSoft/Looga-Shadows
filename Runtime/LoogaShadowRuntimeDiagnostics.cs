using System;

namespace LoogaSoft.Shadows
{
    /// <summary>
    /// Read-only information about the latest package-owned directional shadow render.
    /// </summary>
    public static class LoogaShadowRuntimeDiagnostics
    {
        public static bool IsRendering { get; private set; }
        public static int OutputWidth { get; private set; }
        public static int OutputHeight { get; private set; }
        public static int AtlasResolution { get; private set; }
        public static int ClipmapCount { get; private set; }
        public static int LastRenderedFrame { get; private set; } = -1;
        public static string LastCameraName { get; private set; } = string.Empty;
        public static string MainLightName { get; private set; } = string.Empty;
        public static string SettingsSource { get; private set; } = string.Empty;
        [Obsolete("Use SettingsSource. Profiles are now only optional per-light overrides.")]
        public static string ProfileSource => SettingsSource;
        public static LoogaShadowDebugView DebugView { get; private set; }

        internal static void RecordCamera(
            string cameraName,
            int width,
            int height,
            int atlasResolution,
            int clipmapCount,
            LoogaShadowDebugView debugView,
            string mainLightName,
            string settingsSource)
        {
            IsRendering = true;
            OutputWidth = width;
            OutputHeight = height;
            AtlasResolution = atlasResolution;
            ClipmapCount = clipmapCount;
            LastRenderedFrame = UnityEngine.Time.frameCount;
            LastCameraName = cameraName ?? string.Empty;
            MainLightName = mainLightName ?? string.Empty;
            SettingsSource = settingsSource ?? string.Empty;
            DebugView = debugView;
        }

        internal static void Reset()
        {
            IsRendering = false;
            OutputWidth = 0;
            OutputHeight = 0;
            AtlasResolution = 0;
            ClipmapCount = 0;
            LastRenderedFrame = -1;
            LastCameraName = string.Empty;
            MainLightName = string.Empty;
            SettingsSource = string.Empty;
            DebugView = LoogaShadowDebugView.Off;
        }
    }
}
