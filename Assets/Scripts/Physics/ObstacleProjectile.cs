using System;
using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Handles physical collision and impact impulse delivery for projectiles thrown at the ragdoll.
    /// Guarantees that force is transferred ONLY upon physical contact with ragdoll bones,
    /// eliminating premature movement before the projectile arrives.
    /// Also signals RagdollFaceController to trigger Sad Face upon physical impact.
    /// Supports multi-ragdoll dodgeball matches with repeat throw debounce.
    /// </summary>
    [AddComponentMenu("Physics/Obstacle Projectile")]
    public class ObstacleProjectile : MonoBehaviour
    {
        [Tooltip("Impulse vector to transfer on physical contact.")]
        public Vector3 impactImpulse;

        [Tooltip("Root transform of the ragdoll to verify hit target (optional).")]
        public Transform targetRoot;

        [Tooltip("Minimum time between registered impacts to allow repeat throws.")]
        public float hitDebounceTime = 0.4f;

        public event Action<RagdollFaceController, Collision> OnHitRegistered;

        private float _lastHitTime = -999f;
        private RagdollFaceController _lastHitFaceCtrl;

        public void ResetImpact()
        {
            _lastHitTime = -999f;
            _lastHitFaceCtrl = null;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (Time.time - _lastHitTime < hitDebounceTime) return;

            Rigidbody hitRb = collision.rigidbody;
            if (hitRb != null)
            {
                // Detect which ragdoll was struck
                var faceCtrl = hitRb.GetComponentInParent<RagdollFaceController>();
                if (faceCtrl == null && targetRoot != null)
                {
                    if (hitRb.transform.IsChildOf(targetRoot) || hitRb.transform == targetRoot)
                    {
                        faceCtrl = targetRoot.GetComponentInChildren<RagdollFaceController>();
                    }
                }

                if (faceCtrl != null)
                {
                    _lastHitTime = Time.time;
                    _lastHitFaceCtrl = faceCtrl;

                    if (impactImpulse != Vector3.zero)
                    {
                        hitRb.AddForce(impactImpulse, ForceMode.Impulse);
                    }

                    Debug.Log($"[ObstacleProjectile] Physical impact on '{hitRb.gameObject.name}' ({faceCtrl.gameObject.name})! Sad Face triggered.");
                    faceCtrl.NotifyDodgeballHit(collision.collider);
                    OnHitRegistered?.Invoke(faceCtrl, collision);
                }
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (_lastHitFaceCtrl != null)
            {
                _lastHitFaceCtrl.NotifyDodgeballContactEnded(collision.collider);
            }
        }

        private void OnDestroy()
        {
            if (_lastHitFaceCtrl != null)
            {
                _lastHitFaceCtrl.NotifyDodgeballContactEnded(null);
            }
        }
    }
}
