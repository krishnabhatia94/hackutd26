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
        [Header("Dynamic Waypoint Queue")]
        [Tooltip("Dynamic physics-driven pose queue controller")]
        public DynamicPoseQueueManager queueManager;

        private Rigidbody _chestRb;
        private Transform _leftCalfTransform;
        private Transform _rightCalfTransform;
        private Rigidbody[] _allRigidbodies;
        private float _telemetryTimer = 0f;

        private SpacetimeDBNetworkManager _networkManager;

        public bool IsConnected => isConnected;
        public bool IsSubscribed => isSubscribed;

        public void EnsureQueueManager()
        {
            if (queueManager == null)
            {
                queueManager = GetComponent<DynamicPoseQueueManager>();
                if (queueManager == null)
                {
                    queueManager = gameObject.AddComponent<DynamicPoseQueueManager>();
                }
            }
            queueManager.OnTrajectoryFinished -= OnDynamicTrajectoryFinished;
            queueManager.OnTrajectoryFinished += OnDynamicTrajectoryFinished;
            queueManager.OnObstacleEncountered -= OnDynamicObstacleEncountered;
            queueManager.OnObstacleEncountered += OnDynamicObstacleEncountered;
        }

        private void Awake()
        {
            _networkManager = FindObjectOfType<SpacetimeDBNetworkManager>();

            if (poseGoalController == null)
            {
                poseGoalController = GetComponent<RagdollPoseGoalController>();
            }

            EnsureQueueManager();

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
            if (_conn != null)
            {
                if (_networkManager != null)
                {
                    _networkManager.RemoveConnection(_conn);
                }
                try { _conn.Disconnect(); } catch {}
                _conn = null;
            }

            isConnected = false;
            isSubscribed = false;

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

                if (_networkManager != null)
                {
                    _networkManager.AddConnection(_conn);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SpacetimeRigController] Failed to build DbConnection: {ex.Message}");
                TriggerAutoReconnect();
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

            // Check if this step contains a dynamic trajectory queue to execute
            EnsureQueueManager();
            if (queueManager != null && !string.IsNullOrEmpty(step.CurrentSubtask))
            {
                if (step.Status == "Queued" || step.CurrentSubtask.Contains("bone_targets") || step.CurrentSubtask.Contains("keyframes"))
                {
                    queueManager.EnqueueTrajectoryFromJson(step.Goal, step.CurrentSubtask, step.StepId);
                }
                else if (step.Status == "Ready" || step.Status == "Reset")
                {
                    queueManager.StopAndResetToTPose();
                }
            }
        }

        private void OnDynamicTrajectoryFinished(string actionName, bool success, string message)
        {
            Debug.Log($"[SpacetimeRigController] Dynamic Trajectory '{actionName}' successfully completed in Unity! Committing holding pose to SpacetimeDB.");
            if (_conn != null && isConnected)
            {
                try
                {
                    _conn.Reducers.SetWorkflowStep(latestStepId, actionName, "Dynamic Trajectory Completed", "Completed");

                    // Commit final holding waypoint targets to SpacetimeDB BoneTarget table
                    if (queueManager != null && queueManager.holdingPose != null)
                    {
                        foreach (var kvp in queueManager.holdingPose.boneTargets)
                        {
                            var bd = kvp.Value;
                            float holdKp = Mathf.Max(bd.kp, 25000f);
                            float holdKd = Mathf.Max(bd.kd, 2000f);
                            _conn.Reducers.SetPoseKeyframe(bd.boneName, bd.pitch, bd.yaw, bd.roll, holdKp, holdKd, latestStepId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SpacetimeRigController] Could not commit completed status to SpacetimeDB: {ex.Message}");
                }
            }
        }

        private void OnDynamicObstacleEncountered(string actionName, string boneName, float errorDeg)
        {
            Debug.LogWarning($"[SpacetimeRigController] Obstacle encountered on {boneName} ({errorDeg:F1}°)! Alerting SpacetimeDB.");
            if (_conn != null && isConnected)
            {
                try
                {
                    _conn.Reducers.SetWorkflowStep(latestStepId, actionName, $"Blocked by Obstacle on {boneName}", "ObstacleBlocked");
                }
                catch {}
            }
        }

        private void OnConnectError(Exception err)
        {
            isConnected = false;
            isSubscribed = false;
            _conn = null;
            Debug.LogError($"[SpacetimeRigController] Connection error: {err.Message}");
            TriggerAutoReconnect();
        }

        private void OnDisconnected(DbConnection conn, Exception err)
        {
            isConnected = false;
            isSubscribed = false;
            _conn = null;
            Debug.LogWarning($"[SpacetimeRigController] Disconnected from SpacetimeDB: {err?.Message}");
            TriggerAutoReconnect();
        }

        private void TriggerAutoReconnect()
        {
            if (isActiveAndEnabled && gameObject.activeInHierarchy)
            {
                StopCoroutine(nameof(ReconnectCoroutine));
                StartCoroutine(nameof(ReconnectCoroutine));
            }
        }

        private IEnumerator ReconnectCoroutine()
        {
            yield return new WaitForSeconds(2.0f);
            if (!isConnected && isActiveAndEnabled)
            {
                Debug.Log("[SpacetimeRigController] Auto-reconnecting to SpacetimeDB...");
                ConnectToSpacetimeDB();
            }
        }

        [ContextMenu("Reset Humanoid to T-Pose")]
        public void ResetToTPose()
        {
            if (poseGoalController != null)
            {
                poseGoalController.ApplyStandingTPose();
            }
            if (queueManager != null)
            {
                queueManager.StopAndResetToTPose();
            }
            if (_conn != null && isConnected)
            {
                foreach (var kvp in _boneMap)
                {
                    string bname = kvp.Key;
                    float kp = (bname == "Pelvis" || bname == "Spine" || bname == "Chest") ? 25000f : 20000f;
                    float kd = (bname == "Pelvis" || bname == "Spine" || bname == "Chest") ? 1500f : 1000f;
                    try
                    {
                        _conn.Reducers.SetPoseKeyframe(bname, 0f, 0f, 0f, kp, kd, 0);
                    }
                    catch {}
                }
                try
                {
                    _conn.Reducers.SetWorkflowStep(0, "Upright Standing T-Pose (Default)", "Neutral Balance", "Ready");
                }
                catch {}
            }
            Debug.Log("[SpacetimeRigController] Humanoid rig reset to neutral Upright Standing T-Pose.");
        }

        private void OnSubscriptionApplied(SubscriptionEventContext ctx)
        {
            isSubscribed = true;
            Debug.Log("[SpacetimeRigController] Initial subscription applied. Ensuring humanoid starts in clean T-Pose...");
            ResetToTPose();
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
                Debug.Log($"[SpacetimeRigController] Target applied: {target.BoneName} -> pitch={target.Pitch:F1}, yaw={target.Yaw:F1}, roll={target.Roll:F1} (KF {target.KeyframeId})");
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
