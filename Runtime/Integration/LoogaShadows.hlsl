#ifndef LOOGA_SHADOWS_INCLUDED
#define LOOGA_SHADOWS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D_X(_LoogaMainLightShadowTexture);
#ifndef LOOGA_SHADOW_GLOBALS_DECLARED
#define LOOGA_SHADOW_GLOBALS_DECLARED
float _LoogaShadowsEnabled;
#endif

half LoogaSampleMainLightShadow(float2 normalizedScreenUV)
{
    if (_LoogaShadowsEnabled < 0.5)
        return 1.0h;

    return SAMPLE_TEXTURE2D_X(
        _LoogaMainLightShadowTexture,
        sampler_PointClamp,
        UnityStereoTransformScreenSpaceTex(normalizedScreenUV)).r;
}

#endif
