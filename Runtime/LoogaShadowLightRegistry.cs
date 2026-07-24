using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Shadows
{
    /// <summary>
    /// Maps authored light overrides without introducing a required scene singleton.
    /// </summary>
    internal static class LoogaShadowLightRegistry
    {
        private static readonly Dictionary<int, LoogaShadowLight> Lights = new(4);

        public static void Register(LoogaShadowLight shadowLight)
        {
            Light light = shadowLight != null ? shadowLight.Light : null;
            if (light != null)
                Lights[light.GetInstanceID()] = shadowLight;
        }

        public static void Unregister(LoogaShadowLight shadowLight)
        {
            Light light = shadowLight != null ? shadowLight.Light : null;
            if (light == null)
                return;

            int lightId = light.GetInstanceID();
            if (Lights.TryGetValue(lightId, out LoogaShadowLight current) && current == shadowLight)
                Lights.Remove(lightId);
        }

        public static bool TryGet(Light light, out LoogaShadowLight shadowLight)
        {
            shadowLight = null;
            if (light == null || !Lights.TryGetValue(light.GetInstanceID(), out LoogaShadowLight candidate))
                return false;

            if (candidate == null || !candidate.isActiveAndEnabled)
            {
                Lights.Remove(light.GetInstanceID());
                return false;
            }

            shadowLight = candidate;
            return true;
        }
    }
}
