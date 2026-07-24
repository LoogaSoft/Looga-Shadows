# Shadow Validation

Use **LoogaSoft > Shadows > Create Validation Scene** to generate an editable project scene.

The generated scene covers:

- fixed world-space references for camera-motion stability;
- casters at five receiver gaps;
- increasing caster heights for penumbra growth;
- curved, faceted, character-sized, and thin casters;
- a vertical receiver;
- near, mid-distance, and far cascade fixtures;
- named camera waypoints for repeatable strafing and forward-motion tests.

Before comparing results, add and configure the Looga Shadows renderer feature. The generated sun
also contains an optional Looga Shadow Light component so a per-light profile override can be
tested. Capture each debug mode at the project target resolution and verify Game and Scene cameras.

## Recommended Baseline

Start with one shadowed directional light, render scale 1.0, dynamic resolution disabled, and
TAA disabled. Use four cascades and a shadow distance that includes every distance fixture. Move
the camera between the named waypoints while watching **Raw Visibility**: fixed shadow edges and
the sampling pattern must remain attached to the world. Then enable temporal stabilization and
repeat the test in **Resolved Visibility**.

After the baseline passes, validate TAA, dynamic resolution, forward, deferred, camera stacking,
and the target quality tiers one at a time. This keeps a pipeline integration problem from being
mistaken for a filtering problem.
