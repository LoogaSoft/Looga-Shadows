Shader "Hidden/LoogaSoft/Shadows/VirtualShadowResolve"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

        TEXTURE2D_SHADOW(_LoogaVirtualShadowAtlas);
        SAMPLER_CMP(sampler_LoogaVirtualShadowAtlas);
        TEXTURE2D(_LoogaVirtualShadowDepthAtlas);
        SAMPLER(sampler_LoogaVirtualShadowDepthAtlas);
        TEXTURE2D(_LoogaBlueNoiseTexture);
        TEXTURE2D_X(_LoogaDebugFinalTexture);
        TEXTURE2D_X(_LoogaDebugRawTexture);

        float4x4 _LoogaWorldToShadow[4];
        float4 _LoogaClipmapCenters[4];
        float4 _LoogaClipmapRadii[4];
        float4 _LoogaVirtualShadowAtlasSize;
        float4 _LoogaShadowLightDirection;
        float4 _LoogaShadowSampleCounts;
        float4 _LoogaSoftShadowData;
        float4 _LoogaShadowBiasData;
        float4 _LoogaShadowDistanceData;
        float4 _LoogaDenoiseDirection;
        float _LoogaBlueNoiseAvailable;
        int _LoogaNormalsSource;
        int _LoogaClipmapCount;

        #define LOOGA_MAX_BLOCKER_SAMPLES 16
        #define LOOGA_MAX_FILTER_SAMPLES 48
        #define LOOGA_GOLDEN_ANGLE 2.39996323
        #define LOOGA_PI 3.14159265

        struct LoogaShadowEvaluation
        {
            float visibility;
            float rawVisibility;
            float penumbra;
            float clipmap;
        };

        bool LoogaIsSky(float deviceDepth)
        {
        #if UNITY_REVERSED_Z
            return deviceDepth <= 0.000001;
        #else
            return deviceDepth >= 0.999999;
        #endif
        }

        float3 LoogaReconstructWorldPosition(float2 uv, float deviceDepth)
        {
            return ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
        }

        float3 LoogaNormalFromPositionDerivatives(
            float3 positionWS,
            float3 positionDerivativeX,
            float3 positionDerivativeY)
        {
            float3 normalWS = cross(positionDerivativeX, positionDerivativeY);
            float normalLengthSquared = dot(normalWS, normalWS);
            if (normalLengthSquared <= 0.0000000001)
                return float3(0.0, 1.0, 0.0);

            normalWS *= rsqrt(normalLengthSquared);
            float3 viewDirectionWS = normalize(_WorldSpaceCameraPos - positionWS);
            return dot(normalWS, viewDirectionWS) < 0.0
                ? -normalWS
                : normalWS;
        }

        float3 LoogaReconstructNormalFromDepth(float3 positionWS)
        {
            return LoogaNormalFromPositionDerivatives(
                positionWS,
                ddx(positionWS),
                ddy(positionWS));
        }

        float3 LoogaResolveSurfaceNormal(float2 uv, float3 positionWS)
        {
            if (_LoogaNormalsSource == 1)
                return LoogaReconstructNormalFromDepth(positionWS);

            return normalize(SampleSceneNormals(uv));
        }

        float2 LoogaTileOrigin(int level)
        {
            return float2(level & 1, level >> 1) * 0.5;
        }

        float2 LoogaAtlasToLocalUv(float2 atlasUv, int level)
        {
            return (atlasUv - LoogaTileOrigin(level)) * 2.0;
        }

        float2 LoogaLocalToAtlasUv(float2 localUv, int level)
        {
            return LoogaTileOrigin(level) + localUv * 0.5;
        }

        float2 LoogaClampToTile(float2 atlasUv, int level)
        {
            float guard = _LoogaVirtualShadowAtlasSize.y * 1.5;
            float2 tileMin = LoogaTileOrigin(level) + guard;
            float2 tileMax = LoogaTileOrigin(level) + 0.5 - guard;
            return clamp(atlasUv, tileMin, tileMax);
        }

        float4 LoogaGetShadowCoordinate(float3 positionWS, int level)
        {
            return mul(_LoogaWorldToShadow[level], float4(positionWS, 1.0));
        }

        bool LoogaContainsPosition(float4 shadowCoord, int level)
        {
            float2 localUv = LoogaAtlasToLocalUv(shadowCoord.xy, level);
            float guard = _LoogaVirtualShadowAtlasSize.w * 1.5;
            return all(localUv >= guard) &&
                all(localUv <= 1.0 - guard) &&
                shadowCoord.z > 0.0 && shadowCoord.z < 1.0;
        }

        int LoogaFindClipmap(float3 positionWS)
        {
            [unroll]
            for (int level = 0; level < 4; level++)
            {
                if (level >= _LoogaClipmapCount)
                    break;

                if (LoogaContainsPosition(LoogaGetShadowCoordinate(positionWS, level), level))
                    return level;
            }

            return -1;
        }

        float LoogaSampleComparisonDepth(
            float2 uv,
            float comparisonDepth)
        {
            return SAMPLE_TEXTURE2D_SHADOW(
                _LoogaVirtualShadowAtlas,
                sampler_LoogaVirtualShadowAtlas,
                float3(uv, comparisonDepth));
        }

        float LoogaSampleComparison(
            float2 uv,
            float receiverDepth,
            float receiverBiasWorld)
        {
            float normalizedBias =
                receiverBiasWorld / max(_LoogaShadowBiasData.z, 0.00001);
        #if UNITY_REVERSED_Z
            float comparisonDepth = receiverDepth + normalizedBias;
        #else
            float comparisonDepth = receiverDepth - normalizedBias;
        #endif
            return LoogaSampleComparisonDepth(
                uv,
                comparisonDepth);
        }

        float LoogaSampleRawDepth(float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(
                _LoogaVirtualShadowDepthAtlas,
                sampler_LoogaVirtualShadowDepthAtlas,
                uv,
                0.0).r;
        }

        bool LoogaIsBlocker(
            float storedDepth,
            float receiverDepth,
            float receiverBiasWorld,
            float additionalNormalizedBias)
        {
            float normalizedBias =
                receiverBiasWorld / max(_LoogaShadowBiasData.z, 0.00001) +
                additionalNormalizedBias;
        #if UNITY_REVERSED_Z
            return storedDepth > receiverDepth + normalizedBias;
        #else
            return storedDepth < receiverDepth - normalizedBias;
        #endif
        }

        float LoogaBlockerDistance(float storedDepth, float receiverDepth)
        {
            return abs(storedDepth - receiverDepth) * _LoogaShadowBiasData.z;
        }

        float2 LoogaReceiverDepthGradient(
            float3 positionDerivativeX,
            float3 positionDerivativeY,
            int level)
        {
            // Evaluate screen derivatives before dynamic clipmap selection, then
            // transform vectors (w = 0) into the selected clipmap. Derivatives
            // evaluated inside divergent level branches create moving seams.
            float3 shadowDerivativeX = mul(
                _LoogaWorldToShadow[level],
                float4(positionDerivativeX, 0.0)).xyz;
            float3 shadowDerivativeY = mul(
                _LoogaWorldToShadow[level],
                float4(positionDerivativeY, 0.0)).xyz;
            float determinant =
                shadowDerivativeX.x * shadowDerivativeY.y -
                shadowDerivativeX.y * shadowDerivativeY.x;
            if (abs(determinant) <= 0.0000000001)
                return 0.0;

            return float2(
                shadowDerivativeX.z * shadowDerivativeY.y -
                    shadowDerivativeX.y * shadowDerivativeY.z,
                shadowDerivativeX.x * shadowDerivativeY.z -
                    shadowDerivativeX.z * shadowDerivativeY.x) / determinant;
        }

        float2 LoogaDiskSample(int sampleIndex, int sampleCount)
        {
            float radius = sqrt((sampleIndex + 0.5) / max((float)sampleCount, 1.0));
            float angle = sampleIndex * LOOGA_GOLDEN_ANGLE;
            return float2(cos(angle), sin(angle)) * radius;
        }

        float2 LoogaRotateSample(float2 samplePosition, float angle)
        {
            float sineAngle = sin(angle);
            float cosineAngle = cos(angle);
            return float2(
                samplePosition.x * cosineAngle - samplePosition.y * sineAngle,
                samplePosition.x * sineAngle + samplePosition.y * cosineAngle);
        }

        float LoogaHashNoise(int2 coordinate)
        {
            uint2 coordinateBits = asuint(coordinate);
            uint hash = coordinateBits.x * 0x9E3779B9u +
                coordinateBits.y * 0x85EBCA6Bu +
                0xC2B2AE35u;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) * (1.0 / 16777216.0);
        }

        float LoogaReceiverNoise(float2 screenUv)
        {
            int2 pixel = (int2)floor(screenUv * _ScreenParams.xy);
            if (_LoogaBlueNoiseAvailable > 0.5)
            {
                uint2 blueNoiseTexel = asuint(pixel) & 63u;
                return LOAD_TEXTURE2D_LOD(
                    _LoogaBlueNoiseTexture,
                    blueNoiseTexel,
                    0).a;
            }

            return LoogaHashNoise(pixel);
        }

        float LoogaRawVisibility(
            float4 shadowCoord,
            float receiverBiasWorld,
            int level)
        {
            return LoogaSampleComparison(
                shadowCoord.xy,
                shadowCoord.z,
                receiverBiasWorld);
        }

        float LoogaFindAverageBlockerDistance(
            float4 shadowCoord,
            int level,
            float sourceSlope,
            float sampleRotation,
            float2 receiverDepthGradient,
            float receiverBiasWorld,
            out float blockerConfidence,
            out float nearBlockerDistance,
            out float farBlockerDistance,
            out float blockerLayerConfidence)
        {
            int blockerSampleCount = clamp(
                (int)_LoogaShadowSampleCounts.x,
                1,
                LOOGA_MAX_BLOCKER_SAMPLES);
            float worldTexel = _LoogaClipmapRadii[level].y;
            float atlasUvPerWorldUnit = _LoogaVirtualShadowAtlasSize.w * 0.5 /
                max(worldTexel, 0.00001);
            float maximumSearchWorld = min(
                _LoogaSoftShadowData.z,
                _LoogaClipmapRadii[level].x * 0.45);
            float blockerDistanceSum = 0.0;
            float blockerWeightSum = 0.0;
            float unweightedBlockerDistanceSum = 0.0;
            float unweightedBlockerDistanceSquaredSum = 0.0;
            float unweightedBlockerCount = 0.0;
            float minimumBlockerDistance = 1e20;
            float maximumBlockerDistance = 0.0;
            [loop]
            for (int sampleIndex = 0; sampleIndex < LOOGA_MAX_BLOCKER_SAMPLES; sampleIndex++)
            {
                if (sampleIndex >= blockerSampleCount)
                    break;

                float2 diskPosition = 0.0;
                float radialDistance = 0.0;
                if (sampleIndex > 0)
                {
                    float2 uniformDisk = LoogaDiskSample(
                        sampleIndex - 1,
                        max(blockerSampleCount - 1, 1));
                    float uniformRadius = length(uniformDisk);
                    float concentratedRadius =
                        uniformRadius * uniformRadius *
                        uniformRadius * uniformRadius;
                    diskPosition = LoogaRotateSample(
                        normalize(uniformDisk) * concentratedRadius,
                        sampleRotation);
                    radialDistance = concentratedRadius * maximumSearchWorld;
                }

                float2 sampleUv = shadowCoord.xy +
                    diskPosition * maximumSearchWorld * atlasUvPerWorldUnit;
                sampleUv = LoogaClampToTile(sampleUv, level);
                float receiverDepthOffset = dot(
                    receiverDepthGradient,
                    sampleUv - shadowCoord.xy);
                float candidateBlockerDistance = max(
                    radialDistance / max(sourceSlope, 0.000001),
                    worldTexel);
                float maximumDepthOffset =
                    candidateBlockerDistance * 0.25 /
                    max(_LoogaShadowBiasData.z, 0.00001);
                receiverDepthOffset = clamp(
                    receiverDepthOffset,
                    -maximumDepthOffset,
                    maximumDepthOffset);
                float receiverDepth =
                    shadowCoord.z + receiverDepthOffset;
                float storedDepth = LoogaSampleRawDepth(sampleUv);
                float rawTexelHalfExtentUv =
                    worldTexel * atlasUvPerWorldUnit * 0.5;
                float rawDepthPlaneBias =
                    dot(abs(receiverDepthGradient), rawTexelHalfExtentUv.xx) +
                    (0.5 / 65535.0);
                bool isBlocker = LoogaIsBlocker(
                    storedDepth,
                    receiverDepth,
                    receiverBiasWorld,
                    rawDepthPlaneBias);
                if (!isBlocker)
                    continue;

                float blockerDistance = LoogaBlockerDistance(
                    storedDepth,
                    receiverDepth);
                unweightedBlockerDistanceSum += blockerDistance;
                unweightedBlockerDistanceSquaredSum +=
                    blockerDistance * blockerDistance;
                unweightedBlockerCount += 1.0;
                minimumBlockerDistance = min(
                    minimumBlockerDistance,
                    blockerDistance);
                maximumBlockerDistance = max(
                    maximumBlockerDistance,
                    blockerDistance);
                float coneRadius = max(
                    blockerDistance * sourceSlope,
                    worldTexel);
                float coneRatio = radialDistance / coneRadius;
                float coneWeight = exp2(
                    -1.5 * coneRatio * coneRatio);
                blockerDistanceSum += blockerDistance * coneWeight;
                blockerWeightSum += coneWeight;
            }

            blockerConfidence =
                unweightedBlockerCount > 0.0001 ? 1.0 : 0.0;
            float unweightedBlockerDistance =
                unweightedBlockerDistanceSum /
                max(unweightedBlockerCount, 0.0001);
            float coneBlockerDistance =
                blockerDistanceSum /
                max(blockerWeightSum, 0.0001);
            float coneBlend = smoothstep(
                0.0,
                1.0,
                saturate(blockerWeightSum));
            float averageBlockerDistance = lerp(
                unweightedBlockerDistance,
                coneBlockerDistance,
                coneBlend);
            float blockerVariance = max(
                unweightedBlockerDistanceSquaredSum /
                    max(unweightedBlockerCount, 0.0001) -
                    unweightedBlockerDistance *
                    unweightedBlockerDistance,
                0.0);
            float blockerDeviation = sqrt(blockerVariance);
            float blockerSpan = max(
                maximumBlockerDistance - minimumBlockerDistance,
                0.0);
            float distanceScale = max(
                unweightedBlockerDistance,
                worldTexel * 8.0);
            float absoluteSeparationThreshold = max(
                worldTexel * 6.0,
                0.01);
            float spanConfidence = smoothstep(
                absoluteSeparationThreshold,
                absoluteSeparationThreshold * 3.0,
                blockerSpan);
            float relativeSpanConfidence = smoothstep(
                0.2,
                0.75,
                blockerSpan / distanceScale);
            float deviationConfidence = smoothstep(
                0.08,
                0.3,
                blockerDeviation / distanceScale);
            float supportConfidence = saturate(
                (unweightedBlockerCount - 1.0) / 3.0);
            blockerLayerConfidence =
                spanConfidence *
                relativeSpanConfidence *
                deviationConfidence *
                supportConfidence;
            nearBlockerDistance = lerp(
                averageBlockerDistance,
                minimumBlockerDistance,
                blockerLayerConfidence);
            farBlockerDistance = lerp(
                averageBlockerDistance,
                maximumBlockerDistance,
                blockerLayerConfidence);
            return averageBlockerDistance;
        }

        float LoogaFilterPCSS(
            float4 shadowCoord,
            int level,
            float penumbraWorld,
            float blockerDistanceWorld,
            float sampleRotation,
            float2 receiverDepthGradient,
            float receiverBiasWorld)
        {
            float worldTexel = _LoogaClipmapRadii[level].y;
            float filterRadiusWorld = penumbraWorld;
            if (filterRadiusWorld <= worldTexel * 0.5)
                return LoogaRawVisibility(
                    shadowCoord,
                    receiverBiasWorld,
                    level);

            int filterSampleCount = clamp(
                (int)_LoogaShadowSampleCounts.y,
                1,
                LOOGA_MAX_FILTER_SAMPLES);
            float atlasUvPerWorldUnit = _LoogaVirtualShadowAtlasSize.w * 0.5 /
                max(worldTexel, 0.00001);
            float visibility = 0.0;
            float weightSum = 0.0;
            [loop]
            for (int sampleIndex = 0; sampleIndex < LOOGA_MAX_FILTER_SAMPLES; sampleIndex++)
            {
                if (sampleIndex >= filterSampleCount)
                    break;

                float2 diskPosition = LoogaDiskSample(
                    sampleIndex,
                    filterSampleCount);
                diskPosition = LoogaRotateSample(diskPosition, sampleRotation);
                float2 sampleUv = shadowCoord.xy +
                    diskPosition * filterRadiusWorld * atlasUvPerWorldUnit;
                float2 localUv = LoogaAtlasToLocalUv(sampleUv, level);
                float guard = _LoogaVirtualShadowAtlasSize.w * 1.5;
                if (any(localUv < guard) || any(localUv > 1.0 - guard))
                    continue;

                float receiverDepthOffset = dot(
                    receiverDepthGradient,
                    sampleUv - shadowCoord.xy);
                float maximumDepthOffset =
                    blockerDistanceWorld * 0.25 /
                    max(_LoogaShadowBiasData.z, 0.00001);
                receiverDepthOffset = clamp(
                    receiverDepthOffset,
                    -maximumDepthOffset,
                    maximumDepthOffset);
                float receiverDepth =
                    shadowCoord.z + receiverDepthOffset;
                visibility += LoogaSampleComparison(
                    sampleUv,
                    receiverDepth,
                    receiverBiasWorld);
                weightSum += 1.0;
            }

            return visibility / max(weightSum, 0.0001);
        }

        LoogaShadowEvaluation LoogaEvaluateLevel(
            float3 positionWS,
            int level,
            float sampleRotation,
            float3 positionDerivativeX,
            float3 positionDerivativeY)
        {
            LoogaShadowEvaluation result;
            float4 shadowCoord = LoogaGetShadowCoordinate(positionWS, level);
            float2 receiverDepthGradient =
                LoogaReceiverDepthGradient(
                    positionDerivativeX,
                    positionDerivativeY,
                    level);
            float worldTexel = _LoogaClipmapRadii[level].y;
            // Receiver-plane depth gradients account for slope at every PCSS
            // tap. A second slope-scaled comparison bias detaches shadows from
            // contacting geometry, so retain only a sub-texel precision floor.
            float receiverBiasWorld = max(
                _LoogaShadowBiasData.x,
                worldTexel * 0.0625);
            result.rawVisibility = LoogaRawVisibility(
                shadowCoord,
                receiverBiasWorld,
                level);
            result.clipmap = level;
            float sourceSlope = tan(radians(_LoogaSoftShadowData.x) * 0.5) *
                _LoogaSoftShadowData.y;
            if (sourceSlope <= 0.000001)
            {
                result.penumbra = 0.0;
                result.visibility = result.rawVisibility;
                return result;
            }

            float blockerConfidence;
            float nearBlockerDistance;
            float farBlockerDistance;
            float blockerLayerConfidence;
            float blockerRotation = sampleRotation;
            float blockerDistance = LoogaFindAverageBlockerDistance(
                shadowCoord,
                level,
                sourceSlope,
                blockerRotation,
                receiverDepthGradient,
                receiverBiasWorld,
                blockerConfidence,
                nearBlockerDistance,
                farBlockerDistance,
                blockerLayerConfidence);
            if (blockerConfidence <= 0.0)
            {
                result.penumbra = 0.0;
                result.visibility = 1.0;
                return result;
            }

            result.penumbra = min(
                blockerDistance * sourceSlope,
                _LoogaSoftShadowData.z);
            result.visibility = result.rawVisibility;
            return result;
        }

        float LoogaClipmapEdgeBlend(
            float4 shadowCoord,
            int level)
        {
            if (level + 1 >= _LoogaClipmapCount)
                return 0.0;

            float2 localUv = LoogaAtlasToLocalUv(shadowCoord.xy, level);
            float edgeDistance = min(min(localUv.x, localUv.y), min(1.0 - localUv.x, 1.0 - localUv.y));
            float radius = _LoogaClipmapRadii[level].x;
            float blockerSearchWorld = min(
                _LoogaSoftShadowData.z,
                radius * 0.45);
            // The blocker pass must use a position-only handoff. Feeding its
            // estimated penumbra back into level selection makes the estimate
            // and transition weight depend on one another, which produces a
            // camera-relative outline after denoising.
            float kernelMargin =
                blockerSearchWorld / max(radius * 2.0, 0.00001);
            kernelMargin += _LoogaVirtualShadowAtlasSize.w * 1.5;
            if (kernelMargin >= 0.5)
                return 1.0;

            float blendWidth = max(_LoogaSoftShadowData.w, 0.0001);
            float edgeBlend = 1.0 - smoothstep(
                kernelMargin,
                min(kernelMargin + blendWidth, 0.5),
                edgeDistance);
            return edgeBlend;
        }

        float LoogaClipmapFootprintBlend(
            int level,
            float penumbraWorld)
        {
            float radius = _LoogaClipmapRadii[level].x;
            // Filtering happens after the blocker radius has been denoised, so
            // this handoff is stable and shared by every filter sample.
            return smoothstep(
                0.10,
                0.20,
                penumbraWorld / max(radius, 0.00001));
        }

        LoogaShadowEvaluation LoogaEvaluateShadow(
            float3 positionWS,
            float3 biasNormalWS,
            float sampleRotation,
            float3 positionDerivativeX,
            float3 positionDerivativeY)
        {
            LoogaShadowEvaluation lit = (LoogaShadowEvaluation)0;
            lit.visibility = 1.0;
            lit.rawVisibility = 1.0;
            lit.clipmap = -1.0;

            float3 biasedPosition =
                positionWS + biasNormalWS * _LoogaShadowBiasData.y;
            int level = LoogaFindClipmap(biasedPosition);
            if (level < 0)
                return lit;

            LoogaShadowEvaluation result = LoogaEvaluateLevel(
                biasedPosition,
                level,
                sampleRotation,
                positionDerivativeX,
                positionDerivativeY);
            [unroll]
            for (int transition = 0; transition < 3; transition++)
            {
                if (level + 1 >= _LoogaClipmapCount)
                    break;

                float edgeBlend = LoogaClipmapEdgeBlend(
                    LoogaGetShadowCoordinate(biasedPosition, level),
                    level);
                if (edgeBlend <= 0.0)
                    break;

                LoogaShadowEvaluation next = LoogaEvaluateLevel(
                    biasedPosition,
                    level + 1,
                    sampleRotation,
                    positionDerivativeX,
                    positionDerivativeY);
                result.visibility = lerp(result.visibility, next.visibility, edgeBlend);
                result.rawVisibility = lerp(result.rawVisibility, next.rawVisibility, edgeBlend);
                result.penumbra = lerp(result.penumbra, next.penumbra, edgeBlend);
                level++;
                if (edgeBlend < 0.999)
                    break;
            }

            float cameraDistance = distance(positionWS, _WorldSpaceCameraPos);
            float distanceFade = smoothstep(
                _LoogaShadowDistanceData.y,
                _LoogaShadowDistanceData.x,
                cameraDistance);
            result.visibility = lerp(result.visibility, 1.0, distanceFade);
            result.rawVisibility = lerp(result.rawVisibility, 1.0, distanceFade);
            return result;
        }

        LoogaShadowEvaluation LoogaFilterLevel(
            float3 positionWS,
            int level,
            float penumbraWorld,
            float sampleRotation,
            float3 positionDerivativeX,
            float3 positionDerivativeY)
        {
            LoogaShadowEvaluation result;
            float4 shadowCoord = LoogaGetShadowCoordinate(positionWS, level);
            float2 receiverDepthGradient =
                LoogaReceiverDepthGradient(
                    positionDerivativeX,
                    positionDerivativeY,
                    level);
            float worldTexel = _LoogaClipmapRadii[level].y;
            float receiverBiasWorld = max(
                _LoogaShadowBiasData.x,
                worldTexel * 0.0625);
            result.rawVisibility = LoogaRawVisibility(
                shadowCoord,
                receiverBiasWorld,
                level);
            result.clipmap = level;
            result.penumbra = penumbraWorld;
            float sourceSlope = tan(radians(_LoogaSoftShadowData.x) * 0.5) *
                _LoogaSoftShadowData.y;
            if (sourceSlope <= 0.000001 || penumbraWorld <= 0.000001)
            {
                result.visibility = result.rawVisibility;
                return result;
            }

            float blockerDistance = penumbraWorld / sourceSlope;
            result.visibility = LoogaFilterPCSS(
                shadowCoord,
                level,
                penumbraWorld,
                blockerDistance,
                sampleRotation,
                receiverDepthGradient,
                receiverBiasWorld);
            return result;
        }

        LoogaShadowEvaluation LoogaFilterShadow(
            float3 positionWS,
            float3 biasNormalWS,
            float penumbraWorld,
            float sampleRotation,
            float3 positionDerivativeX,
            float3 positionDerivativeY)
        {
            LoogaShadowEvaluation lit = (LoogaShadowEvaluation)0;
            lit.visibility = 1.0;
            lit.rawVisibility = 1.0;
            lit.clipmap = -1.0;

            float3 biasedPosition =
                positionWS + biasNormalWS * _LoogaShadowBiasData.y;
            int level = LoogaFindClipmap(biasedPosition);
            if (level < 0)
                return lit;

            LoogaShadowEvaluation result = LoogaFilterLevel(
                biasedPosition,
                level,
                penumbraWorld,
                sampleRotation,
                positionDerivativeX,
                positionDerivativeY);
            [unroll]
            for (int transition = 0; transition < 3; transition++)
            {
                if (level + 1 >= _LoogaClipmapCount)
                    break;

                float edgeBlend = LoogaClipmapEdgeBlend(
                    LoogaGetShadowCoordinate(biasedPosition, level),
                    level);
                edgeBlend = max(
                    edgeBlend,
                    LoogaClipmapFootprintBlend(level, penumbraWorld));
                if (edgeBlend <= 0.0)
                    break;

                LoogaShadowEvaluation next = LoogaFilterLevel(
                    biasedPosition,
                    level + 1,
                    penumbraWorld,
                    sampleRotation,
                    positionDerivativeX,
                    positionDerivativeY);
                result.visibility = lerp(
                    result.visibility,
                    next.visibility,
                    edgeBlend);
                result.rawVisibility = lerp(
                    result.rawVisibility,
                    next.rawVisibility,
                    edgeBlend);
                level++;
                if (edgeBlend < 0.999)
                    break;
            }

            float cameraDistance = distance(positionWS, _WorldSpaceCameraPos);
            float distanceFade = smoothstep(
                _LoogaShadowDistanceData.y,
                _LoogaShadowDistanceData.x,
                cameraDistance);
            result.visibility = lerp(
                result.visibility,
                1.0,
                distanceFade);
            result.rawVisibility = lerp(
                result.rawVisibility,
                1.0,
                distanceFade);
            return result;
        }

        LoogaShadowEvaluation LoogaEvaluateScreen(float2 uv, out float3 positionWS, out float3 normalWS, out float deviceDepth)
        {
            deviceDepth = SampleSceneDepth(uv);
            positionWS = LoogaReconstructWorldPosition(uv, deviceDepth);
            float3 positionDerivativeX = ddx(positionWS);
            float3 positionDerivativeY = ddy(positionWS);
            normalWS = LoogaResolveSurfaceNormal(uv, positionWS);
            float3 biasNormalWS = LoogaNormalFromPositionDerivatives(
                positionWS,
                positionDerivativeX,
                positionDerivativeY);
            if (LoogaIsSky(deviceDepth))
            {
                LoogaShadowEvaluation lit = (LoogaShadowEvaluation)0;
                lit.visibility = 1.0;
                lit.rawVisibility = 1.0;
                lit.clipmap = -1.0;
                return lit;
            }

            float sampleRotation = LoogaReceiverNoise(uv) * (2.0 * LOOGA_PI);
            return LoogaEvaluateShadow(
                positionWS,
                biasNormalWS,
                sampleRotation,
                positionDerivativeX,
                positionDerivativeY);
        }

        half4 FragResolve(Varyings input) : SV_Target
        {
            float3 positionWS;
            float3 normalWS;
            float deviceDepth;
            LoogaShadowEvaluation shadow = LoogaEvaluateScreen(input.texcoord, positionWS, normalWS, deviceDepth);
            return half4(shadow.visibility, shadow.penumbra, 0.0, 1.0);
        }

        half4 FragRefilter(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            float deviceDepth = SampleSceneDepth(uv);
            float3 positionWS = LoogaReconstructWorldPosition(uv, deviceDepth);
            float3 positionDerivativeX = ddx(positionWS);
            float3 positionDerivativeY = ddy(positionWS);
            if (LoogaIsSky(deviceDepth))
                return half4(1.0, 0.0, 0.0, 1.0);

            float3 biasNormalWS = LoogaNormalFromPositionDerivatives(
                positionWS,
                positionDerivativeX,
                positionDerivativeY);
            float penumbraWorld = SAMPLE_TEXTURE2D_X(
                _BlitTexture,
                sampler_PointClamp,
                uv).g;
            float sampleRotation =
                LoogaReceiverNoise(uv) * (2.0 * LOOGA_PI);
            LoogaShadowEvaluation shadow = LoogaFilterShadow(
                positionWS,
                biasNormalWS,
                penumbraWorld,
                sampleRotation,
                positionDerivativeX,
                positionDerivativeY);
            return half4(
                shadow.visibility,
                penumbraWorld,
                0.0,
                1.0);
        }

        half4 FragDenoise(Varyings input) : SV_Target
        {
            float2 centerUv = input.texcoord;
            float centerDeviceDepth = SampleSceneDepth(centerUv);
            float2 centerShadow = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, centerUv).rg;
            if (LoogaIsSky(centerDeviceDepth))
                return half4(centerShadow, 0.0, 1.0);

            float3 centerPosition = LoogaReconstructWorldPosition(centerUv, centerDeviceDepth);
            float3 centerNormal = LoogaResolveSurfaceNormal(
                centerUv,
                centerPosition);
            // Reconstruct the pixel footprint at the center depth. Screen-space
            // derivatives span both surfaces at silhouettes and would otherwise
            // relax the bilateral depth rejection exactly where it must be strict.
            float3 footprintPositionX = LoogaReconstructWorldPosition(
                saturate(centerUv + float2(_CameraDepthTexture_TexelSize.x, 0.0)),
                centerDeviceDepth);
            float3 footprintPositionY = LoogaReconstructWorldPosition(
                saturate(centerUv + float2(0.0, _CameraDepthTexture_TexelSize.y)),
                centerDeviceDepth);
            float receiverFootprint = max(
                length(footprintPositionX - centerPosition),
                length(footprintPositionY - centerPosition));
            float2 direction = _LoogaDenoiseDirection.xy * _BlitTexture_TexelSize.xy;
            float denoiseStridePixels = max(
                abs(_LoogaDenoiseDirection.x) +
                abs(_LoogaDenoiseDirection.y),
                1.0);
            float visibility = 0.0;
            float penumbra = 0.0;
            float maximumPenumbra = centerShadow.y;
            float totalWeight = 0.0;

            [unroll]
            for (int sampleIndex = -2; sampleIndex <= 2; sampleIndex++)
            {
                float sampleOffset = sampleIndex;
                float2 sampleUv = saturate(
                    centerUv + direction * sampleOffset);
                float sampleDeviceDepth = SampleSceneDepth(sampleUv);
                if (LoogaIsSky(sampleDeviceDepth))
                    continue;

                float3 samplePosition = LoogaReconstructWorldPosition(sampleUv, sampleDeviceDepth);
                float3 sampleNormal = LoogaResolveSurfaceNormal(
                    sampleUv,
                    samplePosition);
                float planeDistance = abs(dot(samplePosition - centerPosition, centerNormal));
                float pixelDistance = abs(sampleOffset) * denoiseStridePixels;
                float planeTolerance = max(
                    0.002,
                    receiverFootprint * (1.5 + pixelDistance));
                float planeWeight = 1.0 - smoothstep(
                    planeTolerance,
                    planeTolerance * 3.0,
                    planeDistance);
                float normalWeight = smoothstep(
                    0.5,
                    0.95,
                    dot(centerNormal, sampleNormal));
                float spatialWeight = exp2(
                    -0.5 * sampleIndex * sampleIndex);
                float sampleWeight = spatialWeight * planeWeight * normalWeight;
                float2 sampleShadow = SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_PointClamp,
                    sampleUv).rg;
                maximumPenumbra = max(maximumPenumbra, sampleShadow.y);
                visibility += sampleShadow.x * sampleWeight;
                penumbra += sampleShadow.y * sampleWeight;
                totalWeight += sampleWeight;
            }

            float denoiseStrength = smoothstep(
                receiverFootprint * 1.5,
                receiverFootprint * 6.0,
                maximumPenumbra);
            float inverseWeight = rcp(max(totalWeight, 0.0001));
            float resolvedVisibility = lerp(
                centerShadow.x,
                visibility * inverseWeight,
                denoiseStrength);
            float resolvedPenumbra = lerp(
                centerShadow.y,
                penumbra * inverseWeight,
                denoiseStrength);
            return half4(
                resolvedVisibility,
                resolvedPenumbra,
                0.0,
                1.0);
        }

        half4 FragDebugFinal(Varyings input) : SV_Target
        {
            float visibility = SAMPLE_TEXTURE2D_X(
                _BlitTexture,
                sampler_PointClamp,
                input.texcoord).r;
            return half4(visibility.xxx, 1.0);
        }

        half4 FragDebugRaw(Varyings input) : SV_Target
        {
            float visibility = SAMPLE_TEXTURE2D_X(
                _BlitTexture,
                sampler_PointClamp,
                input.texcoord).r;
            return half4(visibility.xxx, 1.0);
        }

        half4 FragDebugPenumbra(Varyings input) : SV_Target
        {
            float2 shadow = SAMPLE_TEXTURE2D_X(
                _BlitTexture,
                sampler_PointClamp,
                input.texcoord).rg;
            float value = saturate(
                log2(1.0 + shadow.y * 64.0 / max(_LoogaSoftShadowData.z, 0.0001)) /
                6.0);
            float3 color = lerp(
                float3(0.02, 0.03, 0.08),
                float3(1.0, 0.18, 0.0),
                value);
            return half4(color, 1.0);
        }

        half4 FragDebugClipmaps(Varyings input) : SV_Target
        {
            float3 positionWS;
            float3 normalWS;
            float deviceDepth;
            deviceDepth = SampleSceneDepth(input.texcoord);
            if (LoogaIsSky(deviceDepth))
                return half4(0.0, 0.0, 0.0, 1.0);

            positionWS = LoogaReconstructWorldPosition(input.texcoord, deviceDepth);
            int level = LoogaFindClipmap(positionWS);
            const half3 colors[4] = {
                half3(0.20, 0.80, 1.00),
                half3(0.25, 1.00, 0.35),
                half3(1.00, 0.75, 0.20),
                half3(1.00, 0.25, 0.45)
            };
            if (level < 0)
                return half4(0.0, 0.0, 0.0, 1.0);

            float2 localUv = LoogaAtlasToLocalUv(
                LoogaGetShadowCoordinate(positionWS, level).xy,
                level);
            float2 centeredUv = abs(localUv * 2.0 - 1.0);
            float edgeProximity = saturate(max(centeredUv.x, centeredUv.y));
            float contour = 0.5 + 0.5 * cos(
                edgeProximity * 16.0 * LOOGA_PI);
            float pattern = lerp(0.25, 1.0, edgeProximity);
            pattern *= lerp(0.7, 1.0, contour);
            float3 color = colors[level] * pattern;
            return half4(color, 1.0);
        }

        half4 FragDebugTexels(Varyings input) : SV_Target
        {
            float deviceDepth = SampleSceneDepth(input.texcoord);
            if (LoogaIsSky(deviceDepth))
                return half4(0.0, 0.0, 0.0, 1.0);

            float3 positionWS = LoogaReconstructWorldPosition(input.texcoord, deviceDepth);
            int level = LoogaFindClipmap(positionWS);
            if (level < 0)
                return half4(0.0, 0.0, 0.0, 1.0);

            float2 localUv = LoogaAtlasToLocalUv(
                LoogaGetShadowCoordinate(positionWS, level).xy,
                level);
            float2 texel = localUv * _LoogaVirtualShadowAtlasSize.z;
            float footprint = max(length(ddx(texel)), length(ddy(texel)));
            float gridStep = clamp(
                exp2(ceil(log2(max(footprint * 4.0, 1.0)))),
                1.0,
                64.0);
            float2 gridUv = texel / gridStep;
            float2 edge = min(frac(gridUv), 1.0 - frac(gridUv));
            float2 width = max(fwidth(gridUv) * 1.25, 0.0001);
            float gridLine = 1.0 - min(
                smoothstep(0.0, width.x, edge.x),
                smoothstep(0.0, width.y, edge.y));
            float footprintValue = saturate(log2(max(footprint, 1.0)) / 6.0);
            float3 baseColor = lerp(
                float3(0.04, 0.12, 0.20),
                float3(0.65, 0.12, 0.05),
                footprintValue);
            float coordinatePattern = 0.55 + 0.45 * (
                0.5 + 0.5 * cos(
                    (localUv.x + localUv.y) *
                    32.0 *
                    LOOGA_PI));
            baseColor *= coordinatePattern;
            return half4(lerp(baseColor, 1.0.xxx, gridLine), 1.0);
        }

        half4 FragDebugDepth(Varyings input) : SV_Target
        {
            float deviceDepth = SampleSceneDepth(input.texcoord);
            if (LoogaIsSky(deviceDepth))
                return half4(0.0, 0.0, 0.0, 1.0);

            float eyeDepth = LinearEyeDepth(deviceDepth, _ZBufferParams);
            float value = saturate(
                log2(1.0 + eyeDepth) /
                log2(1.0 + max(_LoogaShadowDistanceData.x, 1.0)));
            return half4(value.xxx, 1.0);
        }

        half4 FragDebugNormals(Varyings input) : SV_Target
        {
            float deviceDepth = SampleSceneDepth(input.texcoord);
            if (LoogaIsSky(deviceDepth))
                return half4(0.0, 0.0, 0.0, 1.0);

            float3 positionWS = LoogaReconstructWorldPosition(
                input.texcoord,
                deviceDepth);
            float3 normalWS = LoogaResolveSurfaceNormal(
                input.texcoord,
                positionWS);
            return half4(normalWS * 0.5 + 0.5, 1.0);
        }

        ENDHLSL

        Pass
        {
            Name "Resolve"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragResolve
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

        Pass
        {
            Name "Debug Final Visibility"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugFinal
            ENDHLSL
        }

        Pass
        {
            Name "Debug Raw Visibility"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugRaw
            ENDHLSL
        }

        Pass
        {
            Name "Debug Penumbra"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugPenumbra
            ENDHLSL
        }

        Pass
        {
            Name "Debug Clipmap Levels"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugClipmaps
            ENDHLSL
        }

        Pass
        {
            Name "Debug Virtual Texels"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugTexels
            ENDHLSL
        }

        Pass
        {
            Name "Debug Linear Depth"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugDepth
            ENDHLSL
        }

        Pass
        {
            Name "Debug World Normals"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDebugNormals
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

        Pass
        {
            Name "Denoise Visibility"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDenoise
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

        Pass
        {
            Name "Filter Denoised Blockers"
            ZTest Always ZWrite Off Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragRefilter
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

    }

    Fallback Off
}
