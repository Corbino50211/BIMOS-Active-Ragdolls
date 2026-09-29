using System;
using UnityEngine;

namespace ActiveRagdoll
{
    public enum SelfCollisionMode
    {
        /// <summary>No collisions between bodies of the same character.</summary>
        Disabled = 0,

        /// <summary>Bodies within <see cref="RagdollPhysicsSettings.selfCollisionIgnoreDepth"/> joints of each other don't collide (recommended).</summary>
        IgnoreNearby = 1,

        /// <summary>Only directly jointed bodies are excluded (joint enableCollision is always false).</summary>
        Full = 2,
    }

    /// <summary>
    /// Rigidbody settings enforced on every ragdoll body at startup. Defaults are tuned for BIMOS's
    /// 144 Hz fixed step and remain stable down to 72 Hz.
    /// </summary>
    [Serializable]
    public sealed class RagdollPhysicsSettings
    {
        [Tooltip("Position solver iterations per body. Chains of strong joint drives need more than Unity's default of 6.")]
        [Range(1, 60)] public int solverIterations = 12;

        [Tooltip("Velocity solver iterations per body. Higher values reduce jitter on hard impacts.")]
        [Range(1, 30)] public int solverVelocityIterations = 4;

        [Tooltip("Rad/s. Limbs need more than Unity's old default of 7 to punch and flail convincingly.")]
        [Min(1f)] public float maxAngularVelocity = 40f;

        [Tooltip("Caps how fast overlapping bodies are pushed apart. Low values prevent ragdoll 'explosions'.")]
        [Min(0.1f)] public float maxDepenetrationVelocity = 4f;

        [Min(0f)] public float linearDamping = 0.02f;

        [Tooltip("Mild angular damping keeps limp limbs from spinning forever without looking stiff.")]
        [Min(0f)] public float angularDamping = 0.3f;

        [Tooltip("Interpolation keeps the rendered body smooth when the display rate differs from the physics rate (90 Hz display vs 144 Hz physics in BIMOS).")]
        public RigidbodyInterpolation interpolation = RigidbodyInterpolation.Interpolate;

        [Tooltip("Collision detection for fast segments (hands, forearms, feet). Speculative CCD stops strikes tunnelling through thin colliders at low cost.")]
        public CollisionDetectionMode fastSegmentCollisionDetection = CollisionDetectionMode.ContinuousSpeculative;

        public CollisionDetectionMode defaultCollisionDetection = CollisionDetectionMode.Discrete;

        public SelfCollisionMode selfCollision = SelfCollisionMode.IgnoreNearby;

        [Tooltip("With IgnoreNearby, bodies this many joints apart or closer never collide with each other.")]
        [Range(1, 4)] public int selfCollisionIgnoreDepth = 2;

        [Tooltip("Layer applied to every ragdoll body at startup. Leave empty to keep the authored layers. Missing layers are reported once and skipped.")]
        public string ragdollLayer = RagdollLayers.DefaultRagdollLayer;

        internal void Sanitize()
        {
            solverIterations = Mathf.Clamp(solverIterations, 1, 60);
            solverVelocityIterations = Mathf.Clamp(solverVelocityIterations, 1, 30);
            maxAngularVelocity = Mathf.Max(1f, maxAngularVelocity);
            maxDepenetrationVelocity = Mathf.Max(0.1f, maxDepenetrationVelocity);
            linearDamping = Mathf.Max(0f, linearDamping);
            angularDamping = Mathf.Max(0f, angularDamping);
            selfCollisionIgnoreDepth = Mathf.Clamp(selfCollisionIgnoreDepth, 1, 4);
        }
    }
}
