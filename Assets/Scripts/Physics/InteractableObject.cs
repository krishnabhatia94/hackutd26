using System;
using System.Collections.Generic;
using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Marks an object in the scene as interactable by the humanoid agent.
    /// Tracks relative 3D vectors, heading, and distance to the humanoid rig.
    /// </summary>
    [SelectionBase]
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Interactable Object")]
    public class InteractableObject : MonoBehaviour
    {
        public static readonly List<InteractableObject> AllInteractables = new();

        [Header("Object Metadata")]
        [Tooltip("Human-readable name of the interactable item.")]
        public string objectName = "Red Dodgeball";

        [Tooltip("Type of interaction supported (e.g. Pickup, Kick, Inspect).")]
        public string interactType = "Pickup";

        [Tooltip("Short description for Nemotron reasoning context.")]
        public string description = "A regulation red dodgeball resting on the floor in front of the dummy.";

        [Tooltip("Target interaction radius in meters.")]
        public float interactionRadius = 0.5f;

        private void OnEnable()
        {
            if (!AllInteractables.Contains(this))
            {
                AllInteractables.Add(this);
            }

            if (!gameObject.CompareTag("Interactable"))
            {
                try
                {
                    gameObject.tag = "Interactable";
                }
                catch
                {
                    // Fallback if tag is not defined yet in editor
                }
            }
        }

        private void OnDisable()
        {
            AllInteractables.Remove(this);
        }

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(objectName))
            {
                objectName = gameObject.name;
            }

#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null && gameObject != null && !gameObject.CompareTag("Interactable"))
                {
                    try { gameObject.tag = "Interactable"; } catch {}
                }
            };
#endif
        }

        public Vector3 GetPosition() => transform.position;

        /// <summary>
        /// Computes 3D offset and distance relative to a reference position (e.g. humanoid pelvis).
        /// </summary>
        public (Vector3 vectorFromHumanoid, float distance, float bearingDeg) GetTelemetryRelativeTo(Transform humanoid)
        {
            if (humanoid == null) return (Vector3.zero, 0f, 0f);

            Vector3 offset = transform.position - humanoid.position;
            float dist = offset.magnitude;

            // Compute horizontal bearing angle relative to humanoid's forward orientation
            Vector3 forward = humanoid.forward;
            forward.y = 0f;
            Vector3 horizOffset = offset;
            horizOffset.y = 0f;

            float bearing = 0f;
            if (forward.sqrMagnitude > 0.001f && horizOffset.sqrMagnitude > 0.001f)
            {
                bearing = Vector3.SignedAngle(forward.normalized, horizOffset.normalized, Vector3.up);
            }

            return (offset, dist, bearing);
        }
    }
}

