using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Non-allocating physics queries that skip the querying character's own colliders.
    /// Main-thread only (shares a static hit buffer).
    /// </summary>
    public static class PhysicsQuery
    {
        private const int BufferSize = 32;
        private static readonly RaycastHit[] s_hits = new RaycastHit[BufferSize];

        /// <summary>
        /// Nearest raycast hit that does not belong to <paramref name="ignoreCharacter"/> and is not
        /// under <paramref name="ignoreRoot"/>. Triggers are always ignored.
        /// </summary>
        public static bool Raycast(Vector3 origin, Vector3 direction, float distance, int layerMask,
            ActiveRagdollCharacter ignoreCharacter, Transform ignoreRoot, out RaycastHit hit)
        {
            hit = default;
            if (distance <= 0f || direction.sqrMagnitude < 1e-10f || !RagdollMath.IsFinite(origin))
                return false;

            int count = Physics.RaycastNonAlloc(origin, direction, s_hits, distance, layerMask, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            int bestIndex = -1;
            for (int i = 0; i < count; i++)
            {
                Collider c = s_hits[i].collider;
                if (c == null)
                    continue;
                if (ignoreCharacter != null && ignoreCharacter.Owns(c))
                    continue;
                if (ignoreRoot != null && c.transform.IsChildOf(ignoreRoot))
                    continue;
                if (s_hits[i].distance < best)
                {
                    best = s_hits[i].distance;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return false;

            hit = s_hits[bestIndex];
            return true;
        }
    }

    /// <summary>Resolves project layers by name without hard failures when a layer is missing.</summary>
    public static class RagdollLayers
    {
        public const string DefaultRagdollLayer = "ActiveRagdoll";

        /// <summary>BIMOS puts its whole player physics rig on this layer (see BIMOS.PhysicsRig).</summary>
        public const string BIMOSRigLayer = "BIMOSRig";

        /// <summary>Returns the layer mask bit for <paramref name="layerName"/>, or 0 if the layer does not exist.</summary>
        public static int MaskOf(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return 0;
            int layer = LayerMask.NameToLayer(layerName);
            return layer < 0 ? 0 : 1 << layer;
        }

        /// <summary>Layers that should never count as walkable ground or block NPC perception by default.</summary>
        public static int NonEnvironmentMask()
        {
            return MaskOf("Ignore Raycast") | MaskOf(BIMOSRigLayer);
        }
    }
}
