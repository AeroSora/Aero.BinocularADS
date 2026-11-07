using System;
using UnityEngine;

namespace Aero.BinocularADS
{
    // Use the same renderer filters for each weapon registry.
    internal static class WeaponRendererPolicy
    {
        internal enum Decision
        {
            Candidate,
            Missing,
            OwnedClone,
            UnsupportedType,
            SightPicture,
            Ineligible,
            ReducedLod,
            Beam,
        }

        private static readonly string[] BeamMarkers =
            { "laser", "beam", "lightray", "ray_" };

        internal static Decision Evaluate(
            Renderer renderer,
            bool sightPicture,
            bool ownedClone,
            bool requireEligible)
        {
            if (renderer == null)
                return Decision.Missing;
            if (ownedClone)
                return Decision.OwnedClone;
            if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                return Decision.UnsupportedType;
            if (sightPicture)
                return Decision.SightPicture;
            if (requireEligible && !IsEligible(renderer))
                return Decision.Ineligible;
            if (IsReducedLod(renderer))
                return Decision.ReducedLod;
            if (IsBeam(renderer))
                return Decision.Beam;
            return Decision.Candidate;
        }

        internal static bool IsEligible(Renderer renderer)
        {
            return renderer != null && renderer.enabled && !renderer.forceRenderingOff &&
                   renderer.gameObject.activeInHierarchy;
        }

        internal static bool IsAmmunition(Renderer renderer)
        {
            return renderer != null && IsAmmunition(renderer.name);
        }

        internal static bool IsAmmunition(string name)
        {
            return name != null && name.StartsWith("patron_", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsBeam(Renderer renderer)
        {
            if (renderer == null)
                return false;

            string name = renderer.name;
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < BeamMarkers.Length; i++)
                    if (name.IndexOf(BeamMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
                return false;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Shader shader = materials[materialIndex] != null ? materials[materialIndex].shader : null;
                if (shader == null || shader.name == null)
                    continue;
                for (int markerIndex = 0; markerIndex < BeamMarkers.Length; markerIndex++)
                    if (shader.name.IndexOf(BeamMarkers[markerIndex], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            }
            return false;
        }

        // Recognise optical materials by shader name. CW FX/BackLens and similar glass can use an opaque render queue.
        private static readonly string[] OpticShaderMarkers =
            { "lens", "glass", "optic", "reticle", "collimator" };

        // Optical-path material such as a lens, glass or reticle.
        internal static bool IsOpticMaterial(Material material)
        {
            Shader shader = material != null ? material.shader : null;
            if (shader == null || shader.name == null)
                return false;

            for (int k = 0; k < OpticShaderMarkers.Length; k++)
                if (shader.name.IndexOf(OpticShaderMarkers[k], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Require every slot to be optical because a lens and its housing can share a renderer.
        internal static bool IsOpticSurface(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return false;

            for (int i = 0; i < materials.Length; i++)
                if (materials[i] != null && !IsOpticMaterial(materials[i]))
                    return false;
            return true;
        }

        internal static bool IsReducedLod(Renderer renderer)
        {
            if (renderer == null)
                return false;

            LODGroup group = renderer.GetComponentInParent<LODGroup>();
            if (group == null)
            {
                string name = renderer.name;
                if (string.IsNullOrEmpty(name))
                    return false;
                for (int level = 1; level <= 4; level++)
                    if (name.EndsWith("_LOD" + level, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("_lod" + level, StringComparison.Ordinal) ||
                        name.IndexOf("_LOD" + level + "_", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                return false;
            }

            LOD[] levels = group.GetLODs();
            if (levels == null || levels.Length == 0)
                return false;
            Renderer[] highest = levels[0].renderers;
            if (highest == null)
                return false;
            for (int i = 0; i < highest.Length; i++)
                if (ReferenceEquals(highest[i], renderer))
                    return false;
            for (int level = 1; level < levels.Length; level++)
            {
                Renderer[] renderers = levels[level].renderers;
                if (renderers == null)
                    continue;
                for (int i = 0; i < renderers.Length; i++)
                    if (ReferenceEquals(renderers[i], renderer))
                        return true;
            }
            return false;
        }
    }
}
