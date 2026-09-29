using System;
using UnityEngine;

namespace ActiveRagdoll
{
    public enum StrikePhase
    {
        None = 0,
        Windup = 1,
        Strike = 2,
        Recover = 3,
    }

    /// <summary>
    /// A physically performed strike. The procedural layer moves the animated hand along windup → strike →
    /// recover; the muscles (boosted, plus a hand pin) make the physical fist follow. Damage comes from the
    /// fist actually colliding with something during the active window. Offsets are authored for the RIGHT
    /// hand, in arm-length units relative to the shoulder in the character's heading frame, and mirrored for
    /// the left hand.
    /// </summary>
    [Serializable]
    public sealed class StrikeDefinition
    {
        public string name = "Strike";

        [Tooltip("Hand that throws the strike.")]
        public BodySide side = BodySide.Right;

        [Tooltip("Relative likelihood of being chosen.")]
        [Min(0f)] public float weight = 1f;

        [Header("Timing (s)")]
        [Min(0.01f)] public float windupTime = 0.2f;
        [Min(0.01f)] public float strikeTime = 0.14f;
        [Min(0.01f)] public float recoverTime = 0.3f;

        [Tooltip("Fraction of the recover phase that still counts as an active hit window.")]
        [Range(0f, 1f)] public float activeTail = 0.25f;

        [Header("Path")]
        [Tooltip("Wrist position at the end of the windup (right hand, arm-length units: x = outward, y = up, z = forward).")]
        public Vector3 windupOffset = new Vector3(0.1f, 0.05f, 0.25f);

        [Tooltip("How far (m) past the target point the fist is driven, so it strikes through rather than stopping on contact.")]
        [Min(0f)] public float overshoot = 0.15f;

        [Tooltip("Sideways arc during the strike (arm-length units). 0 = straight jab/cross, ~0.35 = hook.")]
        public float hookArc;

        [Tooltip("Vertical arc during the strike (arm-length units). Negative = scooping uppercut.")]
        public float rise;

        [Tooltip("Torso twist (degrees) thrown into the strike.")]
        [Range(0f, 60f)] public float torsoTwist = 20f;

        [Tooltip("Aim at the target's head (true) or centre of mass (false).")]
        public bool targetHead = true;

        [Header("Physics")]
        [Tooltip("Muscle strength multiplier for the striking arm during the strike.")]
        [Range(1f, 5f)] public float muscleBoost = 2.5f;

        [Tooltip("Pin weight pulling the fist along its path during the strike (0 = pure muscles).")]
        [Range(0f, 1f)] public float handPinWeight = 0.5f;

        [Tooltip("Forward speed (m/s) the character steps in with during windup and strike.")]
        [Min(0f)] public float lungeSpeed = 1f;

        [Header("Damage")]
        [Min(0f)] public float damage = 10f;

        [Tooltip("Extra impulse (N·s) applied to what the fist hits, on top of the physical collision.")]
        [Min(0f)] public float impulse = 6f;

        [Tooltip("Contact speed (m/s) at which full damage is dealt. Slower contacts scale damage down.")]
        [Min(0.1f)] public float fullDamageSpeed = 4f;

        public float TotalTime => windupTime + strikeTime + recoverTime;

        public StrikeDefinition Clone() => (StrikeDefinition)MemberwiseClone();

        /// <summary>Boxer-style defaults: left jab, right cross, both hooks.</summary>
        public static StrikeDefinition[] CreateDefaultSet()
        {
            return new[]
            {
                new StrikeDefinition
                {
                    name = "Left Jab", side = BodySide.Left, weight = 1.2f,
                    windupTime = 0.14f, strikeTime = 0.11f, recoverTime = 0.24f,
                    windupOffset = new Vector3(-0.05f, 0.05f, 0.35f), overshoot = 0.12f,
                    torsoTwist = 12f, muscleBoost = 2.2f, handPinWeight = 0.45f,
                    lungeSpeed = 0.8f, damage = 7f, impulse = 4f,
                },
                new StrikeDefinition
                {
                    name = "Right Cross", side = BodySide.Right, weight = 1f,
                    windupTime = 0.22f, strikeTime = 0.14f, recoverTime = 0.32f,
                    windupOffset = new Vector3(0.05f, 0.05f, 0.2f), overshoot = 0.18f,
                    torsoTwist = 28f, muscleBoost = 2.8f, handPinWeight = 0.55f,
                    lungeSpeed = 1.1f, damage = 12f, impulse = 7f,
                },
                new StrikeDefinition
                {
                    name = "Right Hook", side = BodySide.Right, weight = 0.7f,
                    windupTime = 0.24f, strikeTime = 0.16f, recoverTime = 0.34f,
                    windupOffset = new Vector3(0.4f, 0.05f, 0.25f), overshoot = 0.15f, hookArc = 0.35f,
                    torsoTwist = 35f, muscleBoost = 3f, handPinWeight = 0.5f,
                    lungeSpeed = 0.9f, damage = 14f, impulse = 8f,
                },
                new StrikeDefinition
                {
                    name = "Left Hook", side = BodySide.Left, weight = 0.6f,
                    windupTime = 0.22f, strikeTime = 0.16f, recoverTime = 0.32f,
                    windupOffset = new Vector3(0.4f, 0.05f, 0.25f), overshoot = 0.15f, hookArc = 0.35f,
                    torsoTwist = 30f, muscleBoost = 2.8f, handPinWeight = 0.5f,
                    lungeSpeed = 0.9f, damage = 11f, impulse = 7f,
                },
            };
        }
    }
}
