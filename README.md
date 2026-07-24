# Looga Shadows

Looga Shadows is a RenderGraph-first directional shadow renderer for Unity 6 URP. It renders
ShadowCaster geometry into package-owned, camera-centered clipmaps and publishes the resolved
visibility through URP's screen-space main-light contract.

## Current Scope

- package-owned directional shadow depth atlas;
- stable, texel-snapped clipmap coverage from near detail to long range;
- receiver-plane PCSS with contact hardening and continuous penumbrae;
- smooth clipmap overlap and distance fading;
- quality presets controlling atlas resolution, clipmap count, blocker samples, and filter samples;
- optional per-light profile and physical source-angle overrides;
- standard URP and Looga custom-shader integration;
- clipmap, texel, depth, normal, penumbra, and visibility debug views;
- validation scene generator and runtime debugger.

Soft visibility is resolved directly from the owned clipmap depth. Blocker discovery samples the
original depth on an analytic receiver plane, then filters the hardware comparison using the
physical source angle and measured receiver-to-blocker separation. A penumbra-driven,
depth-aware reconstruction pass removes sparse-sample contouring without imposing a fixed outer
blur boundary.

Material shaders can sample the final screen-space visibility through
`Runtime/Integration/LoogaShadows.hlsl`.

The renderer feature owns the baseline settings directly. Assigning a profile to the active
directional light's **Looga Shadow Light** component overrides that baseline for the light.

Looga Shadows does not sample URP's main-light shadow atlas or fall back to it for transparent
rendering.

Standard URP transparent shaders are currently rendered without main-light shadow reception. A
future world-space Looga transparent integration will sample the owned clipmaps directly.

## Debug Views

Debug views are translucent Scene/Game overlays rather than replacement renders:

- **Final Visibility** highlights final shadow attenuation in red.
- **Raw Visibility** highlights unfiltered attenuation in cyan.
- **Penumbra** highlights filtered penumbra width in orange.
- **Clipmap Levels** tints receivers by their selected clipmap.
- **Virtual Texels** tints shadow-map footprint and adaptive grid density.
- **Linear Depth** applies logarithmic eye-depth shading.
- **World Normals** overlays depth-reconstructed geometric normals.

Visibility and penumbra overlays remain unchanged where the selected signal is absent.
A uniform clipmap tint means that the current view is correctly contained by one clipmap level.

The current physical clipmap atlas and receiver-plane PCSS resolve are the rendering foundation
for a later sparse virtual-page allocator and static-page cache. It does not yet allocate or cache
128-texel virtual pages like a complete UE5-style Virtual Shadow Map implementation.

See `Documentation~/setup.md` for installation and renderer configuration.
