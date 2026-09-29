using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    /// <summary>Creates project layers used by the active ragdolls.</summary>
    public static class LayerSetup
    {
        /// <summary>Returns the index of <paramref name="layerName"/>, creating it in the first free user slot if needed. -1 if impossible.</summary>
        public static int EnsureLayer(string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
                return -1;

            int existing = LayerMask.NameToLayer(layerName);
            if (existing >= 0)
                return existing;

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} Could not open ProjectSettings/TagManager.asset to create layer '{layerName}'.");
                return -1;
            }

            var tagManager = new SerializedObject(assets[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            if (layers == null || !layers.isArray)
            {
                Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} TagManager has no layer list; create layer '{layerName}' manually.");
                return -1;
            }

            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(slot.stringValue))
                    continue;
                slot.stringValue = layerName;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log($"{ActiveRagdollCharacter.LogPrefix} Created layer '{layerName}' in slot {i}.");
                return i;
            }

            Debug.LogError($"{ActiveRagdollCharacter.LogPrefix} No free user layer for '{layerName}'. Free a slot in Project Settings > Tags and Layers.");
            return -1;
        }

        [MenuItem("Tools/Active Ragdoll/Setup Layers", priority = 20)]
        private static void SetupLayers()
        {
            int layer = EnsureLayer(RagdollLayers.DefaultRagdollLayer);
            if (layer < 0)
                return;
            // Ragdolls must collide with each other (corpses pile up, NPCs shove each other) and with everything else.
            Physics.IgnoreLayerCollision(layer, layer, false);
            EditorUtility.DisplayDialog("Active Ragdoll",
                $"Layer '{RagdollLayers.DefaultRagdollLayer}' is ready (slot {layer}).\n\nIt collides with every layer, including BIMOSRig, so NPCs and the BIMOS player physically interact.",
                "OK");
        }
    }
}
