using System;
using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    internal static class ActiveRagdollMenu
    {
        private const string DesktopPlayerType = "ActiveRagdoll.Samples.DesktopTestPlayer, ActiveRagdoll.Samples.DesktopTest";
        private const string BIMOSGrabType = "BIMOS.Grab, kadenzombie8.bimos";

        [MenuItem("Tools/Active Ragdoll/Create Mannequin NPC", priority = 1)]
        [MenuItem("GameObject/Active Ragdoll/Mannequin NPC", priority = 10)]
        private static void CreateMannequinNpc()
        {
            Vector3 position = SpawnPoint();
            CreateMannequin(position, Quaternion.identity);
        }

        [MenuItem("Tools/Active Ragdoll/Create Test Arena", priority = 2)]
        private static void CreateTestArena()
        {
            int group = Undo.GetCurrentGroup();
            var arena = new GameObject("Active Ragdoll Test Arena");
            Undo.RegisterCreatedObjectUndo(arena, "Create Test Arena");

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Undo.RegisterCreatedObjectUndo(floor, "Create Test Arena");
            floor.name = "Floor";
            floor.transform.SetParent(arena.transform, false);
            floor.transform.localScale = new Vector3(6f, 1f, 6f);

            if (UnityEngine.Object.FindAnyObjectByType<Light>() == null)
            {
                var light = new GameObject("Directional Light");
                Undo.RegisterCreatedObjectUndo(light, "Create Test Arena");
                light.transform.SetParent(arena.transform, false);
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                light.AddComponent<Light>().type = LightType.Directional;
            }

            for (int i = 0; i < 3; i++)
            {
                Vector3 pos = new Vector3((i - 1) * 2.5f, 0f, 6f);
                ActiveRagdollCharacter npc = CreateMannequin(pos, Quaternion.Euler(0f, 180f, 0f));
                if (npc != null)
                    Undo.SetTransformParent(npc.transform, arena.transform, "Create Test Arena");
            }

            GameObject knife = CreateKnife(new Vector3(0.6f, 0.03f, -1f), Quaternion.Euler(0f, 90f, 0f));
            Undo.SetTransformParent(knife.transform, arena.transform, "Create Test Arena");

            Type desktopPlayer = Type.GetType(DesktopPlayerType);
            if (desktopPlayer != null && UnityEngine.Object.FindAnyObjectByType(desktopPlayer) == null)
            {
                var player = new GameObject("Desktop Test Player");
                Undo.RegisterCreatedObjectUndo(player, "Create Test Arena");
                player.transform.SetParent(arena.transform, false);
                player.transform.position = new Vector3(0f, 0f, -2f);
                player.AddComponent(desktopPlayer);
            }
            else if (desktopPlayer == null)
            {
                Debug.Log($"{ActiveRagdollCharacter.LogPrefix} Test arena created. Import the 'Desktop Test Harness' sample (Package Manager) for a mouse/keyboard test player, or drop in your BIMOS player.");
            }

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = arena;
        }

        [MenuItem("Tools/Active Ragdoll/Create Test Knife", priority = 3)]
        [MenuItem("GameObject/Active Ragdoll/Test Knife", priority = 11)]
        private static void CreateTestKnife()
        {
            Selection.activeGameObject = CreateKnife(SpawnPoint() + Vector3.up * 0.03f, Quaternion.identity);
        }

        /// <summary>
        /// A 30 cm knife (18 cm blade along +Z) with a <see cref="BladeWeapon"/>, and a BIMOS Grab on the
        /// handle when BIMOS is installed.
        /// </summary>
        internal static GameObject CreateKnife(Vector3 position, Quaternion rotation)
        {
            var knife = new GameObject("Test Knife");
            Undo.RegisterCreatedObjectUndo(knife, "Create Test Knife");
            knife.transform.SetPositionAndRotation(position, rotation);

            var body = knife.AddComponent<Rigidbody>();
            body.mass = 0.25f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            GameObject handle = Part(knife, "Handle", new Vector3(0f, 0f, 0.055f), new Vector3(0.028f, 0.022f, 0.11f));
            Part(knife, "Guard", new Vector3(0f, 0f, 0.115f), new Vector3(0.024f, 0.06f, 0.01f));
            GameObject blade = Part(knife, "Blade", new Vector3(0f, 0f, 0.21f), new Vector3(0.006f, 0.03f, 0.18f));
            var tip = new GameObject("Tip");
            tip.transform.SetParent(knife.transform, false);
            tip.transform.localPosition = new Vector3(0f, 0f, 0.3f);

            var weapon = knife.AddComponent<BladeWeapon>();
            var so = new SerializedObject(weapon);
            so.FindProperty("_tip").objectReferenceValue = tip.transform;
            so.FindProperty("_bladeAxis").vector3Value = Vector3.forward;
            so.FindProperty("_bladeLength").floatValue = 0.18f;
            SerializedProperty colliders = so.FindProperty("_bladeColliders");
            colliders.arraySize = 1;
            colliders.GetArrayElementAtIndex(0).objectReferenceValue = blade.GetComponent<Collider>();
            so.ApplyModifiedPropertiesWithoutUndo();

            Type grabType = Type.GetType(BIMOSGrabType);
            if (grabType != null)
            {
                Component grab = handle.AddComponent(grabType);
                foreach (string field in new[] { "EnableGrabs", "DisableGrabs" })
                    grabType.GetField(field)?.SetValue(grab, Array.CreateInstance(grabType, 0));
            }
            return knife;
        }

        private static GameObject Part(GameObject parent, string name, Vector3 localPosition, Vector3 size)
        {
            GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent.transform, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = size;
            return part;
        }

        internal static ActiveRagdollCharacter CreateMannequin(Vector3 position, Quaternion rotation)
        {
            RigDefinition rig = MannequinFactory.Create(position, rotation, out GameObject mannequin);
            ActiveRagdollCharacter character = ActiveRagdollBuilder.Build(rig, new BuildOptions(), out string error);
            if (character == null)
            {
                Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} Mannequin build failed: {error}");
                Undo.DestroyObjectImmediate(mannequin);
                return null;
            }
            character.gameObject.name = "Mannequin NPC";
            return character;
        }

        private static Vector3 SpawnPoint()
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
                return Vector3.zero;
            Vector3 pivot = view.pivot;
            if (Physics.Raycast(pivot + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point;
            return new Vector3(pivot.x, 0f, pivot.z);
        }
    }
}
