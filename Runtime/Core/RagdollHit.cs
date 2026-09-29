using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>Delegate for <see cref="ActiveRagdollCharacter.HitReceived"/>. Passed by reference to avoid copies.</summary>
    public delegate void RagdollHitHandler(in RagdollHit hit);

    /// <summary>
    /// A physical hit registered on a ragdoll body part: collision, bullet, stab, strike, explosion...
    /// Raised for every hit whether or not it did damage, and also on corpses.
    /// </summary>
    public readonly struct RagdollHit
    {
        public readonly int boneIndex;
        public readonly BoneRole role;
        public readonly Vector3 point;

        /// <summary>Impulse (N·s) the hit transferred to the body, whether applied by the solver or by script.</summary>
        public readonly Vector3 impulse;

        /// <summary>Damage actually applied to health after multipliers (0 for non-damaging hits).</summary>
        public readonly float damage;

        public readonly DamageType damageType;
        public readonly GameObject source;

        /// <summary>Normalised 0..1 estimate of how hard the hit was, used for stagger/flinch decisions.</summary>
        public readonly float severity;

        public RagdollHit(int boneIndex, BoneRole role, Vector3 point, Vector3 impulse, float damage,
            DamageType damageType, GameObject source, float severity)
        {
            this.boneIndex = boneIndex;
            this.role = role;
            this.point = point;
            this.impulse = impulse;
            this.damage = damage;
            this.damageType = damageType;
            this.source = source;
            this.severity = severity;
        }

        public Vector3 Direction => RagdollMath.SafeNormalize(impulse, Vector3.zero);
    }
}
