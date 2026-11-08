using UnityEngine;
using UnityEditor;
using Physics;

namespace Physics.Editor
{
    [CustomEditor(typeof(RagdollPoseGoalController))]
    public class RagdollPoseGoalControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            RagdollPoseGoalController controller = (RagdollPoseGoalController)target;

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Pose Goal Actions", EditorStyles.boldLabel);

            // Large, clear Reset to Standing T-Pose Button
            GUI.backgroundColor = new Color(0.2f, 0.75f, 1.0f);
            if (GUILayout.Button("Reset to Standing T-Pose", GUILayout.Height(34)))
            {
                controller.ApplyStandingTPose();
            }

            GUI.backgroundColor = Color.white;
        }
    }
}

