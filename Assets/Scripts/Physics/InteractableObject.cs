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

        [Header("Live Held State")]
        [Tooltip("Whether this object is currently held by a humanoid.")]
        public bool isHeld = false;

        [Tooltip("ID of the NPC currently holding this object.")]
        public uint heldByNpcId = 0;

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
        /// Computes 3D local offset and distance relative to a reference position (e.g. humanoid pelvis/root).
        /// vectorFromHumanoid is in the humanoid's local coordinate space, where Z is forward, X is lateral, and Y is vertical.
        /// </summary>
        public (Vector3 vectorFromHumanoid, float distance, float bearingDeg) GetTelemetryRelativeTo(Transform humanoid)
        {
            if (humanoid == null) return (Vector3.zero, 0f, 0f);

            Vector3 localOffset = humanoid.InverseTransformPoint(transform.position);
            float dist = localOffset.magnitude;
            float bearing = Mathf.Atan2(localOffset.x, localOffset.z) * Mathf.Rad2Deg;

            return (localOffset, dist, bearing);
        }
    }
}

