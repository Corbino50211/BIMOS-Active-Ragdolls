using System;
using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    internal static class ActiveRagdollMenu
    {
        private const string DesktopPlayerType = "ActiveRagdoll.Samples.DesktopTestPlayer, ActiveRagdoll.Samples.DesktopTest";

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
