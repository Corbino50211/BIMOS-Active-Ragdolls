using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ActiveRagdoll.EditorTools
{
    [Serializable]
    public sealed class BuildOptions
    {
        [Tooltip("Total body mass (kg). Segment masses follow standard anthropometric fractions.")]
        [Min(10f)] public float totalMass = 70f;

        [Tooltip("Simulate the hands as separate bodies (better fists and grabs). Otherwise hand colliders ride on the forearms.")]
        public bool includeHands = true;

        [Tooltip("Use separate Spine and Chest bodies when the rig has a chest bone (better torso twist).")]
        public bool includeChest = true;

        [Tooltip("Scales every limb collider radius.")]
        [Range(0.5f, 2f)] public float limbThickness = 1f;

        public string layerName = RagdollLayers.DefaultRagdollLayer;

        [Tooltip("Add perception, navigation and the NPC state machine.")]
        public bool addAI = true;

        [Tooltip("Team for the NPC's CombatTarget (the player is team 0).")]
        public int team = 1;

        [Tooltip("Add BIMOSRagdollGrabs when the BIMOS integration is compiled (BIMOS installed).")]
        public bool addBIMOSGrabs = true;

        [Tooltip("Remove Rigidbodies, Colliders and Joints already on the model (e.g. from Unity's Ragdoll Wizard).")]
        public bool removeExistingPhysics = true;
    }

    /// <summary>
    /// Builds a complete active-ragdoll NPC from a rig: a separate flat ragdoll hierarchy (rigidbodies,
    /// colliders, ConfigurableJoints with anatomical limits), the visual model wrapped under an animated root at
    /// floor level, and every runtime component wired up. Fully undoable.
    /// </summary>
    public static class ActiveRagdollBuilder
    {
        private const string BIMOSGrabsType = "ActiveRagdoll.BIMOSIntegration.BIMOSRagdollGrabs, ActiveRagdoll.BIMOS";

        private static readonly BoneRole[] BuildOrder =
        {
            BoneRole.Pelvis, BoneRole.Spine, BoneRole.Chest, BoneRole.Head,
            BoneRole.LeftUpperArm, BoneRole.LeftLowerArm, BoneRole.LeftHand,
            BoneRole.RightUpperArm, BoneRole.RightLowerArm, BoneRole.RightHand,
            BoneRole.LeftUpperLeg, BoneRole.LeftLowerLeg, BoneRole.LeftFoot,
            BoneRole.RightUpperLeg, BoneRole.RightLowerLeg, BoneRole.RightFoot,
        };

        public static bool IsBIMOSIntegrationAvailable => Type.GetType(BIMOSGrabsType) != null;

        public static ActiveRagdollCharacter Build(RigDefinition rig, BuildOptions options, out string error)
        {
            options ??= new BuildOptions();
            if (rig == null)
            {
                error = "No rig definition.";
                return null;
            }
            if (!rig.Validate(out error))
                return null;

            Transform model = rig.root;
            if (EditorUtility.IsPersistent(model))
            {
                error = "Select a model instance in the scene, not an asset.";
                return null;
            }
            if (PrefabUtility.IsPartOfPrefabInstance(model) && !PrefabUtility.IsOutermostPrefabInstanceRoot(model.gameObject))
            {
                error = "The model is nested inside a prefab instance. Unpack the prefab first.";
                return null;
            }
            if (model.GetComponentInParent<ActiveRagdollCharacter>() != null)
            {
                error = "This model already belongs to an active ragdoll.";
                return null;
            }

            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Build Active Ragdoll");

            int layer = LayerSetup.EnsureLayer(options.layerName);
            if (options.removeExistingPhysics)
                RemoveExistingPhysics(model);

            // ---- Character frame and scale
            Vector3 forward = RagdollMath.SafeNormalize(RagdollMath.Flatten(model.forward), Vector3.forward);
            Quaternion frame = Quaternion.LookRotation(forward, Vector3.up);
            Vector3 right = frame * Vector3.right;

            Transform leftFoot = rig[BoneRole.LeftFoot];
            Transform rightFoot = rig[BoneRole.RightFoot];
            Transform head = rig[BoneRole.Head];
            float footY = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            float scale = Mathf.Max(0.1f, (head.position.y - footY) / 1.5f);
            float groundY = rig.leftToes != null && rig.rightToes != null
                ? Mathf.Min(rig.leftToes.position.y, rig.rightToes.position.y) - 0.02f * scale
                : footY - 0.08f * scale;

            // ---- Hierarchy: NPC root > Visual (animated root at floor) > model ; NPC root > Ragdoll
            Vector3 hips = rig[BoneRole.Pelvis].position;
            var npc = new GameObject(model.name + " (Active Ragdoll)");
            Undo.RegisterCreatedObjectUndo(npc, "Build Active Ragdoll");
            npc.transform.SetPositionAndRotation(new Vector3(hips.x, groundY, hips.z), frame);
            if (model.parent != null)
                Undo.SetTransformParent(npc.transform, model.parent, "Build Active Ragdoll");

            var visual = new GameObject("Visual");
            Undo.RegisterCreatedObjectUndo(visual, "Build Active Ragdoll");
            visual.transform.SetParent(npc.transform, false);
            Undo.SetTransformParent(model, visual.transform, "Build Active Ragdoll");

            var ragdollRoot = new GameObject("Ragdoll");
            Undo.RegisterCreatedObjectUndo(ragdollRoot, "Build Active Ragdoll");
            ragdollRoot.transform.SetParent(npc.transform, false);
            if (layer >= 0) ragdollRoot.layer = layer;

            // ---- Bodies
            bool useChest = options.includeChest && rig[BoneRole.Chest] != null && rig[BoneRole.Chest] != rig[BoneRole.Spine];
            Dictionary<BoneRole, float> masses = ComputeMasses(options.totalMass, useChest, options.includeHands);
            var bodies = new Dictionary<BoneRole, Rigidbody>();
            var bones = new List<RagdollBone>();
            var ctx = new Context(rig, frame, right, forward, scale, groundY, options.limbThickness, layer, useChest, options.includeHands);

            foreach (BoneRole role in BuildOrder)
            {
                if (!masses.ContainsKey(role))
                    continue;
                Transform source = rig[role];
                if (source == null)
                    continue;

                var go = new GameObject(role.ToString());
                Undo.RegisterCreatedObjectUndo(go, "Build Active Ragdoll");
                go.transform.SetParent(ragdollRoot.transform, false);
                go.transform.SetPositionAndRotation(source.position, source.rotation);
                if (layer >= 0) go.layer = layer;

                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = masses[role];
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                bodies[role] = rb;

                CreateColliders(ctx, role, go.transform);

                BodyPart part = go.AddComponent<BodyPart>();
                part.SetRole(role);

                ConfigurableJoint joint = null;
                if (role != BoneRole.Pelvis)
                {
                    Rigidbody parent = bodies[ParentOf(role, useChest)];
                    joint = CreateJoint(ctx, role, go, parent);
                }

                bones.Add(new RagdollBone { role = role, body = rb, joint = joint, target = source, muscleMultiplier = 1f });
            }

            if (!options.includeHands)
            {
                // Fists still need colliders: put them on the forearms.
                AddHandCollider(ctx, BodySide.Left, bodies[BoneRole.LeftLowerArm].transform);
                AddHandCollider(ctx, BodySide.Right, bodies[BoneRole.RightLowerArm].transform);
            }

            // ---- Components
            ActiveRagdollCharacter character = Undo.AddComponent<ActiveRagdollCharacter>(npc);
            Undo.AddComponent<JointMotorDriver>(npc);
            Undo.AddComponent<BalanceController>(npc);
            Undo.AddComponent<LocomotionController>(npc);
            Undo.AddComponent<ProceduralAnimator>(npc);
            Undo.AddComponent<RagdollHealth>(npc);
            character.EditorAssign(visual.transform, rig.animator, ragdollRoot.transform, bones);

            if (rig.animator != null)
            {
                Undo.RecordObject(rig.animator, "Build Active Ragdoll");
                rig.animator.applyRootMotion = false;
                rig.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (rig.animator.runtimeAnimatorController != null)
                    Undo.AddComponent<AnimatorParameterBridge>(npc);
            }

            if (options.addAI)
            {
                CombatTarget target = Undo.AddComponent<CombatTarget>(npc);
                Rigidbody chest = bodies.ContainsKey(BoneRole.Chest) ? bodies[BoneRole.Chest] : bodies[BoneRole.Spine];
                target.EditorSetup(options.team, bodies[BoneRole.Head].transform, chest.transform, bodies[BoneRole.Pelvis]);
                Undo.AddComponent<NPCPerception>(npc);
                Undo.AddComponent<NPCNavigator>(npc);
                Undo.AddComponent<NPCStateMachine>(npc);
                Undo.AddComponent<NPCWeaponHolder>(npc);
            }

            if (options.addBIMOSGrabs)
            {
                Type grabs = Type.GetType(BIMOSGrabsType);
                if (grabs != null)
                    Undo.AddComponent(npc, grabs);
            }

            EditorUtility.SetDirty(character);
            Undo.CollapseUndoOperations(group);

            var errors = new List<string>();
            var warnings = new List<string>();
            if (!character.Validate(errors, warnings))
                Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} Built '{npc.name}' but validation failed:\n- " + string.Join("\n- ", errors), npc);
            foreach (string w in warnings)
                Debug.LogWarning($"{ActiveRagdollCharacter.LogPrefix} '{npc.name}': {w}", npc);

            Selection.activeGameObject = npc;
            error = null;
            return character;
        }

        // ------------------------------------------------------------------ Masses

        private static Dictionary<BoneRole, float> ComputeMasses(float total, bool useChest, bool includeHands)
        {
            // Segment mass fractions (Winter, Biomechanics and Motor Control of Human Movement).
            var f = new Dictionary<BoneRole, float>
            {
                [BoneRole.Pelvis] = 0.142f,
                [BoneRole.Spine] = 0.139f,
                [BoneRole.Chest] = 0.216f,
                [BoneRole.Head] = 0.081f,
                [BoneRole.LeftUpperArm] = 0.028f, [BoneRole.RightUpperArm] = 0.028f,
                [BoneRole.LeftLowerArm] = 0.016f, [BoneRole.RightLowerArm] = 0.016f,
                [BoneRole.LeftHand] = 0.006f, [BoneRole.RightHand] = 0.006f,
                [BoneRole.LeftUpperLeg] = 0.100f, [BoneRole.RightUpperLeg] = 0.100f,
                [BoneRole.LeftLowerLeg] = 0.0465f, [BoneRole.RightLowerLeg] = 0.0465f,
                [BoneRole.LeftFoot] = 0.0145f, [BoneRole.RightFoot] = 0.0145f,
            };

            if (!useChest)
            {
                f[BoneRole.Spine] += f[BoneRole.Chest];
                f.Remove(BoneRole.Chest);
            }
            if (!includeHands)
            {
                f[BoneRole.LeftLowerArm] += f[BoneRole.LeftHand];
                f[BoneRole.RightLowerArm] += f[BoneRole.RightHand];
                f.Remove(BoneRole.LeftHand);
                f.Remove(BoneRole.RightHand);
            }

            float sum = 0f;
            foreach (float v in f.Values) sum += v;
            var masses = new Dictionary<BoneRole, float>(f.Count);
            foreach (KeyValuePair<BoneRole, float> kv in f)
                masses[kv.Key] = total * kv.Value / sum;
            return masses;
        }

        private static BoneRole ParentOf(BoneRole role, bool useChest)
        {
            BoneRole upperTorso = useChest ? BoneRole.Chest : BoneRole.Spine;
            switch (role)
            {
                case BoneRole.Spine: return BoneRole.Pelvis;
                case BoneRole.Chest: return BoneRole.Spine;
                case BoneRole.Head: return upperTorso;
                case BoneRole.LeftUpperArm:
                case BoneRole.RightUpperArm: return upperTorso;
                case BoneRole.LeftLowerArm: return BoneRole.LeftUpperArm;
                case BoneRole.RightLowerArm: return BoneRole.RightUpperArm;
                case BoneRole.LeftHand: return BoneRole.LeftLowerArm;
                case BoneRole.RightHand: return BoneRole.RightLowerArm;
                case BoneRole.LeftUpperLeg:
                case BoneRole.RightUpperLeg: return BoneRole.Pelvis;
                case BoneRole.LeftLowerLeg: return BoneRole.LeftUpperLeg;
                case BoneRole.RightLowerLeg: return BoneRole.RightUpperLeg;
                case BoneRole.LeftFoot: return BoneRole.LeftLowerLeg;
                case BoneRole.RightFoot: return BoneRole.RightLowerLeg;
                default: return BoneRole.Pelvis;
            }
        }

        // ------------------------------------------------------------------ Colliders

        private sealed class Context
        {
            public readonly RigDefinition rig;
            public readonly Quaternion frame;
            public readonly Vector3 right;
            public readonly Vector3 forward;
            public readonly float scale;
            public readonly float groundY;
            public readonly float thickness;
            public readonly int layer;
            public readonly bool useChest;
            public readonly bool includeHands;

            public Context(RigDefinition rig, Quaternion frame, Vector3 right, Vector3 forward, float scale, float groundY,
                float thickness, int layer, bool useChest, bool includeHands)
            {
                this.rig = rig;
                this.frame = frame;
                this.right = right;
                this.forward = forward;
                this.scale = scale;
                this.groundY = groundY;
                this.thickness = thickness;
                this.layer = layer;
                this.useChest = useChest;
                this.includeHands = includeHands;
            }

            public Vector3 P(BoneRole role) => rig[role].position;
        }

        private static void CreateColliders(Context c, BoneRole role, Transform body)
        {
            float s = c.scale;
            float t = c.thickness;
            switch (role)
            {
                case BoneRole.Pelvis:
                {
                    Vector3 l = c.P(BoneRole.LeftUpperLeg);
                    Vector3 r = c.P(BoneRole.RightUpperLeg);
                    float width = Mathf.Max(0.2f * s, Vector3.Distance(l, r) + 0.12f * s);
                    float bottom = Mathf.Min(l.y, r.y) - 0.06f * s;
                    float top = c.P(BoneRole.Spine).y + 0.02f * s;
                    float height = Mathf.Max(0.12f * s, top - bottom);
                    Vector3 center = new Vector3(c.P(BoneRole.Pelvis).x, (top + bottom) * 0.5f, c.P(BoneRole.Pelvis).z);
                    AddBox(c, body, center, c.frame, new Vector3(width, height, 0.2f * s));
                    break;
                }
                case BoneRole.Spine:
                {
                    Vector3 from = c.P(BoneRole.Spine);
                    float topY = c.useChest ? c.P(BoneRole.Chest).y : UpperTorsoTop(c);
                    float width = c.useChest ? 0.28f * s : ShoulderWidth(c) * 0.9f;
                    AddTorsoBox(c, body, from, topY, width, (c.useChest ? 0.19f : 0.21f) * s);
                    break;
                }
                case BoneRole.Chest:
                    AddTorsoBox(c, body, c.P(BoneRole.Chest), UpperTorsoTop(c), ShoulderWidth(c) * 0.9f, 0.21f * s);
                    break;
                case BoneRole.Head:
                    AddSphere(c, body, c.P(BoneRole.Head) + Vector3.up * (0.08f * s) + c.forward * (0.02f * s), 0.1f * s);
                    break;
                case BoneRole.LeftUpperArm:
                case BoneRole.RightUpperArm:
                    AddCapsule(c, body, c.P(role), c.P(role == BoneRole.LeftUpperArm ? BoneRole.LeftLowerArm : BoneRole.RightLowerArm), 0.05f * s * t);
                    break;
                case BoneRole.LeftLowerArm:
                case BoneRole.RightLowerArm:
                    AddCapsule(c, body, c.P(role), c.P(role == BoneRole.LeftLowerArm ? BoneRole.LeftHand : BoneRole.RightHand), 0.042f * s * t);
                    break;
                case BoneRole.LeftHand:
                    AddHandCollider(c, BodySide.Left, body);
                    break;
                case BoneRole.RightHand:
                    AddHandCollider(c, BodySide.Right, body);
                    break;
                case BoneRole.LeftUpperLeg:
                case BoneRole.RightUpperLeg:
                    AddCapsule(c, body, c.P(role), c.P(role == BoneRole.LeftUpperLeg ? BoneRole.LeftLowerLeg : BoneRole.RightLowerLeg), 0.075f * s * t);
                    break;
                case BoneRole.LeftLowerLeg:
                case BoneRole.RightLowerLeg:
                    AddCapsule(c, body, c.P(role), c.P(role == BoneRole.LeftLowerLeg ? BoneRole.LeftFoot : BoneRole.RightFoot), 0.055f * s * t);
                    break;
                case BoneRole.LeftFoot:
                    AddFootBox(c, body, c.P(role), c.rig.leftToes);
                    break;
                case BoneRole.RightFoot:
                    AddFootBox(c, body, c.P(role), c.rig.rightToes);
                    break;
            }
        }

        private static float ShoulderWidth(Context c)
        {
            return Mathf.Max(0.28f * c.scale, Vector3.Distance(c.P(BoneRole.LeftUpperArm), c.P(BoneRole.RightUpperArm)));
        }

        private static float UpperTorsoTop(Context c)
        {
            if (c.rig.neck != null)
                return c.rig.neck.position.y;
            return c.P(BoneRole.Head).y - 0.05f * c.scale;
        }

        private static void AddTorsoBox(Context c, Transform body, Vector3 from, float topY, float width, float depth)
        {
            float height = Mathf.Max(0.08f * c.scale, topY - from.y);
            Vector3 center = new Vector3(from.x, from.y + height * 0.5f, from.z);
            AddBox(c, body, center, c.frame, new Vector3(width, height, depth));
        }

        private static void AddHandCollider(Context c, BodySide side, Transform body)
        {
            Transform hand = c.rig[BoneRoles.Hand(side)];
            Transform forearm = c.rig[BoneRoles.LowerArm(side)];
            Transform end = side == BodySide.Left ? c.rig.leftHandEnd : c.rig.rightHandEnd;
            Vector3 start = hand.position;
            Vector3 dir = RagdollMath.SafeNormalize(start - forearm.position, c.forward);
            Vector3 tip = end != null ? end.position + dir * (0.03f * c.scale) : start + dir * (0.11f * c.scale);
            AddCapsule(c, body, start, tip, 0.045f * c.scale * c.thickness, "Fist");
        }

        private static void AddFootBox(Context c, Transform body, Vector3 ankle, Transform toes)
        {
            float s = c.scale;
            Vector3 toeTip = toes != null ? toes.position : ankle + c.forward * (0.14f * s);
            Vector3 heel = ankle;
            Vector3 footForward = RagdollMath.SafeNormalize(RagdollMath.Flatten(toeTip - heel), c.forward);
            heel -= footForward * (0.05f * s);
            toeTip += footForward * (0.05f * s);
            float length = Mathf.Max(0.15f * s, Vector3.Dot(RagdollMath.Flatten(toeTip - heel), footForward));
            float bottom = c.groundY;
            float top = ankle.y + 0.02f * s;
            float height = Mathf.Max(0.04f * s, top - bottom);
            Vector3 mid = RagdollMath.Flatten(heel) + footForward * (length * 0.5f);
            Vector3 center = new Vector3(mid.x, (top + bottom) * 0.5f, mid.z);
            AddBox(c, body, center, Quaternion.LookRotation(footForward, Vector3.up), new Vector3(0.1f * s, height, length));
        }

        private static GameObject CreateColliderObject(Context c, Transform body, string name, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Build Active Ragdoll");
            go.transform.SetParent(body, false);
            go.transform.SetPositionAndRotation(position, rotation);
            if (c.layer >= 0) go.layer = c.layer;
            return go;
        }

        private static void AddCapsule(Context c, Transform body, Vector3 a, Vector3 b, float radius, string name = "Collider")
        {
            Vector3 dir = b - a;
            float length = dir.magnitude;
            if (length < 1e-3f)
            {
                AddSphere(c, body, a, radius);
                return;
            }
            Vector3 up = Mathf.Abs(Vector3.Dot(dir / length, Vector3.up)) > 0.9f ? c.forward : Vector3.up;
            GameObject go = CreateColliderObject(c, body, name, a, Quaternion.LookRotation(dir, up));
            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 2;
            capsule.radius = radius;
            capsule.height = Mathf.Max(length, radius * 2f);
            capsule.center = new Vector3(0f, 0f, length * 0.5f);
        }

        private static void AddBox(Context c, Transform body, Vector3 center, Quaternion rotation, Vector3 size)
        {
            GameObject go = CreateColliderObject(c, body, "Collider", center, rotation);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = size;
        }

        private static void AddSphere(Context c, Transform body, Vector3 center, float radius)
        {
            GameObject go = CreateColliderObject(c, body, "Collider", center, Quaternion.identity);
            SphereCollider sphere = go.AddComponent<SphereCollider>();
            sphere.radius = radius;
        }

        // ------------------------------------------------------------------ Joints

        /// <summary>
        /// Anatomical limits. Joint angle sign convention: rotating the child's bone direction D toward M
        /// about axis cross(D, M) is a NEGATIVE ConfigurableJoint X angle (the same convention Unity's own
        /// Ragdoll Wizard uses for knees and hips).
        /// </summary>
        private static ConfigurableJoint CreateJoint(Context c, BoneRole role, GameObject go, Rigidbody parent)
        {
            Vector3 R = c.right;
            Vector3 U = Vector3.up;
            Vector3 F = c.forward;

            switch (role)
            {
                case BoneRole.Spine:
                    return AddJoint(go, parent, R, U, -40f, 25f, 25f, 20f, false);
                case BoneRole.Chest:
                    return AddJoint(go, parent, R, U, -30f, 20f, 20f, 15f, false);
                case BoneRole.Head:
                    return AddJoint(go, parent, R, U, -50f, 40f, 70f, 35f, false);

                case BoneRole.LeftUpperLeg:
                case BoneRole.RightUpperLeg:
                    // Flexion (thigh forward) is positive about R.
                    return AddJoint(go, parent, R, U, -30f, 110f, 30f, 40f, false);
                case BoneRole.LeftLowerLeg:
                case BoneRole.RightLowerLeg:
                    // Knee flexion (shin backward) is negative about R.
                    return AddJoint(go, parent, R, U, -145f, 3f, 0f, 0f, true);
                case BoneRole.LeftFoot:
                case BoneRole.RightFoot:
                    // Toe-up is positive about R.
                    return AddJoint(go, parent, R, U, -45f, 30f, 15f, 20f, false);

                case BoneRole.LeftUpperArm:
                case BoneRole.RightUpperArm:
                {
                    Vector3 d = BoneDirection(c, role, BoneRoles.LowerArm(BoneRoles.GetSide(role)));
                    Vector3 axis = RagdollMath.SafeNormalize(Vector3.Cross(d, F), U);
                    // Raising the arm forward is negative; Y swings the arm up/down beside the body.
                    return AddJoint(go, parent, axis, F, -100f, 40f, 95f, 60f, false);
                }
                case BoneRole.LeftLowerArm:
                case BoneRole.RightLowerArm:
                {
                    Vector3 d = BoneDirection(c, role, BoneRoles.Hand(BoneRoles.GetSide(role)));
                    Vector3 axis = RagdollMath.SafeNormalize(Vector3.Cross(d, F), U);
                    // Elbow flexion (forearm toward the front) is negative.
                    return AddJoint(go, parent, axis, F, -145f, 3f, 0f, 0f, true);
                }
                case BoneRole.LeftHand:
                case BoneRole.RightHand:
                {
                    Vector3 d = RagdollMath.SafeNormalize(c.P(role) - c.P(BoneRoles.LowerArm(BoneRoles.GetSide(role))), F);
                    Vector3 axis = RagdollMath.SafeNormalize(Vector3.Cross(d, F), U);
                    return AddJoint(go, parent, axis, F, -70f, 70f, 30f, 25f, false);
                }
                default:
                    return AddJoint(go, parent, R, U, -30f, 30f, 30f, 30f, false);
            }
        }

        private static Vector3 BoneDirection(Context c, BoneRole from, BoneRole to)
        {
            return RagdollMath.SafeNormalize(c.P(to) - c.P(from), Vector3.down);
        }

        private static ConfigurableJoint AddJoint(GameObject go, Rigidbody parent, Vector3 worldAxis, Vector3 worldSecondary,
            float lowX, float highX, float swingY, float swingZ, bool hinge)
        {
            ConfigurableJoint j = go.AddComponent<ConfigurableJoint>();
            j.connectedBody = parent;
            j.autoConfigureConnectedAnchor = false;
            j.anchor = Vector3.zero;
            j.connectedAnchor = parent.transform.InverseTransformPoint(go.transform.position);

            Vector3 axis = go.transform.InverseTransformDirection(worldAxis).normalized;
            Vector3 secondary = go.transform.InverseTransformDirection(worldSecondary);
            secondary = RagdollMath.SafeNormalize(secondary - axis * Vector3.Dot(secondary, axis), Vector3.Cross(axis, Vector3.right));
            j.axis = axis;
            j.secondaryAxis = secondary;

            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = hinge ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
            j.angularZMotion = hinge ? ConfigurableJointMotion.Locked : ConfigurableJointMotion.Limited;
            j.lowAngularXLimit = new SoftJointLimit { limit = lowX };
            j.highAngularXLimit = new SoftJointLimit { limit = highX };
            j.angularYLimit = new SoftJointLimit { limit = swingY };
            j.angularZLimit = new SoftJointLimit { limit = swingZ };

            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = float.MaxValue };
            j.enableCollision = false;
            j.enablePreprocessing = false;
            return j;
        }

        // ------------------------------------------------------------------ Cleanup

        private static void RemoveExistingPhysics(Transform model)
        {
            var removed = 0;
            foreach (Joint joint in model.GetComponentsInChildren<Joint>(true))
            {
                Undo.DestroyObjectImmediate(joint);
                removed++;
            }
            foreach (Rigidbody rb in model.GetComponentsInChildren<Rigidbody>(true))
            {
                Undo.DestroyObjectImmediate(rb);
                removed++;
            }
            foreach (Collider col in model.GetComponentsInChildren<Collider>(true))
            {
                Undo.DestroyObjectImmediate(col);
                removed++;
            }
            if (removed > 0)
                Debug.Log($"{ActiveRagdollCharacter.LogPrefix} Removed {removed} physics component(s) from '{model.name}' (the visual rig must not carry physics).");
        }

        /// <summary>Rebuilds the bone list of an existing character from the BodyParts under its ragdoll root.</summary>
        public static bool AutoAssignBones(ActiveRagdollCharacter character, out string error)
        {
            error = null;
            Transform ragdollRoot = character.RagdollRoot != null ? character.RagdollRoot : character.transform.Find("Ragdoll");
            if (ragdollRoot == null)
            {
                error = "No ragdoll root found (assign one or name a child 'Ragdoll').";
                return false;
            }

            Animator animator = character.Animator != null ? character.Animator : character.GetComponentInChildren<Animator>();
            Transform animatedRoot = character.AnimatedRoot != null ? character.AnimatedRoot : (animator != null ? animator.transform : null);
            var bones = new List<RagdollBone>();
            foreach (BodyPart part in ragdollRoot.GetComponentsInChildren<BodyPart>(true))
            {
                Transform target = null;
                if (animator != null && animator.isHuman)
                {
                    target = animator.GetBoneTransform(RigDefinition.ToHumanBone(part.Role));
                    if (target == null && part.Role == BoneRole.Chest)
                        target = animator.GetBoneTransform(HumanBodyBones.UpperChest);
                }
                if (target == null && animatedRoot != null)
                    target = FindByName(animatedRoot, part.Role.ToString());

                bones.Add(new RagdollBone
                {
                    role = part.Role,
                    body = part.GetComponent<Rigidbody>(),
                    joint = part.GetComponent<ConfigurableJoint>(),
                    target = target,
                    muscleMultiplier = 1f,
                });
            }

            if (bones.Count == 0)
            {
                error = "No BodyPart components found under the ragdoll root. Add a BodyPart (with its role) to each ragdoll rigidbody.";
                return false;
            }

            Undo.RecordObject(character, "Auto Assign Bones");
            character.EditorAssign(animatedRoot, animator, ragdollRoot, bones);
            EditorUtility.SetDirty(character);
            return true;
        }

        private static Transform FindByName(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        internal static void DestroyColliders(GameObject go)
        {
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);
        }
    }
}
