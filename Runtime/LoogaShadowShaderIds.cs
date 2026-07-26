using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Shadows
{
    internal static class LoogaShadowShaderIds
    {
        public static readonly int MainLightShadowTexture = Shader.PropertyToID("_LoogaMainLightShadowTexture");
        public static readonly int DebugFinalTexture = Shader.PropertyToID("_LoogaDebugFinalTexture");
        public static readonly int DebugRawTexture = Shader.PropertyToID("_LoogaDebugRawTexture");
        public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        public static readonly int CameraNormalsTexture = Shader.PropertyToID("_CameraNormalsTexture");
        public static readonly int VirtualShadowAtlas = Shader.PropertyToID("_LoogaVirtualShadowAtlas");
        public static readonly int VirtualShadowDepthAtlas = Shader.PropertyToID("_LoogaVirtualShadowDepthAtlas");
        public static readonly int[] VirtualShadowClipmaps =
        {
            Shader.PropertyToID("_LoogaVirtualShadowClipmap0"),
            Shader.PropertyToID("_LoogaVirtualShadowClipmap1"),
            Shader.PropertyToID("_LoogaVirtualShadowClipmap2"),
            Shader.PropertyToID("_LoogaVirtualShadowClipmap3")
        };
        public static readonly int[] VirtualShadowDepthClipmaps =
        {
            Shader.PropertyToID("_LoogaVirtualShadowDepthClipmap0"),
            Shader.PropertyToID("_LoogaVirtualShadowDepthClipmap1"),
            Shader.PropertyToID("_LoogaVirtualShadowDepthClipmap2"),
            Shader.PropertyToID("_LoogaVirtualShadowDepthClipmap3")
        };
        public static readonly int UrpScreenSpaceShadowTexture = Shader.PropertyToID("_ScreenSpaceShadowmapTexture");
        public static readonly int ShadowsEnabled = Shader.PropertyToID("_LoogaShadowsEnabled");
        public static readonly int WorldToShadow = Shader.PropertyToID("_LoogaWorldToShadow");
        public static readonly int ClipmapCenters = Shader.PropertyToID("_LoogaClipmapCenters");
        public static readonly int ClipmapRadii = Shader.PropertyToID("_LoogaClipmapRadii");
        public static readonly int ClipmapRects = Shader.PropertyToID("_LoogaClipmapRects");
        public static readonly int ClipmapCount = Shader.PropertyToID("_LoogaClipmapCount");
        public static readonly int AtlasSize = Shader.PropertyToID("_LoogaVirtualShadowAtlasSize");
        public static readonly int LightDirection = Shader.PropertyToID("_LoogaShadowLightDirection");
        public static readonly int SampleCounts = Shader.PropertyToID("_LoogaShadowSampleCounts");
        public static readonly int SoftShadowData = Shader.PropertyToID("_LoogaSoftShadowData");
        public static readonly int BiasData = Shader.PropertyToID("_LoogaShadowBiasData");
        public static readonly int DistanceData = Shader.PropertyToID("_LoogaShadowDistanceData");
        public static readonly int DenoiseDirection = Shader.PropertyToID("_LoogaDenoiseDirection");
        public static readonly int BlueNoiseTexture = Shader.PropertyToID("_LoogaBlueNoiseTexture");
        public static readonly int BlueNoiseAvailable = Shader.PropertyToID("_LoogaBlueNoiseAvailable");

        public static readonly GlobalKeyword MainLightShadows = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS");
        public static readonly GlobalKeyword MainLightShadowCascades = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_CASCADE");
        public static readonly GlobalKeyword MainLightShadowScreen = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_SCREEN");
        public static readonly GlobalKeyword CastingPunctualLightShadow = GlobalKeyword.Create("_CASTING_PUNCTUAL_LIGHT_SHADOW");
    }
}
