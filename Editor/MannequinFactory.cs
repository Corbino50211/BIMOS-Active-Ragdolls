using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    /// <summary>
    /// Generates a 1.8 m capsule mannequin (plain transforms + primitive meshes, no avatar needed) so the
    /// active ragdoll can be tested without any art. Relaxed A-pose, facing +Z.
    /// </summary>
    public static class MannequinFactory
    {
        private const float ArmDown = 60f; // degrees below horizontal in the bind pose

        public static RigDefinition Create(Vector3 position, Quaternion rotation, out GameObject root)
        {
            root = new GameObject("Mannequin");
            Undo.RegisterCreatedObjectUndo(root, "Create Mannequin");
            Transform r = root.transform;

            Transform hips = Bone("Hips", r, new Vector3(0f, 1.0f, 0f));
            Transform spine = Bone("Spine", hips, new Vector3(0f, 1.1f, 0f));
            Transform chest = Bone("Chest", spine, new Vector3(0f, 1.28f, 0f));
            Transform neck = Bone("Neck", chest, new Vector3(0f, 1.5f, 0f));
            Transform head = Bone("Head", neck, new Vector3(0f, 1.6f, 0f));

            var rig = new RigDefinition { root = r, neck = neck };
            rig[BoneRole.Pelvis] = hips;
            rig[BoneRole.Spine] = spine;
            rig[BoneRole.Chest] = chest;
            rig[BoneRole.Head] = head;

            for (int s = 0; s < 2; s++)
            {
                BodySide side = s == 0 ? BodySide.Left : BodySide.Right;
                float x = BoneRoles.Sign(side);
                string prefix = side.ToString();

                float a = ArmDown * Mathf.Deg2Rad;
                Vector3 armDir = new Vector3(x * Mathf.Cos(a), -Mathf.Sin(a), 0f);
                Vector3 shoulder = new Vector3(x * 0.19f, 1.45f, 0f);
                Transform upperArm = Bone(prefix + "UpperArm", chest, shoulder);
                Transform lowerArm = Bone(prefix + "LowerArm", upperArm, shoulder + armDir * 0.29f);
                Transform hand = Bone(prefix + "Hand", lowerArm, shoulder + armDir * 0.55f);
                Transform handEnd = Bone(prefix + "HandEnd", hand, shoulder + armDir * 0.64f);

                Transform upperLeg = Bone(prefix + "UpperLeg", hips, new Vector3(x * 0.1f, 0.95f, 0f));
                Transform lowerLeg = Bone(prefix + "LowerLeg", upperLeg, new Vector3(x * 0.1f, 0.52f, 0f));
                Transform foot = Bone(prefix + "Foot", lowerLeg, new Vector3(x * 0.1f, 0.09f, 0f));
                Transform toes = Bone(prefix + "Toes", foot, new Vector3(x * 0.1f, 0.02f, 0.14f));

                rig[BoneRoles.UpperArm(side)] = upperArm;
                rig[BoneRoles.LowerArm(side)] = lowerArm;
                rig[BoneRoles.Hand(side)] = hand;
                rig[BoneRoles.UpperLeg(side)] = upperLeg;
                rig[BoneRoles.LowerLeg(side)] = lowerLeg;
                rig[BoneRoles.Foot(side)] = foot;
                if (side == BodySide.Left)
                {
                    rig.leftToes = toes;
                    rig.leftHandEnd = handEnd;
                }
                else
                {
                    rig.rightToes = toes;
                    rig.rightHandEnd = handEnd;
                }

                Limb(upperArm, lowerArm, 0.09f);
                Limb(lowerArm, hand, 0.075f);
                Limb(hand, handEnd, 0.08f);
                Limb(upperLeg, lowerLeg, 0.14f);
                Limb(lowerLeg, foot, 0.1f);
                Block(foot, new Vector3(x * 0.1f, 0.045f, 0.05f), new Vector3(0.09f, 0.09f, 0.25f));
            }

            Block(hips, new Vector3(0f, 1.0f, 0f), new Vector3(0.32f, 0.2f, 0.2f));
            Block(spine, new Vector3(0f, 1.19f, 0f), new Vector3(0.28f, 0.2f, 0.18f));
            Block(chest, new Vector3(0f, 1.39f, 0f), new Vector3(0.36f, 0.24f, 0.22f));
            Limb(neck, head, 0.09f);
            Ball(head, new Vector3(0f, 1.69f, 0.02f), 0.22f);

            r.SetPositionAndRotation(position, rotation);
            return rig;
        }

        private static Transform Bone(string name, Transform parent, Vector3 rootSpacePosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Transform root = parent;
            while (root.parent != null) root = root.parent;
            go.transform.SetPositionAndRotation(root.TransformPoint(rootSpacePosition), root.rotation);
            return go.transform;
        }

        private static void Limb(Transform from, Transform to, float diameter)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            ActiveRagdollBuilder.DestroyColliders(go);
            go.name = from.name + "_Mesh";
            go.transform.SetParent(from, false);
            Vector3 d = to.position - from.position;
            go.transform.position = from.position + d * 0.5f;
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, d);
            go.transform.localScale = new Vector3(diameter, Mathf.Max(d.magnitude * 0.5f, diameter * 0.5f), diameter);
        }

        private static void Block(Transform bone, Vector3 rootSpaceCenter, Vector3 size)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ActiveRagdollBuilder.DestroyColliders(go);
            go.name = bone.name + "_Mesh";
            go.transform.SetParent(bone, false);
            Transform root = bone;
            while (root.parent != null) root = root.parent;
            go.transform.SetPositionAndRotation(root.TransformPoint(rootSpaceCenter), root.rotation);
            go.transform.localScale = size;
        }

        private static void Ball(Transform bone, Vector3 rootSpaceCenter, float diameter)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ActiveRagdollBuilder.DestroyColliders(go);
            go.name = bone.name + "_Mesh";
            go.transform.SetParent(bone, false);
            Transform root = bone;
            while (root.parent != null) root = root.parent;
            go.transform.position = root.TransformPoint(rootSpaceCenter);
            go.transform.localScale = Vector3.one * diameter;
        }
    }
}
