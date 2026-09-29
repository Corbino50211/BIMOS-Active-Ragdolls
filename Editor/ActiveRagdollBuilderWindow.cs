using UnityEditor;
using UnityEngine;

namespace ActiveRagdoll.EditorTools
{
    /// <summary>Tools > Active Ragdoll > Ragdoll Builder: turns a Humanoid model in the scene into an active-ragdoll NPC.</summary>
    public sealed class ActiveRagdollBuilderWindow : EditorWindow
    {
        [SerializeField] private Animator _model;
        [SerializeField] private BuildOptions _options = new BuildOptions();
        private Vector2 _scroll;
        private SerializedObject _serialized;

        [MenuItem("Tools/Active Ragdoll/Ragdoll Builder", priority = 0)]
        private static void Open()
        {
            var window = GetWindow<ActiveRagdollBuilderWindow>("Ragdoll Builder");
            window.minSize = new Vector2(360f, 420f);
            window.PickFromSelection();
        }

        private void OnEnable()
        {
            _options ??= new BuildOptions();
            _serialized = new SerializedObject(this);
        }

        private void OnSelectionChange()
        {
            PickFromSelection();
            Repaint();
        }

        private void PickFromSelection()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null || EditorUtility.IsPersistent(go))
                return;
            Animator animator = go.GetComponentInChildren<Animator>();
            if (animator != null && animator.GetComponentInParent<ActiveRagdollCharacter>() == null)
                _model = animator;
        }

        private void OnGUI()
        {
            _serialized ??= new SerializedObject(this);
            _serialized.Update();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Select a Humanoid model instance in the scene (standing in its bind/T/A pose, feet on the floor) and press Build. " +
                "The model becomes the animated/visual rig; a separate physical ragdoll is generated next to it and every runtime component is added.",
                MessageType.Info);

            EditorGUILayout.PropertyField(_serialized.FindProperty(nameof(_model)), new GUIContent("Humanoid Model"));
            EditorGUILayout.PropertyField(_serialized.FindProperty(nameof(_options)), new GUIContent("Options"), true);
            _serialized.ApplyModifiedProperties();

            string problem = ValidateModel();
            if (problem != null)
                EditorGUILayout.HelpBox(problem, MessageType.Warning);

            if (_options.addBIMOSGrabs && !ActiveRagdollBuilder.IsBIMOSIntegrationAvailable)
                EditorGUILayout.HelpBox("BIMOS integration not compiled (BIMOS not installed as a package, or ACTIVE_RAGDOLL_BIMOS not defined). Grabs will be skipped.", MessageType.None);

            using (new EditorGUI.DisabledScope(problem != null))
            {
                if (GUILayout.Button("Build Active Ragdoll", GUILayout.Height(32f)))
                    Build();
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Create Mannequin NPC (no model needed)"))
                ActiveRagdollMenu.CreateMannequin(Vector3.zero, Quaternion.identity);

            EditorGUILayout.EndScrollView();
        }

        private string ValidateModel()
        {
            if (_model == null)
                return "Assign a Humanoid model from the scene.";
            if (EditorUtility.IsPersistent(_model))
                return "That is an asset. Drag an instance of it into the scene first.";
            if (!_model.isHuman)
                return "The model's avatar is not Humanoid (Model import settings > Rig > Animation Type: Humanoid).";
            if (_model.GetComponentInParent<ActiveRagdollCharacter>() != null)
                return "This model already has an active ragdoll.";
            return null;
        }

        private void Build()
        {
            if (!RigDefinition.TryFromAnimator(_model, out RigDefinition rig, out string error))
            {
                EditorUtility.DisplayDialog("Ragdoll Builder", error, "OK");
                return;
            }

            ActiveRagdollCharacter character = ActiveRagdollBuilder.Build(rig, _options, out error);
            if (character == null)
                EditorUtility.DisplayDialog("Ragdoll Builder", error, "OK");
            else
                _model = null;
        }
    }
}
