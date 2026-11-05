// WindUnifier public API for other mods (0.4.0).
// WindCore is internal, but these wrappers are part of the supported surface.
using UnityEngine;

namespace WindUnifier
{
    public static class API
    {
        /// <summary>True when the plugin is loaded, enabled and the unified slot is active.</summary>
        public static bool IsActive { get { return WindCore.Active && WindUnifierAddon.Cfg.enabled; } }

        /// <summary>Short status string (also shown by debugHud).</summary>
        public static string Status { get { return WindCore.Status; } }

        /// <summary>
        /// Aerodynamic unified wind in world space (NW -> KWP -> zero). Use this when
        /// your mod must consume exactly the same wind as FAR.
        /// </summary>
        public static Vector3 UnifiedWind(CelestialBody body, Part part, Vector3 position)
        {
            return WindCore.UnifiedWind(body, part, position);
        }

        /// <summary>
        /// Visual-only wind (unified wind vs Nature's Fury local wind, larger magnitude).
        /// Do not feed this into FAR/aerodynamics: NF already applies its own forces.
        /// </summary>
        public static Vector3 VisualWind(CelestialBody body, Part part, Vector3 position)
        {
            return WindCore.VisualWind(body, part, position);
        }
    }
}
