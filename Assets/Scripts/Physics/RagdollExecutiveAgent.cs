using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Physics
{
    public enum ExecutiveState
    {
        Dormant,
        Thinking,
        SeekingBall,
        GraspingBall,
        AimingAtOpponent,
        LaunchingBall,
        DefensiveStance,
        Recovery
    }

    /// <summary>
    /// Autonomous Executive Function Agent attached to each humanoid ragdoll.
    /// Operates independently with NO central match coordinator.
    /// Evaluates the ragdoll's live sensor state and scene information from SpacetimeDB,
    /// then invokes nemotron-agent/agent.py via background subprocess to dynamically
    /// determine and execute the next motor action goal.
    /// Contains ZERO hardcoded trajectory keyframes.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Ragdoll Executive Agent")]
    public class RagdollExecutiveAgent : MonoBehaviour
    {
        [Header("Agent Identity")]
        [Tooltip("Unique ID for this ragdoll (1 = Team Blue / South, 2 = Team Red / North).")]
        public uint npcId = 1;

        [Tooltip("Opposing humanoid ragdoll competitor.")]
        public GameObject opponentRig;

        [Header("Sensory Reach & Configuration")]
        [Tooltip("Maximum distance from pelvis/hands to dodgeball to consider 'Near Ball' for grasping.")]
        public float pickupReachDistance = 1.65f;

        [Tooltip("Custom path to Python executable (leave empty for auto-detection).")]
        public string pythonExecutable = "";

        [Tooltip("Custom path to agent.py (leave empty for auto-detection).")]
        public string agentScriptPath = "";

        [Header("Live Executive State (Read-Only)")]
        public ExecutiveState currentState = ExecutiveState.Dormant;
        public bool isHoldingBall = false;
        public bool isNearBall = false;
        public bool isFacingOpponent = false;
        public float distanceToNearestBall = 999f;
        public float distanceToOpponent = 999f;
        public float angleToOpponent = 0f;
        public InteractableObject targetBall = null;
        public string currentActionGoal = "Idle / Dormant";
        public string lastExecutiveReasoning = "";

        private DynamicPoseQueueManager _queueManager;
        private ObjectPickupController _pickupController;
        private RagdollFaceController _faceController;
        private SpacetimeRigController _rigController;
        private Transform _pelvisTransform;

        private bool _isAutonomousActive = false;
        private bool _isQueryRunning = false;
        private float _idleWatchdogTimer = 0f;

        private void Awake()
        {
            FindComponents();
        }

        private void Start()
        {
            FindComponents();
            if (opponentRig == null)
            {
                FindOpponent();
            }
        }

        public void FindComponents()
        {
            if (_queueManager == null) _queueManager = GetComponent<DynamicPoseQueueManager>();
            if (_pickupController == null) _pickupController = GetComponent<ObjectPickupController>();
            if (_faceController == null) _faceController = GetComponent<RagdollFaceController>();
            if (_rigController == null) _rigController = GetComponent<SpacetimeRigController>();

            if (_pelvisTransform == null)
            {
                var p = transform.Find("Pelvis");
                _pelvisTransform = p != null ? p : transform;
            }

            if (_queueManager != null)
            {
                _queueManager.OnTrajectoryFinished -= HandleTrajectoryFinished;
                _queueManager.OnTrajectoryFinished += HandleTrajectoryFinished;
            }

            if (_pickupController != null)
            {
                _pickupController.OnObjectPickedUp -= HandleObjectPickedUp;
                _pickupController.OnObjectPickedUp += HandleObjectPickedUp;
                _pickupController.OnObjectReleased -= HandleObjectReleased;
                _pickupController.OnObjectReleased += HandleObjectReleased;
            }
        }

        public void FindOpponent()
        {
            var agents = FindObjectsOfType<RagdollExecutiveAgent>();
            foreach (var a in agents)
            {
                if (a != this && a.gameObject != this.gameObject)
                {
                    opponentRig = a.gameObject;
                    break;
                }
            }

            if (opponentRig == null)
            {
                if (name.Contains("Team1") || npcId == 1)
                {
                    opponentRig = GameObject.Find("HumanoidRig_Team2");
                }
                else
                {
                    opponentRig = GameObject.Find("HumanoidRig_Team1");
                }
            }

            if (opponentRig != null && _pickupController != null)
            {
                var ch = opponentRig.transform.Find("Pelvis/Spine/Chest");
                _pickupController.opponentTarget = ch != null ? ch : opponentRig.transform;
            }
        }

        private void Update()
        {
            if (!_isAutonomousActive) return;

            UpdateSensoryState();

            // Dynamic Turning: Face target dodgeball when seeking/grasping, or face opponent when holding/aiming/launching
            if (_queueManager != null)
            {
                if (isHoldingBall || currentState == ExecutiveState.AimingAtOpponent || currentState == ExecutiveState.LaunchingBall)
                {
                    if (opponentRig != null)
                    {
                        var oppPelvis = opponentRig.transform.Find("Pelvis");
                        Vector3 oppPos = oppPelvis != null ? oppPelvis.position : opponentRig.transform.position;
                        _queueManager.TurnToFace(oppPos);
                    }
                }
                else if (targetBall != null && (currentState == ExecutiveState.SeekingBall || currentState == ExecutiveState.GraspingBall || currentState == ExecutiveState.Thinking || currentState == ExecutiveState.Dormant))
                {
                    _queueManager.TurnToFace(targetBall.transform.position);
                }
            }

            // Periodic watchdog: If idle and no trajectory is executing or querying, re-evaluate after 2.5s (or 0.5s if in defensive stance and a ball is detected)
            if (_queueManager != null && !_queueManager.isExecuting && !_isQueryRunning)
            {
                _idleWatchdogTimer += Time.deltaTime;
                float watchdogTimeout = (currentState == ExecutiveState.DefensiveStance && targetBall != null) ? 0.5f : 2.5f;
                if (_idleWatchdogTimer >= watchdogTimeout)
                {
                    _idleWatchdogTimer = 0f;
                    Debug.Log($"[RagdollExecutiveAgent-{npcId}] Watchdog triggered re-evaluation of executive state.");
                    TriggerExecutiveTurn();
                }
            }
            else
            {
                _idleWatchdogTimer = 0f;
            }
        }

        /// <summary>
        /// Evaluates all available, unheld dodgeballs in the accessible court.
        /// If multiple balls are equidistant (such as initial centerline lineup), picks randomly to distribute targets.
        /// Otherwise (scattered thrown balls), selects the closest accessible ball on the player's court half.
        /// </summary>
        public void EvaluateAndPickTargetBall(bool forceNewPick = false)
        {
            if (!forceNewPick && targetBall != null && !targetBall.isHeld)
            {
                float ballZ = targetBall.transform.position.z;
                bool isAccessible = (npcId == 1 && ballZ <= 0.55f) || (npcId == 2 && ballZ >= -0.55f);
                if (isAccessible) return;
            }

            var allInteractables = FindObjectsOfType<InteractableObject>();
            var validCandidates = new List<InteractableObject>();

            foreach (var candidate in allInteractables)
            {
                if (candidate == null) continue;
                if (candidate.isHeld) continue;

                // Ignore high-speed thrown balls currently flying through the air (> 3.5m/s)
                if (candidate.TryGetComponent<Rigidbody>(out var crb) && crb.velocity.sqrMagnitude > 12.0f)
                {
                    continue;
                }

                float bz = candidate.transform.position.z;
                if (npcId == 1 && bz > 0.55f) continue;
                if (npcId == 2 && bz < -0.55f) continue;

                validCandidates.Add(candidate);
            }

            if (validCandidates.Count > 0)
            {
                Vector3 myPos = _pelvisTransform != null ? _pelvisTransform.position : transform.position;

                // Sort candidates by distance to current pelvis position
                validCandidates.Sort((a, b) =>
                    Vector3.Distance(myPos, a.transform.position).CompareTo(Vector3.Distance(myPos, b.transform.position))
                );

                // If multiple candidates are roughly equidistant (within 0.75m of each other, e.g. initial centerline lineup):
                // Randomly select one out of the evenly spaced dodgeballs
                float minDist = Vector3.Distance(myPos, validCandidates[0].transform.position);
                var nearTies = validCandidates.FindAll(c => Mathf.Abs(Vector3.Distance(myPos, c.transform.position) - minDist) < 0.75f);
                if (nearTies.Count > 1)
                {
                    int randomIndex = UnityEngine.Random.Range(0, nearTies.Count);
                    targetBall = nearTies[randomIndex];
                }
                else
                {
                    targetBall = validCandidates[0];
                }

                Debug.Log($"[RagdollExecutiveAgent-{npcId}] Selected target dodgeball '{targetBall.objectName}' (dist {Vector3.Distance(myPos, targetBall.transform.position):F2}m) out of {validCandidates.Count} candidate balls.");
            }
            else
            {
                targetBall = null;
            }
        }

        /// <summary>
        /// Updates live sensory perception features from the physical ragdoll and arena.
        /// </summary>
        public void UpdateSensoryState()
        {
            isHoldingBall = (_pickupController != null && _pickupController.isHolding);

            // Re-evaluate candidate target ball if current target is absent, held by another, or was thrown over divider
            bool targetOutOfReach = targetBall != null && ((npcId == 1 && targetBall.transform.position.z > 0.55f) || (npcId == 2 && targetBall.transform.position.z < -0.55f));
            if (targetBall == null || targetBall.isHeld || targetOutOfReach)
            {
                EvaluateAndPickTargetBall(forceNewPick: false);
            }

            Vector3 myPos = _pelvisTransform != null ? _pelvisTransform.position : transform.position;
            if (targetBall != null)
            {
                distanceToNearestBall = Vector3.Distance(myPos, targetBall.transform.position);
                isNearBall = (distanceToNearestBall <= pickupReachDistance);
            }
            else
            {
                distanceToNearestBall = 999f;
                isNearBall = false;
            }

            // Sensory state relative to opponent
            if (opponentRig != null)
            {
                var oppPelvis = opponentRig.transform.Find("Pelvis");
                Vector3 oppPos = oppPelvis != null ? oppPelvis.position : opponentRig.transform.position;
                Vector3 toOpp = oppPos - myPos;
                toOpp.y = 0f;
                distanceToOpponent = toOpp.magnitude;
                Vector3 fwd = _pelvisTransform != null ? _pelvisTransform.forward : transform.forward;
                fwd.y = 0f;
                angleToOpponent = Vector3.Angle(fwd, toOpp.normalized);
                isFacingOpponent = (angleToOpponent < 35f);
            }
            else
            {
                distanceToOpponent = 999f;
                angleToOpponent = 0f;
                isFacingOpponent = true;
            }
        }

        /// <summary>
        /// Activates autonomous executive function mode for this ragdoll.
        /// Immediately queries agent.py for the initial decision.
        /// </summary>
        public void ActivateExecutive()
        {
            FindComponents();
            FindOpponent();
            EvaluateAndPickTargetBall(forceNewPick: true);
            if (targetBall != null && _queueManager != null)
            {
                _queueManager.TurnToFace(targetBall.transform.position, immediate: false);
            }
            _isAutonomousActive = true;
            Debug.Log($"[RagdollExecutiveAgent-{npcId}] Autonomous Executive Agent ACTIVATED! Target Ball: {(targetBall != null ? targetBall.objectName : "None")}. Launching initial agent.py query...");
            TriggerExecutiveTurn();
        }

        /// <summary>
        /// Deactivates autonomous executive mode and settles into neutral pose.
        /// </summary>
        public void DeactivateExecutive()
        {
            _isAutonomousActive = false;
            _isQueryRunning = false;
            StopAllCoroutines();
            currentState = ExecutiveState.Dormant;
            currentActionGoal = "Idle / Dormant";
            targetBall = null;
            if (_queueManager != null)
            {
                _queueManager.StopAndResetToTPose();
                _queueManager.ResetOrientationToForward();
            }
            if (_pickupController != null && _pickupController.isHolding) _pickupController.ReleaseHeldObject();
            Debug.Log($"[RagdollExecutiveAgent-{npcId}] Autonomous Executive Agent DEACTIVATED.");
        }

        /// <summary>
        /// Launches agent.py in a background thread to evaluate live state from SpacetimeDB
        /// and dispatch the next dynamic motor trajectory.
        /// </summary>
        public void TriggerExecutiveTurn()
        {
            if (!_isAutonomousActive) return;
            if (_isQueryRunning)
            {
                Debug.Log($"[RagdollExecutiveAgent-{npcId}] Query already in progress, skipping duplicate dispatch.");
                return;
            }

            UpdateSensoryState();

            _isQueryRunning = true;
            currentState = ExecutiveState.Thinking;
            currentActionGoal = "Evaluating state via agent.py...";

            // Serialize current physical sensory snapshot to JSON
            Vector3 pelvisPos = _pelvisTransform != null ? _pelvisTransform.position : transform.position;
            string stateJson = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"npc_id\":{0},\"is_holding_ball\":{1},\"is_near_ball\":{2},\"distance_to_nearest_ball\":{3:F2},\"nearest_ball_name\":\"{4}\",\"is_facing_opponent\":{5},\"distance_to_opponent\":{6:F2},\"angle_to_opponent\":{7:F1},\"pelvis_pos\":[{8:F2},{9:F2},{10:F2}]}}",
                npcId,
                isHoldingBall ? "true" : "false",
                isNearBall ? "true" : "false",
                distanceToNearestBall,
                targetBall != null ? targetBall.objectName : "None",
                isFacingOpponent ? "true" : "false",
                distanceToOpponent,
                angleToOpponent,
                pelvisPos.x, pelvisPos.y, pelvisPos.z
            );

            // Execute in background ThreadPool to keep Unity main thread smooth
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ExecuteAgentSubprocess(stateJson);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RagdollExecutiveAgent-{npcId}] Error in executive subprocess: {ex.Message}");
                }
                finally
                {
                    _isQueryRunning = false;
                }
            });
        }

        private void ExecuteAgentSubprocess(string stateJson)
        {
            string pyExe = ResolvePythonExecutable();
            string pyScript = ResolveAgentScript();

            if (!File.Exists(pyScript))
            {
                Debug.LogError($"[RagdollExecutiveAgent-{npcId}] agent.py script not found at '{pyScript}'!");
                return;
            }

            // Base64 encode JSON to guarantee zero command line quoting issues on Windows
            string b64State = Convert.ToBase64String(Encoding.UTF8.GetBytes(stateJson));
            string args = $"\"{pyScript}\" --npc-id {npcId} --executive --no-await --state-b64 {b64State}";

            var startInfo = new ProcessStartInfo
            {
                FileName = pyExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(pyScript)
            };

            Debug.Log($"[RagdollExecutiveAgent-{npcId}] Spawning: {pyExe} --npc-id {npcId} --executive --no-await");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    Debug.LogError($"[RagdollExecutiveAgent-{npcId}] Failed to start agent process.");
                    return;
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                bool exited = process.WaitForExit(10000); // 10s maximum safeguard

                if (!exited)
                {
                    try { process.Kill(); } catch {}
                    Debug.LogWarning($"[RagdollExecutiveAgent-{npcId}] agent.py timed out after 10s and was killed.");
                    return;
                }

                if (process.ExitCode == 0)
                {
                    // Parse goal and reasoning from output
                    var goalMatch = Regex.Match(stdout, @"Goal:\s*'([^']+)'");
                    var reasonMatch = Regex.Match(stdout, @"Decision Reasoning:\s*([^\r\n]+)");

                    string detectedGoal = goalMatch.Success ? goalMatch.Groups[1].Value : "Action Executed";
                    string detectedReason = reasonMatch.Success ? reasonMatch.Groups[1].Value : "";

                    currentActionGoal = detectedGoal;
                    lastExecutiveReasoning = detectedReason;

                    // Update local state enum based on goal
                    string gLower = detectedGoal.lowerInvariantFast();
                    if (gLower.Contains("approach") || gLower.Contains("walk"))
                        currentState = ExecutiveState.SeekingBall;
                    else if (gLower.Contains("grasp") || gLower.Contains("reach down") || gLower.Contains("lift"))
                        currentState = ExecutiveState.GraspingBall;
                    else if (gLower.Contains("turn") || gLower.Contains("orient"))
                        currentState = ExecutiveState.AimingAtOpponent;
                    else if (gLower.Contains("throw") || gLower.Contains("launch"))
                        currentState = ExecutiveState.LaunchingBall;
                    else if (gLower.Contains("defensive") || gLower.Contains("stance"))
                        currentState = ExecutiveState.DefensiveStance;

                    Debug.Log($"[RagdollExecutiveAgent-{npcId}] Executive Decision: Goal='{detectedGoal}' | Reasoning='{detectedReason}'");
                }
                else
                {
                    Debug.LogWarning($"[RagdollExecutiveAgent-{npcId}] agent.py exited with error (Code {process.ExitCode}):\n{stderr}");
                }
            }
        }

        private void HandleTrajectoryFinished(string actionName, bool success, string msg)
        {
            if (!_isAutonomousActive) return;

            Debug.Log($"[RagdollExecutiveAgent-{npcId}] Trajectory '{actionName}' completed ({msg}). Re-evaluating next executive action...");
            StartCoroutine(DelayedDecision(0.25f));
        }

        private void HandleObjectPickedUp(InteractableObject obj)
        {
            if (!_isAutonomousActive) return;
            Debug.Log($"[RagdollExecutiveAgent-{npcId}] Grasped & Childed {obj.objectName}! Now aiming towards opponent.");
            if (opponentRig != null && _queueManager != null)
            {
                var oppPelvis = opponentRig.transform.Find("Pelvis");
                Vector3 oppPos = oppPelvis != null ? oppPelvis.position : opponentRig.transform.position;
                _queueManager.TurnToFace(oppPos);
            }
            StartCoroutine(DelayedDecision(0.15f));
        }

        private void HandleObjectReleased(InteractableObject obj)
        {
            if (!_isAutonomousActive) return;
            Debug.Log($"[RagdollExecutiveAgent-{npcId}] Released/Thrown {obj.objectName}! Entering recovery settle.");
            currentState = ExecutiveState.Recovery;
            currentActionGoal = "Recovery / Re-evaluating";
            EvaluateAndPickTargetBall(forceNewPick: true);
            if (targetBall != null && _queueManager != null)
            {
                _queueManager.TurnToFace(targetBall.transform.position);
            }
            StartCoroutine(DelayedDecision(0.9f));
        }

        private IEnumerator DelayedDecision(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (!_isAutonomousActive) yield break;

            // Wait if a background subprocess query is currently in progress so this decision trigger is never dropped
            while (_isQueryRunning && _isAutonomousActive)
            {
                yield return new WaitForSeconds(0.1f);
            }

            if (_isAutonomousActive)
            {
                TriggerExecutiveTurn();
            }
        }

        private string ResolvePythonExecutable()
        {
            if (!string.IsNullOrEmpty(pythonExecutable) && File.Exists(pythonExecutable))
                return pythonExecutable;

            string standardPy = @"C:\Users\krishna\AppData\Local\Programs\Python\Python312\python.exe";
            if (File.Exists(standardPy))
                return standardPy;

            return "python";
        }

        private string ResolveAgentScript()
        {
            if (!string.IsNullOrEmpty(agentScriptPath) && File.Exists(agentScriptPath))
                return Path.GetFullPath(agentScriptPath);

            string path1 = Path.Combine(Application.dataPath, "..", "nemotron-agent", "agent.py");
            if (File.Exists(path1))
                return Path.GetFullPath(path1);

            string path2 = Path.Combine(Application.dataPath, "nemotron-agent", "agent.py");
            if (File.Exists(path2))
                return Path.GetFullPath(path2);

            return Path.GetFullPath(path1);
        }
    }

    internal static class StringExtensions
    {
        public static string lowerInvariantFast(this string str)
        {
            return str != null ? str.ToLowerInvariant() : "";
        }
    }
}
