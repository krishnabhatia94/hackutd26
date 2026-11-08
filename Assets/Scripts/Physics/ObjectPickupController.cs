using System;
using System.Collections.Generic;
using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Handles physical grasping, childing, and lifting of interactable objects (e.g. Dodgeball).
    /// Dynamically positions the object between both hands and parents it to the humanoid
    /// as long as the object is held, releasing upon reset, throw, or high-energy impact.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Object Pickup Controller")]
    public class ObjectPickupController : MonoBehaviour
    {
        [Header("Hand Transforms")]
        public Transform leftHand;
        public Transform rightHand;
        public Transform chestTransform;

        [Header("Aim Target")]
        [Tooltip("Optional opposing target to direct projectile throws at.")]
        public Transform opponentTarget;

        [Header("Pickup & Throw Tuning")]
        [Tooltip("Maximum distance from hand midpoint to object center to trigger pickup.")]
        public float pickupReachDistance = 0.95f;

        [Tooltip("Forward offset from hand midpoint where the object rests.")]
        public float forwardGripOffset = 0.08f;

        [Tooltip("Launch speed (m/s) when throwing or releasing an object forward.")]
        public float defaultThrowSpeed = 9.5f;

        [Header("Live State (Read-Only)")]
        public bool isHolding = false;
        public InteractableObject heldObject = null;

        private Transform _gripAnchor;
        private Rigidbody _heldRb;
        private Collider[] _heldColliders;
        private Collider[] _ragdollColliders;
        private DynamicPoseQueueManager _queueManager;

        public event Action<InteractableObject> OnObjectPickedUp;
        public event Action<InteractableObject> OnObjectReleased;

        private void Awake()
        {
            FindBoneReferences();
            _queueManager = GetComponent<DynamicPoseQueueManager>();
            if (_queueManager != null)
            {
                _queueManager.OnWaypointStarted += HandleWaypointStarted;
                _queueManager.OnObstacleEncountered += HandleObstacleEncountered;
            }
        }

        private void OnDestroy()
        {
            if (_queueManager != null)
            {
                _queueManager.OnWaypointStarted -= HandleWaypointStarted;
                _queueManager.OnObstacleEncountered -= HandleObstacleEncountered;
            }
        }

        public void FindBoneReferences()
        {
            _ragdollColliders = GetComponentsInChildren<Collider>(true);

            if (leftHand == null)
            {
                var lfa = transform.Find("Pelvis/Spine/Chest/LeftUpperArm/LeftForearm");
                if (lfa != null) leftHand = lfa;
                else
                {
                    foreach (var col in _ragdollColliders)
                    {
                        if (col.name == "LeftForearm") { leftHand = col.transform; break; }
                    }
                }
            }

            if (rightHand == null)
            {
                var rfa = transform.Find("Pelvis/Spine/Chest/RightUpperArm/RightForearm");
                if (rfa != null) rightHand = rfa;
                else
                {
                    foreach (var col in _ragdollColliders)
                    {
                        if (col.name == "RightForearm") { rightHand = col.transform; break; }
                    }
                }
            }

            if (chestTransform == null)
            {
                var ch = transform.Find("Pelvis/Spine/Chest");
                if (ch != null) chestTransform = ch;
                else
                {
                    foreach (var col in _ragdollColliders)
                    {
                        if (col.name == "Chest") { chestTransform = col.transform; break; }
                    }
                }
            }

            if (_gripAnchor == null)
            {
                GameObject anchorGo = new GameObject("HandsGripAnchor");
                _gripAnchor = anchorGo.transform;
                _gripAnchor.SetParent(chestTransform != null ? chestTransform : transform, false);
            }
        }

        private void HandleWaypointStarted(string stepName, int index, int total)
        {
            string s = stepName.ToLower();
            if (s.Contains("grasp") || s.Contains("pickup") || s.Contains("pick up") || s.Contains("lift") || s.Contains("clasp"))
            {
                if (!isHolding)
                {
                    TryPickupNearestInteractable();
                }
            }
            else if (s.Contains("throw") || s.Contains("release") || s.Contains("launch") || s.Contains("toss") || s.Contains("drop") || s.Contains("hurl") || s.Contains("pitch") || s.Contains("fling") || s.Contains("pass"))
            {
                if (isHolding)
                {
                    Vector3 throwDir;
                    if (opponentTarget != null)
                    {
                        Vector3 targetPos = opponentTarget.position + Vector3.up * 0.2f;
                        Vector3 origin = (_gripAnchor != null) ? _gripAnchor.position : (transform.position + Vector3.up * 1.2f);
                        throwDir = (targetPos - origin).normalized;
                        throwDir = (throwDir + Vector3.up * 0.12f).normalized;
                    }
                    else
                    {
                        Vector3 fwd = transform.forward;
                        throwDir = (fwd + Vector3.up * 0.22f).normalized;
                    }

                    float speed = (s.Contains("drop")) ? 1.0f : ((s.Contains("power") || s.Contains("hurl") || s.Contains("explosive")) ? 13.5f : defaultThrowSpeed);
                    ReleaseHeldObject(throwDir * speed);
                }
            }
        }

        private void HandleObstacleEncountered(string actionName, string boneName, float error)
        {
            if (isHolding)
            {
                ReleaseHeldObject();
            }
        }

        private void FixedUpdate()
        {
            if (leftHand == null || rightHand == null)
            {
                FindBoneReferences();
            }

            if (leftHand != null && rightHand != null && _gripAnchor != null)
            {
                Vector3 handMid = (leftHand.position + rightHand.position) * 0.5f;
                Vector3 fwd = transform.forward;
                _gripAnchor.position = handMid + fwd * forwardGripOffset;
                _gripAnchor.rotation = Quaternion.LookRotation(fwd, Vector3.up);

                // If currently holding an object, enforce its kinematic grip position
                if (isHolding && heldObject != null)
                {
                    heldObject.transform.position = _gripAnchor.position;
                    heldObject.transform.rotation = _gripAnchor.rotation;
                    if (_heldRb != null)
                    {
                        _heldRb.velocity = Vector3.zero;
                        _heldRb.angularVelocity = Vector3.zero;
                    }
                }
            }
        }

        /// <summary>
        /// Attempts to locate and pick up the nearest Interactable object within reach distance.
        /// </summary>
        public bool TryPickupNearestInteractable()
        {
            if (leftHand == null || rightHand == null) FindBoneReferences();

            Vector3 handMid = (leftHand != null && rightHand != null)
                ? (leftHand.position + rightHand.position) * 0.5f
                : (chestTransform != null ? chestTransform.position + transform.forward * 0.4f : transform.position);

            InteractableObject bestObj = null;
            float bestDist = pickupReachDistance;

            // 1. Search InteractableObject.AllInteractables
            foreach (var candidate in InteractableObject.AllInteractables)
            {
                if (candidate == null) continue;
                float d = Vector3.Distance(handMid, candidate.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestObj = candidate;
                }
            }

            // 2. Fallback search by tag "Interactable"
            if (bestObj == null)
            {
                var tagged = GameObject.FindGameObjectsWithTag("Interactable");
                foreach (var go in tagged)
                {
                    float d = Vector3.Distance(handMid, go.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestObj = go.GetComponent<InteractableObject>();
                        if (bestObj == null)
                        {
                            bestObj = go.AddComponent<InteractableObject>();
                        }
                    }
                }
            }

            if (bestObj != null)
            {
                PickupObject(bestObj);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Explicitly attaches and children an interactable object to the hands grip anchor.
        /// </summary>
        public void PickupObject(InteractableObject target)
        {
            if (target == null) return;
            if (isHolding && heldObject == target) return;

            if (isHolding)
            {
                ReleaseHeldObject();
            }

            heldObject = target;
            isHolding = true;

            _heldRb = heldObject.GetComponent<Rigidbody>();
            if (_heldRb != null)
            {
                _heldRb.isKinematic = true;
                _heldRb.useGravity = false;
                _heldRb.velocity = Vector3.zero;
                _heldRb.angularVelocity = Vector3.zero;
            }

            // Child the object so it moves with the hands
            heldObject.transform.SetParent(_gripAnchor, true);

            var proj = heldObject.GetComponent<ObstacleProjectile>();
            if (proj != null) proj.ResetImpact();

            // Temporarily ignore collisions with humanoid ragdoll colliders while held
            _heldColliders = heldObject.GetComponentsInChildren<Collider>(true);
            if (_ragdollColliders == null || _ragdollColliders.Length == 0)
            {
                _ragdollColliders = GetComponentsInChildren<Collider>(true);
            }
            if (_heldColliders != null && _ragdollColliders != null)
            {
                foreach (var hCol in _heldColliders)
                {
                    foreach (var rCol in _ragdollColliders)
                    {
                        if (hCol != null && rCol != null)
                        {
                            UnityEngine.Physics.IgnoreCollision(hCol, rCol, true);
                        }
                    }
                }
            }

            Debug.Log($"[ObjectPickupController] Successfully grasped and childed '{target.gameObject.name}' to hands grip anchor.");
            OnObjectPickedUp?.Invoke(target);
        }

        /// <summary>
        /// Releases the currently held object and unparents it, restoring PhysX dynamics.
        /// </summary>
        public void ReleaseHeldObject(Vector3 throwForce = default)
        {
            if (!isHolding || heldObject == null) return;

            var released = heldObject;
            isHolding = false;
            heldObject = null;

            // Unparent from humanoid hands
            released.transform.SetParent(null, true);

            var proj = released.GetComponent<ObstacleProjectile>();
            if (proj != null) proj.ResetImpact();

            // Temporarily ignore collisions with humanoid ragdoll colliders so the ball cleanly exits hand volume
            StartCoroutine(RestoreCollisionsAfterDelay(released, 0.4f));

            // Restore PhysX Rigidbody dynamics
            if (_heldRb != null)
            {
                _heldRb.isKinematic = false;
                _heldRb.useGravity = true;
                if (throwForce != Vector3.zero)
                {
                    _heldRb.velocity = throwForce;
                    _heldRb.angularVelocity = transform.right * 12f;
                }
                _heldRb = null;
            }

            Debug.Log($"[ObjectPickupController] Released held object '{released.gameObject.name}' back to world physics with throw velocity {throwForce}.");
            OnObjectReleased?.Invoke(released);
        }

        private System.Collections.IEnumerator RestoreCollisionsAfterDelay(InteractableObject released, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (released != null)
            {
                var releasedColliders = released.GetComponentsInChildren<Collider>(true);
                if (_ragdollColliders == null || _ragdollColliders.Length == 0)
                {
                    _ragdollColliders = GetComponentsInChildren<Collider>(true);
                }

                if (releasedColliders != null && _ragdollColliders != null)
                {
                    foreach (var hCol in releasedColliders)
                    {
                        foreach (var rCol in _ragdollColliders)
                        {
                            if (hCol != null && rCol != null)
                            {
                                UnityEngine.Physics.IgnoreCollision(hCol, rCol, false);
                            }
                        }
                    }
                }
            }
            _heldColliders = null;
        }
    }
}
