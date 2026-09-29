using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    [CustomEditor(typeof(ActiveRagdollCharacter))]
    public sealed class ActiveRagdollCharacterEditor : UnityEditor.Editor
    {
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _warnings = new List<string>();

        public override void OnInspectorGUI()
        {
            var character = (ActiveRagdollCharacter)target;

            if (Application.isPlaying)
                DrawRuntimeState(character);
            else
                DrawValidation(character);

            EditorGUILayout.Space();
            DrawDefaultInspector();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    if (GUILayout.Button("Auto Assign Bones"))
                    {
                        if (!ActiveRagdollBuilder.AutoAssignBones(character, out string error))
                            EditorUtility.DisplayDialog("Active Ragdoll", error, "OK");
                    }
                }
                if (character.RagdollRoot != null && GUILayout.Button("Select Ragdoll"))
                    Selection.activeTransform = character.RagdollRoot;
            }

            if (Application.isPlaying && character.IsValid)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(character.IsDead))
                    {
                        if (GUILayout.Button("Kill"))
                            character.Kill();
                    }
                    if (GUILayout.Button("Revive"))
                    {
                        NPCStateMachine machine = character.GetComponent<NPCStateMachine>();
                        if (machine != null)
                            machine.Respawn(character.Position - Vector3.up * character.StandingPelvisHeight, character.HeadingRotation);
                        else
                            character.Revive();
                    }
                    if (GUILayout.Button("Knock Down") && character.Balance != null)
                        character.Balance.ForceLoseBalance();
                }
                Repaint();
            }
        }

        private void DrawValidation(ActiveRagdollCharacter character)
        {
            _errors.Clear();
            _warnings.Clear();
            bool ok = character.Validate(_errors, _warnings);
            if (ok && _warnings.Count == 0)
            {
                EditorGUILayout.HelpBox($"Rig valid: {character.BoneCount} bodies.", MessageType.Info);
                return;
            }
            foreach (string e in _errors)
                EditorGUILayout.HelpBox(e, MessageType.Error);
            foreach (string w in _warnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);
        }

        private static void DrawRuntimeState(ActiveRagdollCharacter character)
        {
            if (!character.IsValid)
            {
                EditorGUILayout.HelpBox("Invalid rig (see console). Character disabled.", MessageType.Error);
                return;
            }

            NPCStateMachine machine = character.GetComponent<NPCStateMachine>();
            BalanceController balance = character.Balance;
            string state = machine != null ? machine.CurrentState.ToString() : (character.IsDead ? "Dead" : "Alive");
            string text = $"State: {state}\nProfile: {character.Profile}";
            if (balance != null && balance.IsInitialized)
                text += $"\nBalance: {balance.State}  support {balance.Support:0.00}  error {balance.BalanceError:0.00} m  tilt {balance.TiltAngle:0}°  pelvis {balance.PelvisHeightRatio:0.00}";
            if (character.Health != null)
                text += $"\nHealth: {character.Health.CurrentHealth:0}/{character.Health.MaxHealth:0}";
            EditorGUILayout.HelpBox(text, MessageType.None);
        }
    }
}
