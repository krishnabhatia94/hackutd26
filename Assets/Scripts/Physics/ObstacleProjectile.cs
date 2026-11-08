using System;
using System.Collections.Generic;
using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Handles physical collision and impact impulse delivery for projectiles thrown at the ragdoll.
    /// Strictly enforces Dodgeball Airborne Session rules:
    /// 1. Whenever a ball is not picked up / on the floor, it CANNOT count as a hit.
    /// 2. When a ball is thrown, an airborne session begins and it is able to count as a hit.
    /// 3. If a ball hits a ragdoll while airborne, it counts as a hit and triggers Sad Face.
    /// 4. If it hits the same ragdoll again during the same airborne session, it does NOT count again.
    /// 5. Tracks each ragdoll struck during the flight, and clears the record when the airborne session ends (upon contacting floor/ground or being grasped).
    /// </summary>
    [AddComponentMenu("Physics/Obstacle Projectile")]
    public class ObstacleProjectile : MonoBehaviour
    {
        [Header("Projectile Impact Settings")]
        [Tooltip("Impulse vector to transfer on physical contact.")]
        public Vector3 impactImpulse;

        [Tooltip("Root transform of the ragdoll to verify hit target (optional).")]
        public Transform targetRoot;

        [Tooltip("Minimum time between registered impacts to allow repeat throws.")]
        public float hitDebounceTime = 0.25f;

        [Header("Airborne Session State (Read-Only)")]
        [Tooltip("Whether this dodgeball is currently an active airborne projectile capable of registering hits.")]
        public bool isAirborne = false;

        [Tooltip("The thrower who launched this projectile (immune to immediate self-hits upon hand release).")]
        public RagdollFaceController currentThrower;

        public event Action<RagdollFaceController, Collision> OnHitRegistered;

        private float _lastHitTime = -999f;
        private RagdollFaceController _lastHitFaceCtrl;
        private readonly HashSet<RagdollFaceController> _hitRagdollsInSession = new();
        private Rigidbody _rb;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
        }

        private void Start()
        {
            if (_rb == null) _rb = GetComponent<Rigidbody>();
        }

        /// <summary>
        /// Begins a new airborne projectile session when thrown.
        /// Enables hit detection and clears the record of hit ragdolls for this flight.
        /// </summary>
        public void LaunchAirborne(RagdollFaceController thrower = null)
        {
            isAirborne = true;
            currentThrower = thrower;
            _hitRagdollsInSession.Clear();
            _lastHitTime = -999f;
            _lastHitFaceCtrl = null;
            Debug.Log($"[ObstacleProjectile] '{gameObject.name}' launched! Airborne session started (Thrower: {(thrower != null ? thrower.gameObject.name : "None")}).");
        }

        /// <summary>
        /// Concludes the airborne projectile session (e.g. ball landed on floor, was picked up, or match reset).
        /// Disables hit detection and clears the session hit record.
        /// </summary>
        public void EndAirborneSession()
        {
            if (isAirborne)
            {
                Debug.Log($"[ObstacleProjectile] '{gameObject.name}' airborne session ended (touched floor/ground/held). Hits disabled.");
            }
            isAirborne = false;
            currentThrower = null;
            _hitRagdollsInSession.Clear();
            _lastHitFaceCtrl = null;
        }

        public void ResetImpact()
        {
            EndAirborneSession();
            _lastHitTime = -999f;
            _lastHitFaceCtrl = null;
        }

        private void FixedUpdate()
        {
            // Safeguard: If airborne projectile has come to rest on or near the floor, conclude session
            if (isAirborne)
            {
                if (transform.position.y <= 0.35f)
                {
                    if (_rb != null && _rb.velocity.sqrMagnitude < 0.8f)
                    {
                        EndAirborneSession();
                    }
                }
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            // Rule 1: Whenever a ball is not picked up / on the floor, it cannot count as a hit.
            if (!isAirborne)
            {
                return;
            }

            // 1. Detect if a ragdoll was struck
            var faceCtrl = collision.collider.GetComponentInParent<RagdollFaceController>();
            if (faceCtrl == null && collision.rigidbody != null)
            {
                faceCtrl = collision.rigidbody.GetComponentInParent<RagdollFaceController>();
            }
            if (faceCtrl == null && targetRoot != null)
            {
                if (collision.transform.IsChildOf(targetRoot) || collision.transform == targetRoot)
                {
                    faceCtrl = targetRoot.GetComponentInChildren<RagdollFaceController>();
                }
            }
            if (faceCtrl == null)
            {
                var exec = collision.collider.GetComponentInParent<RagdollExecutiveAgent>();
                if (exec != null) faceCtrl = exec.GetComponent<RagdollFaceController>();
            }

            if (faceCtrl != null)
            {
                // Self-hit immunity: Thrower cannot be struck by their own ball upon release
                if (currentThrower != null && faceCtrl == currentThrower)
                {
                    return;
                }

                // Rule 4: If it hits the same ragdoll again during the same airborne session, it should NOT count again
                if (_hitRagdollsInSession.Contains(faceCtrl))
                {
                    Debug.Log($"[ObstacleProjectile] '{gameObject.name}' struck '{faceCtrl.gameObject.name}' again in the same airborne flight. Duplicate hit ignored.");
                    return;
                }

                // Rule 5: Keep track of each ragdoll hit in its current airborne session
                _hitRagdollsInSession.Add(faceCtrl);
                _lastHitTime = Time.time;
                _lastHitFaceCtrl = faceCtrl;

                Rigidbody hitRb = collision.rigidbody != null ? collision.rigidbody : collision.collider.attachedRigidbody;
                if (hitRb != null && impactImpulse != Vector3.zero)
                {
                    hitRb.AddForce(impactImpulse, ForceMode.Impulse);
                }

                Debug.Log($"[ObstacleProjectile] Physical impact on '{collision.collider.gameObject.name}' ({faceCtrl.gameObject.name})! AIRBORNE HIT registered (Unique ragdolls hit in flight: {_hitRagdollsInSession.Count}).");
                faceCtrl.NotifyDodgeballHit(collision.collider);
                OnHitRegistered?.Invoke(faceCtrl, collision);
                return;
            }

            // 2. Not a ragdoll: Check if the projectile contacted the floor / ground surface
            // Contact with the court floor concludes the airborne session and clears session tracking.
            if (IsGroundOrFloor(collision))
            {
                EndAirborneSession();
            }
        }

        private bool IsGroundOrFloor(Collision collision)
        {
            if (collision.collider.CompareTag("Ground")) return true;
            string n = collision.gameObject.name.ToLowerInvariant();
            if (n.Contains("floor") || n.Contains("ground") || n.Contains("court_floor")) return true;

            // Check collision contacts: upward pointing normal indicates horizontal court floor
            for (int i = 0; i < collision.contactCount; i++)
            {
                if (collision.GetContact(i).normal.y > 0.55f)
                {
                    return true;
                }
            }

            return false;
        }

        private void OnCollisionExit(Collision collision)
        {
            if (_lastHitFaceCtrl != null)
            {
                _lastHitFaceCtrl.NotifyDodgeballContactEnded(collision.collider);
            }
        }

        private void OnDisable()
        {
            EndAirborneSession();
        }

        private void OnDestroy()
        {
            if (_lastHitFaceCtrl != null)
            {
                _lastHitFaceCtrl.NotifyDodgeballContactEnded(null);
            }
            EndAirborneSession();
        }
    }
}
