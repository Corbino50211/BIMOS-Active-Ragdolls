using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Overrides how blades stick into this object and its children (see <see cref="StabMaterial"/>).
    /// Without one, ragdoll body parts use the blade's flesh material and everything else uses its world material.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Active Ragdoll/Weapons/Stabbable Surface")]
    public sealed class StabbableSurface : MonoBehaviour
    {
        [SerializeField] private StabMaterial _material = StabMaterial.Wood();

        public StabMaterial Material
        {
            get => _material;
            set => _material = value ?? StabMaterial.Impenetrable();
        }

        public static StabbableSurface Find(Collider collider) => collider != null ? collider.GetComponentInParent<StabbableSurface>() : null;

        private void Reset() => _material = StabMaterial.Wood();
    }
}
