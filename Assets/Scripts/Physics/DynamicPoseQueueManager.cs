using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Physics
{
    [System.Serializable]
    public class BoneWaypointData
    {
        public string boneName;
        public float pitch;
        public float yaw;
        public float roll;
        public float kp = 22000f;
        public float kd = 1200f;
    }

    [System.Serializable]
    public class TrajectoryWaypoint
    {
        public string stepName = "Pose";
        public float targetDuration = 0.35f;
        public float maxWatchdogTimeout = 0.75f;
        public Dictionary<string, BoneWaypointData> boneTargets = new();
        public Vector3 rootDisplacement = Vector3.zero;
        public bool hasRootDisplacement = false;
    }

    /// <summary>
    /// Client-side dynamic waypoint queue manager for active humanoid ragdoll.
    /// Evaluates real-time PhysX angular convergence (Zone / Fly-by transition),
    /// physical stall/collision detection, and watchdog timeout in FixedUpdate.
    /// Eliminates static sleep timers and enables natural physics-reactive pacing.
    /// </summary>
    [AddComponentMenu("Physics/Dynamic Pose Queue Manager")]
    public class DynamicPoseQueueManager : MonoBehaviour
    {
        [Header("Dynamic Waypoint Transition Tuning")]
        [Tooltip("Max angular error (degrees) across moving bones to trigger fly-through to next waypoint.")]
        [Range(3f, 15f)]
        public float waypointToleranceAngle = 6.0f;

        [Tooltip("Minimum time (seconds) to remain in a waypoint before allowing convergence (prevents instant skipping of subtle poses).")]
        [Range(0.10f, 0.4f)]
        public float minWaypointTime = 0.18f;

        [Tooltip("Fallback watchdog timeout multiplier if none specified per waypoint.")]
        public float defaultWatchdogTimeout = 0.85f;

        [Header("Physical Collision & Stall Detection")]
        [Tooltip("Enable detection of physical obstacles blocking limbs.")]
        public bool detectObstacles = true;

        [Tooltip("Angular speed threshold (rad/s) below which a bone with sustained high error is considered stalled.")]
        public float stallAngularSpeedThreshold = 0.12f;

        [Tooltip("Duration (seconds) of sustained stall required to declare an obstacle (must be long enough to avoid false positives on acceleration).")]
        public float stallDetectionTime = 0.45f;

        [Header("Holding State (Active Post-Trajectory)")]
        [Tooltip("Whether the rig is currently locking and actively holding the final pose.")]
        public bool isHolding = false;
        public TrajectoryWaypoint holdingPose = null;
        public float holdingKp = 25000f;
        public float holdingKd = 2000f;

        [Header("Locomotion Pelvis Drive")]
        [Tooltip("Speed (m/s) at which the pelvis locomotion suspension anchor moves across the floor.")]
        public float locomotionSpeed = 1.25f;
        public Vector3 currentPelvisAnchor => _currentPelvisAnchor;
        public Vector3 targetPelvisAnchor => _targetPelvisAnchor;

        [Header("Live Queue Telemetry (Read-Only)")]
        public bool isExecuting = false;
        public string activeActionName = "";
        public uint activeStepId = 0;
        public int currentWaypointIndex = 0;
        public int totalWaypointsInQueue = 0;
        public string currentWaypointName = "";
        public float currentWaypointMaxAngleError = 0f;
        public float currentWaypointMaxAngularSpeed = 0f;
        public float currentWaypointTimer = 0f;
        public bool isStalledByObstacle = false;
        public string stalledBoneName = "";

        // Events
        public event Action<string, int, int> OnWaypointStarted;
        public event Action<string, float, string> OnWaypointCompleted;
        public event Action<string, bool, string> OnTrajectoryFinished;
        public event Action<string, string, float> OnObstacleEncountered;

        private Queue<TrajectoryWaypoint> _waypointQueue = new();
        private TrajectoryWaypoint _activeWaypoint = null;
        private Dictionary<string, BoneTorqueInterpreter> _boneMap = new();
        private RagdollPoseGoalController _goalController;
        private ObjectPickupController _pickupController;
        private float _stallTimer = 0f;

        private ConfigurableJoint _pelvisJoint;
        private Vector3 _initialPelvisAnchor = Vector3.zero;
        private Vector3 _currentPelvisAnchor = Vector3.zero;
        private Vector3 _targetPelvisAnchor = Vector3.zero;
        private bool _isLocomoting = false;

        private void Awake()
        {
            isExecuting = false;
            isHolding = false;
            holdingPose = null;
            activeActionName = "";
            activeStepId = 0;
            currentWaypointIndex = 0;
            totalWaypointsInQueue = 0;
            currentWaypointName = "";
            currentWaypointTimer = 0f;
            isStalledByObstacle = false;
            stalledBoneName = "";
            _waypointQueue.Clear();
            _activeWaypoint = null;

            _goalController = GetComponent<RagdollPoseGoalController>();
            _pickupController = GetComponent<ObjectPickupController>();
            if (_pickupController == null)
            {
                _pickupController = gameObject.AddComponent<ObjectPickupController>();
            }
            IndexBoneControllers();
        }

        public void IndexBoneControllers()
        {
            _boneMap.Clear();
            var interpreters = GetComponentsInChildren<BoneTorqueInterpreter>(true);
            foreach (var bti in interpreters)
            {
                string key = string.IsNullOrEmpty(bti.boneName) ? bti.gameObject.name : bti.boneName;
                _boneMap[key] = bti;
            }

            if (_pelvisJoint == null)
            {
                Transform pelvisTr = transform.Find("Pelvis");
                if (pelvisTr == null)
                {
                    var rbs = GetComponentsInChildren<Rigidbody>(true);
                    foreach (var rb in rbs)
                    {
                        if (rb.name == "Pelvis") { pelvisTr = rb.transform; break; }
                    }
                }
                if (pelvisTr != null)
                {
                    _pelvisJoint = pelvisTr.GetComponent<ConfigurableJoint>();
                    if (_pelvisJoint != null)
                    {
                        _pelvisJoint.autoConfigureConnectedAnchor = false;
                        if (_initialPelvisAnchor == Vector3.zero)
                        {
                            _initialPelvisAnchor = _pelvisJoint.connectedAnchor;
                            if (_initialPelvisAnchor == Vector3.zero)
                            {
                                _initialPelvisAnchor = pelvisTr.position;
                                _pelvisJoint.connectedAnchor = _initialPelvisAnchor;
                            }
                            _currentPelvisAnchor = _initialPelvisAnchor;
                            _targetPelvisAnchor = _initialPelvisAnchor;
                        }
                    }
                }
            }
        }

        public BoneTorqueInterpreter GetInterpreterForBone(string boneName)
        {
            if (_boneMap.TryGetValue(boneName, out var interp)) return interp;
            if (boneName == "LeftShoulder" && _boneMap.TryGetValue("LeftUpperArm", out var lua)) return lua;
            if (boneName == "RightShoulder" && _boneMap.TryGetValue("RightUpperArm", out var rua)) return rua;
            return null;
        }

        private void FixedUpdate()
        {
            if (_boneMap == null || _boneMap.Count == 0)
            {
                IndexBoneControllers();
            }

            // Smoothly advance the pelvis locomotion suspension anchor
            if (_pelvisJoint != null && (_currentPelvisAnchor != _targetPelvisAnchor || _isLocomoting))
            {
                _currentPelvisAnchor = Vector3.MoveTowards(_currentPelvisAnchor, _targetPelvisAnchor, locomotionSpeed * Time.fixedDeltaTime);
                _pelvisJoint.connectedAnchor = _currentPelvisAnchor;
            }

            // Active Holding Loop: Constantly enforce final setpoint and compute error
            if (isHolding && holdingPose != null)
            {
                float holdMaxErr = 0f;
                float holdMaxSpd = 0f;
                foreach (var kvp in holdingPose.boneTargets)
                {
                    var interp = GetInterpreterForBone(kvp.Key);
                    if (interp != null)
                    {
                        float err = interp.CurrentAngularError;
                        float spd = interp.CurrentAngularSpeed;
                        if (err > holdMaxErr) holdMaxErr = err;
                        if (spd > holdMaxSpd) holdMaxSpd = spd;
                        interp.applyTorque = true;
                    }
                }
                currentWaypointMaxAngleError = holdMaxErr;
                currentWaypointMaxAngularSpeed = holdMaxSpd;
            }

            if (!isExecuting || _activeWaypoint == null) return;

            currentWaypointTimer += Time.fixedDeltaTime;

            // 1. Evaluate maximum angular error and speed across bones active in this waypoint
            float maxError = 0f;
            float maxSpeed = 0f;
            string worstBone = "";

            foreach (var kvp in _activeWaypoint.boneTargets)
            {
                string bname = kvp.Key;
                var interp = GetInterpreterForBone(bname);
                if (interp != null)
                {
                    float err = interp.CurrentAngularError;
                    float spd = interp.CurrentAngularSpeed;
                    if (err > maxError)
                    {
                        maxError = err;
                        worstBone = bname;
                    }
                    if (spd > maxSpeed) maxSpeed = spd;
                }
            }

            currentWaypointMaxAngleError = maxError;
            currentWaypointMaxAngularSpeed = maxSpeed;

            // 2. Check Physical Obstacle / Stall Condition (Only after 0.35s to allow static inertia acceleration)
            if (detectObstacles && currentWaypointTimer > 0.35f && maxError > (waypointToleranceAngle * 2.0f))
            {
                if (maxSpeed < stallAngularSpeedThreshold)
                {
                    _stallTimer += Time.fixedDeltaTime;
                    if (_stallTimer >= stallDetectionTime && !isStalledByObstacle)
                    {
                        isStalledByObstacle = true;
                        stalledBoneName = worstBone;
                        Debug.LogWarning($"[DynamicPoseQueue] Physical OBSTACLE detected on {worstBone}! (Error={maxError:F1}°, Speed={maxSpeed:F2} rad/s)");
                        OnObstacleEncountered?.Invoke(activeActionName, worstBone, maxError);
                    }
                }
                else
                {
                    _stallTimer = Mathf.Max(0f, _stallTimer - Time.fixedDeltaTime);
                }
            }

            // 3. Waypoint Transition Conditions:
            bool isFinalWaypoint = (_waypointQueue.Count == 0);
            bool reachedCondition;

            if (isFinalWaypoint)
            {
                // Final destination pose: Allow settling into resting posture (< 5° error + low speed, or target duration elapsed)
                bool settledInZone = (currentWaypointTimer >= minWaypointTime && maxError <= 5.0f && maxSpeed < 0.35f);
                bool durationElapsed = (currentWaypointTimer >= Mathf.Max(0.35f, _activeWaypoint.targetDuration));
                reachedCondition = settledInZone || durationElapsed;
            }
            else
            {
                // Intermediate waypoints: Traversal to target zone (< 6° error) or target duration elapsed
                bool inZone = (currentWaypointTimer >= minWaypointTime && maxError <= waypointToleranceAngle);
                bool durationElapsed = (currentWaypointTimer >= _activeWaypoint.targetDuration);
                reachedCondition = inZone || durationElapsed;
            }

            bool watchdogTimeout = (currentWaypointTimer >= _activeWaypoint.maxWatchdogTimeout);
            bool obstacleStallTimeout = (isStalledByObstacle && _stallTimer >= (stallDetectionTime * 2.0f));

            if (reachedCondition || watchdogTimeout || obstacleStallTimeout)
            {
                string reason = reachedCondition ? (isFinalWaypoint ? $"Final Pose Settled ({maxError:F1}° err, {maxSpeed:F2} rad/s)" : $"Reached Zone ({maxError:F1}° < {waypointToleranceAngle}°)") :
                               (watchdogTimeout ? $"Watchdog Timeout ({currentWaypointTimer:F2}s)" : $"Obstacle Stalled on {stalledBoneName}");

                Debug.Log($"[DynamicPoseQueue] Waypoint Finished: {_activeWaypoint.stepName} after {currentWaypointTimer:F2}s [{reason}]");
                OnWaypointCompleted?.Invoke(_activeWaypoint.stepName, currentWaypointTimer, reason);

                if (isFinalWaypoint)
                {
                    EnterHoldingState(_activeWaypoint);
                }
                else
                {
                    AdvanceToNextWaypoint();
                }
            }
        }

        private void EnterHoldingState(TrajectoryWaypoint finalWp)
        {
            isExecuting = false;
            isHolding = true;
            holdingPose = finalWp;
            _activeWaypoint = null;
            currentWaypointName = $"Holding Final Pose: {finalWp.stepName}";

            // Firmly lock all final waypoint bone targets with critical damping and anatomical holding stiffness
            _isLocomoting = false;
            foreach (var kvp in finalWp.boneTargets)
            {
                string bname = kvp.Key;
                var bdata = kvp.Value;
                var interp = GetInterpreterForBone(bname);
                if (interp != null)
                {
                    float kp = GetAnatomicalDefaultKp(bname, bdata.kp);
                    float kd = GetAnatomicalDefaultKd(bname, bdata.kd);
                    interp.SetTargetAndGains(bdata.pitch, bdata.yaw, bdata.roll, kp, kd);
                    interp.applyTorque = true;
                }
                if (_goalController != null)
                {
                    float targetKp = GetAnatomicalDefaultKp(bname, bdata.kp);
                    float targetKd = GetAnatomicalDefaultKd(bname, bdata.kd);
                    _goalController.UpdateGoalBoneTarget(bname, bdata.pitch, bdata.yaw, bdata.roll, targetKp, targetKd, activeStepId);
                }
            }

            Debug.Log($"[DynamicPoseQueue] Trajectory '{activeActionName}' fully settled into final pose '{finalWp.stepName}'. Holding state ACTIVE.");
            OnTrajectoryFinished?.Invoke(activeActionName, true, "Trajectory completed and holding final pose.");
        }

        private void AdvanceToNextWaypoint()
        {
            _stallTimer = 0f;
            isStalledByObstacle = false;
            stalledBoneName = "";
            currentWaypointTimer = 0f;

            if (_waypointQueue.Count > 0)
            {
                _activeWaypoint = _waypointQueue.Dequeue();
                currentWaypointIndex++;
                currentWaypointName = _activeWaypoint.stepName;

                // Apply targets to BoneTorqueInterpreters
                foreach (var kvp in _activeWaypoint.boneTargets)
                {
                    string bname = kvp.Key;
                    var bdata = kvp.Value;
                    var interp = GetInterpreterForBone(bname);
                    if (interp != null)
                    {
                        interp.SetTargetAndGains(bdata.pitch, bdata.yaw, bdata.roll, bdata.kp, bdata.kd);
                        interp.applyTorque = true;
                    }
                    if (_goalController != null)
                    {
                        _goalController.UpdateGoalBoneTarget(bname, bdata.pitch, bdata.yaw, bdata.roll, bdata.kp, bdata.kd, activeStepId);
                    }
                }

                Debug.Log($"[DynamicPoseQueue] Advanced to [{currentWaypointIndex}/{totalWaypointsInQueue}]: {_activeWaypoint.stepName}");
                OnWaypointStarted?.Invoke(_activeWaypoint.stepName, currentWaypointIndex, totalWaypointsInQueue);

                // Auto-trigger physical pickup/grasp if this waypoint is grasping or lifting
                string stepLower = _activeWaypoint.stepName.ToLower();
                if (stepLower.Contains("grasp") || stepLower.Contains("pickup") || stepLower.Contains("pick up") || stepLower.Contains("lift") || stepLower.Contains("clasp"))
                {
                    if (_pickupController != null && !_pickupController.isHolding)
                    {
                        _pickupController.TryPickupNearestInteractable();
                    }
                }
                else if (stepLower.Contains("throw") || stepLower.Contains("release") || stepLower.Contains("launch") || stepLower.Contains("toss") || stepLower.Contains("drop") || stepLower.Contains("hurl") || stepLower.Contains("pitch") || stepLower.Contains("fling") || stepLower.Contains("pass"))
                {
                    if (_pickupController != null && _pickupController.isHolding)
                    {
                        Vector3 throwDir;
                        if (_pickupController.opponentTarget != null)
                        {
                            Vector3 targetPos = _pickupController.opponentTarget.position + Vector3.up * 0.2f;
                            Vector3 origin = transform.position + Vector3.up * 1.2f;
                            throwDir = (targetPos - origin).normalized;
                            throwDir = (throwDir + Vector3.up * 0.12f).normalized;
                        }
                        else
                        {
                            Vector3 fwd = transform.forward;
                            throwDir = (fwd + Vector3.up * 0.22f).normalized;
                        }

                        float speed = (stepLower.Contains("drop")) ? 1.0f : ((stepLower.Contains("power") || stepLower.Contains("hurl") || stepLower.Contains("explosive")) ? 13.5f : _pickupController.defaultThrowSpeed);
                        _pickupController.ReleaseHeldObject(throwDir * speed);
                    }
                }

                // Advance locomotion pelvis anchor if root displacement is specified or walking action
                if (_activeWaypoint.hasRootDisplacement && _activeWaypoint.rootDisplacement != Vector3.zero)
                {
                    _targetPelvisAnchor += transform.TransformDirection(_activeWaypoint.rootDisplacement);
                    _isLocomoting = true;
                }
                else if (activeActionName.ToLower().Contains("walk") || activeActionName.ToLower().Contains("step") || activeActionName.ToLower().Contains("stride") || activeActionName.ToLower().Contains("approach"))
                {
                    _targetPelvisAnchor += transform.forward * 0.25f;
                    _isLocomoting = true;
                }
            }
            else
            {
                if (_activeWaypoint != null)
                {
                    EnterHoldingState(_activeWaypoint);
                }
                else
                {
                    isExecuting = false;
                    currentWaypointName = "Completed / Holding";
                    Debug.Log($"[DynamicPoseQueue] Trajectory '{activeActionName}' fully completed across all {totalWaypointsInQueue} waypoints!");
                    OnTrajectoryFinished?.Invoke(activeActionName, true, "All waypoints completed dynamically.");
                }
            }
        }

        public void StopAndResetToTPose()
        {
            isExecuting = false;
            _activeWaypoint = null;
            _waypointQueue.Clear();
            currentWaypointIndex = 0;
            totalWaypointsInQueue = 0;
            currentWaypointTimer = 0f;
            _isLocomoting = false;

            if (_pickupController != null)
            {
                _pickupController.ReleaseHeldObject();
            }

            if (_pelvisJoint != null && _initialPelvisAnchor != Vector3.zero)
            {
                _targetPelvisAnchor = _initialPelvisAnchor;
                _currentPelvisAnchor = _initialPelvisAnchor;
                _pelvisJoint.connectedAnchor = _initialPelvisAnchor;

                var rbs = GetComponentsInChildren<Rigidbody>();
                foreach (var rb in rbs)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }

            if (_boneMap == null || _boneMap.Count == 0)
            {
                IndexBoneControllers();
            }

            var tPose = new TrajectoryWaypoint
            {
                stepName = "Upright Standing T-Pose (Default)",
                targetDuration = 0.35f
            };

            foreach (var kvp in _boneMap)
            {
                string bname = kvp.Key;
                var interp = kvp.Value;
                if (interp != null)
                {
                    float kp = GetAnatomicalDefaultKp(bname, 25000f);
                    float kd = GetAnatomicalDefaultKd(bname, 1500f);
                    interp.SetTargetAndGains(0f, 0f, 0f, kp, kd);
                    interp.applyTorque = true;

                    tPose.boneTargets[bname] = new BoneWaypointData
                    {
                        boneName = bname,
                        pitch = 0f,
                        yaw = 0f,
                        roll = 0f,
                        kp = kp,
                        kd = kd
                    };
                }
            }

            isHolding = true;
            holdingPose = tPose;
            currentWaypointName = "Upright Standing T-Pose (Holding)";

            Debug.Log("[DynamicPoseQueue] Stopped active queue and firmly locked humanoid rig into Upright Standing T-Pose.");
        }

        public static float GetAnatomicalDefaultKp(string boneName, float requestedKp)
        {
            float minKp;
            switch (boneName)
            {
                case "Spine": minKp = 100000f; break;
                case "Pelvis": minKp = 60000f; break;
                case "Chest": minKp = 45000f; break;
                case "LeftThigh":
                case "RightThigh": minKp = 40000f; break;
                case "LeftCalf":
                case "RightCalf": minKp = 35000f; break;
                default: minKp = 25000f; break;
            }
            return Mathf.Max(requestedKp, minKp);
        }

        public static float GetAnatomicalDefaultKd(string boneName, float requestedKd)
        {
            float minKd;
            switch (boneName)
            {
                case "Spine": minKd = 5000f; break;
                case "Pelvis": minKd = 3500f; break;
                case "Chest": minKd = 2500f; break;
                case "LeftThigh":
                case "RightThigh": minKd = 2500f; break;
                case "LeftCalf":
                case "RightCalf": minKd = 2000f; break;
                default: minKd = 1500f; break;
            }
            return Mathf.Max(requestedKd, minKd);
        }

        public bool EnqueueTrajectoryFromJson(string actionName, string jsonPayload, uint stepId = 0)
        {
            if (_boneMap == null || _boneMap.Count == 0)
            {
                IndexBoneControllers();
            }

            var waypoints = ParseTrajectoryJson(jsonPayload);
            if (waypoints == null || waypoints.Count == 0)
            {
                Debug.LogWarning($"[DynamicPoseQueue] Failed to parse any waypoints from payload for '{actionName}'.");
                return false;
            }

            activeActionName = actionName;
            activeStepId = stepId;
            isHolding = false;
            holdingPose = null;
            _waypointQueue.Clear();
            foreach (var wp in waypoints)
            {
                _waypointQueue.Enqueue(wp);
            }

            totalWaypointsInQueue = waypoints.Count;
            currentWaypointIndex = 0;
            isExecuting = true;

            Debug.Log($"[DynamicPoseQueue] Successfully queued {totalWaypointsInQueue} dynamic waypoints for '{actionName}'. Starting execution...");
            AdvanceToNextWaypoint();
            return true;
        }

        public static List<TrajectoryWaypoint> ParseTrajectoryJson(string json)
        {
            var waypoints = new List<TrajectoryWaypoint>();
            if (string.IsNullOrEmpty(json)) return waypoints;

            // Match each waypoint object block containing bone_targets
            var kfMatches = Regex.Matches(json, @"\{[^{}]*""bone_targets""\s*:\s*\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}[^{}]*\}");
            if (kfMatches.Count == 0)
            {
                // Fallback simpler match
                kfMatches = Regex.Matches(json, @"\{[\s\S]*?\}");
            }

            string[] validBones = new string[] {
                "Pelvis", "Spine", "Chest", "Head",
                "LeftUpperArm", "LeftForearm", "RightUpperArm", "RightForearm",
                "LeftThigh", "LeftCalf", "RightThigh", "RightCalf",
                "LeftShoulder", "RightShoulder"
            };

            foreach (Match kfMatch in kfMatches)
            {
                string block = kfMatch.Value;
                var wp = new TrajectoryWaypoint();

                var nameMatch = Regex.Match(block, @"""step_name""\s*:\s*""([^""]+)""");
                wp.stepName = nameMatch.Success ? nameMatch.Groups[1].Value : $"Pose {waypoints.Count + 1}";

                var durMatch = Regex.Match(block, @"""duration_seconds""\s*:\s*([0-9.]+)");
                if (durMatch.Success && float.TryParse(durMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float dur))
                {
                    wp.targetDuration = dur;
                    wp.maxWatchdogTimeout = Mathf.Max(dur * 2.0f, 0.70f);
                }

                foreach (var bname in validBones)
                {
                    var bonePattern = $@"""{bname}""\s*:\s*\{{([^}}]+)\}}";
                    var bMatch = Regex.Match(block, bonePattern);
                    if (bMatch.Success)
                    {
                        string bParams = bMatch.Groups[1].Value;
                        var bData = new BoneWaypointData { boneName = bname };

                        var pMatch = Regex.Match(bParams, @"""pitch""\s*:\s*([+-]?[0-9.]+)");
                        if (pMatch.Success) float.TryParse(pMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bData.pitch);

                        var yMatch = Regex.Match(bParams, @"""yaw""\s*:\s*([+-]?[0-9.]+)");
                        if (yMatch.Success) float.TryParse(yMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bData.yaw);

                        var rMatch = Regex.Match(bParams, @"""roll""\s*:\s*([+-]?[0-9.]+)");
                        if (rMatch.Success) float.TryParse(rMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bData.roll);

                        var kpMatch = Regex.Match(bParams, @"""kp""\s*:\s*([+-]?[0-9.]+)");
                        if (kpMatch.Success) float.TryParse(kpMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bData.kp);

                        var kdMatch = Regex.Match(bParams, @"""kd""\s*:\s*([+-]?[0-9.]+)");
                        if (kdMatch.Success) float.TryParse(kdMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out bData.kd);

                        wp.boneTargets[bname] = bData;
                    }
                }

                // Parse optional root displacement: "root_displacement": [x, y, z]
                var rootMatch = Regex.Match(block, @"""root_displacement""\s*:\s*\[([+-]?[0-9.]+),\s*([+-]?[0-9.]+),\s*([+-]?[0-9.]+)\]");
                if (rootMatch.Success)
                {
                    float.TryParse(rootMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rx);
                    float.TryParse(rootMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ry);
                    float.TryParse(rootMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rz);
                    wp.rootDisplacement = new Vector3(rx, ry, rz);
                    wp.hasRootDisplacement = true;
                }

                if (wp.boneTargets.Count > 0)
                {
                    waypoints.Add(wp);
                }
            }

            return waypoints;
        }
    }
}

