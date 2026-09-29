using System;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    /// <summary>Which transforms of a model play which ragdoll roles. Built from a Humanoid avatar or by hand.</summary>
    public sealed class RigDefinition
    {
        public Transform root;
        public Animator animator;
        public readonly Transform[] bones = new Transform[BoneRoles.Count];
        public Transform neck;
        public Transform leftToes;
        public Transform rightToes;
        public Transform leftHandEnd;
        public Transform rightHandEnd;

        public Transform this[BoneRole role]
        {
            get => bones[(int)role];
            set => bones[(int)role] = value;
        }

        public static HumanBodyBones ToHumanBone(BoneRole role)
        {
            switch (role)
            {
                case BoneRole.Pelvis: return HumanBodyBones.Hips;
                case BoneRole.Spine: return HumanBodyBones.Spine;
                case BoneRole.Chest: return HumanBodyBones.Chest;
                case BoneRole.Head: return HumanBodyBones.Head;
                case BoneRole.LeftUpperArm: return HumanBodyBones.LeftUpperArm;
                case BoneRole.LeftLowerArm: return HumanBodyBones.LeftLowerArm;
                case BoneRole.LeftHand: return HumanBodyBones.LeftHand;
                case BoneRole.RightUpperArm: return HumanBodyBones.RightUpperArm;
                case BoneRole.RightLowerArm: return HumanBodyBones.RightLowerArm;
                case BoneRole.RightHand: return HumanBodyBones.RightHand;
                case BoneRole.LeftUpperLeg: return HumanBodyBones.LeftUpperLeg;
                case BoneRole.LeftLowerLeg: return HumanBodyBones.LeftLowerLeg;
                case BoneRole.LeftFoot: return HumanBodyBones.LeftFoot;
                case BoneRole.RightUpperLeg: return HumanBodyBones.RightUpperLeg;
                case BoneRole.RightLowerLeg: return HumanBodyBones.RightLowerLeg;
                case BoneRole.RightFoot: return HumanBodyBones.RightFoot;
                default: throw new ArgumentOutOfRangeException(nameof(role), role, null);
            }
        }

        /// <summary>Maps a Humanoid Animator's avatar onto ragdoll roles.</summary>
        public static bool TryFromAnimator(Animator animator, out RigDefinition rig, out string error)
        {
            rig = null;
            if (animator == null)
            {
                error = "No Animator assigned.";
                return false;
            }
            if (animator.avatar == null || !animator.avatar.isValid || !animator.isHuman)
            {
                error = $"'{animator.name}' does not have a valid Humanoid avatar. Set the model's Rig > Animation Type to Humanoid, or build a generic rig by filling a RigDefinition manually.";
                return false;
            }

            rig = new RigDefinition { root = animator.transform, animator = animator };
            for (int i = 0; i < BoneRoles.Count; i++)
            {
                BoneRole role = (BoneRole)i;
                Transform t = animator.GetBoneTransform(ToHumanBone(role));
                if (t == null && role == BoneRole.Chest)
                    t = animator.GetBoneTransform(HumanBodyBones.UpperChest);
                rig[role] = t;
            }
            rig.neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            rig.leftToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
            rig.rightToes = animator.GetBoneTransform(HumanBodyBones.RightToes);
            rig.leftHandEnd = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal) ?? animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
            rig.rightHandEnd = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal) ?? animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);

            return rig.Validate(out error);
        }

        public bool Validate(out string error)
        {
            if (root == null)
            {
                error = "Rig has no root transform.";
                return false;
            }

            BoneRole[] required =
            {
                BoneRole.Pelvis, BoneRole.Spine, BoneRole.Head,
                BoneRole.LeftUpperLeg, BoneRole.LeftLowerLeg, BoneRole.LeftFoot,
                BoneRole.RightUpperLeg, BoneRole.RightLowerLeg, BoneRole.RightFoot,
                BoneRole.LeftUpperArm, BoneRole.LeftLowerArm, BoneRole.LeftHand,
                BoneRole.RightUpperArm, BoneRole.RightLowerArm, BoneRole.RightHand,
            };
            foreach (BoneRole r in required)
            {
                if (this[r] == null)
                {
                    error = $"Rig is missing the {r} bone.";
                    return false;
                }
            }

            error = null;
            return true;
        }
    }
}
