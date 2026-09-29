using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    [CustomEditor(typeof(NPCStateMachine))]
    [CanEditMultipleObjects]
    public sealed class NPCStateMachineEditor : UnityEditor.Editor
    {
        private static readonly GUIContent RetaliateLabel = new GUIContent("Hostile When Attacked",
            "On: fights back when hurt or grabbed, then calms down once it loses the attacker.\n" +
            "Off: never fights; after reacting physically it goes back to what it was doing.");

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            SerializedProperty disposition = serializedObject.FindProperty("_disposition");
            EditorGUILayout.PropertyField(disposition, new GUIContent("Disposition",
                "Idle: stands still. Wander: strolls around its home point. Hostile: hunts any hostile it sees."));

            var mode = (NPCDisposition)disposition.enumValueIndex;
            if (disposition.hasMultipleDifferentValues || mode != NPCDisposition.Hostile)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_retaliateWhenAttacked"), RetaliateLabel);
            if (disposition.hasMultipleDifferentValues || mode == NPCDisposition.Wander)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_wander"), true);

            if (Application.isPlaying && !serializedObject.isEditingMultipleObjects)
            {
                var machine = (NPCStateMachine)target;
                string provoked = machine.IsProvoked ? "  (provoked)" : string.Empty;
                EditorGUILayout.HelpBox($"State: {machine.CurrentState}{provoked}", MessageType.None);
                Repaint();
            }

            EditorGUILayout.Space();
            DrawPropertiesExcluding(serializedObject, "m_Script", "_disposition", "_retaliateWhenAttacked", "_wander");
            serializedObject.ApplyModifiedProperties();
        }
    }
}
