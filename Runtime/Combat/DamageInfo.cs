using System;
using UnityEngine;

namespace ActiveRagdoll
{
    public enum DamageType
    {
        Generic = 0,
        Blunt = 1,
        Bullet = 2,
        Stab = 3,
        Slash = 4,
        Explosion = 5,

        /// <summary>Collision with static/heavy environment (being thrown into a wall, long falls).</summary>
        Impact = 6,
    }

    /// <summary>Everything a damage receiver needs to react physically and numerically to a hit.</summary>
    public struct DamageInfo
    {
        /// <summary>Raw damage before receiver multipliers.</summary>
        public float amount;
        public DamageType type;

        /// <summary>World-space hit point.</summary>
        public Vector3 point;

        /// <summary>Normalised direction the damaging object was travelling.</summary>
        public Vector3 direction;

        /// <summary>Impulse (N·s) the receiver should apply at <see cref="point"/>. Zero when the physics solver already applied it (collisions).</summary>
        public Vector3 impulse;

        /// <summary>Attacker or weapon, used for aggro and kill attribution. May be null.</summary>
        public GameObject source;

        /// <summary>The collider that was hit, if known.</summary>
        public Collider hitCollider;

        public DamageInfo(float amount, DamageType type, Vector3 point, Vector3 direction, Vector3 impulse,
            GameObject source = null, Collider hitCollider = null)
        {
            this.amount = amount;
            this.type = type;
            this.point = point;
            this.direction = direction;
            this.impulse = impulse;
            this.source = source;
            this.hitCollider = hitCollider;
        }
    }

    /// <summary>Anything that can take damage: ragdoll body parts, the player, props.</summary>
    public interface IDamageable
    {
        void ApplyDamage(in DamageInfo damage);
    }

    public interface IHealth
    {
        bool IsAlive { get; }
        float CurrentHealth { get; }
        float MaxHealth { get; }
    }

    /// <summary>
    /// Marker for rigidbodies (weapons) that compute their own damage in OnCollisionEnter. Body parts skip
    /// their generic impulse-based damage for collisions with these so hits aren't counted twice.
    /// </summary>
    public interface IImpactDamageDealer
    {
    }

    public static class Damageables
    {
        /// <summary>Finds the damage receiver for a collider: the collider's object, its rigidbody's object, then parents.</summary>
        public static IDamageable Find(Collider collider)
        {
            if (collider == null)
                return null;
            if (collider.TryGetComponent(out IDamageable direct))
                return direct;
            Rigidbody rb = collider.attachedRigidbody;
            if (rb != null && rb.TryGetComponent(out IDamageable onBody))
                return onBody;
            return collider.GetComponentInParent<IDamageable>();
        }
    }

    [Serializable]
    public sealed class DamageTypeMultipliers
    {
        [Min(0f)] public float generic = 1f;
        [Min(0f)] public float blunt = 1f;
        [Min(0f)] public float bullet = 1f;
        [Min(0f)] public float stab = 1.2f;
        [Min(0f)] public float slash = 1f;
        [Min(0f)] public float explosion = 1f;
        [Min(0f)] public float impact = 1f;

        public float Get(DamageType type)
        {
            switch (type)
            {
                case DamageType.Blunt: return blunt;
                case DamageType.Bullet: return bullet;
                case DamageType.Stab: return stab;
                case DamageType.Slash: return slash;
                case DamageType.Explosion: return explosion;
                case DamageType.Impact: return impact;
                default: return generic;
            }
        }
    }

    /// <summary>Thresholds for turning physical collisions into damage.</summary>
    [Serializable]
    public sealed class ImpactDamageSettings
    {
        [Header("Moving bodies (fists, props, the player's hands)")]
        [Tooltip("Minimum relative speed (m/s) for a collision with a rigidbody to deal damage.")]
        [Min(0f)] public float minBodySpeed = 2.5f;
        [Tooltip("Impulse (N·s) below which a collision deals no damage.")]
        [Min(0f)] public float minBodyImpulse = 3f;
        [Min(0f)] public float bodyDamagePerImpulse = 1.2f;

        [Header("Static environment (walls, floor)")]
        [Tooltip("Normal falls are well below this; being thrown into a wall is not.")]
        [Min(0f)] public float minEnvironmentSpeed = 7f;
        [Min(0f)] public float minEnvironmentImpulse = 40f;
        [Min(0f)] public float environmentDamagePerImpulse = 0.5f;

        [Header("Reactions")]
        [Tooltip("Non-damaging body collisions above this impulse (N·s) still cause pain/flinch.")]
        [Min(0f)] public float minReactionImpulse = 2f;
    }
}
