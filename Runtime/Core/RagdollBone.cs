using System;
using UnityEngine;

namespace ActiveRagdoll
{
    /// <summary>
    /// One simulated body of the active ragdoll and the animated-rig bone it follows.
    /// Authoring data is serialized; everything else is runtime state owned by <see cref="ActiveRagdollCharacter"/>.
    /// </summary>
    [Serializable]
    public sealed class RagdollBone
    {
        [Tooltip("Anatomical role of this body.")]
        public BoneRole role;

        [Tooltip("The simulated rigidbody. It must live under the Ragdoll root, never under the animated rig.")]
        public Rigidbody body;

        [Tooltip("ConfigurableJoint on this body connecting it to its parent body. Leave empty for the pelvis.")]
        public ConfigurableJoint joint;

        [Tooltip("Bone of the animated rig that supplies this body's target pose and is driven by the simulated pose for rendering.")]
        public Transform target;

        [Tooltip("Per-bone muscle strength multiplier applied on top of the group tuning.")]
        [Min(0f)] public float muscleMultiplier = 1f;

        // ---- Topology (runtime) ----
        [NonSerialized] internal int index = -1;
        [NonSerialized] internal int parentIndex = -1;
        [NonSerialized] internal int[] children = Array.Empty<int>();
        [NonSerialized] internal Transform bodyTransform;
        [NonSerialized] internal BodyPart part;
        [NonSerialized] internal Collider[] colliders = Array.Empty<Collider>();
        [NonSerialized] internal int targetDepth;

        // ---- Bind-pose relationships (runtime) ----
        [NonSerialized] internal Quaternion targetToBody = Quaternion.identity;   // body.rot = target.rot * targetToBody
        [NonSerialized] internal Quaternion bodyToTarget = Quaternion.identity;   // target.rot = body.rot * bodyToTarget
        [NonSerialized] internal Vector3 targetOriginInBody;                        // target origin expressed in body space
        [NonSerialized] internal Vector3 bodyOriginInTarget;                        // body origin expressed in target space
        [NonSerialized] internal Vector3 targetRestLocalPosition;
        [NonSerialized] internal Quaternion targetRestLocalRotation = Quaternion.identity;
        [NonSerialized] internal Vector3 restPosition;                              // relative to pelvis, in the bind yaw frame
        [NonSerialized] internal Quaternion restRotation = Quaternion.identity;    // relative to the bind yaw frame

        // ---- Joint drive frame (runtime) ----
        [NonSerialized] internal Quaternion initialRelativeRotation = Quaternion.identity;
        [NonSerialized] internal Quaternion jointSpace = Quaternion.identity;
        [NonSerialized] internal Quaternion jointSpaceInverse = Quaternion.identity;

        // ---- Captured targets (runtime, written in LateUpdate, consumed in FixedUpdate) ----
        [NonSerialized] internal bool hasTarget;
        [NonSerialized] internal Vector3 targetPosition;
        [NonSerialized] internal Vector3 targetVelocity;
        [NonSerialized] internal Quaternion targetRotation = Quaternion.identity;
        [NonSerialized] internal Quaternion targetRelativeRotation = Quaternion.identity;

        // ---- Mass properties (runtime) ----
        [NonSerialized] internal float subtreeMass;

        public int Index => index;
        public int ParentIndex => parentIndex;
        public Transform BodyTransform => bodyTransform;
        public BodyPart Part => part;
        public BodySide Side => BoneRoles.GetSide(role);
        public BoneGroup Group => BoneRoles.GetGroup(role);
        public bool HasTarget => hasTarget;

        /// <summary>World position the animated rig wants this body at (updated every rendered frame).</summary>
        public Vector3 TargetPosition => targetPosition;

        /// <summary>World rotation the animated rig wants this body at (updated every rendered frame).</summary>
        public Quaternion TargetRotation => targetRotation;

        /// <summary>Mass of this body plus every body below it in the joint hierarchy.</summary>
        public float SubtreeMass => subtreeMass;
    }
}
