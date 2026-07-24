# Changelog

## [0.3.0] - 2026-07-22

- Removed the screen-space contact-shadow pass, settings, debug mode, and validation fixtures.
- Made renderer-feature settings authoritative, with profiles reserved for optional Looga Shadow Light overrides.
- Added one-way migration from the former renderer profile mode into inline renderer settings.
- Corrected contact rays to march from receivers toward the directional light rather than along emitted light rays.
- Replaced noisy contact jitter and coplanar-normal heuristics with stable near-biased depth intersections composed after the depth/normal-aware PCSS reconstruction.
- Replaced experimental normal-curvature cavity detection with the published Uncharted 4/Unity Core AO visibility-cone micro-shadow model.
- Retired the nonphysical micro-shadow radius and sensitivity controls from the profile inspector.
- Added optional, quality-scaled screen-space contact detail for sub-texel gaps while keeping clipmap PCSS authoritative.
- Added distance, screen-edge, depth-thickness, and coplanar-receiver rejection to keep contact detail local and prevent empty-surface artifacts.
- Added Unity Core's AO and shaded-normal micro-shadow function to the custom-shader integration contract.
- Compose short-range contact detail after PCSS reconstruction so thin attachment shadows are no longer averaged away by the denoiser.
- Restrict contact self-rejection to similarly oriented coplanar surfaces and add a dedicated contact-shadow debug view.
- Publish inactive micro-shadow state every camera so optional lighting integrations cannot retain stale add-on settings.
- Integrate directional material micro-shadows automatically when the optional Looga Lighting package is present.
- Resolved the raw-depth copy shader from URP's `UniversalRendererResources` and retry initialization when startup import ordering temporarily leaves it unavailable.
- Switched clipmap caster submission to Unity shadow renderer lists so per-renderer modes such as `TwoSided` and `ShadowsOnly` are honored.
- Replaced the SMRT experiment with receiver-plane PCSS over the package-owned directional clipmaps.
- Replaced coarse conservative blocker depths with direct mip-0 blocker measurements so penumbrae grow from actual receiver-to-caster separation.
- Added analytic receiver-plane depth interpolation to eliminate camera-angle-dependent ground self-shadowing and curved-receiver comparison errors.
- Added compact, normalized PCF filtering with physical receiver-to-blocker penumbra growth.
- Added penumbra-driven, depth-aware reconstruction without a fixed dilation radius or confidence terminator.
- Removed the unused depth-pyramid allocation and compute dispatch after blocker measurement moved back to the original depth atlas.
- Reworked quality presets as explicit blocker/filter budgets, with lower tiers retaining smaller deterministic sample counts.
- Reduced the highest-detail clipmap half-width to one meter, yielding approximately 0.49 mm texels in the Ultra 4096-square clipmap tile.
- Removed the obsolete PCSS blocker search, conservative reduction, and filter-level shader paths.
- Replaced URP-atlas reconstruction with package-owned directional shadow clipmaps.
- Removed the legacy URP-atlas PCSS fallback, temporal history, screen-space contact rays, and spatial denoising passes.
- Added texel-snapped clipmap origins, deterministic contact-hardening filtering, clipmap overlap blending, and smooth distance fading.
- Added package-owned atlas, clipmap-level, and virtual-texel runtime diagnostics.
- Simplified profiles and per-light overrides around physical coverage, source angle, precision, and quality presets.
- Fixed D3D11 compilation of the virtual-texel debug view.
- Allocated an independent shadow renderer list per clipmap so each list executes exactly once per frame.
- Matched URP 17's unsafe RenderGraph screen-space shadow handoff so forward and deferred opaque lighting consume the package-owned mask.
- Switched DX12 atlas filtering to hardware comparison sampling and applied URP's directional caster bias per clipmap.
- Corrected the directional clipmap view orientation and platform shadow transform used by receivers.
- Interpreted receiver depth bias in world-space meters and reduced the default normal offset to keep contact shadows attached.
- Added hardware-bilinear PCSS filtering with comparison-sampler blocker reconstruction for distance-dependent soft edges.
- Culled shadow casters against the largest light-space clipmap so offscreen geometry continues contributing to the atlas.
- Distributed clipmap radii geometrically to reserve substantially more atlas resolution for close-up shadows without reducing long-range coverage.
- Increased PCSS filter budgets and evaluate broad penumbrae from coarser clipmaps so thin casters do not expose individual filter taps at close range.
- Increased blocker-depth precision and made blocker search angular-size-aware to suppress faint offset shadow echoes around isolated casters.
- Reweighted PCSS filtering with a normalized center-biased tent kernel so outer disk taps cannot appear as pale duplicate silhouettes.
- Replaced comparison-sampler depth probing with a URP RenderGraph D32-to-R16 depth copy so blocker search uses actual atlas depths at silhouettes.
- Replaced radial-shell PCF sampling with a deterministic golden-angle disk whose taps occupy distinct radii and use tapered outer weights.
- Removed the point-sampled blocker-confidence mask that reintroduced a second quantized silhouette over the filtered edge.
- Weighted raw blocker depths by hardware comparison coverage and gathered neighboring depths to keep penumbra estimates continuous across atlas texel boundaries.
- Stabilized PCSS silhouettes with a conservative blocker-depth reduction so shallow edge texels cannot collapse the penumbra into detached, stair-stepped rings.
- Kept supported broad penumbrae in the finest containing clipmap instead of prematurely sampling a coarser caster silhouette.
- Added RG16 screen-mask intermediates carrying visibility and physical penumbra width through depth/normal-aware separable reconstruction.
- Rejected isolated blocker probes, scaled penumbrae by multi-sample support, and applied a bounded screen-space reconstruction footprint to suppress grazing-angle outlines.
- Declared and bound the raw-depth atlas dependency for RenderGraph debug overlays.
- Extended blocker discovery for grazing directional lights, concentrated search taps near the receiver, and increased Ultra blocker coverage so broad penumbrae fade to fully lit without a hard outer contour.
- Propagated physical penumbra width through the separable reconstruction pass so neighboring unclassified pixels participate in a continuous edge fade.
- Bounded directional blocker search from receiver depth and light angular size, then stopped broad concentric searches at the nearest supported blocker ring to avoid unrelated-caster washout.
- Replaced the truncated reconstruction Gaussian with a 13-tap compact cosine kernel whose value and slope reach zero at the penumbra boundary.
- Restored production-style average-blocker PCSS so penumbra width changes continuously across blocker populations instead of snapping to the center blocker.
- Made the reconstruction taps contiguous because their job is sample-noise removal, eliminating the interleaved pattern produced by penumbra-scaled screen-space strides.
- Replaced visibility clamps and enclosure classification with HDRP-style bounded receiver-plane depth divergence, limited to 25% of blocker distance to reduce leakage without amputating valid inner penumbrae.
- Switched PCSS filtering to an equal-area, equal-energy disk like the NVIDIA, HDRP, and Umbra reference implementations.
- Bounded screen-space reconstruction by the projected physical penumbra so sub-pixel contact shadows stay attached when a receiver moves into a coarser clipmap.
- Blended cone-weighted contact depths into full-disk outer-penumbra depths before PCSS filtering, removing the blocker-confidence silhouette without sacrificing attached caster contacts.
- Replaced static interleaved-gradient and light-space hash rotations with URP's screen-space blue-noise resource and HDRP-style progressive Fibonacci filtering, removing structured interleaving and perspective-clustered speckles without widening penumbrae.

## [0.2.5] - 2026-07-22

- Replaced atlas-texel-seeded kernel rotation with a fixed low-discrepancy cascade kernel to eliminate camera-driven noise crawling.
- Extended spatial denoising into the unclassified ring outside reconstructed penumbrae to remove hard fade boundaries.
- Added same-plane normal rejection and smooth distance/viewport fades to screen-space contact shadows.

## [0.2.4] - 2026-07-22

- Removed frame-varying penumbra rotation that could remain visible as shifting Monte Carlo noise.
- Added receiver-plane, normal-bias, and light-direction rejection to prevent contact shadows from self-occluding flat ground.
- Retained the denser high and ultra sample budgets with a spatially stable atlas-texel sequence.

## [0.2.3] - 2026-07-22

- Increased the high and ultra blocker/filter sample budgets to reduce visible penumbra quantization.
- Added temporally rotated, shadow-atlas-texel-stable sampling for smooth accumulated penumbrae without coarse world-space patterns.
- Expanded temporal neighborhood clamping to a 3x3 footprint for more robust reconstruction of changing shadow samples.

## [0.2.2] - 2026-07-22

- Replaced quantized world-cell shadow sampling with a stable low-discrepancy disk kernel.
- Stabilized contact-shadow ray steps so camera motion no longer exposes crawling block noise.
- Prevented blocker and penumbra kernels from accumulating against cascade atlas boundaries.
- Included contact-only shadow pixels in the spatial denoising pass.
- Rejected receivers outside the final cascade instead of clamping them into it.

## [0.2.1] - 2026-07-21

- Anchored blocker-search, filter, and contact-shadow jitter to world-space receiver cells.
- Matched temporal reprojection to URP's non-jittered GPU view-projection convention.
- Corrected platform projection handling for contact-shadow rays and history UVs.
- Expanded the generated validation scene with stability, contact, penumbra, thin-geometry, and distance fixtures.

## [0.2.0] - 2026-07-21

- Added hybrid scene authoring through optional Looga Shadow Light components.
- Added blocker search and variable-penumbra filtering for the main directional light.
- Added screen-space contact shadows with configurable range, thickness, and strength.
- Added depth- and normal-aware spatial denoising.
- Added reprojection, neighborhood clamping, and disocclusion-aware temporal stabilization.
- Added bounded per-camera history textures with cut, resize, and projection invalidation.
- Expanded runtime diagnostics and intermediate debug visualizations.

## [0.1.0] - 2026-07-21

- Added the Unity 6 URP RenderGraph renderer feature foundation.
- Added the Looga Shadow Profile and bounded camera registry.
- Added URP and Looga custom-shader texture contracts.
- Added runtime diagnostics, debug views, and validation scene tooling.
