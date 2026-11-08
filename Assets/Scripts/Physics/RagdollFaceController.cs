// Biomechanical Facial Expression Controller v1.1.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Physics
{
    public enum FaceEmotionState
    {
        Idle,
        Happy,
        Sad
    }

    /// <summary>
    /// Programmatic facial expression controller for the active humanoid ragdoll.
    /// Manages real-time transitions between Idle, Happy, and Sad facial sprites
    /// based on physical motion state, dynamic trajectory execution, and dodgeball collisions.
    /// Operates purely programmatically without Mecanim Animator conditions.
    /// </summary>
    [AddComponentMenu("Physics/Ragdoll Face Controller")]
    public class RagdollFaceController : MonoBehaviour
    {
        [Header("Target Face Visual")]
        [Tooltip("SpriteRenderer displaying the face sprite on the humanoid head.")]
        public SpriteRenderer faceRenderer;

        [Header("Face Sprites")]
        [Tooltip("Default expression displayed at rest (idleface.png).")]
        public Sprite idleSprite;

        [Tooltip("Expression displayed whenever the ragdoll is moving or executing trajectories (happyface.png).")]
        public Sprite happySprite;

        [Tooltip("Expression displayed when struck by a dodgeball / projectile (sadface.png).")]
        public Sprite sadSprite;

        [Header("Transition Timing")]
        [Tooltip("Time in seconds to wait after a trigger condition ceases before resetting back to Idle face.")]
        public float resetDelay = 1.8f;

        [Header("Motion Detection Tuning")]
        [Tooltip("Angular velocity threshold (rad/s) across bones to qualify as active movement.")]
        public float angularVelocityThreshold = 1.0f;

        [Tooltip("Linear velocity threshold (m/s) across bones to qualify as active movement.")]
        public float linearVelocityThreshold = 0.35f;

        [Header("Live State Telemetry (Read-Only)")]
        [SerializeField] private FaceEmotionState currentEmotion = FaceEmotionState.Idle;
        [SerializeField] private bool isMoving = false;
        [SerializeField] private bool isHitActive = false;
        [SerializeField] private float timeSinceSadConditionEnded = 0f;
        [SerializeField] private float timeSinceHappyConditionEnded = 0f;

        public FaceEmotionState CurrentEmotion => currentEmotion;
        public bool IsMoving => isMoving;
        public bool IsHitActive => isHitActive;

        private DynamicPoseQueueManager _queueManager;
        private Rigidbody[] _ragdollRigidbodies;
        private readonly HashSet<Collider> _activeHitColliders = new();

        // Timing variables
        private float _sadUntilTime = -999f;
        private float _happyUntilTime = -999f;
        private float _lastContactTime = -999f;

        private void Awake()
        {
            // Self-heal any stale thresholds from old scene serializations
            if (angularVelocityThreshold < 0.5f) angularVelocityThreshold = 1.0f;
            if (linearVelocityThreshold < 0.2f) linearVelocityThreshold = 0.35f;

            InitializeReferences();
        }

        private void Start()
        {
            InitializeReferences();
            ApplyEmotion(FaceEmotionState.Idle, true);
        }

        public void InitializeReferences()
        {
            if (_queueManager == null)
            {
                _queueManager = GetComponent<DynamicPoseQueueManager>();
                if (_queueManager == null)
                {
                    _queueManager = GetComponentInParent<DynamicPoseQueueManager>();
                }
            }

            if (_ragdollRigidbodies == null || _ragdollRigidbodies.Length == 0)
            {
                _ragdollRigidbodies = GetComponentsInChildren<Rigidbody>(true);
            }

            FindFaceRenderer();
            LoadSpritesIfMissing();
        }

        public void FindFaceRenderer()
        {
            if (faceRenderer != null) return;

            // 1. Check under Head
            Transform head = transform.Find("Pelvis/Spine/Chest/Head");
            if (head != null)
            {
                Transform visual = head.Find("Visual");
                if (visual != null)
                {
                    Transform fv = visual.Find("face_visual");
                    if (fv != null)
                    {
                        faceRenderer = fv.GetComponent<SpriteRenderer>();
                        if (faceRenderer != null) return;
                    }
                }
            }

            // 2. Search children
            var srs = GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs)
            {
                if (sr.gameObject.name.Contains("face") || sr.gameObject.name.Contains("Face"))
                {
                    faceRenderer = sr;
                    return;
                }
            }

            // 3. Fallback to scene search
            var globalFv = GameObject.Find("face_visual");
            if (globalFv != null)
            {
                faceRenderer = globalFv.GetComponent<SpriteRenderer>();
            }
        }

        public void LoadSpritesIfMissing()
        {
#if UNITY_EDITOR
            if (idleSprite == null)
            {
                idleSprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/idleface.png");
            }
            if (happySprite == null)
            {
                happySprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/happyface.png");
            }
            if (sadSprite == null)
            {
                sadSprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/sadface.png");
            }
#endif
        }

        private void OnValidate()
        {
            LoadSpritesIfMissing();
            FindFaceRenderer();
        }

        /// <summary>
        /// Called when an obstacle/dodgeball projectile impacts the ragdoll.
        /// </summary>
        public void NotifyDodgeballHit(Collider col = null)
        {
            if (col != null)
            {
                _activeHitColliders.Add(col);
            }
            isHitActive = true;
            _lastContactTime = Time.time;
            _sadUntilTime = Time.time + resetDelay;

            ApplyEmotion(FaceEmotionState.Sad);
            Debug.Log($"[RagdollFaceController] Projectile / Dodgeball impact registered -> Sad Face triggered (Active until {Time.time + resetDelay:F2}s).");
        }

        /// <summary>
        /// Called when physical contact with the dodgeball projectile ends.
        /// </summary>
        public void NotifyDodgeballContactEnded(Collider col = null)
        {
            if (col != null)
            {
                _activeHitColliders.Remove(col);
            }
            else
            {
                _activeHitColliders.Clear();
            }

            if (_activeHitColliders.Count == 0 && isHitActive)
            {
                isHitActive = false;
                _sadUntilTime = Time.time + resetDelay;
                Debug.Log($"[RagdollFaceController] Dodgeball contact ceased at {Time.time:F2}s. 1.8s reset timer started (will reset to Idle at {_sadUntilTime:F2}s).");
            }
        }

        /// <summary>
        /// Evaluates whether agent.py is currently commanding dynamic movement.
        /// Ignores general physical movement / balance jitter, triggering Happy face ONLY when actively commanded.
        /// </summary>
        private bool CheckIsAgentCommandingMove()
        {
            return _queueManager != null && _queueManager.isExecuting;
        }

        public bool IsSadConditionActive()
        {
            return isHitActive || (Time.time < _sadUntilTime);
        }

        public bool IsHappyConditionActive()
        {
            return isMoving || (Time.time < _happyUntilTime);
        }

        private void Update()
        {
            // Safety cleanup for active hit colliders
            _activeHitColliders.RemoveWhere(c => c == null || !c.gameObject.activeInHierarchy);
            if (isHitActive && _activeHitColliders.Count == 0 && (Time.time - _lastContactTime) > 0.15f)
            {
                isHitActive = false;
                _sadUntilTime = Time.time + resetDelay;
                Debug.Log($"[RagdollFaceController] Dodgeball contact window concluded. 1.8s reset timer running until {_sadUntilTime:F2}s.");
            }

            // Evaluate agent.py commanded movement state ONLY (ignores ambient physics movement)
            bool agentCommanding = CheckIsAgentCommandingMove();
            if (agentCommanding)
            {
                isMoving = true;
                _happyUntilTime = Time.time + resetDelay;
            }
            else
            {
                if (isMoving)
                {
                    isMoving = false;
                    _happyUntilTime = Time.time + resetDelay;
                }
            }

            // Update telemetry
            timeSinceSadConditionEnded = isHitActive ? 0f : Mathf.Max(0f, resetDelay - (_sadUntilTime - Time.time));
            timeSinceHappyConditionEnded = isMoving ? 0f : Mathf.Max(0f, resetDelay - (_happyUntilTime - Time.time));

            // State Machine Resolution with STRICT Priority:
            // Priority 1: Struck by dodgeball -> Sad Face (plays during hit and 1.8s cooldown after contact ends)
            // Priority 2: Commanded by agent.py -> Happy Face (plays while agent is moving it and 1.8s cooldown after commands stop)
            // Priority 3: Default -> Idle Face
            FaceEmotionState targetEmotion;

            if (IsSadConditionActive())
            {
                targetEmotion = FaceEmotionState.Sad;
            }
            else if (IsHappyConditionActive())
            {
                targetEmotion = FaceEmotionState.Happy;
            }
            else
            {
                targetEmotion = FaceEmotionState.Idle;
            }

            ApplyEmotion(targetEmotion);
        }

        public void ApplyEmotion(FaceEmotionState emotion, bool force = false)
        {
            if (!force && currentEmotion == emotion && faceRenderer != null && faceRenderer.sprite != null)
            {
                return;
            }

            currentEmotion = emotion;

            if (faceRenderer == null)
            {
                FindFaceRenderer();
                if (faceRenderer == null) return;
            }

            Sprite targetSprite = null;
            switch (emotion)
            {
                case FaceEmotionState.Idle:
                    targetSprite = idleSprite;
                    break;
                case FaceEmotionState.Happy:
                    targetSprite = happySprite;
                    break;
                case FaceEmotionState.Sad:
                    targetSprite = sadSprite;
                    break;
            }

            if (targetSprite != null)
            {
                faceRenderer.sprite = targetSprite;
            }
        }
    }
}
