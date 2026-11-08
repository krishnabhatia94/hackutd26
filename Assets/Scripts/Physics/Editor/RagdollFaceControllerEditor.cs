using UnityEngine;
using UnityEditor;
using Physics;

namespace Physics.Editor
{
    [CustomEditor(typeof(RagdollFaceController))]
    public class RagdollFaceControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            RagdollFaceController controller = (RagdollFaceController)target;

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Expression Manual Override (Testing)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Idle Face (:3)", GUILayout.Height(28)))
            {
                controller.ApplyEmotion(FaceEmotionState.Idle, true);
            }
            if (GUILayout.Button("Happy Face (>w<)", GUILayout.Height(28)))
            {
                controller.ApplyEmotion(FaceEmotionState.Happy, true);
            }
            if (GUILayout.Button("Sad Face (T_T)", GUILayout.Height(28)))
            {
                controller.NotifyDodgeballHit(null);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}

