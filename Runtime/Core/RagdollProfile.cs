using System;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// Behaviour-level strength settings for the whole ragdoll. States blend between profiles instead of
    /// switching physics on/off: the body is always simulated, only how hard it fights back changes.
    /// </summary>
    [Serializable]
    public struct RagdollProfile
    {
        [Tooltip("Scales every joint muscle. 0 = completely limp (dead), 1 = normal, >1 = tensed.")]
        [Range(0f, 3f)] public float muscleStrength;

        [Tooltip("Scales the upright torque and height support. 0 = no balance at all.")]
        [Range(0f, 2f)] public float balanceStrength;

        [Tooltip("Scales propulsion and turning.")]
        [Range(0f, 2f)] public float locomotionStrength;

        [Tooltip("Weight of the procedural stepping gait on the legs.")]
        [Range(0f, 1f)] public float gaitWeight;

        [Tooltip("0 = relaxed arms, 1 = fists up in a guard.")]
        [Range(0f, 1f)] public float guardWeight;

        [Tooltip("Weight of the head look-at.")]
        [Range(0f, 1f)] public float lookWeight;

        public RagdollProfile(float muscleStrength, float balanceStrength, float locomotionStrength,
            float gaitWeight, float guardWeight, float lookWeight)
        {
            this.muscleStrength = muscleStrength;
            this.balanceStrength = balanceStrength;
            this.locomotionStrength = locomotionStrength;
            this.gaitWeight = gaitWeight;
            this.guardWeight = guardWeight;
            this.lookWeight = lookWeight;
        }

        public static RagdollProfile Standing => new RagdollProfile(1f, 1f, 1f, 1f, 0.3f, 1f);

        public static RagdollProfile Limp => new RagdollProfile(0f, 0f, 0f, 0f, 0f, 0f);

        public static RagdollProfile Lerp(in RagdollProfile a, in RagdollProfile b, float t)
        {
            t = Mathf.Clamp01(t);
            return new RagdollProfile(
                Mathf.Lerp(a.muscleStrength, b.muscleStrength, t),
                Mathf.Lerp(a.balanceStrength, b.balanceStrength, t),
                Mathf.Lerp(a.locomotionStrength, b.locomotionStrength, t),
                Mathf.Lerp(a.gaitWeight, b.gaitWeight, t),
                Mathf.Lerp(a.guardWeight, b.guardWeight, t),
                Mathf.Lerp(a.lookWeight, b.lookWeight, t));
        }

        /// <summary>Returns a copy with every field clamped to its valid range and NaNs replaced by 0.</summary>
        public RagdollProfile Sanitized()
        {
            return new RagdollProfile(
                Clean(muscleStrength, 3f),
                Clean(balanceStrength, 2f),
                Clean(locomotionStrength, 2f),
                Clean(gaitWeight, 1f),
                Clean(guardWeight, 1f),
                Clean(lookWeight, 1f));
        }

        private static float Clean(float value, float max)
        {
            if (!RagdollMath.IsFinite(value)) return 0f;
            return Mathf.Clamp(value, 0f, max);
        }

        public override string ToString()
        {
            return $"muscle {muscleStrength:0.00}, balance {balanceStrength:0.00}, locomotion {locomotionStrength:0.00}, gait {gaitWeight:0.00}, guard {guardWeight:0.00}, look {lookWeight:0.00}";
        }
    }
}
