# Shader Integration

Custom shaders can include the package helper:

```hlsl
#include "Packages/com.loogasoft.loogashadows/Runtime/Integration/LoogaShadows.hlsl"
```

Then sample the resolved main-light shadow using normalized screen coordinates:

```hlsl
half mainLightShadow = LoogaSampleMainLightShadow(screenUV);
```

The helper returns fully lit visibility when Looga Shadows is inactive. Looga Lighting can
use this contract without taking a C# assembly dependency on Looga Shadows.
