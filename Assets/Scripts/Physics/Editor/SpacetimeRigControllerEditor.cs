using UnityEngine;
using UnityEditor;
using Physics;

namespace Physics.Editor
{
    [CustomEditor(typeof(SpacetimeRigController))]
    public class SpacetimeRigControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            SpacetimeRigController controller = (SpacetimeRigController)target;

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Biomechanical Rig Actions", EditorStyles.boldLabel);

            // Large, clear Reset to T-Pose Button
            GUI.backgroundColor = new Color(0.2f, 0.75f, 1.0f);
            if (GUILayout.Button("Reset Humanoid to T-Pose", GUILayout.Height(34)))
            {
                controller.ResetToTPose();
            }

            EditorGUILayout.Space(6);

            // Interactive Throw Obstacle Button for testing PhysX reaction
            GUI.backgroundColor = new Color(1.0f, 0.45f, 0.35f);
            if (GUILayout.Button("Shoot Projectile at Dummy / Shove", GUILayout.Height(30)))
            {
                float fx = Random.Range(-100f, 100f);
                float fy = Random.Range(20f, 40f);
                float fz = Random.Range(-160f, -80f);
                controller.TriggerShove(fx, fy, fz);
            }

            GUI.backgroundColor = Color.white;
        }
    }
}

