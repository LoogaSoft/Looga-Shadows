using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;
using UnityEngine.Serialization;

namespace LoogaSoft.Shadows
{
    /// <summary>
    /// Renders package-owned, camera-centered directional clipmaps and publishes the resolved result
    /// through URP's screen-space main-light shadow contract. The renderer never samples URP's atlas.
    /// </summary>
    [SupportedOnRenderer(typeof(UniversalRendererData))]
    [DisallowMultipleRendererFeature("Looga Shadows")]
    [Tooltip("Renders package-owned directional shadow clipmaps and resolves them for URP lighting.")]
    public sealed class LoogaShadowRendererFeature : ScriptableRendererFeature
    {
#if UNITY_EDITOR
        [UnityEditor.ShaderKeywordFilter.SelectIf(true, keywordNames: "_MAIN_LIGHT_SHADOWS_SCREEN")]
        private const bool RequiresScreenSpaceShadowReceiverVariant = true;
#endif

        private const string FeatureName = "Looga Shadows";
        private const string ShaderName = "Hidden/LoogaSoft/Shadows/VirtualShadowResolve";
        private const string CopyDepthShaderName = "Hidden/Universal Render Pipeline/CopyDepth";
        private const int MaximumClipmapCount = 4;
        private const int RendererSettingsVersion = 1;

        [SerializeField]
        private LoogaShadowSettings _settings = LoogaShadowSettings.Default;

        // Kept hidden for one release so renderer assets using the former profile mode can
        // copy that profile into their inline settings without changing their rendered result.
        [SerializeField, HideInInspector, FormerlySerializedAs("_profile")]
        private LoogaShadowProfile _legacyProfile;

        [SerializeField, HideInInspector, FormerlySerializedAs("_settingsSource")]
        private int _legacySettingsSource;

        [SerializeField, HideInInspector]
        private int _rendererSettingsVersion;

        [SerializeField, HideInInspector]
        private Shader _resolveShader;

        private readonly Matrix4x4[] _worldToShadow = new Matrix4x4[MaximumClipmapCount];
        private readonly Matrix4x4[] _viewMatrices = new Matrix4x4[MaximumClipmapCount];
        private readonly Matrix4x4[] _projectionMatrices = new Matrix4x4[MaximumClipmapCount];
        private readonly Vector4[] _clipmapCenters = new Vector4[MaximumClipmapCount];
        private readonly Vector4[] _clipmapRadii = new Vector4[MaximumClipmapCount];

        private Material _resolveMaterial;
        private Texture2D _blueNoiseTexture;
        private ClipmapAtlasPass _atlasPass;
        private ResolvePass _resolvePass;
        private DisableTransparentShadowsPass _disableTransparentShadowsPass;
        private DebugOverlayPass _debugOverlayPass;
        private ScriptableRenderer _cachedRenderer;
        private bool _cachedRendererUsesDeferredLighting;

        private sealed class LoogaShadowFrameData : ContextItem
        {
            public TextureHandle Atlas = TextureHandle.nullHandle;
            public TextureHandle DepthAtlas = TextureHandle.nullHandle;
            public TextureHandle RawVisibility = TextureHandle.nullHandle;
            public TextureHandle ResolvedVisibility = TextureHandle.nullHandle;

            public override void Reset()
            {
                Atlas = TextureHandle.nullHandle;
                DepthAtlas = TextureHandle.nullHandle;
                RawVisibility = TextureHandle.nullHandle;
                ResolvedVisibility = TextureHandle.nullHandle;
            }
        }

        public LoogaShadowSettings Settings => _settings;

        public override void Create()
        {
            MigrateLegacySettings();
            name = FeatureName;
            EnsureMaterial();

            _atlasPass ??= new ClipmapAtlasPass();
            _resolvePass ??= new ResolvePass();
            _disableTransparentShadowsPass ??= new DisableTransparentShadowsPass();
            _debugOverlayPass ??= new DebugOverlayPass();

            _atlasPass.renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingShadows + 1);
            _disableTransparentShadowsPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            _debugOverlayPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!isActive || !EnsureMaterial())
                return;

            Camera camera = renderingData.cameraData.camera;
            int mainLightIndex = renderingData.lightData.mainLightIndex;
            if (camera == null || mainLightIndex < 0)
                return;

            VisibleLight visibleLight = renderingData.lightData.visibleLights[mainLightIndex];
            Light mainLight = visibleLight.light;
            if (mainLight == null || visibleLight.lightType != LightType.Directional || mainLight.shadows == LightShadows.None)
                return;

            LoogaShadowLightRegistry.TryGet(mainLight, out LoogaShadowLight shadowLight);
            LoogaShadowResolvedSettings settings = LoogaShadowResolvedSettings.Resolve(
                _settings,
                shadowLight);
            if (!ShouldRenderCamera(camera, settings.RenderSceneView))
                return;

            BuildClipmaps(camera, mainLight, settings);

            _atlasPass.Setup(
                mainLightIndex,
                visibleLight,
                settings,
                _worldToShadow,
                _viewMatrices,
                _projectionMatrices,
                _clipmapCenters,
                _clipmapRadii);

            _resolvePass.renderPassEvent = GetUsesDeferredLighting(renderer)
                ? (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 1)
                : (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 1);
            _resolvePass.Setup(
                _resolveMaterial,
                settings,
                _worldToShadow,
                _clipmapCenters,
                _clipmapRadii,
                -mainLight.transform.forward);

            renderer.EnqueuePass(_atlasPass);
            renderer.EnqueuePass(_resolvePass);
            renderer.EnqueuePass(_disableTransparentShadowsPass);

            if (settings.DebugView != LoogaShadowDebugView.Off)
            {
                _debugOverlayPass.Setup(
                    _resolveMaterial,
                    settings,
                    _worldToShadow,
                    _clipmapCenters,
                    _clipmapRadii,
                    -mainLight.transform.forward);
                renderer.EnqueuePass(_debugOverlayPass);
            }

            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
            LoogaShadowRuntimeDiagnostics.RecordCamera(
                camera.name,
                descriptor.width,
                descriptor.height,
                settings.AtlasResolution,
                settings.ClipmapCount,
                settings.DebugView,
                mainLight.name,
                settings.SettingsSource);
        }

        protected override void Dispose(bool disposing)
        {
            Shader.SetGlobalInteger(LoogaShadowShaderIds.ShadowsEnabled, 0);
            _atlasPass?.Dispose();
            CoreUtils.Destroy(_resolveMaterial);
            _resolveMaterial = null;
            _blueNoiseTexture = null;
            LoogaShadowRuntimeDiagnostics.Reset();
            base.Dispose(disposing);
        }

        private void OnValidate()
        {
            MigrateLegacySettings();
            _settings.EnsureInitialized();
            _settings.Validate();
        }

        private void MigrateLegacySettings()
        {
            if (_rendererSettingsVersion >= RendererSettingsVersion)
                return;

            // The former Profile enum value was zero. Renderer-feature mode was one.
            if (_legacySettingsSource == 0 && _legacyProfile != null)
                _settings = _legacyProfile.Settings;
            else
                _settings.EnsureInitialized();

            _settings.Validate();
            _legacyProfile = null;
            _legacySettingsSource = 1;
            _rendererSettingsVersion = RendererSettingsVersion;
        }

        private static bool ShouldRenderCamera(Camera camera, bool renderSceneView)
        {
            if (camera.cameraType == CameraType.Game)
                return true;

            return camera.cameraType == CameraType.SceneView && renderSceneView;
        }

        private bool EnsureMaterial()
        {
            if (_resolveMaterial == null)
            {
                if (_resolveShader == null)
                    _resolveShader = Shader.Find(ShaderName);

                if (_resolveShader == null)
                    return false;

                _resolveMaterial = CoreUtils.CreateEngineMaterial(_resolveShader);
                if (_resolveMaterial == null)
                    return false;
            }

            EnsureBlueNoiseTexture();
            return true;
        }

        private void EnsureBlueNoiseTexture()
        {
            if (_blueNoiseTexture == null)
            {
                UniversalRenderPipelineRuntimeTextures runtimeTextures =
                    GraphicsSettings.GetRenderPipelineSettings<UniversalRenderPipelineRuntimeTextures>();
                _blueNoiseTexture = runtimeTextures?.blueNoise64LTex;
            }

            bool available = _blueNoiseTexture != null;
            if (available)
                _resolveMaterial.SetTexture(LoogaShadowShaderIds.BlueNoiseTexture, _blueNoiseTexture);

            _resolveMaterial.SetFloat(
                LoogaShadowShaderIds.BlueNoiseAvailable,
                available ? 1f : 0f);
        }

        private bool GetUsesDeferredLighting(ScriptableRenderer renderer)
        {
            if (renderer == null)
                return false;

            if (_cachedRenderer == renderer)
                return _cachedRendererUsesDeferredLighting;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;

            System.Reflection.PropertyInfo property = renderer.GetType().GetProperty("usesDeferredLighting", flags);
            _cachedRenderer = renderer;
            _cachedRendererUsesDeferredLighting =
                property?.PropertyType == typeof(bool) && (bool)property.GetValue(renderer);
            return _cachedRendererUsesDeferredLighting;
        }

        private void BuildClipmaps(Camera camera, Light light, LoogaShadowResolvedSettings settings)
        {
            // Shadow cameras sit toward the light and look along the rays toward the scene.
            // Unity's directional-light transform.forward is the direction the light travels.
            Vector3 lightDirection = light.transform.forward.normalized;
            Vector3 up = Mathf.Abs(Vector3.Dot(lightDirection, Vector3.up)) > 0.98f
                ? Vector3.forward
                : Vector3.up;
            Quaternion lightRotation = Quaternion.LookRotation(lightDirection, up);
            Matrix4x4 lightWorldToLocal = Matrix4x4.Rotate(Quaternion.Inverse(lightRotation));
            Matrix4x4 lightLocalToWorld = Matrix4x4.Rotate(lightRotation);

            Vector3 cameraForward = Vector3.ProjectOnPlane(camera.transform.forward, lightDirection).normalized;
            if (cameraForward.sqrMagnitude < 0.01f)
                cameraForward = Vector3.ProjectOnPlane(camera.transform.up, lightDirection).normalized;

            for (int level = 0; level < MaximumClipmapCount; level++)
            {
                if (level >= settings.ClipmapCount)
                {
                    _worldToShadow[level] = Matrix4x4.zero;
                    _viewMatrices[level] = Matrix4x4.identity;
                    _projectionMatrices[level] = Matrix4x4.identity;
                    _clipmapCenters[level] = Vector4.zero;
                    _clipmapRadii[level] = Vector4.zero;
                    continue;
                }

                float clipmapT = settings.ClipmapCount > 1
                    ? level / (settings.ClipmapCount - 1f)
                    : 0f;
                float coverageRatio = Mathf.Max(
                    settings.ShadowDistance / settings.NearClipmapRadius,
                    1f);
                float radius = settings.NearClipmapRadius * Mathf.Pow(coverageRatio, clipmapT);
                Vector3 desiredCenter = camera.transform.position + cameraForward * radius * 0.35f;
                Vector3 lightSpaceCenter = lightWorldToLocal.MultiplyPoint3x4(desiredCenter);
                float worldTexelSize = radius * 2f / settings.TileResolution;

                // Quantized origins keep texels stationary under sub-texel camera motion.
                lightSpaceCenter.x = Mathf.Floor(lightSpaceCenter.x / worldTexelSize) * worldTexelSize;
                lightSpaceCenter.y = Mathf.Floor(lightSpaceCenter.y / worldTexelSize) * worldTexelSize;
                Vector3 center = lightLocalToWorld.MultiplyPoint3x4(lightSpaceCenter);
                Vector3 eye = center - lightDirection * settings.DepthRange * 0.5f;

                // Unity camera/view space looks down -Z, while Transform.forward is +Z.
                // Match Camera.worldToCameraMatrix so the orthographic projection's near/far
                // range and the receiver shadow transform use the same depth convention.
                Matrix4x4 view =
                    Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                    Matrix4x4.TRS(eye, lightRotation, Vector3.one).inverse;
                Matrix4x4 projection = Matrix4x4.Ortho(
                    -radius,
                    radius,
                    -radius,
                    radius,
                    0.01f,
                    settings.DepthRange);

                _viewMatrices[level] = view;
                _projectionMatrices[level] = projection;
                _worldToShadow[level] = GetAtlasShadowTransform(view, projection, level);
                _clipmapCenters[level] = new Vector4(center.x, center.y, center.z, radius);
                _clipmapRadii[level] = new Vector4(radius, worldTexelSize, 1f / worldTexelSize, 0f);
            }
        }

        private static Matrix4x4 GetAtlasShadowTransform(Matrix4x4 view, Matrix4x4 projection, int level)
        {
            // Match URP's ShadowUtils.GetShadowTransform. SetViewProjectionMatrices applies
            // the render-target convention while drawing; receiver coordinates only need the
            // platform's reversed-Z correction here.
            Matrix4x4 shadowProjection = projection;
            if (SystemInfo.usesReversedZBuffer)
            {
                shadowProjection.m20 = -shadowProjection.m20;
                shadowProjection.m21 = -shadowProjection.m21;
                shadowProjection.m22 = -shadowProjection.m22;
                shadowProjection.m23 = -shadowProjection.m23;
            }

            Matrix4x4 textureScaleBias = Matrix4x4.identity;
            textureScaleBias.m00 = 0.5f;
            textureScaleBias.m11 = 0.5f;
            textureScaleBias.m22 = 0.5f;
            textureScaleBias.m03 = 0.5f;
            textureScaleBias.m13 = 0.5f;
            textureScaleBias.m23 = 0.5f;

            int tileX = level & 1;
            int tileY = level >> 1;
            Matrix4x4 atlasTransform = Matrix4x4.identity;
            atlasTransform.m00 = 0.5f;
            atlasTransform.m11 = 0.5f;
            atlasTransform.m03 = tileX * 0.5f;
            atlasTransform.m13 = tileY * 0.5f;
            return atlasTransform * textureScaleBias * shadowProjection * view;
        }

        private sealed class ClipmapAtlasPass : ScriptableRenderPass
        {
            private static readonly int ShadowBias = Shader.PropertyToID("_ShadowBias");
            private static readonly int LightDirection = Shader.PropertyToID("_LightDirection");
            private static readonly int LightPosition = Shader.PropertyToID("_LightPosition");
            private static readonly int WorldSpaceCameraPosition = Shader.PropertyToID("_WorldSpaceCameraPos");

            private readonly ProfilingSampler _profilingSampler = new("Looga Shadows Render Clipmaps");
            private CopyDepthPass _copyDepthPass;
            private int _mainLightIndex;
            private VisibleLight _mainLight;
            private LoogaShadowResolvedSettings _settings;
            private Matrix4x4[] _worldToShadow;
            private Matrix4x4[] _viewMatrices;
            private Matrix4x4[] _projectionMatrices;
            private Vector4[] _clipmapCenters;
            private Vector4[] _clipmapRadii;
            private readonly Plane[] _shadowCullPlanes = new Plane[6];

            public ClipmapAtlasPass()
            {
                EnsureCopyDepthPass();
            }

            public void Dispose()
            {
                _copyDepthPass?.Dispose();
                _copyDepthPass = null;
            }

            private bool EnsureCopyDepthPass()
            {
                if (_copyDepthPass != null)
                    return true;

                Shader copyDepthShader = null;
                if (GraphicsSettings.TryGetRenderPipelineSettings<UniversalRendererResources>(
                        out var rendererResources))
                {
                    copyDepthShader = rendererResources.copyDepthPS;
                }

                if (copyDepthShader == null)
                    copyDepthShader = Shader.Find(CopyDepthShaderName);

                if (copyDepthShader == null)
                    return false;

                _copyDepthPass = new CopyDepthPass(
                    RenderPassEvent.BeforeRenderingShadows,
                    copyDepthShader,
                    copyResolvedDepth: true,
                    customPassName: "Looga Shadows Copy Raw Depth");
                return true;
            }

            private sealed class PassData
            {
                public RendererListHandle RendererList0;
                public RendererListHandle RendererList1;
                public RendererListHandle RendererList2;
                public RendererListHandle RendererList3;
                public int MainLightIndex;
                public VisibleLight MainLight;
                public UniversalShadowData ShadowData;
                public LoogaShadowResolvedSettings Settings;
                public Matrix4x4[] WorldToShadow;
                public Matrix4x4[] ViewMatrices;
                public Matrix4x4[] ProjectionMatrices;
                public Vector4[] ClipmapCenters;
                public Vector4[] ClipmapRadii;
                public Matrix4x4 CameraView;
                public Matrix4x4 CameraProjection;
                public Vector3 CameraPosition;

                public RendererListHandle GetRendererList(int level)
                {
                    return level switch
                    {
                        0 => RendererList0,
                        1 => RendererList1,
                        2 => RendererList2,
                        3 => RendererList3,
                        _ => default
                    };
                }
            }

            public void Setup(
                int mainLightIndex,
                VisibleLight mainLight,
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Matrix4x4[] viewMatrices,
                Matrix4x4[] projectionMatrices,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii)
            {
                _mainLightIndex = mainLightIndex;
                _mainLight = mainLight;
                _settings = settings;
                _worldToShadow = worldToShadow;
                _viewMatrices = viewMatrices;
                _projectionMatrices = projectionMatrices;
                _clipmapCenters = clipmapCenters;
                _clipmapRadii = clipmapRadii;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_mainLight.light == null ||
                    _mainLightIndex < 0 ||
                    !EnsureCopyDepthPass())
                {
                    return;
                }

                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
                CullContextData cullContextData = frameData.Get<CullContextData>();
                CullingResults shadowCullResults = CullShadowCasters(
                    cameraData,
                    cullContextData,
                    renderingData.cullResults);
                RendererListHandle rendererList0 = CreateShadowRendererList(
                    renderGraph,
                    shadowCullResults);
                RendererListHandle rendererList1 = _settings.ClipmapCount > 1
                    ? CreateShadowRendererList(renderGraph, shadowCullResults)
                    : default;
                RendererListHandle rendererList2 = _settings.ClipmapCount > 2
                    ? CreateShadowRendererList(renderGraph, shadowCullResults)
                    : default;
                RendererListHandle rendererList3 = _settings.ClipmapCount > 3
                    ? CreateShadowRendererList(renderGraph, shadowCullResults)
                    : default;

                RenderTextureDescriptor descriptor = new(
                    _settings.AtlasResolution,
                    _settings.AtlasResolution,
                    RenderTextureFormat.Shadowmap,
                    32)
                {
                    shadowSamplingMode = ShadowSamplingMode.CompareDepths,
                    msaaSamples = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                TextureHandle atlas = renderGraph.CreateTexture(new TextureDesc(descriptor)
                {
                    name = "Looga Virtual Shadow Atlas",
                    clearBuffer = true,
                    clearColor = SystemInfo.usesReversedZBuffer ? Color.black : Color.white,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                });
                TextureHandle depthAtlas = renderGraph.CreateTexture(new TextureDesc(
                    _settings.AtlasResolution,
                    _settings.AtlasResolution)
                {
                    name = "Looga Virtual Shadow Raw Depth",
                    format = GraphicsFormat.R16_UNorm,
                    clearBuffer = false,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                });
                LoogaShadowFrameData shadowFrameData = frameData.GetOrCreate<LoogaShadowFrameData>();
                shadowFrameData.Atlas = atlas;
                shadowFrameData.DepthAtlas = depthAtlas;

                IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Looga Shadows Render Clipmaps",
                    out PassData passData,
                    _profilingSampler);

                passData.RendererList0 = rendererList0;
                passData.RendererList1 = rendererList1;
                passData.RendererList2 = rendererList2;
                passData.RendererList3 = rendererList3;
                passData.MainLightIndex = _mainLightIndex;
                passData.MainLight = _mainLight;
                passData.ShadowData = shadowData;
                passData.Settings = _settings;
                passData.WorldToShadow = _worldToShadow;
                passData.ViewMatrices = _viewMatrices;
                passData.ProjectionMatrices = _projectionMatrices;
                passData.ClipmapCenters = _clipmapCenters;
                passData.ClipmapRadii = _clipmapRadii;
                passData.CameraView = cameraData.GetViewMatrix();
                passData.CameraProjection = cameraData.GetProjectionMatrix();
                passData.CameraPosition = cameraData.worldSpaceCameraPos;

                builder.UseRendererList(rendererList0);
                if (_settings.ClipmapCount > 1)
                    builder.UseRendererList(rendererList1);
                if (_settings.ClipmapCount > 2)
                    builder.UseRendererList(rendererList2);
                if (_settings.ClipmapCount > 3)
                    builder.UseRendererList(rendererList3);
                builder.SetRenderAttachmentDepth(atlas, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(atlas, LoogaShadowShaderIds.VirtualShadowAtlas);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    int tileResolution = data.Settings.TileResolution;
                    Vector3 direction = -data.MainLight.light.transform.forward.normalized;
                    context.cmd.SetGlobalVector(WorldSpaceCameraPosition, data.CameraPosition);
                    context.cmd.SetGlobalVector(LightDirection, new Vector4(direction.x, direction.y, direction.z, 0f));
                    context.cmd.SetGlobalVector(LightPosition, new Vector4(-direction.x, -direction.y, -direction.z, 0f));
                    context.cmd.SetKeyword(LoogaShadowShaderIds.CastingPunctualLightShadow, false);

                    context.cmd.SetGlobalDepthBias(1f, 2.5f);
                    for (int level = 0; level < data.Settings.ClipmapCount; level++)
                    {
                        int tileX = level & 1;
                        int tileY = level >> 1;
                        context.cmd.SetViewport(new Rect(
                            tileX * tileResolution,
                            tileY * tileResolution,
                            tileResolution,
                            tileResolution));
                        Vector4 shadowBias = ShadowUtils.GetShadowBias(
                            ref data.MainLight,
                            data.MainLightIndex,
                            data.ShadowData,
                            data.ProjectionMatrices[level],
                            tileResolution);
                        context.cmd.SetGlobalVector(ShadowBias, shadowBias);
                        context.cmd.SetViewProjectionMatrices(
                            data.ViewMatrices[level],
                            data.ProjectionMatrices[level]);
                        context.cmd.DrawRendererList(data.GetRendererList(level));
                    }
                    context.cmd.SetGlobalDepthBias(0f, 0f);

                    context.cmd.SetViewProjectionMatrices(data.CameraView, data.CameraProjection);
                    context.cmd.SetGlobalMatrixArray(LoogaShadowShaderIds.WorldToShadow, data.WorldToShadow);
                    context.cmd.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapCenters, data.ClipmapCenters);
                    context.cmd.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapRadii, data.ClipmapRadii);
                    context.cmd.SetGlobalInteger(LoogaShadowShaderIds.ClipmapCount, data.Settings.ClipmapCount);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.AtlasSize,
                        new Vector4(
                            data.Settings.AtlasResolution,
                            1f / data.Settings.AtlasResolution,
                            tileResolution,
                            1f / tileResolution));
                });
                builder.Dispose();

                _copyDepthPass.Render(
                    renderGraph,
                    frameData,
                    depthAtlas,
                    atlas,
                    passName: "Looga Shadows Copy Raw Depth");
            }

            private RendererListHandle CreateShadowRendererList(
                RenderGraph renderGraph,
                CullingResults shadowCullResults)
            {
                ShadowDrawingSettings shadowDrawingSettings = new(
                    shadowCullResults,
                    _mainLightIndex)
                {
                    useRenderingLayerMaskTest =
                        UniversalRenderPipeline.asset != null &&
                        UniversalRenderPipeline.asset.useRenderingLayers
                };
                return renderGraph.CreateShadowRendererList(
                    ref shadowDrawingSettings);
            }

            private CullingResults CullShadowCasters(
                UniversalCameraData cameraData,
                CullContextData cullContextData,
                CullingResults fallbackResults)
            {
                if (!cameraData.camera.TryGetCullingParameters(false, out ScriptableCullingParameters cullingParameters))
                    return fallbackResults;

                int largestLevel = Mathf.Max(0, _settings.ClipmapCount - 1);
                Matrix4x4 cullingMatrix =
                    _projectionMatrices[largestLevel] * _viewMatrices[largestLevel];
                cullingParameters.cullingMatrix = cullingMatrix;
                GeometryUtility.CalculateFrustumPlanes(cullingMatrix, _shadowCullPlanes);
                cullingParameters.cullingPlaneCount = _shadowCullPlanes.Length;
                for (int planeIndex = 0; planeIndex < _shadowCullPlanes.Length; planeIndex++)
                    cullingParameters.SetCullingPlane(planeIndex, _shadowCullPlanes[planeIndex]);
                cullingParameters.cullingOptions &= ~CullingOptions.OcclusionCull;
                cullingParameters.shadowDistance = _settings.DepthRange;
                return cullContextData.Cull(ref cullingParameters);
            }
        }

        private sealed class ResolvePass : ScriptableRenderPass
        {
            private const int ResolveShaderPass = 0;
            private const int DenoiseShaderPass = 8;
            private readonly ProfilingSampler _profilingSampler = new("Looga Shadows Resolve Virtual Clipmaps");
            private Material _material;
            private LoogaShadowResolvedSettings _settings;
            private Matrix4x4[] _worldToShadow;
            private Vector4[] _clipmapCenters;
            private Vector4[] _clipmapRadii;
            private Vector3 _lightDirection;

            private sealed class PassData
            {
                public TextureHandle Atlas;
                public TextureHandle DepthAtlas;
                public TextureHandle RawTarget;
                public TextureHandle DenoiseTarget;
                public TextureHandle Target;
                public TextureHandle CameraDepth;
                public TextureHandle CameraNormals;
                public Material Material;
                public LoogaShadowResolvedSettings Settings;
                public Matrix4x4[] WorldToShadow;
                public Vector4[] ClipmapCenters;
                public Vector4[] ClipmapRadii;
                public Vector3 LightDirection;
            }

            public void Setup(
                Material material,
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii,
                Vector3 lightDirection)
            {
                _material = material;
                _settings = settings;
                _worldToShadow = worldToShadow;
                _clipmapCenters = clipmapCenters;
                _clipmapRadii = clipmapRadii;
                _lightDirection = lightDirection;
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null)
                    return;

                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!frameData.Contains<LoogaShadowFrameData>())
                    return;

                TextureHandle atlas = frameData.Get<LoogaShadowFrameData>().Atlas;
                TextureHandle depthAtlas = frameData.Get<LoogaShadowFrameData>().DepthAtlas;
                TextureHandle cameraDepth = resourceData.cameraDepthTexture.IsValid()
                    ? resourceData.cameraDepthTexture
                    : resourceData.activeDepthTexture;
                TextureHandle cameraNormals = resourceData.cameraNormalsTexture;
                if (!atlas.IsValid() || !depthAtlas.IsValid() ||
                    !cameraDepth.IsValid() || !cameraNormals.IsValid())
                    return;

                RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
                descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                descriptor.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                TextureHandle rawTarget = renderGraph.CreateTexture(new TextureDesc(descriptor)
                {
                    name = "Looga Main Light Shadow Raw",
                    clearBuffer = true,
                    clearColor = Color.white,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                });
                TextureHandle denoiseTarget = renderGraph.CreateTexture(new TextureDesc(descriptor)
                {
                    name = "Looga Main Light Shadow Horizontal Reconstruction",
                    clearBuffer = true,
                    clearColor = Color.white,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                });
                TextureHandle target = renderGraph.CreateTexture(new TextureDesc(descriptor)
                {
                    name = "Looga Main Light Shadow",
                    clearBuffer = true,
                    clearColor = Color.white,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                });

                LoogaShadowFrameData shadowFrameData = frameData.Get<LoogaShadowFrameData>();
                shadowFrameData.RawVisibility = rawTarget;
                shadowFrameData.ResolvedVisibility = target;

                using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                    "Looga Shadows Resolve Virtual Clipmaps",
                    out PassData passData,
                    _profilingSampler);

                passData.Atlas = atlas;
                passData.DepthAtlas = depthAtlas;
                passData.RawTarget = rawTarget;
                passData.DenoiseTarget = denoiseTarget;
                passData.Target = target;
                passData.CameraDepth = cameraDepth;
                passData.CameraNormals = cameraNormals;
                passData.Material = _material;
                passData.Settings = _settings;
                passData.WorldToShadow = _worldToShadow;
                passData.ClipmapCenters = _clipmapCenters;
                passData.ClipmapRadii = _clipmapRadii;
                passData.LightDirection = _lightDirection;

                builder.UseAllGlobalTextures(true);
                builder.UseTexture(atlas, AccessFlags.Read);
                builder.UseTexture(depthAtlas, AccessFlags.Read);
                builder.UseTexture(rawTarget, AccessFlags.ReadWrite);
                builder.UseTexture(denoiseTarget, AccessFlags.ReadWrite);
                builder.UseTexture(target, AccessFlags.WriteAll);
                builder.UseTexture(cameraDepth, AccessFlags.Read);
                builder.UseTexture(cameraNormals, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetGlobalTextureAfterPass(target, LoogaShadowShaderIds.MainLightShadowTexture);
                builder.SetGlobalTextureAfterPass(target, LoogaShadowShaderIds.UrpScreenSpaceShadowTexture);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    RTHandle atlas = data.Atlas;
                    RTHandle depthAtlas = data.DepthAtlas;
                    RTHandle rawTarget = data.RawTarget;
                    RTHandle denoiseTarget = data.DenoiseTarget;
                    RTHandle target = data.Target;
                    RTHandle cameraDepth = data.CameraDepth;
                    RTHandle cameraNormals = data.CameraNormals;
                    context.cmd.SetGlobalTexture(
                        LoogaShadowShaderIds.CameraDepthTexture,
                        cameraDepth);
                    context.cmd.SetGlobalTexture(
                        LoogaShadowShaderIds.CameraNormalsTexture,
                        cameraNormals);
                    context.cmd.SetRenderTarget(
                        rawTarget,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowAtlas, atlas);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowDepthAtlas, depthAtlas);
                    ApplySettings(context.cmd, data);
                    Blitter.BlitTexture(context.cmd, depthAtlas, Vector2.one, data.Material, ResolveShaderPass);
                    context.cmd.SetRenderTarget(
                        denoiseTarget,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DenoiseDirection,
                        new Vector4(1f, 0f, 0f, 0f));
                    Blitter.BlitTexture(context.cmd, rawTarget, Vector2.one, data.Material, DenoiseShaderPass);
                    context.cmd.SetRenderTarget(
                        target,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DenoiseDirection,
                        new Vector4(0f, 1f, 0f, 0f));
                    Blitter.BlitTexture(context.cmd, denoiseTarget, Vector2.one, data.Material, DenoiseShaderPass);
                    context.cmd.SetGlobalInteger(LoogaShadowShaderIds.ShadowsEnabled, 1);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadows, false);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowCascades, false);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowScreen, true);
                });
            }

            private static void ApplySettings(IBaseCommandBuffer command, PassData data)
            {
                LoogaShadowResolvedSettings settings = data.Settings;
                command.SetGlobalMatrixArray(LoogaShadowShaderIds.WorldToShadow, data.WorldToShadow);
                command.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapCenters, data.ClipmapCenters);
                command.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapRadii, data.ClipmapRadii);
                command.SetGlobalInteger(LoogaShadowShaderIds.ClipmapCount, settings.ClipmapCount);
                command.SetGlobalVector(
                    LoogaShadowShaderIds.AtlasSize,
                    new Vector4(
                        settings.AtlasResolution,
                        1f / settings.AtlasResolution,
                        settings.TileResolution,
                        1f / settings.TileResolution));
                command.SetGlobalVector(
                    LoogaShadowShaderIds.LightDirection,
                    new Vector4(data.LightDirection.x, data.LightDirection.y, data.LightDirection.z, 0f));
                command.SetGlobalVector(
                    LoogaShadowShaderIds.SampleCounts,
                    new Vector4(settings.BlockerSampleCount, settings.FilterSampleCount, 0f, 0f));
                command.SetGlobalVector(
                    LoogaShadowShaderIds.SoftShadowData,
                    new Vector4(
                        settings.SourceAngularDiameter,
                        settings.Softness,
                        settings.MaximumPenumbra,
                        settings.ClipmapBlend));
                command.SetGlobalVector(
                    LoogaShadowShaderIds.BiasData,
                    new Vector4(settings.DepthBias, settings.NormalBias, settings.DepthRange, 0f));
                command.SetGlobalVector(
                    LoogaShadowShaderIds.DistanceData,
                    new Vector4(settings.ShadowDistance, settings.ShadowDistance * 0.9f, 0f, 0f));
            }
        }

        /// <summary>
        /// Standard URP transparent shaders cannot reconstruct their own receiver depth from the
        /// opaque screen mask. Disable all standard main-light shadow paths instead of silently
        /// returning them to URP's atlas.
        /// </summary>
        private sealed class DisableTransparentShadowsPass : ScriptableRenderPass
        {
            private readonly ProfilingSampler _profilingSampler = new("Looga Shadows Disable Transparent Fallback");

            private sealed class PassData
            {
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Looga Shadows Disable Transparent Fallback",
                    out PassData _,
                    _profilingSampler);

                builder.SetRenderAttachment(
                    resourceData.activeColorTexture,
                    0,
                    AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData _, RasterGraphContext context) =>
                {
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowScreen, false);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadows, false);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowCascades, false);
                });
            }
        }

        private sealed class DebugOverlayPass : ScriptableRenderPass
        {
            private readonly ProfilingSampler _profilingSampler = new("Looga Shadows Debug Overlay");
            private Material _material;
            private LoogaShadowResolvedSettings _settings;
            private Matrix4x4[] _worldToShadow;
            private Vector4[] _clipmapCenters;
            private Vector4[] _clipmapRadii;
            private Vector3 _lightDirection;
            private LoogaShadowDebugView _debugView;

            private sealed class PassData
            {
                public TextureHandle Atlas;
                public TextureHandle DepthAtlas;
                public TextureHandle RawVisibility;
                public TextureHandle ResolvedVisibility;
                public TextureHandle CameraDepth;
                public TextureHandle CameraNormals;
                public TextureHandle DebugSource;
                public Material Material;
                public int ShaderPass;
                public LoogaShadowResolvedSettings Settings;
                public Matrix4x4[] WorldToShadow;
                public Vector4[] ClipmapCenters;
                public Vector4[] ClipmapRadii;
                public Vector3 LightDirection;
            }

            public void Setup(
                Material material,
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii,
                Vector3 lightDirection)
            {
                _material = material;
                _settings = settings;
                _worldToShadow = worldToShadow;
                _clipmapCenters = clipmapCenters;
                _clipmapRadii = clipmapRadii;
                _lightDirection = lightDirection;
                _debugView = settings.DebugView;
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null || _debugView == LoogaShadowDebugView.Off)
                    return;

                if (!frameData.Contains<LoogaShadowFrameData>())
                    return;

                LoogaShadowFrameData shadowFrameData = frameData.Get<LoogaShadowFrameData>();
                TextureHandle atlas = shadowFrameData.Atlas;
                TextureHandle depthAtlas = shadowFrameData.DepthAtlas;
                TextureHandle rawVisibility = shadowFrameData.RawVisibility;
                TextureHandle resolvedVisibility = shadowFrameData.ResolvedVisibility;
                if (!atlas.IsValid() || !depthAtlas.IsValid() ||
                    !rawVisibility.IsValid() || !resolvedVisibility.IsValid())
                    return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                TextureHandle cameraDepth = resourceData.cameraDepthTexture.IsValid()
                    ? resourceData.cameraDepthTexture
                    : resourceData.activeDepthTexture;
                TextureHandle cameraNormals = resourceData.cameraNormalsTexture;
                if (!cameraDepth.IsValid() || !cameraNormals.IsValid())
                    return;

                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Looga Shadows Debug Overlay",
                    out PassData passData,
                    _profilingSampler);

                passData.Atlas = atlas;
                passData.DepthAtlas = depthAtlas;
                passData.RawVisibility = rawVisibility;
                passData.ResolvedVisibility = resolvedVisibility;
                passData.CameraDepth = cameraDepth;
                passData.CameraNormals = cameraNormals;
                passData.DebugSource = _debugView switch
                {
                    LoogaShadowDebugView.RawVisibility => rawVisibility,
                    LoogaShadowDebugView.FinalVisibility or LoogaShadowDebugView.Penumbra
                        => resolvedVisibility,
                    _ => cameraDepth
                };
                passData.Material = _material;
                passData.ShaderPass = GetShaderPass(_debugView);
                passData.Settings = _settings;
                passData.WorldToShadow = _worldToShadow;
                passData.ClipmapCenters = _clipmapCenters;
                passData.ClipmapRadii = _clipmapRadii;
                passData.LightDirection = _lightDirection;
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.UseTexture(atlas, AccessFlags.Read);
                builder.UseTexture(depthAtlas, AccessFlags.Read);
                builder.UseTexture(rawVisibility, AccessFlags.Read);
                builder.UseTexture(resolvedVisibility, AccessFlags.Read);
                builder.UseTexture(cameraDepth, AccessFlags.Read);
                builder.UseTexture(cameraNormals, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    RTHandle debugSource = data.DebugSource;
                    LoogaShadowResolvedSettings settings = data.Settings;
                    context.cmd.SetGlobalMatrixArray(
                        LoogaShadowShaderIds.WorldToShadow,
                        data.WorldToShadow);
                    context.cmd.SetGlobalVectorArray(
                        LoogaShadowShaderIds.ClipmapCenters,
                        data.ClipmapCenters);
                    context.cmd.SetGlobalVectorArray(
                        LoogaShadowShaderIds.ClipmapRadii,
                        data.ClipmapRadii);
                    context.cmd.SetGlobalInteger(
                        LoogaShadowShaderIds.ClipmapCount,
                        settings.ClipmapCount);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.AtlasSize,
                        new Vector4(
                            settings.AtlasResolution,
                            1f / settings.AtlasResolution,
                            settings.TileResolution,
                            1f / settings.TileResolution));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.LightDirection,
                        new Vector4(
                            data.LightDirection.x,
                            data.LightDirection.y,
                            data.LightDirection.z,
                            0f));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.SampleCounts,
                        new Vector4(
                            settings.BlockerSampleCount,
                            settings.FilterSampleCount,
                            0f,
                            0f));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.SoftShadowData,
                        new Vector4(
                            settings.SourceAngularDiameter,
                            settings.Softness,
                            settings.MaximumPenumbra,
                            settings.ClipmapBlend));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.BiasData,
                        new Vector4(
                            settings.DepthBias,
                            settings.NormalBias,
                            settings.DepthRange,
                            0f));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DistanceData,
                        new Vector4(
                            settings.ShadowDistance,
                            settings.ShadowDistance * 0.9f,
                            0f,
                            0f));
                    Blitter.BlitTexture(
                        context.cmd,
                        debugSource,
                        Vector2.one,
                        data.Material,
                        data.ShaderPass);
                });
            }

            private static int GetShaderPass(LoogaShadowDebugView debugView)
            {
                return debugView switch
                {
                    LoogaShadowDebugView.FinalVisibility => 1,
                    LoogaShadowDebugView.RawVisibility => 2,
                    LoogaShadowDebugView.Penumbra => 3,
                    LoogaShadowDebugView.ClipmapLevels => 4,
                    LoogaShadowDebugView.VirtualTexels => 5,
                    LoogaShadowDebugView.LinearDepth => 6,
                    LoogaShadowDebugView.WorldNormals => 7,
                    _ => 1
                };
            }
        }
    }
}
