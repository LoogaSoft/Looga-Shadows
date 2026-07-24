# Setup

1. Add **Looga Shadows** to the project through Package Manager.
2. Open the active Universal Renderer Data asset.
3. Disable or remove URP's built-in **Screen Space Shadows** renderer feature.
4. Add **Looga Shadows** as a renderer feature and configure its settings.
5. Enable a depth texture and opaque texture on the active URP asset.
6. Keep shadows enabled on the scene's main directional Light so ShadowCaster geometry participates.

Looga Shadows owns the directional depth atlas used by its resolve pass. URP main-light atlas
resolution and cascade settings do not control Looga shadow quality. Start with the **High** quality
preset, a near clipmap radius around 8 meters, and a shadow distance appropriate for the level.

Renderer-feature settings are sufficient for most scenes. Add **Looga Shadow Light** to the active
main directional Light when that light needs a profile override or a different source angular
diameter. Create override profiles through **Assets > Create > LoogaSoft > Shadows > Shadow
Profile**. The real sun is approximately 0.53 degrees.

The feature publishes both URP's `_ScreenSpaceShadowmapTexture` and the package-owned
`_LoogaMainLightShadowTexture`. Standard URP shaders use the former automatically. Custom shaders
can include `Runtime/Integration/LoogaShadows.hlsl`.

Standard URP transparent shaders currently render without main-light shadow reception. This is
intentional: falling back to URP's atlas would violate the package-owned path, while reusing the
opaque screen mask would evaluate the wrong receiver depth.

Use **LoogaSoft > Shadows > Debugger** to inspect atlas resolution, clipmap count, active camera,
settings source, and debug output. Debug views are selected on the renderer feature or active
light override profile.

Use **LoogaSoft > Shadows > Create Validation Scene** for repeatable visual testing. First verify
**Raw Visibility** and **Clipmap Levels** with TAA and dynamic resolution disabled. Clipmap origins
are quantized to world texels, so translating the camera by less than one texel must not move the
underlying shadow samples.
