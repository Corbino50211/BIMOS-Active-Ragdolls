namespace ActiveRagdoll
{
    /// <summary>
    /// Anatomical role of a simulated ragdoll body. The set mirrors the bones a BIMOS / Unity Humanoid
    /// rig exposes, reduced to the segments that carry meaningful mass.
    /// </summary>
    public enum BoneRole
    {
        Pelvis = 0,
        Spine = 1,
        Chest = 2,
        Head = 3,
        LeftUpperArm = 4,
        LeftLowerArm = 5,
        LeftHand = 6,
        RightUpperArm = 7,
        RightLowerArm = 8,
        RightHand = 9,
        LeftUpperLeg = 10,
        LeftLowerLeg = 11,
        LeftFoot = 12,
        RightUpperLeg = 13,
        RightLowerLeg = 14,
        RightFoot = 15,
    }

    public enum BodySide
    {
        Center = 0,
        Left = 1,
        Right = 2,
    }

    /// <summary>Groups used for muscle tuning and damage multipliers.</summary>
    public enum BoneGroup
    {
        Torso = 0,
        Head = 1,
        Arm = 2,
        Hand = 3,
        Leg = 4,
        Foot = 5,
    }

    /// <summary>Allocation-free helpers for <see cref="BoneRole"/>.</summary>
    public static class BoneRoles
    {
        public const int Count = 16;

        public static BodySide GetSide(BoneRole role)
        {
            switch (role)
            {
                case BoneRole.LeftUpperArm:
                case BoneRole.LeftLowerArm:
                case BoneRole.LeftHand:
                case BoneRole.LeftUpperLeg:
                case BoneRole.LeftLowerLeg:
                case BoneRole.LeftFoot:
                    return BodySide.Left;
                case BoneRole.RightUpperArm:
                case BoneRole.RightLowerArm:
                case BoneRole.RightHand:
                case BoneRole.RightUpperLeg:
                case BoneRole.RightLowerLeg:
                case BoneRole.RightFoot:
                    return BodySide.Right;
                default:
                    return BodySide.Center;
            }
        }

        public static BoneGroup GetGroup(BoneRole role)
        {
            switch (role)
            {
                case BoneRole.Head:
                    return BoneGroup.Head;
                case BoneRole.LeftUpperArm:
                case BoneRole.LeftLowerArm:
                case BoneRole.RightUpperArm:
                case BoneRole.RightLowerArm:
                    return BoneGroup.Arm;
                case BoneRole.LeftHand:
                case BoneRole.RightHand:
                    return BoneGroup.Hand;
                case BoneRole.LeftUpperLeg:
                case BoneRole.LeftLowerLeg:
                case BoneRole.RightUpperLeg:
                case BoneRole.RightLowerLeg:
                    return BoneGroup.Leg;
                case BoneRole.LeftFoot:
                case BoneRole.RightFoot:
                    return BoneGroup.Foot;
                default:
                    return BoneGroup.Torso;
            }
        }

        public static bool IsArm(BoneRole role)
        {
            BoneGroup g = GetGroup(role);
            return g == BoneGroup.Arm || g == BoneGroup.Hand;
        }

        public static bool IsLeg(BoneRole role)
        {
            BoneGroup g = GetGroup(role);
            return g == BoneGroup.Leg || g == BoneGroup.Foot;
        }

        public static bool IsTorso(BoneRole role) => GetGroup(role) == BoneGroup.Torso;

        /// <summary>
        /// Identifier of the limb chain a bone belongs to (0 = torso, 1 = left arm, 2 = right arm,
        /// 3 = left leg, 4 = right leg, 5 = head). Used to weaken whole limbs when one segment is grabbed.
        /// </summary>
        public static int GetLimbId(BoneRole role)
        {
            if (role == BoneRole.Head) return 5;
            BodySide side = GetSide(role);
            if (side == BodySide.Center) return 0;
            if (IsArm(role)) return side == BodySide.Left ? 1 : 2;
            return side == BodySide.Left ? 3 : 4;
        }

        public static BoneRole UpperArm(BodySide side) => side == BodySide.Left ? BoneRole.LeftUpperArm : BoneRole.RightUpperArm;
        public static BoneRole LowerArm(BodySide side) => side == BodySide.Left ? BoneRole.LeftLowerArm : BoneRole.RightLowerArm;
        public static BoneRole Hand(BodySide side) => side == BodySide.Left ? BoneRole.LeftHand : BoneRole.RightHand;
        public static BoneRole UpperLeg(BodySide side) => side == BodySide.Left ? BoneRole.LeftUpperLeg : BoneRole.RightUpperLeg;
        public static BoneRole LowerLeg(BodySide side) => side == BodySide.Left ? BoneRole.LeftLowerLeg : BoneRole.RightLowerLeg;
        public static BoneRole Foot(BodySide side) => side == BodySide.Left ? BoneRole.LeftFoot : BoneRole.RightFoot;

        public static BodySide Opposite(BodySide side)
        {
            if (side == BodySide.Left) return BodySide.Right;
            if (side == BodySide.Right) return BodySide.Left;
            return BodySide.Center;
        }

        /// <summary>-1 for left, +1 for right, 0 for center. Used to mirror right-handed offsets.</summary>
        public static float Sign(BodySide side) => side == BodySide.Left ? -1f : side == BodySide.Right ? 1f : 0f;
    }
}
