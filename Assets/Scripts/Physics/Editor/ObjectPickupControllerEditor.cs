using UnityEngine;
using UnityEditor;
using Physics;

namespace Physics.Editor
{
    [CustomEditor(typeof(ObjectPickupController))]
    public class ObjectPickupControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            ObjectPickupController controller = (ObjectPickupController)target;

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Interactive Grasp Controls", EditorStyles.boldLabel);

            if (!controller.isHolding)
            {
                GUI.backgroundColor = new Color(0.2f, 0.85f, 0.4f);
                if (GUILayout.Button("Pick Up Nearest Interactable", GUILayout.Height(32)))
                {
                    controller.TryPickupNearestInteractable();
                }
            }
            else
            {
                GUI.backgroundColor = new Color(1.0f, 0.4f, 0.3f);
                if (GUILayout.Button($"Release Held Object ({controller.heldObject?.name})", GUILayout.Height(32)))
                {
                    controller.ReleaseHeldObject();
                }
            }

            GUI.backgroundColor = Color.white;
        }
    }
}

