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

        [UnityEditor.ShaderKeywordFilter.SelectIf(true, keywordNames: "_MAIN_LIGHT_SHADOWS_CASCADE")]
        private const bool RequiresTransparentCascadeShadowReceiverVariant = true;
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
        private readonly Vector4[] _clipmapRects = new Vector4[MaximumClipmapCount];

        private Material _resolveMaterial;
        private Texture2D _blueNoiseTexture;
        private ClipmapAtlasPass _atlasPass;
        private ResolvePass _resolvePass;
        private TransparentShadowReceiverPass _transparentShadowReceiverPass;
        private DebugOverlayPass _debugOverlayPass;
        private ScriptableRenderer _cachedRenderer;
        private bool _cachedRendererUsesDeferredLighting;

        private sealed class LoogaShadowFrameData : ContextItem
        {
            public readonly TextureHandle[] Clipmaps = new TextureHandle[MaximumClipmapCount];
            public readonly TextureHandle[] DepthClipmaps = new TextureHandle[MaximumClipmapCount];
            public TextureHandle RawVisibility = TextureHandle.nullHandle;
            public TextureHandle ResolvedVisibility = TextureHandle.nullHandle;
            public bool HasShadowCasters;

            public override void Reset()
            {
                for (int level = 0; level < MaximumClipmapCount; level++)
                {
                    Clipmaps[level] = TextureHandle.nullHandle;
                    DepthClipmaps[level] = TextureHandle.nullHandle;
                }
                RawVisibility = TextureHandle.nullHandle;
                ResolvedVisibility = TextureHandle.nullHandle;
                HasShadowCasters = false;
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
            _transparentShadowReceiverPass ??= new TransparentShadowReceiverPass();
            _debugOverlayPass ??= new DebugOverlayPass();

            _atlasPass.renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingShadows + 1);
            _transparentShadowReceiverPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
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
            bool usesDeferredLighting = GetUsesDeferredLighting(renderer);
            bool usesAccurateGBufferNormals =
                usesDeferredLighting && GetUsesAccurateGBufferNormals(renderer);

            _atlasPass.Setup(
                mainLightIndex,
                visibleLight,
                settings,
                _worldToShadow,
                _viewMatrices,
                _projectionMatrices,
                _clipmapCenters,
                _clipmapRadii,
                _clipmapRects);

            _resolvePass.renderPassEvent = usesDeferredLighting
                ? (RenderPassEvent)((int)RenderPassEvent.AfterRenderingGbuffer + 1)
                : (RenderPassEvent)((int)RenderPassEvent.AfterRenderingPrePasses + 1);
            _resolvePass.Setup(
                _resolveMaterial,
                settings,
                _worldToShadow,
                _clipmapCenters,
                _clipmapRadii,
                _clipmapRects,
                -mainLight.transform.forward,
                usesDeferredLighting,
                usesAccurateGBufferNormals);
            _transparentShadowReceiverPass.Setup(
                settings,
                _worldToShadow,
                _clipmapCenters,
                _clipmapRadii,
                mainLight);

            renderer.EnqueuePass(_atlasPass);
            renderer.EnqueuePass(_resolvePass);
            renderer.EnqueuePass(_transparentShadowReceiverPass);

            if (settings.DebugView != LoogaShadowDebugView.Off)
            {
                _debugOverlayPass.Setup(
                    _resolveMaterial,
                    settings,
                    _worldToShadow,
                    _clipmapCenters,
                    _clipmapRadii,
                    _clipmapRects,
                    -mainLight.transform.forward,
                    usesDeferredLighting,
                    usesAccurateGBufferNormals);
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

        private static bool GetUsesAccurateGBufferNormals(ScriptableRenderer renderer)
        {
            if (renderer == null)
                return false;

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            System.Reflection.PropertyInfo property =
                renderer.GetType().GetProperty("accurateGbufferNormals", flags);
            return property?.PropertyType == typeof(bool) &&
                (bool)property.GetValue(renderer);
        }

        private static LoogaShadowNormalsSource GetEffectiveNormalsSource(
            LoogaShadowNormalsSource requestedSource,
            bool usesDeferredLighting)
        {
            if (requestedSource == LoogaShadowNormalsSource.GBuffer && !usesDeferredLighting)
                return LoogaShadowNormalsSource.ReconstructFromDepth;

            return requestedSource;
        }

        private static ScriptableRenderPassInput GetRequiredInputs(
            LoogaShadowNormalsSource normalsSource)
        {
            ScriptableRenderPassInput inputs = ScriptableRenderPassInput.Depth;
            if (normalsSource == LoogaShadowNormalsSource.DepthNormalsPass)
                inputs |= ScriptableRenderPassInput.Normal;

            return inputs;
        }

        private static TextureHandle GetNormalsTexture(
            UniversalResourceData resourceData,
            LoogaShadowNormalsSource normalsSource)
        {
            return normalsSource switch
            {
                LoogaShadowNormalsSource.GBuffer => resourceData.gBuffer[2],
                LoogaShadowNormalsSource.DepthNormalsPass => resourceData.cameraNormalsTexture,
                _ => TextureHandle.nullHandle
            };
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
                    _clipmapRects[level] = Vector4.zero;
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
                int clipmapResolution = settings.TileResolution;
                float worldTexelSize = radius * 2f / clipmapResolution;

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
                _clipmapRadii[level] = new Vector4(
                    radius,
                    worldTexelSize,
                    1f / worldTexelSize,
                    1f / clipmapResolution);
                _clipmapRects[level] = Vector4.zero;
            }
        }

        private static Matrix4x4 GetAtlasShadowTransform(
            Matrix4x4 view,
            Matrix4x4 projection,
            int level)
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
            private Vector4[] _clipmapRects;
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

            private sealed class PackedPassData
            {
                public RendererListHandle RendererList0;
                public RendererListHandle RendererList1;
                public RendererListHandle RendererList2;
                public RendererListHandle RendererList3;
                public VisibleLight MainLight;
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

            private sealed class SeparatePassData
            {
                public RendererListHandle RendererList;
                public int Level;
                public VisibleLight MainLight;
                public LoogaShadowResolvedSettings Settings;
                public Matrix4x4[] WorldToShadow;
                public Matrix4x4[] ViewMatrices;
                public Matrix4x4[] ProjectionMatrices;
                public Vector4[] ClipmapCenters;
                public Vector4[] ClipmapRadii;
                public Matrix4x4 CameraView;
                public Matrix4x4 CameraProjection;
                public Vector3 CameraPosition;
            }

            public void Setup(
                int mainLightIndex,
                VisibleLight mainLight,
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Matrix4x4[] viewMatrices,
                Matrix4x4[] projectionMatrices,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii,
                Vector4[] clipmapRects)
            {
                _mainLightIndex = mainLightIndex;
                _mainLight = mainLight;
                _settings = settings;
                _worldToShadow = worldToShadow;
                _viewMatrices = viewMatrices;
                _projectionMatrices = projectionMatrices;
                _clipmapCenters = clipmapCenters;
                _clipmapRadii = clipmapRadii;
                _clipmapRects = clipmapRects;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                const bool useRawShadowDepth = false;
                if (_mainLight.light == null ||
                    _mainLightIndex < 0 ||
                    (!useRawShadowDepth && !EnsureCopyDepthPass()))
                {
                    return;
                }

                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                CullContextData cullContextData = frameData.Get<CullContextData>();
                CullingResults shadowCullResults = CullShadowCasters(
                    cameraData,
                    cullContextData,
                    renderingData.cullResults);
                LoogaShadowFrameData shadowFrameData = frameData.GetOrCreate<LoogaShadowFrameData>();
                shadowFrameData.HasShadowCasters =
                    shadowCullResults.GetShadowCasterBounds(
                        _mainLightIndex,
                        out _);
                if (!shadowFrameData.HasShadowCasters)
                    return;

                RecordPackedAtlas(
                    renderGraph,
                    frameData,
                    cameraData,
                    shadowCullResults,
                    shadowFrameData,
                    useRawShadowDepth);
            }

            private void RecordPackedAtlas(
                RenderGraph renderGraph,
                ContextContainer frameData,
                UniversalCameraData cameraData,
                CullingResults shadowCullResults,
                LoogaShadowFrameData shadowFrameData,
                bool useRawShadowDepth)
            {
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
                TextureHandle depthAtlas = useRawShadowDepth
                    ? atlas
                    : renderGraph.CreateTexture(new TextureDesc(
                        _settings.AtlasResolution,
                        _settings.AtlasResolution)
                    {
                        name = "Looga Virtual Shadow Raw Depth",
                        format = GraphicsFormat.R16_UNorm,
                        clearBuffer = false,
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    });

                for (int level = 0; level < MaximumClipmapCount; level++)
                {
                    shadowFrameData.Clipmaps[level] = atlas;
                    shadowFrameData.DepthClipmaps[level] = depthAtlas;
                }

                IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Looga Shadows Render Clipmaps",
                    out PackedPassData passData,
                    _profilingSampler);
                passData.RendererList0 = rendererList0;
                passData.RendererList1 = rendererList1;
                passData.RendererList2 = rendererList2;
                passData.RendererList3 = rendererList3;
                passData.MainLight = _mainLight;
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
                builder.SetRenderFunc(static (PackedPassData data, RasterGraphContext context) =>
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
                        context.cmd.SetGlobalVector(
                            ShadowBias,
                            GetCasterShadowBias(
                                data.MainLight,
                                data.ClipmapRadii[level].y,
                                data.Settings));
                        context.cmd.SetViewProjectionMatrices(
                            data.ViewMatrices[level],
                            data.ProjectionMatrices[level]);
                        context.cmd.DrawRendererList(data.GetRendererList(level));
                    }
                    context.cmd.SetGlobalDepthBias(0f, 0f);
                    context.cmd.SetViewProjectionMatrices(data.CameraView, data.CameraProjection);
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
                        data.Settings.ClipmapCount);
                });
                builder.Dispose();

                if (!useRawShadowDepth)
                {
                    _copyDepthPass.Render(
                        renderGraph,
                        frameData,
                        depthAtlas,
                        atlas,
                        passName: "Looga Shadows Copy Raw Depth");
                }
            }

            private void RecordAsymmetricClipmaps(
                RenderGraph renderGraph,
                ContextContainer frameData,
                UniversalCameraData cameraData,
                CullingResults shadowCullResults,
                LoogaShadowFrameData shadowFrameData,
                bool useRawShadowDepth)
            {
                for (int level = 0; level < _settings.ClipmapCount; level++)
                {
                    int resolution = _settings.GetClipmapResolution(level);
                    RenderTextureDescriptor descriptor = new(
                        resolution,
                        resolution,
                        RenderTextureFormat.Shadowmap,
                        32)
                    {
                        shadowSamplingMode = ShadowSamplingMode.CompareDepths,
                        msaaSamples = 1,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    TextureHandle clipmap = renderGraph.CreateTexture(new TextureDesc(descriptor)
                    {
                        name = $"Looga Virtual Shadow Clipmap {level}",
                        clearBuffer = true,
                        clearColor = SystemInfo.usesReversedZBuffer ? Color.black : Color.white,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    });
                    TextureHandle depthClipmap = useRawShadowDepth
                        ? clipmap
                        : renderGraph.CreateTexture(new TextureDesc(resolution, resolution)
                        {
                            name = $"Looga Virtual Shadow Raw Depth {level}",
                            format = GraphicsFormat.R16_UNorm,
                            clearBuffer = false,
                            filterMode = FilterMode.Point,
                            wrapMode = TextureWrapMode.Clamp
                        });
                    shadowFrameData.Clipmaps[level] = clipmap;
                    shadowFrameData.DepthClipmaps[level] = depthClipmap;
                    RendererListHandle rendererList = CreateShadowRendererList(
                        renderGraph,
                        shadowCullResults);

                    IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                        $"Looga Shadows Render Clipmap {level}",
                        out SeparatePassData passData,
                        _profilingSampler);
                    passData.RendererList = rendererList;
                    passData.Level = level;
                    passData.MainLight = _mainLight;
                    passData.Settings = _settings;
                    passData.WorldToShadow = _worldToShadow;
                    passData.ViewMatrices = _viewMatrices;
                    passData.ProjectionMatrices = _projectionMatrices;
                    passData.ClipmapCenters = _clipmapCenters;
                    passData.ClipmapRadii = _clipmapRadii;
                    passData.CameraView = cameraData.GetViewMatrix();
                    passData.CameraProjection = cameraData.GetProjectionMatrix();
                    passData.CameraPosition = cameraData.worldSpaceCameraPos;

                    builder.UseRendererList(rendererList);
                    builder.SetRenderAttachmentDepth(clipmap, AccessFlags.Write);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (SeparatePassData data, RasterGraphContext context) =>
                    {
                        int level = data.Level;
                        int resolution = data.Settings.GetClipmapResolution(level);
                        Vector3 direction = -data.MainLight.light.transform.forward.normalized;
                        context.cmd.SetGlobalVector(WorldSpaceCameraPosition, data.CameraPosition);
                        context.cmd.SetGlobalVector(LightDirection, new Vector4(direction.x, direction.y, direction.z, 0f));
                        context.cmd.SetGlobalVector(LightPosition, new Vector4(-direction.x, -direction.y, -direction.z, 0f));
                        context.cmd.SetKeyword(LoogaShadowShaderIds.CastingPunctualLightShadow, false);
                        context.cmd.SetViewport(new Rect(0f, 0f, resolution, resolution));
                        context.cmd.SetGlobalDepthBias(1f, 2.5f);
                        context.cmd.SetGlobalVector(
                            ShadowBias,
                            GetCasterShadowBias(
                                data.MainLight,
                                data.ClipmapRadii[level].y,
                                data.Settings));
                        context.cmd.SetViewProjectionMatrices(
                            data.ViewMatrices[level],
                            data.ProjectionMatrices[level]);
                        context.cmd.DrawRendererList(data.RendererList);
                        context.cmd.SetGlobalDepthBias(0f, 0f);
                        context.cmd.SetViewProjectionMatrices(
                            data.CameraView,
                            data.CameraProjection);
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
                            data.Settings.ClipmapCount);
                    });
                    builder.Dispose();

                    if (!useRawShadowDepth)
                    {
                        _copyDepthPass.Render(
                            renderGraph,
                            frameData,
                            depthClipmap,
                            clipmap,
                            passName: $"Looga Shadows Copy Raw Depth {level}");
                    }
                }

                for (int level = _settings.ClipmapCount; level < MaximumClipmapCount; level++)
                {
                    shadowFrameData.Clipmaps[level] = shadowFrameData.Clipmaps[0];
                    shadowFrameData.DepthClipmaps[level] = shadowFrameData.DepthClipmaps[0];
                }
            }

            private static bool SupportsRawShadowDepthSampling()
            {
                return SystemInfo.supportsRawShadowDepthSampling;
            }

            private static Vector4 GetCasterShadowBias(
                VisibleLight visibleLight,
                float worldTexelSize,
                LoogaShadowResolvedSettings settings)
            {
                Light light = visibleLight.light;
                if (light == null)
                    return new Vector4(0f, 0f, (float)LightType.Directional, 0f);

                float kernelRadius = 1f;
                if (light.shadows == LightShadows.Soft)
                {
                    SoftShadowQuality quality = SoftShadowQuality.Medium;
                    if (light.TryGetComponent(out UniversalAdditionalLightData additionalLightData))
                        quality = additionalLightData.softShadowQuality;

                    kernelRadius = quality switch
                    {
                        SoftShadowQuality.Low => 1.5f,
                        SoftShadowQuality.High => 3.5f,
                        _ => 2.5f
                    };
                }

                float depthBias = Mathf.Max(
                    light.shadowBias * worldTexelSize * kernelRadius,
                    settings.DepthBias * kernelRadius);
                float normalBias = Mathf.Max(
                    light.shadowNormalBias * worldTexelSize * kernelRadius,
                    settings.NormalBias * kernelRadius);
                return new Vector4(
                    -depthBias,
                    -normalBias,
                    (float)LightType.Directional,
                    0f);
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
            private const int RefilterShaderPass = 9;
            private readonly ProfilingSampler _profilingSampler = new("Looga Shadows Resolve Virtual Clipmaps");
            private Material _material;
            private LoogaShadowResolvedSettings _settings;
            private Matrix4x4[] _worldToShadow;
            private Vector4[] _clipmapCenters;
            private Vector4[] _clipmapRadii;
            private Vector4[] _clipmapRects;
            private Vector3 _lightDirection;
            private LoogaShadowNormalsSource _normalsSource;
            private bool _requiresCameraNormals;
            private bool _normalsOctEncoded;

            private sealed class PassData
            {
                public TextureHandle Clipmap0;
                public TextureHandle Clipmap1;
                public TextureHandle Clipmap2;
                public TextureHandle Clipmap3;
                public TextureHandle DepthClipmap0;
                public TextureHandle DepthClipmap1;
                public TextureHandle DepthClipmap2;
                public TextureHandle DepthClipmap3;
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
                public Vector4[] ClipmapRects;
                public Vector3 LightDirection;
                public LoogaShadowNormalsSource NormalsSource;
                public bool RequiresCameraNormals;
                public bool NormalsOctEncoded;
            }

            private sealed class FullyLitPassData
            {
            }

            public void Setup(
                Material material,
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii,
                Vector4[] clipmapRects,
                Vector3 lightDirection,
                bool usesDeferredLighting,
                bool usesAccurateGBufferNormals)
            {
                _material = material;
                _settings = settings;
                _worldToShadow = worldToShadow;
                _clipmapCenters = clipmapCenters;
                _clipmapRadii = clipmapRadii;
                _clipmapRects = clipmapRects;
                _lightDirection = lightDirection;
                _normalsSource = GetEffectiveNormalsSource(
                    settings.NormalsSource,
                    usesDeferredLighting);
                _requiresCameraNormals =
                    _normalsSource != LoogaShadowNormalsSource.ReconstructFromDepth;
                _normalsOctEncoded =
                    _requiresCameraNormals && usesAccurateGBufferNormals;
                _material.DisableKeyword("_LOOGA_SEPARATE_CLIPMAPS");
                ConfigureInput(GetRequiredInputs(_normalsSource));
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null)
                    return;

                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!frameData.Contains<LoogaShadowFrameData>())
                    return;

                LoogaShadowFrameData shadowFrameData = frameData.Get<LoogaShadowFrameData>();
                if (!shadowFrameData.HasShadowCasters)
                {
                    RecordFullyLitShadow(
                        renderGraph,
                        cameraData,
                        shadowFrameData);
                    return;
                }

                TextureHandle clipmap0 = shadowFrameData.Clipmaps[0];
                TextureHandle clipmap1 = shadowFrameData.Clipmaps[1];
                TextureHandle clipmap2 = shadowFrameData.Clipmaps[2];
                TextureHandle clipmap3 = shadowFrameData.Clipmaps[3];
                TextureHandle depthClipmap0 = shadowFrameData.DepthClipmaps[0];
                TextureHandle depthClipmap1 = shadowFrameData.DepthClipmaps[1];
                TextureHandle depthClipmap2 = shadowFrameData.DepthClipmaps[2];
                TextureHandle depthClipmap3 = shadowFrameData.DepthClipmaps[3];
                TextureHandle cameraDepth = resourceData.cameraDepthTexture.IsValid()
                    ? resourceData.cameraDepthTexture
                    : resourceData.activeDepthTexture;
                TextureHandle cameraNormals = GetNormalsTexture(
                    resourceData,
                    _normalsSource);
                if (!clipmap0.IsValid() || !clipmap1.IsValid() ||
                    !clipmap2.IsValid() || !clipmap3.IsValid() ||
                    !depthClipmap0.IsValid() || !depthClipmap1.IsValid() ||
                    !depthClipmap2.IsValid() || !depthClipmap3.IsValid() ||
                    !cameraDepth.IsValid() ||
                    (_requiresCameraNormals && !cameraNormals.IsValid()))
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
                    filterMode = FilterMode.Point,
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

                shadowFrameData.RawVisibility = rawTarget;
                shadowFrameData.ResolvedVisibility = target;

                using IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(
                    "Looga Shadows Resolve Virtual Clipmaps",
                    out PassData passData,
                    _profilingSampler);

                passData.Clipmap0 = clipmap0;
                passData.Clipmap1 = clipmap1;
                passData.Clipmap2 = clipmap2;
                passData.Clipmap3 = clipmap3;
                passData.DepthClipmap0 = depthClipmap0;
                passData.DepthClipmap1 = depthClipmap1;
                passData.DepthClipmap2 = depthClipmap2;
                passData.DepthClipmap3 = depthClipmap3;
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
                passData.ClipmapRects = _clipmapRects;
                passData.LightDirection = _lightDirection;
                passData.NormalsSource = _normalsSource;
                passData.RequiresCameraNormals = _requiresCameraNormals;
                passData.NormalsOctEncoded = _normalsOctEncoded;

                builder.UseAllGlobalTextures(true);
                builder.UseTexture(clipmap0, AccessFlags.Read);
                builder.UseTexture(clipmap1, AccessFlags.Read);
                builder.UseTexture(clipmap2, AccessFlags.Read);
                builder.UseTexture(clipmap3, AccessFlags.Read);
                builder.UseTexture(depthClipmap0, AccessFlags.Read);
                builder.UseTexture(depthClipmap1, AccessFlags.Read);
                builder.UseTexture(depthClipmap2, AccessFlags.Read);
                builder.UseTexture(depthClipmap3, AccessFlags.Read);
                builder.UseTexture(rawTarget, AccessFlags.ReadWrite);
                builder.UseTexture(denoiseTarget, AccessFlags.ReadWrite);
                builder.UseTexture(target, AccessFlags.ReadWrite);
                builder.UseTexture(cameraDepth, AccessFlags.Read);
                if (_requiresCameraNormals)
                    builder.UseTexture(cameraNormals, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetGlobalTextureAfterPass(target, LoogaShadowShaderIds.MainLightShadowTexture);
                builder.SetGlobalTextureAfterPass(target, LoogaShadowShaderIds.UrpScreenSpaceShadowTexture);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    RTHandle clipmap0 = data.Clipmap0;
                    RTHandle clipmap1 = data.Clipmap1;
                    RTHandle clipmap2 = data.Clipmap2;
                    RTHandle clipmap3 = data.Clipmap3;
                    RTHandle depthClipmap0 = data.DepthClipmap0;
                    RTHandle depthClipmap1 = data.DepthClipmap1;
                    RTHandle depthClipmap2 = data.DepthClipmap2;
                    RTHandle depthClipmap3 = data.DepthClipmap3;
                    RTHandle rawTarget = data.RawTarget;
                    RTHandle denoiseTarget = data.DenoiseTarget;
                    RTHandle target = data.Target;
                    RTHandle cameraDepth = data.CameraDepth;
                    RTHandle cameraNormals = data.RequiresCameraNormals
                        ? data.CameraNormals
                        : null;
                    context.cmd.SetGlobalTexture(
                        LoogaShadowShaderIds.CameraDepthTexture,
                        cameraDepth);
                    if (cameraNormals != null)
                    {
                        context.cmd.SetGlobalTexture(
                            LoogaShadowShaderIds.CameraNormalsTexture,
                            cameraNormals);
                    }
                    context.cmd.SetRenderTarget(
                        rawTarget,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowClipmaps[0], clipmap0);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowClipmaps[1], clipmap1);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowClipmaps[2], clipmap2);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowClipmaps[3], clipmap3);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowDepthClipmaps[0], depthClipmap0);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowDepthClipmaps[1], depthClipmap1);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowDepthClipmaps[2], depthClipmap2);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowDepthClipmaps[3], depthClipmap3);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowAtlas, clipmap0);
                    context.cmd.SetGlobalTexture(LoogaShadowShaderIds.VirtualShadowDepthAtlas, depthClipmap0);
                    ApplySettings(context.cmd, data);
                    Blitter.BlitTexture(context.cmd, depthClipmap0, Vector2.one, data.Material, ResolveShaderPass);
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
                    context.cmd.SetRenderTarget(
                        denoiseTarget,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DenoiseDirection,
                        new Vector4(2f, 0f, 0f, 0f));
                    Blitter.BlitTexture(context.cmd, target, Vector2.one, data.Material, DenoiseShaderPass);
                    context.cmd.SetRenderTarget(
                        target,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DenoiseDirection,
                        new Vector4(0f, 2f, 0f, 0f));
                    Blitter.BlitTexture(context.cmd, denoiseTarget, Vector2.one, data.Material, DenoiseShaderPass);
                    context.cmd.SetRenderTarget(
                        rawTarget,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    Blitter.BlitTexture(
                        context.cmd,
                        target,
                        Vector2.one,
                        data.Material,
                        RefilterShaderPass);
                    context.cmd.SetRenderTarget(
                        denoiseTarget,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DenoiseDirection,
                        new Vector4(1f, 0f, 0f, 0f));
                    Blitter.BlitTexture(
                        context.cmd,
                        rawTarget,
                        Vector2.one,
                        data.Material,
                        DenoiseShaderPass);
                    context.cmd.SetRenderTarget(
                        target,
                        RenderBufferLoadAction.DontCare,
                        RenderBufferStoreAction.Store);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.DenoiseDirection,
                        new Vector4(0f, 1f, 0f, 0f));
                    Blitter.BlitTexture(
                        context.cmd,
                        denoiseTarget,
                        Vector2.one,
                        data.Material,
                        DenoiseShaderPass);
                    context.cmd.SetGlobalInteger(LoogaShadowShaderIds.ShadowsEnabled, 1);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadows, false);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowCascades, false);
                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowScreen, true);
                });
            }

            private static void RecordFullyLitShadow(
                RenderGraph renderGraph,
                UniversalCameraData cameraData,
                LoogaShadowFrameData shadowFrameData)
            {
                RenderTextureDescriptor descriptor =
                    cameraData.cameraTargetDescriptor;
                descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                descriptor.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                TextureHandle target = renderGraph.CreateTexture(
                    new TextureDesc(descriptor)
                    {
                        name = "Looga Main Light Shadow Fully Lit",
                        clearBuffer = true,
                        clearColor = new Color(1f, 0f, 0f, 1f),
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    });

                shadowFrameData.RawVisibility = target;
                shadowFrameData.ResolvedVisibility = target;

                using IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass(
                        "Looga Shadows No Visible Casters",
                        out FullyLitPassData _);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetGlobalTextureAfterPass(
                    target,
                    LoogaShadowShaderIds.MainLightShadowTexture);
                builder.SetGlobalTextureAfterPass(
                    target,
                    LoogaShadowShaderIds.UrpScreenSpaceShadowTexture);
                builder.SetRenderFunc(static (
                    FullyLitPassData _,
                    RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalInteger(
                        LoogaShadowShaderIds.ShadowsEnabled,
                        1);
                    context.cmd.SetKeyword(
                        LoogaShadowShaderIds.MainLightShadows,
                        false);
                    context.cmd.SetKeyword(
                        LoogaShadowShaderIds.MainLightShadowCascades,
                        false);
                    context.cmd.SetKeyword(
                        LoogaShadowShaderIds.MainLightShadowScreen,
                        true);
                });
            }

            private static void ApplySettings(IBaseCommandBuffer command, PassData data)
            {
                LoogaShadowResolvedSettings settings = data.Settings;
                command.SetGlobalMatrixArray(LoogaShadowShaderIds.WorldToShadow, data.WorldToShadow);
                command.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapCenters, data.ClipmapCenters);
                command.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapRadii, data.ClipmapRadii);
                command.SetGlobalVectorArray(LoogaShadowShaderIds.ClipmapRects, data.ClipmapRects);
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
                command.SetGlobalInteger(
                    LoogaShadowShaderIds.NormalsSource,
                    (int)data.NormalsSource);
                command.SetGlobalInteger(
                    LoogaShadowShaderIds.NormalsOctEncoded,
                    data.NormalsOctEncoded ? 1 : 0);
            }
        }

        /// <summary>
        /// Restores URP's world-space transparent shadow path using Looga's packed clipmap atlas.
        /// Transparent shaders cannot use the opaque screen mask because it represents the surface
        /// behind them, but URP's standard forward passes can sample this atlas at their own world
        /// position without requiring material changes.
        /// </summary>
        private sealed class TransparentShadowReceiverPass : ScriptableRenderPass
        {
            private const int UrpShadowMatrixCount = MaximumClipmapCount + 1;
            private readonly ProfilingSampler _profilingSampler =
                new("Looga Shadows Bind Transparent Clipmaps");
            private readonly Matrix4x4[] _worldToShadow =
                new Matrix4x4[UrpShadowMatrixCount];
            private readonly Vector4[] _splitSpheres =
                new Vector4[MaximumClipmapCount];
            private Vector4 _splitSphereRadii;
            private LoogaShadowResolvedSettings _settings;
            private float _shadowStrength;
            private float _softShadowQuality;

            private sealed class PassData
            {
                public TextureHandle Atlas;
                public bool HasAtlas;
                public int ClipmapCount;
                public int AtlasResolution;
                public Matrix4x4[] WorldToShadow;
                public Vector4[] SplitSpheres;
                public Vector4 SplitSphereRadii;
                public Vector4 ShadowParams;
            }

            public void Setup(
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii,
                Light mainLight)
            {
                _settings = settings;
                _shadowStrength = mainLight != null
                    ? mainLight.shadowStrength
                    : 1f;
                _softShadowQuality = mainLight != null &&
                    mainLight.shadows == LightShadows.Soft
                        ? 1f
                        : 0f;

                Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
                noOpShadowMatrix.m22 = SystemInfo.usesReversedZBuffer
                    ? 1f
                    : 0f;

                _splitSphereRadii = Vector4.zero;
                for (int level = 0; level < MaximumClipmapCount; level++)
                {
                    bool active = level < settings.ClipmapCount;
                    _worldToShadow[level] = active
                        ? worldToShadow[level]
                        : noOpShadowMatrix;
                    _splitSpheres[level] = active
                        ? clipmapCenters[level]
                        : Vector4.zero;
                    _splitSphereRadii[level] = active
                        ? clipmapRadii[level].x * clipmapRadii[level].x
                        : 0f;
                }

                _worldToShadow[MaximumClipmapCount] = noOpShadowMatrix;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                bool hasAtlas = false;
                TextureHandle atlas = TextureHandle.nullHandle;
                if (frameData.Contains<LoogaShadowFrameData>())
                {
                    LoogaShadowFrameData shadowFrameData =
                        frameData.Get<LoogaShadowFrameData>();
                    hasAtlas =
                        shadowFrameData.HasShadowCasters &&
                        shadowFrameData.Clipmaps[0].IsValid();
                    if (hasAtlas)
                        atlas = shadowFrameData.Clipmaps[0];
                }

                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Looga Shadows Bind Transparent Clipmaps",
                    out PassData passData,
                    _profilingSampler);

                float shadowDistanceSquared =
                    _settings.ShadowDistance * _settings.ShadowDistance;
                float fadeStartSquared = shadowDistanceSquared * 0.81f;
                float fadeRangeSquared = Mathf.Max(
                    shadowDistanceSquared - fadeStartSquared,
                    0.0001f);

                passData.Atlas = atlas;
                passData.HasAtlas = hasAtlas;
                passData.ClipmapCount = _settings.ClipmapCount;
                passData.AtlasResolution = _settings.AtlasResolution;
                passData.WorldToShadow = _worldToShadow;
                passData.SplitSpheres = _splitSpheres;
                passData.SplitSphereRadii = _splitSphereRadii;
                passData.ShadowParams = new Vector4(
                    _shadowStrength,
                    _softShadowQuality,
                    1f / fadeRangeSquared,
                    -fadeStartSquared / fadeRangeSquared);

                builder.SetRenderAttachment(
                    resourceData.activeColorTexture,
                    0,
                    AccessFlags.ReadWrite);
                if (hasAtlas)
                    builder.UseTexture(atlas, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    if (!data.HasAtlas)
                    {
                        context.cmd.SetKeyword(
                            LoogaShadowShaderIds.MainLightShadowScreen,
                            false);
                        context.cmd.SetKeyword(
                            LoogaShadowShaderIds.MainLightShadows,
                            false);
                        context.cmd.SetKeyword(
                            LoogaShadowShaderIds.MainLightShadowCascades,
                            false);
                        return;
                    }

                    float inverseAtlasResolution =
                        1f / data.AtlasResolution;
                    float halfTexel = inverseAtlasResolution * 0.5f;

                    context.cmd.SetGlobalTexture(
                        "_MainLightShadowmapTexture",
                        data.Atlas);
                    context.cmd.SetGlobalMatrixArray(
                        LoogaShadowShaderIds.UrpMainLightWorldToShadow,
                        data.WorldToShadow);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpCascadeShadowSplitSpheres0,
                        data.SplitSpheres[0]);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpCascadeShadowSplitSpheres1,
                        data.SplitSpheres[1]);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpCascadeShadowSplitSpheres2,
                        data.SplitSpheres[2]);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpCascadeShadowSplitSpheres3,
                        data.SplitSpheres[3]);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpCascadeShadowSplitSphereRadii,
                        data.SplitSphereRadii);
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpMainLightShadowOffset0,
                        new Vector4(
                            -halfTexel,
                            -halfTexel,
                            halfTexel,
                            -halfTexel));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpMainLightShadowOffset1,
                        new Vector4(
                            -halfTexel,
                            halfTexel,
                            halfTexel,
                            halfTexel));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpMainLightShadowmapSize,
                        new Vector4(
                            inverseAtlasResolution,
                            inverseAtlasResolution,
                            data.AtlasResolution,
                            data.AtlasResolution));
                    context.cmd.SetGlobalVector(
                        LoogaShadowShaderIds.UrpMainLightShadowParams,
                        data.ShadowParams);

                    context.cmd.SetKeyword(LoogaShadowShaderIds.MainLightShadowScreen, false);
                    context.cmd.SetKeyword(
                        LoogaShadowShaderIds.MainLightShadows,
                        data.ClipmapCount == 1);
                    context.cmd.SetKeyword(
                        LoogaShadowShaderIds.MainLightShadowCascades,
                        data.ClipmapCount > 1);
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
            private Vector4[] _clipmapRects;
            private Vector3 _lightDirection;
            private LoogaShadowDebugView _debugView;
            private LoogaShadowNormalsSource _normalsSource;
            private bool _requiresCameraNormals;
            private bool _normalsOctEncoded;

            private sealed class PassData
            {
                public TextureHandle Clipmap0;
                public TextureHandle Clipmap1;
                public TextureHandle Clipmap2;
                public TextureHandle Clipmap3;
                public TextureHandle DepthClipmap0;
                public TextureHandle DepthClipmap1;
                public TextureHandle DepthClipmap2;
                public TextureHandle DepthClipmap3;
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
                public Vector4[] ClipmapRects;
                public Vector3 LightDirection;
                public LoogaShadowNormalsSource NormalsSource;
                public bool RequiresCameraNormals;
                public bool NormalsOctEncoded;
            }

            public void Setup(
                Material material,
                LoogaShadowResolvedSettings settings,
                Matrix4x4[] worldToShadow,
                Vector4[] clipmapCenters,
                Vector4[] clipmapRadii,
                Vector4[] clipmapRects,
                Vector3 lightDirection,
                bool usesDeferredLighting,
                bool usesAccurateGBufferNormals)
            {
                _material = material;
                _settings = settings;
                _worldToShadow = worldToShadow;
                _clipmapCenters = clipmapCenters;
                _clipmapRadii = clipmapRadii;
                _clipmapRects = clipmapRects;
                _lightDirection = lightDirection;
                _debugView = settings.DebugView;
                _normalsSource = GetEffectiveNormalsSource(
                    settings.NormalsSource,
                    usesDeferredLighting);
                _requiresCameraNormals =
                    _normalsSource != LoogaShadowNormalsSource.ReconstructFromDepth;
                _normalsOctEncoded =
                    _requiresCameraNormals && usesAccurateGBufferNormals;
                ConfigureInput(GetRequiredInputs(_normalsSource));
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (_material == null || _debugView == LoogaShadowDebugView.Off)
                    return;

                if (!frameData.Contains<LoogaShadowFrameData>())
                    return;

                LoogaShadowFrameData shadowFrameData = frameData.Get<LoogaShadowFrameData>();
                TextureHandle clipmap0 = shadowFrameData.Clipmaps[0];
                TextureHandle clipmap1 = shadowFrameData.Clipmaps[1];
                TextureHandle clipmap2 = shadowFrameData.Clipmaps[2];
                TextureHandle clipmap3 = shadowFrameData.Clipmaps[3];
                TextureHandle depthClipmap0 = shadowFrameData.DepthClipmaps[0];
                TextureHandle depthClipmap1 = shadowFrameData.DepthClipmaps[1];
                TextureHandle depthClipmap2 = shadowFrameData.DepthClipmaps[2];
                TextureHandle depthClipmap3 = shadowFrameData.DepthClipmaps[3];
                TextureHandle rawVisibility = shadowFrameData.RawVisibility;
                TextureHandle resolvedVisibility = shadowFrameData.ResolvedVisibility;
                if (!clipmap0.IsValid() || !clipmap1.IsValid() ||
                    !clipmap2.IsValid() || !clipmap3.IsValid() ||
                    !depthClipmap0.IsValid() || !depthClipmap1.IsValid() ||
                    !depthClipmap2.IsValid() || !depthClipmap3.IsValid() ||
                    !rawVisibility.IsValid() || !resolvedVisibility.IsValid())
                    return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                TextureHandle cameraDepth = resourceData.cameraDepthTexture.IsValid()
                    ? resourceData.cameraDepthTexture
                    : resourceData.activeDepthTexture;
                TextureHandle cameraNormals = GetNormalsTexture(
                    resourceData,
                    _normalsSource);
                if (!cameraDepth.IsValid() ||
                    (_requiresCameraNormals && !cameraNormals.IsValid()))
                    return;

                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(
                    "Looga Shadows Debug Overlay",
                    out PassData passData,
                    _profilingSampler);

                passData.Clipmap0 = clipmap0;
                passData.Clipmap1 = clipmap1;
                passData.Clipmap2 = clipmap2;
                passData.Clipmap3 = clipmap3;
                passData.DepthClipmap0 = depthClipmap0;
                passData.DepthClipmap1 = depthClipmap1;
                passData.DepthClipmap2 = depthClipmap2;
                passData.DepthClipmap3 = depthClipmap3;
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
                passData.ClipmapRects = _clipmapRects;
                passData.LightDirection = _lightDirection;
                passData.NormalsSource = _normalsSource;
                passData.RequiresCameraNormals = _requiresCameraNormals;
                passData.NormalsOctEncoded = _normalsOctEncoded;
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                builder.UseTexture(clipmap0, AccessFlags.Read);
                builder.UseTexture(clipmap1, AccessFlags.Read);
                builder.UseTexture(clipmap2, AccessFlags.Read);
                builder.UseTexture(clipmap3, AccessFlags.Read);
                builder.UseTexture(depthClipmap0, AccessFlags.Read);
                builder.UseTexture(depthClipmap1, AccessFlags.Read);
                builder.UseTexture(depthClipmap2, AccessFlags.Read);
                builder.UseTexture(depthClipmap3, AccessFlags.Read);
                builder.UseTexture(rawVisibility, AccessFlags.Read);
                builder.UseTexture(resolvedVisibility, AccessFlags.Read);
                builder.UseTexture(cameraDepth, AccessFlags.Read);
                if (_requiresCameraNormals)
                    builder.UseTexture(cameraNormals, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    RTHandle debugSource = data.DebugSource;
                    LoogaShadowResolvedSettings settings = data.Settings;
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowClipmap0",
                        data.Clipmap0);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowClipmap1",
                        data.Clipmap1);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowClipmap2",
                        data.Clipmap2);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowClipmap3",
                        data.Clipmap3);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowDepthClipmap0",
                        data.DepthClipmap0);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowDepthClipmap1",
                        data.DepthClipmap1);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowDepthClipmap2",
                        data.DepthClipmap2);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowDepthClipmap3",
                        data.DepthClipmap3);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowAtlas",
                        data.Clipmap0);
                    context.cmd.SetGlobalTexture(
                        "_LoogaVirtualShadowDepthAtlas",
                        data.DepthClipmap0);
                    context.cmd.SetGlobalMatrixArray(
                        LoogaShadowShaderIds.WorldToShadow,
                        data.WorldToShadow);
                    context.cmd.SetGlobalVectorArray(
                        LoogaShadowShaderIds.ClipmapCenters,
                        data.ClipmapCenters);
                    context.cmd.SetGlobalVectorArray(
                        LoogaShadowShaderIds.ClipmapRadii,
                        data.ClipmapRadii);
                    context.cmd.SetGlobalVectorArray(
                        LoogaShadowShaderIds.ClipmapRects,
                        data.ClipmapRects);
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
                    context.cmd.SetGlobalInteger(
                        LoogaShadowShaderIds.NormalsSource,
                        (int)data.NormalsSource);
                    context.cmd.SetGlobalInteger(
                        LoogaShadowShaderIds.NormalsOctEncoded,
                        data.NormalsOctEncoded ? 1 : 0);
                    if (data.RequiresCameraNormals)
                    {
                        context.cmd.SetGlobalTexture(
                            LoogaShadowShaderIds.CameraNormalsTexture,
                            data.CameraNormals);
                    }
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
