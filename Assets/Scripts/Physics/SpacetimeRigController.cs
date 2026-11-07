using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Physics;

namespace Physics
{
    /// <summary>
    /// Connects the NPC humanoid rig to SpacetimeDB.
    /// Reactively applies BoneTarget updates to matching BoneTorqueInterpreter components,
    /// and periodically calculates and reports pelvis/body balance telemetry back to BodyTelemetry table.
    /// </summary>
    [AddComponentMenu("Physics/Spacetime Rig Controller")]
    public class SpacetimeRigController : MonoBehaviour
    {
        [Header("SpacetimeDB Configuration")]
        [Tooltip("SpacetimeDB Server URL (maincloud or local host)")]
        public string serverUrl = "https://maincloud.spacetimedb.com";

        [Tooltip("Database name or identity")]
        public string databaseName = "knet";

        [Tooltip("NPC identifier reported in BodyTelemetry table")]
        public uint npcId = 1;

        [Header("Telemetry Settings")]
        [Tooltip("Interval in seconds between telemetry sync reducer calls")]
        public float telemetryInterval = 0.1f; // 10 Hz

        [Tooltip("Floor layer mask for contact detection")]
        public LayerMask groundLayerMask = ~0;

        [Header("Goal Pose Controller")]
        public RagdollPoseGoalController poseGoalController;

        [Header("Live Connection Status (Read-Only)")]
        [SerializeField] private bool isConnected = false;
        [SerializeField] private bool isSubscribed = false;
        [SerializeField] private float lastTelemetrySent = 0f;

        [Header("Live Telemetry Telemetry (Read-Only)")]
        [SerializeField] private float currentComHeight;
        [SerializeField] private float currentTiltAngle;
        [SerializeField] private bool currentIsFallen;
        [SerializeField] private string currentActiveContact = "None";

        [Header("Live Workflow (Read-Only)")]
        [SerializeField] private uint latestStepId = 0;
        [SerializeField] private string latestGoal = "Stand by / Awaiting Nemotron";
        [SerializeField] private string latestSubtask = "Idle";
        [SerializeField] private string latestStatus = "Ready";

        public uint LatestStepId => latestStepId;
        public string LatestGoal => latestGoal;
        public string LatestSubtask => latestSubtask;
        public string LatestStatus => latestStatus;
        public float CurrentComHeight => currentComHeight;
        public float CurrentTiltAngle => currentTiltAngle;
        public bool CurrentIsFallen => currentIsFallen;
        public string CurrentActiveContact => currentActiveContact;

        public event Action<AgentWorkflowStep> OnWorkflowUpdated;

        private DbConnection _conn;
        private Dictionary<string, BoneTorqueInterpreter> _boneMap = new();
        private Transform _pelvisTransform;
        private Rigidbody _chestRb;
        private Transform _leftCalfTransform;
        private Transform _rightCalfTransform;
        private Rigidbody[] _allRigidbodies;
        private float _telemetryTimer = 0f;

        private SpacetimeDBNetworkManager _networkManager;

        public bool IsConnected => isConnected;
        public bool IsSubscribed => isSubscribed;

        private void Awake()
        {
            _networkManager = FindObjectOfType<SpacetimeDBNetworkManager>();

            if (poseGoalController == null)
            {
                poseGoalController = GetComponent<RagdollPoseGoalController>();
            }

            // Index all bone torque controllers on this rig by boneName
            _boneMap.Clear();
            var interpreters = GetComponentsInChildren<BoneTorqueInterpreter>(true);
            foreach (var bti in interpreters)
            {
                string key = string.IsNullOrEmpty(bti.boneName) ? bti.gameObject.name : bti.boneName;
                _boneMap[key] = bti;
            }

            _allRigidbodies = GetComponentsInChildren<Rigidbody>(true);

            // Locate key anatomical balance references
            Transform[] allTransforms = GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                if (t.name == "Pelvis") _pelvisTransform = t;
                else if (t.name == "Chest") _chestRb = t.GetComponent<Rigidbody>();
                else if (t.name == "LeftCalf") _leftCalfTransform = t;
                else if (t.name == "RightCalf") _rightCalfTransform = t;
            }

            if (_pelvisTransform == null)
            {
                _pelvisTransform = transform;
            }
        }

        private void Start()
        {
            ConnectToSpacetimeDB();
        }

        public void ConnectToSpacetimeDB()
        {
            if (_conn != null && _conn.IsActive)
            {
                Debug.LogWarning("[SpacetimeRigController] Connection already active.");
                return;
            }

            Debug.Log($"[SpacetimeRigController] Connecting to SpacetimeDB '{databaseName}' at {serverUrl}...");

            try
            {
                _conn = DbConnection.Builder()
                    .WithUri(serverUrl)
                    .WithDatabaseName(databaseName)
                    .OnConnect(OnConnected)
                    .OnConnectError(OnConnectError)
                    .OnDisconnect(OnDisconnected)
                    .Build();

                // Register with network manager if present
                if (_networkManager != null)
                {
                    _networkManager.AddConnection(_conn);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SpacetimeRigController] Failed to build DbConnection: {ex.Message}");
            }
        }

        private void OnConnected(DbConnection conn, Identity identity, string authToken)
        {
            isConnected = true;
            Debug.Log($"[SpacetimeRigController] Connected successfully! Identity: {identity}");

            // Subscribe to all tables
            conn.SubscriptionBuilder()
                .OnApplied(OnSubscriptionApplied)
                .OnError((ctx, err) => Debug.LogError($"[SpacetimeRigController] Subscription error: {err}"))
                .SubscribeToAllTables();

            // Reactive sync for BoneTarget and AgentWorkflowStep updates
            conn.Db.BoneTarget.OnInsert += OnBoneTargetInserted;
            conn.Db.BoneTarget.OnUpdate += OnBoneTargetUpdated;
            conn.Db.AgentWorkflowStep.OnInsert += (c, row) => UpdateWorkflowStep(row);
            conn.Db.AgentWorkflowStep.OnUpdate += (c, oldRow, newRow) => UpdateWorkflowStep(newRow);

            // Shove NPC reducer broadcast listener
            conn.Reducers.OnShoveNpc += OnShoveNpcReceived;
        }

        private void OnShoveNpcReceived(ReducerEventContext ctx, uint targetNpcId, float fx, float fy, float fz)
        {
            if (targetNpcId == npcId)
            {
                ApplyChestImpulse(new Vector3(fx, fy, fz));
            }
        }

        private void UpdateWorkflowStep(AgentWorkflowStep step)
        {
            latestStepId = step.StepId;
            latestGoal = step.Goal;
            latestSubtask = step.CurrentSubtask;
            latestStatus = step.Status;

            if (poseGoalController != null)
            {
                poseGoalController.SetGoalPoseInfo(step.Goal, step.StepId);
            }

            OnWorkflowUpdated?.Invoke(step);
            Debug.Log($"[SpacetimeRigController] Workflow Step {step.StepId}: {step.Goal} | {step.CurrentSubtask} [{step.Status}]");
        }

        private void OnConnectError(Exception err)
        {
            isConnected = false;
            Debug.LogError($"[SpacetimeRigController] Connection error: {err.Message}");
        }

        private void OnDisconnected(DbConnection conn, Exception err)
        {
            isConnected = false;
            isSubscribed = false;
            Debug.LogWarning($"[SpacetimeRigController] Disconnected from SpacetimeDB: {err?.Message}");
        }

        private void OnSubscriptionApplied(SubscriptionEventContext ctx)
        {
            isSubscribed = true;
            Debug.Log("[SpacetimeRigController] Initial subscription snapshot applied. Replaying tables...");

            // Apply all existing bone targets in database cache
            foreach (var target in _conn.Db.BoneTarget.Iter())
            {
                ApplyBoneTarget(target);
            }

            foreach (var step in _conn.Db.AgentWorkflowStep.Iter())
            {
                UpdateWorkflowStep(step);
            }
        }

        private void OnBoneTargetInserted(EventContext ctx, BoneTarget row)
        {
            ApplyBoneTarget(row);
        }

        private void OnBoneTargetUpdated(EventContext ctx, BoneTarget oldRow, BoneTarget newRow)
        {
            ApplyBoneTarget(newRow);
        }

        private void ApplyBoneTarget(BoneTarget target)
        {
            if (string.IsNullOrEmpty(target.BoneName)) return;

            if (_boneMap.TryGetValue(target.BoneName, out var interpreter))
            {
                interpreter.SetTargetAndGains(target.Pitch, target.Yaw, target.Roll, target.Kp, target.Kd);
            }

            if (poseGoalController != null)
            {
                poseGoalController.UpdateGoalBoneTarget(target.BoneName, target.Pitch, target.Yaw, target.Roll, target.Kp, target.Kd, target.KeyframeId);
            }
        }

        private void Update()
        {
            // Pump connection frame tick if network manager is not doing it
            if (_conn != null && _networkManager == null)
            {
                _conn.FrameTick();
            }

            // Periodic telemetry reporting
            if (isConnected && isSubscribed && _conn != null)
            {
                _telemetryTimer += Time.deltaTime;
                if (_telemetryTimer >= telemetryInterval)
                {
                    _telemetryTimer = 0f;
                    CalculateAndSendTelemetry();
                }
            }
        }

        private void CalculateAndSendTelemetry()
        {
            // 1. Center of Mass height
            float comHeight = 0f;
            float totalMass = 0f;
            Vector3 weightedCom = Vector3.zero;

            if (_allRigidbodies != null && _allRigidbodies.Length > 0)
            {
                foreach (var rb in _allRigidbodies)
                {
                    if (rb == null) continue;
                    weightedCom += rb.worldCenterOfMass * rb.mass;
                    totalMass += rb.mass;
                }
            }

            if (totalMass > 0f)
            {
                comHeight = weightedCom.y / totalMass;
            }
            else if (_pelvisTransform != null)
            {
                comHeight = _pelvisTransform.position.y;
            }

            // 2. Tilt Angle relative to world upright (Vector3.up)
            float tiltAngle = 0f;
            if (_pelvisTransform != null)
            {
                tiltAngle = Vector3.Angle(_pelvisTransform.up, Vector3.up);
            }

            // 3. Is Fallen check (excessive tilt or COM dropping significantly)
            bool isFallen = (tiltAngle > 60f) || (comHeight < 0.45f);

            // 4. Active Contact detection
            bool pelvisContact = false;
            bool leftCalfContact = false;
            bool rightCalfContact = false;

            if (_pelvisTransform != null)
            {
                pelvisContact = UnityEngine.Physics.Raycast(_pelvisTransform.position, Vector3.down, 0.25f, groundLayerMask);
            }
            if (_leftCalfTransform != null)
            {
                leftCalfContact = UnityEngine.Physics.Raycast(_leftCalfTransform.position, Vector3.down, 0.45f, groundLayerMask);
            }
            if (_rightCalfTransform != null)
            {
                rightCalfContact = UnityEngine.Physics.Raycast(_rightCalfTransform.position, Vector3.down, 0.45f, groundLayerMask);
            }

            string activeContact;
            if (pelvisContact)
            {
                activeContact = "Pelvis/Floor";
            }
            else if (leftCalfContact && rightCalfContact)
            {
                activeContact = "BothFeet";
            }
            else if (leftCalfContact)
            {
                activeContact = "LeftFoot";
            }
            else if (rightCalfContact)
            {
                activeContact = "RightFoot";
            }
            else
            {
                activeContact = "Airborne";
            }

            // Store telemetry for Inspector debug
            currentComHeight = comHeight;
            currentTiltAngle = tiltAngle;
            currentIsFallen = isFallen;
            currentActiveContact = activeContact;
            lastTelemetrySent = Time.time;

            // Transmit via UpdateTelemetry reducer
            try
            {
                _conn.Reducers.UpdateTelemetry(npcId, comHeight, tiltAngle, isFallen, activeContact);

                // Transmit all bones' live 3D positions, rotations, and angular velocities for Nemotron
                if (poseGoalController != null)
                {
                    foreach (var b in poseGoalController.GetAllBoneLiveStates())
                    {
                        _conn.Reducers.UpdateBoneLiveState(
                            b.boneName,
                            b.worldPosition.x, b.worldPosition.y, b.worldPosition.z,
                            b.localEuler.x, b.localEuler.y, b.localEuler.z,
                            b.angularVelocity.x, b.angularVelocity.y, b.angularVelocity.z
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SpacetimeRigController] Failed to send telemetry: {ex.Message}");
            }
        }

        /// <summary>
        /// Invokes SpacetimeDB ShoveNpc reducer to apply an external impulse across all connected clients.
        /// </summary>
        public void TriggerShove(float forceX = 180f, float forceY = 30f, float forceZ = 75f)
        {
            if (_conn != null && _conn.IsActive)
            {
                Debug.Log($"[SpacetimeRigController] Invoking ShoveNpc reducer ({forceX}, {forceY}, {forceZ}) via SpacetimeDB...");
                try
                {
                    _conn.Reducers.ShoveNpc(npcId, forceX, forceY, forceZ);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SpacetimeRigController] Error calling ShoveNpc reducer: {ex.Message}");
                    ApplyChestImpulse(new Vector3(forceX, forceY, forceZ));
                }
            }
            else
            {
                ApplyChestImpulse(new Vector3(forceX, forceY, forceZ));
            }
        }

        public void ApplyChestImpulse(Vector3 impulse)
        {
            if (_chestRb != null && !_chestRb.isKinematic)
            {
                _chestRb.AddForce(impulse, ForceMode.Impulse);
                Debug.Log($"[SpacetimeRigController] Applied shove impulse {impulse} to Chest!");
            }
            else if (_pelvisTransform != null && _pelvisTransform.TryGetComponent<Rigidbody>(out var pelvisRb) && !pelvisRb.isKinematic)
            {
                pelvisRb.AddForce(impulse, ForceMode.Impulse);
            }

            SpawnFlyingObstacle(impulse);
        }

        private void SpawnFlyingObstacle(Vector3 impulse)
        {
            Vector3 targetPos = _chestRb != null ? _chestRb.position : (_pelvisTransform != null ? _pelvisTransform.position : Vector3.up * 1.5f);
            Vector3 spawnPos = targetPos - impulse.normalized * 2.5f + Vector3.up * 0.2f;

            GameObject obstacle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            obstacle.name = "Obstacle_Projectile";
            obstacle.transform.position = spawnPos;
            obstacle.transform.localScale = Vector3.one * 0.35f;

            var rend = obstacle.GetComponent<Renderer>();
            if (rend != null)
            {
                rend.material.color = new Color(0.95f, 0.25f, 0.2f);
            }

            var rb = obstacle.AddComponent<Rigidbody>();
            rb.mass = 5f;
            rb.velocity = impulse.normalized * 10f;

            Destroy(obstacle, 4f);
        }

        private void OnDestroy()
        {
            if (_conn != null)
            {
                if (_networkManager != null)
                {
                    _networkManager.RemoveConnection(_conn);
                }
                _conn.Disconnect();
                _conn = null;
            }
        }
    }
}
