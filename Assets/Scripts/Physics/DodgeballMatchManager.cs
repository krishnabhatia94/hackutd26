using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Physics
{
    /// <summary>
    /// Coordinates autonomous dodgeball matches between two active humanoid ragdolls on the court.
    /// Manages the match lifecycle:
    /// 1. Queries both ragdolls to rush forward towards a dodgeball on the center dividing line.
    /// 2. Executes bilateral grasp and childing to lift the ball.
    /// 3. Aims/orients towards the opposing ragdoll's live world position.
    /// 4. Executes explosive forward throw launching the dodgeball at the opponent.
    /// 5. Automatically tracks hits and triggers Sad Face reactions on impact.
    /// Provides an on-screen UI with 'Start Dodgeball Match', 'Reset Match', and 'Toggle Camera' buttons.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Dodgeball Match Manager")]
    public class DodgeballMatchManager : MonoBehaviour
    {
        [Header("Ragdoll Competitors")]
        [Tooltip("Humanoid 1 (Team Blue, South end).")]
        public GameObject dummy1;

        [Tooltip("Humanoid 2 (Team Red, North end).")]
        public GameObject dummy2;

        [Header("Dodgeball References")]
        [Tooltip("Centerline dodgeballs in the court.")]
        public List<InteractableObject> dodgeballs = new();

        [Header("Cameras")]
        [Tooltip("Third-person follow camera childed to Dummy 1 Pelvis.")]
        public Camera dummy1Camera;

        [Tooltip("Overhead sideline arena broadcast camera.")]
        public Camera spectatorCamera;

        [Header("Match Tuning")]
        [Tooltip("Launch speed (m/s) when throwing dodgeballs across the court.")]
        public float matchThrowSpeed = 12.5f;

        [Header("Live Match Telemetry (Read-Only)")]
        public string matchState = "Idle / Ready";
        public int dummy1Hits = 0;
        public int dummy2Hits = 0;
        public bool isMatchRunning = false;

        private DynamicPoseQueueManager _queue1;
        private DynamicPoseQueueManager _queue2;
        private ObjectPickupController _pickup1;
        private ObjectPickupController _pickup2;
        private RagdollFaceController _face1;
        private RagdollFaceController _face2;
        private RagdollExecutiveAgent _executive1;
        private RagdollExecutiveAgent _executive2;

        private Vector3 _dummy1InitialPos = new Vector3(0f, 0f, -8f);
        private Quaternion _dummy1InitialRot = Quaternion.identity;
        private Vector3 _dummy2InitialPos = new Vector3(0f, 0f, 8f);
        private Quaternion _dummy2InitialRot = Quaternion.Euler(0f, 180f, 0f);

        private readonly List<(Vector3 pos, Quaternion rot)> _ballInitialTransforms = new();

        // UI References
        private Text _statusText;
        private Text _scoreText;
        private Button _startMatchBtn;
        private Button _resetBtn;
        private Button _camToggleBtn;

        private void Awake()
        {
            FindRagdollReferences();
            CacheInitialTransforms();
            EnsureUIHierarchy();
            ApplyCourtAndTeamColors();
        }

        private void Start()
        {
            FindRagdollReferences();
            SubscribeProjectileHits();
            ApplyCourtAndTeamColors();
            SetMatchState("Arena Ready! Click 'Start Dodgeball Match'");
        }

        private void Update()
        {
            // Keyboard shortcut 'C' for camera toggle
            if (Input.GetKeyDown(KeyCode.C))
            {
                ToggleCamera();
            }

            // Keyboard shortcut 'Space' to start match if idle
            if (Input.GetKeyDown(KeyCode.Space) && !isMatchRunning)
            {
                StartDodgeballMatch();
            }

            // Update live telemetry HUD of both independent executive agents
            if (isMatchRunning)
            {
                string s1 = _executive1 != null ? _executive1.currentState.ToString() : (_queue1 != null && _queue1.isExecuting ? "Executing" : "Idle");
                string s2 = _executive2 != null ? _executive2.currentState.ToString() : (_queue2 != null && _queue2.isExecuting ? "Executing" : "Idle");
                string g1 = _executive1 != null ? _executive1.currentActionGoal : "";
                string g2 = _executive2 != null ? _executive2.currentActionGoal : "";
                string hudMsg = $"BLUE [NPC 1]: {s1} ({g1})   |   RED [NPC 2]: {s2} ({g2})";
                if (_statusText != null && _statusText.text != hudMsg)
                {
                    _statusText.text = hudMsg;
                }
            }
        }

        public void FindRagdollReferences()
        {
            if (dummy1 == null) dummy1 = GameObject.Find("HumanoidRig");
            if (dummy1 == null) dummy1 = GameObject.Find("HumanoidRig_Team1");

            if (dummy2 == null) dummy2 = GameObject.Find("HumanoidRig_Team2");
            if (dummy2 == null) dummy2 = GameObject.Find("HumanoidRig (1)");

            if (dummy1 != null)
            {
                _executive1 = dummy1.GetComponent<RagdollExecutiveAgent>();
                if (_executive1 == null) _executive1 = dummy1.AddComponent<RagdollExecutiveAgent>();
                _executive1.npcId = 1;

                _queue1 = dummy1.GetComponent<DynamicPoseQueueManager>();
                _pickup1 = dummy1.GetComponent<ObjectPickupController>();
                _face1 = dummy1.GetComponent<RagdollFaceController>();
            }

            if (dummy2 != null)
            {
                _executive2 = dummy2.GetComponent<RagdollExecutiveAgent>();
                if (_executive2 == null) _executive2 = dummy2.AddComponent<RagdollExecutiveAgent>();
                _executive2.npcId = 2;

                _queue2 = dummy2.GetComponent<DynamicPoseQueueManager>();
                _pickup2 = dummy2.GetComponent<ObjectPickupController>();
                _face2 = dummy2.GetComponent<RagdollFaceController>();
            }

            if (_executive1 != null && dummy2 != null) _executive1.opponentRig = dummy2;
            if (_executive2 != null && dummy1 != null) _executive2.opponentRig = dummy1;

            // Cross-target chest transforms for throwing aim
            if (_pickup1 != null && dummy2 != null)
            {
                var ch2 = dummy2.transform.Find("Pelvis/Spine/Chest");
                _pickup1.opponentTarget = ch2 != null ? ch2 : dummy2.transform;
            }
            if (_pickup2 != null && dummy1 != null)
            {
                var ch1 = dummy1.transform.Find("Pelvis/Spine/Chest");
                _pickup2.opponentTarget = ch1 != null ? ch1 : dummy1.transform;
            }

            if (dodgeballs == null || dodgeballs.Count == 0)
            {
                var found = FindObjectsOfType<InteractableObject>();
                dodgeballs = new List<InteractableObject>(found);
            }
        }

        private void CacheInitialTransforms()
        {
            if (dummy1 != null)
            {
                _dummy1InitialPos = dummy1.transform.position;
                _dummy1InitialRot = Quaternion.identity;
            }
            if (dummy2 != null)
            {
                _dummy2InitialPos = dummy2.transform.position;
                _dummy2InitialRot = Quaternion.Euler(0f, 180f, 0f);
            }

            _ballInitialTransforms.Clear();
            foreach (var b in dodgeballs)
            {
                if (b != null)
                {
                    _ballInitialTransforms.Add((b.transform.position, b.transform.rotation));
                }
            }
        }

        private void SubscribeProjectileHits()
        {
            var projectiles = FindObjectsOfType<ObstacleProjectile>();
            if (projectiles == null || projectiles.Length == 0)
            {
                var allBalls = FindObjectsOfType<InteractableObject>();
                foreach (var ball in allBalls)
                {
                    if (ball != null && ball.GetComponent<ObstacleProjectile>() == null)
                    {
                        ball.gameObject.AddComponent<ObstacleProjectile>();
                    }
                }
                projectiles = FindObjectsOfType<ObstacleProjectile>();
            }

            foreach (var proj in projectiles)
            {
                proj.OnHitRegistered -= HandleProjectileHit;
                proj.OnHitRegistered += HandleProjectileHit;
            }
        }

        private void HandleProjectileHit(RagdollFaceController hitRagdoll, Collision col)
        {
            if (_face1 == null || _face2 == null)
            {
                FindRagdollReferences();
            }

            if (hitRagdoll != null)
            {
                bool isDummy1 = (hitRagdoll == _face1) ||
                                (dummy1 != null && (hitRagdoll.gameObject == dummy1 || hitRagdoll.transform.IsChildOf(dummy1.transform))) ||
                                hitRagdoll.name.Contains("Team1");

                bool isDummy2 = (hitRagdoll == _face2) ||
                                (dummy2 != null && (hitRagdoll.gameObject == dummy2 || hitRagdoll.transform.IsChildOf(dummy2.transform))) ||
                                hitRagdoll.name.Contains("Team2");

                if (isDummy1)
                {
                    dummy1Hits++;
                    Debug.Log($"[DodgeballMatchManager] Dummy 1 (Team Blue) STRUCK by Dodgeball! Hits: {dummy1Hits}");
                }
                else if (isDummy2)
                {
                    dummy2Hits++;
                    Debug.Log($"[DodgeballMatchManager] Dummy 2 (Team Red) STRUCK by Dodgeball! Hits: {dummy2Hits}");
                }

                UpdateScoreUI();
            }
        }

        /// <summary>
        /// Initiates the autonomous dodgeball match:
        /// Activates both ragdolls' independent Executive Agents to query state, choose action, and compete!
        /// </summary>
        [ContextMenu("Start Dodgeball Match")]
        public void StartDodgeballMatch()
        {
            if (isMatchRunning) return;

            FindRagdollReferences();
            SubscribeProjectileHits();
            isMatchRunning = true;

            if (_executive1 != null) _executive1.ActivateExecutive();
            if (_executive2 != null) _executive2.ActivateExecutive();

            SetMatchState("Match Started! Each ragdoll executive agent autonomously analyzing state & competing!");
        }

        /// <summary>
        /// Resets both humanoid ragdolls and returns dodgeballs back to the center dividing line.
        /// </summary>
        [ContextMenu("Reset Match")]
        public void ResetMatch()
        {
            StopAllCoroutines();
            isMatchRunning = false;

            if (_executive1 != null) _executive1.DeactivateExecutive();
            if (_executive2 != null) _executive2.DeactivateExecutive();

            if (_queue1 != null) _queue1.StopAndResetToTPose();
            if (_queue2 != null) _queue2.StopAndResetToTPose();

            if (_pickup1 != null) _pickup1.ReleaseHeldObject();
            if (_pickup2 != null) _pickup2.ReleaseHeldObject();

            // Explicitly reset heading yaw and orientation of both ragdolls to face forward
            if (_queue1 != null) _queue1.ResetOrientationToForward();
            if (_queue2 != null) _queue2.ResetOrientationToForward();

            // Return competitors to initial transforms
            if (dummy1 != null)
            {
                dummy1.transform.position = _dummy1InitialPos;
                dummy1.transform.rotation = _dummy1InitialRot;
                if (_executive1 != null)
                {
                    _executive1.targetBall = null;
                    _executive1.currentState = ExecutiveState.Dormant;
                }
            }
            if (dummy2 != null)
            {
                dummy2.transform.position = _dummy2InitialPos;
                dummy2.transform.rotation = _dummy2InitialRot;
                if (_executive2 != null)
                {
                    _executive2.targetBall = null;
                    _executive2.currentState = ExecutiveState.Dormant;
                }
            }

            // Return balls to starting locations
            for (int i = 0; i < dodgeballs.Count && i < _ballInitialTransforms.Count; i++)
            {
                var b = dodgeballs[i];
                if (b != null)
                {
                    var rb = b.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    b.transform.position = _ballInitialTransforms[i].pos;
                    b.transform.rotation = _ballInitialTransforms[i].rot;

                    var proj = b.GetComponent<ObstacleProjectile>();
                    if (proj != null) proj.ResetImpact();
                }
            }

            dummy1Hits = 0;
            dummy2Hits = 0;
            UpdateScoreUI();
            ApplyCourtAndTeamColors();
            SetMatchState("Match Reset! Both competitors returned to starting positions.");
        }

        /// <summary>
        /// Styles one half of the court Blue (South, Team 1) and the other half Red (North, Team 2),
        /// and colors the humanoid torsos (Pelvis, Spine, Chest) Red or Blue matching their team side.
        /// </summary>
        public void ApplyCourtAndTeamColors()
        {
            // 1. Color Humanoid Torsos
            // Team 1 (South, Blue): Torso bones colored Team Blue
            if (dummy1 != null)
            {
                ApplyTorsoColor(dummy1, new Color(0.15f, 0.45f, 0.92f, 1f));
            }

            // Team 2 (North, Red): Torso bones colored Team Red
            if (dummy2 != null)
            {
                ApplyTorsoColor(dummy2, new Color(0.90f, 0.20f, 0.20f, 1f));
            }

            // 2. Color Court Floor Halves (South = Blue, North = Red)
            var floor = GameObject.Find("Court_Floor");
            var southHalf = GameObject.Find("Court_Floor_South");
            var northHalf = GameObject.Find("Court_Floor_North");

            if (southHalf == null && floor != null)
            {
                Transform arenaParent = floor.transform.parent;
                southHalf = Instantiate(floor, arenaParent);
                southHalf.name = "Court_Floor_South";
                southHalf.transform.localPosition = new Vector3(0f, -0.15f, -7f);
                southHalf.transform.localScale = new Vector3(16f, 0.3f, 14f);
            }

            if (northHalf == null && floor != null)
            {
                Transform arenaParent = floor.transform.parent;
                northHalf = Instantiate(floor, arenaParent);
                northHalf.name = "Court_Floor_North";
                northHalf.transform.localPosition = new Vector3(0f, -0.15f, 7f);
                northHalf.transform.localScale = new Vector3(16f, 0.3f, 14f);
            }

            if (floor != null && (southHalf != null || northHalf != null))
            {
                floor.SetActive(false);
            }

            if (southHalf != null)
            {
                var smr = southHalf.GetComponent<MeshRenderer>();
                if (smr != null)
                {
                    smr.material.color = new Color(0.14f, 0.36f, 0.74f, 1f);
                }
            }

            if (northHalf != null)
            {
                var nmr = northHalf.GetComponent<MeshRenderer>();
                if (nmr != null)
                {
                    nmr.material.color = new Color(0.76f, 0.20f, 0.20f, 1f);
                }
            }
        }

        private void ApplyTorsoColor(GameObject dummy, Color color)
        {
            if (dummy == null) return;
            string[] torsoBones = new[] { "Pelvis", "Spine", "Chest" };
            foreach (var bName in torsoBones)
            {
                Transform boneTr = dummy.transform.Find(bName);
                if (boneTr == null)
                {
                    foreach (var t in dummy.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name == bName) { boneTr = t; break; }
                    }
                }

                if (boneTr != null)
                {
                    var renderers = boneTr.GetComponentsInChildren<MeshRenderer>(true);
                    foreach (var mr in renderers)
                    {
                        // Ensure we color only the torso visuals
                        if (mr.name == "Visual" || mr.transform.parent == boneTr || mr.transform == boneTr)
                        {
                            mr.material.color = color;
                        }
                    }
                }
            }
        }

        public void ToggleCamera()
        {
            if (dummy1Camera == null || spectatorCamera == null)
            {
                var cams = FindObjectsOfType<Camera>();
                foreach (var c in cams)
                {
                    if (c.gameObject.name.Contains("Spectator") || c.gameObject.name.Contains("Court"))
                        spectatorCamera = c;
                    else if (c.gameObject.name.Contains("Main") || c.transform.IsChildOf(dummy1 != null ? dummy1.transform : transform))
                        dummy1Camera = c;
                }
            }

            if (dummy1Camera != null && spectatorCamera != null)
            {
                bool specActive = spectatorCamera.enabled;
                spectatorCamera.enabled = !specActive;
                dummy1Camera.enabled = specActive;

                Debug.Log($"[DodgeballMatchManager] Active Camera: {(dummy1Camera.enabled ? "Dummy 1 Pelvis Follower" : "Court Spectator Broadcast View")}");
            }
        }

        private void SetMatchState(string msg)
        {
            matchState = msg;
            if (_statusText != null)
            {
                _statusText.text = msg;
            }
            Debug.Log($"[DodgeballMatchManager] {msg}");
        }

        private void UpdateScoreUI()
        {
            if (_scoreText != null)
            {
                _scoreText.text = $"DUMMY 1 (BLUE): {dummy1Hits} HITS   |   DUMMY 2 (RED): {dummy2Hits} HITS";
            }
        }

        /// <summary>
        /// Generates a valid JSON trajectory for approaching the ball, picking it up, aiming, and explosive throw.
        /// </summary>
        public static string GenerateMatchTrajectoryJson(string teamName, float fwdDist, float latDist, int totalSteps, float throwSpeed)
        {
            float stepZ = (float)Math.Round(fwdDist / 7.0f, 3);
            float stepX = (float)Math.Round(latDist / 7.0f, 3);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"keyframes\":[");

            // 7 Locomotion Strides
            for (int i = 1; i <= 7; i++)
            {
                bool rightLead = (i % 2 != 0);
                float rThigh = rightLead ? 35f : -12f;
                float rCalf = rightLead ? -28f : 0f;
                float lThigh = rightLead ? -12f : 35f;
                float lCalf = rightLead ? 0f : -28f;
                float lArmYaw = rightLead ? 25f : -25f;
                float rArmYaw = rightLead ? -25f : 25f;

                sb.Append("{");
                sb.Append($"\"step_name\":\"{i}/16 {teamName} Stride {i}\",");
                sb.Append("\"duration_seconds\":0.28,");
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"root_displacement\":[{0:F3},0.0,{1:F3}],", stepX, stepZ));
                sb.Append("\"bone_targets\":{");
                sb.Append("\"Spine\":{\"pitch\":5.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"RightThigh\":{{\"pitch\":{0:F1},\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0}},", rThigh));
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"RightCalf\":{{\"pitch\":{0:F1},\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0}},", rCalf));
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"LeftThigh\":{{\"pitch\":{0:F1},\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0}},", lThigh));
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"LeftCalf\":{{\"pitch\":{0:F1},\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0}},", lCalf));
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"LeftUpperArm\":{{\"pitch\":0.0,\"yaw\":{0:F1},\"roll\":-10.0,\"kp\":25000.0,\"kd\":1500.0}},", lArmYaw));
                sb.Append(string.Format(System.Globalization.CultureInfo.InvariantCulture, "\"RightUpperArm\":{{\"pitch\":0.0,\"yaw\":{0:F1},\"roll\":10.0,\"kp\":25000.0,\"kd\":1500.0}}", rArmYaw));
                sb.Append("}},");
            }

            // Step 8: Shoulder Preparation & Wide Arm Spread
            sb.Append("{\"step_name\":\"8/16 Shoulder Preparation & Wide Arm Spread\",\"duration_seconds\":0.30,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":4.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":2.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":0.0,\"yaw\":45.0,\"roll\":-15.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":0.0,\"yaw\":-45.0,\"roll\":15.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":10.0,\"roll\":0.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":-10.0,\"roll\":0.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 9: Torso Incline Downward
            sb.Append("{\"step_name\":\"9/16 Torso Incline Downward\",\"duration_seconds\":0.32,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":20.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":12.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":18.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-22.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":18.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-22.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":15.0,\"yaw\":55.0,\"roll\":-5.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":15.0,\"yaw\":-55.0,\"roll\":5.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":15.0,\"roll\":0.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":-15.0,\"roll\":0.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 10: Knee Compliance & Low Reach
            sb.Append("{\"step_name\":\"10/16 Knee Compliance & Low Reach\",\"duration_seconds\":0.34,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":32.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":16.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":28.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-38.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":28.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-38.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":22.0,\"yaw\":68.0,\"roll\":0.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":22.0,\"yaw\":-68.0,\"roll\":0.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":25.0,\"roll\":5.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":-25.0,\"roll\":-5.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 11: Dual-Hand Grasp and Child Object
            sb.Append("{\"step_name\":\"11/16 Dual-Hand Grasp and Child Object\",\"duration_seconds\":0.35,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":32.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":16.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":28.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-38.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":28.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-38.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":18.0,\"yaw\":78.0,\"roll\":8.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":18.0,\"yaw\":-78.0,\"roll\":-8.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":45.0,\"roll\":10.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":-45.0,\"roll\":-10.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 12: Spinal Ascent & Ground Lift
            sb.Append("{\"step_name\":\"12/16 Spinal Ascent & Ground Lift\",\"duration_seconds\":0.32,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":18.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":10.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":14.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-16.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":14.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-16.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":10.0,\"yaw\":75.0,\"roll\":-5.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":10.0,\"yaw\":-75.0,\"roll\":5.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":40.0,\"roll\":15.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":-40.0,\"roll\":-15.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 13: Upright Lift Extension & Chest Elevation
            sb.Append("{\"step_name\":\"13/16 Upright Lift Extension & Chest Elevation\",\"duration_seconds\":0.32,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":5.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":2.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":2.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":42000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-2.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":2.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":42000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-2.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":0.0,\"yaw\":72.0,\"roll\":-8.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":0.0,\"yaw\":-72.0,\"roll\":8.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":35.0,\"roll\":25.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":-35.0,\"roll\":-25.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 14: Preparatory Wind-Up & Arm Cocking
            sb.Append("{\"step_name\":\"14/16 Preparatory Wind-Up & Arm Cocking\",\"duration_seconds\":0.28,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":-6.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":-4.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":10.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":42000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-12.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":10.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":42000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-12.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":-12.0,\"yaw\":62.0,\"roll\":-10.0,\"kp\":28000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":-12.0,\"yaw\":-62.0,\"roll\":10.0,\"kp\":28000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":-10.0,\"yaw\":40.0,\"roll\":15.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":-10.0,\"yaw\":-40.0,\"roll\":-15.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 15: Explosive Release and Throw Dodgeball (triggers physics release and impulse!)
            sb.Append("{\"step_name\":\"15/16 Explosive Release and Throw Dodgeball\",\"duration_seconds\":0.25,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":22.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":14.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":12.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":-8.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":12.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":-8.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":32.0,\"yaw\":22.0,\"roll\":-5.0,\"kp\":35000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":32.0,\"yaw\":-22.0,\"roll\":5.0,\"kp\":35000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":22.0,\"yaw\":10.0,\"roll\":0.0,\"kp\":25000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":22.0,\"yaw\":-10.0,\"roll\":0.0,\"kp\":25000.0,\"kd\":1200.0}");
            sb.Append("}},");

            // Step 16: Forward Follow-Through & Upright T-Pose Settle
            sb.Append("{\"step_name\":\"16/16 Forward Follow-Through & Upright T-Pose Settle\",\"duration_seconds\":0.35,\"bone_targets\":{");
            sb.Append("\"Spine\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":100000.0,\"kd\":5000.0},");
            sb.Append("\"Chest\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":45000.0,\"kd\":2500.0},");
            sb.Append("\"RightThigh\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":40000.0,\"kd\":2500.0},");
            sb.Append("\"RightCalf\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftThigh\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":40000.0,\"kd\":2500.0},");
            sb.Append("\"LeftCalf\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":35000.0,\"kd\":2000.0},");
            sb.Append("\"LeftUpperArm\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"RightUpperArm\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":25000.0,\"kd\":1500.0},");
            sb.Append("\"LeftForearm\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":22000.0,\"kd\":1200.0},");
            sb.Append("\"RightForearm\":{\"pitch\":0.0,\"yaw\":0.0,\"roll\":0.0,\"kp\":22000.0,\"kd\":1200.0}");
            sb.Append("}}");

            sb.Append("]}");
            return sb.ToString();
        }

        private void EnsureUIHierarchy()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                var existingCanvas = GameObject.Find("DodgeballCourtCanvas");
                if (existingCanvas != null)
                {
                    canvas = existingCanvas.GetComponent<Canvas>();
                }
            }

            if (canvas == null)
            {
                GameObject canvasGo = new GameObject("DodgeballCourtCanvas");
                canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                canvasGo.AddComponent<GraphicRaycaster>();

                if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
                {
                    GameObject es = new GameObject("EventSystem");
                    es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                }
            }

            BuildCourtHUD(canvas.gameObject);
        }

        private void BuildCourtHUD(GameObject parent)
        {
            Font defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (defaultFont == null) defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

            // Top Status Panel
            GameObject topPanel = new GameObject("CourtTopPanel");
            topPanel.transform.SetParent(parent.transform, false);
            RectTransform topRect = topPanel.AddComponent<RectTransform>();
            topRect.anchorMin = new Vector2(0.5f, 1f);
            topRect.anchorMax = new Vector2(0.5f, 1f);
            topRect.pivot = new Vector2(0.5f, 1f);
            topRect.anchoredPosition = new Vector2(0f, -15f);
            topRect.sizeDelta = new Vector2(850f, 130f);

            Image topImg = topPanel.AddComponent<Image>();
            topImg.color = new Color(0.06f, 0.08f, 0.12f, 0.90f);

            // Title
            GameObject titleGo = CreateTextObject(topPanel, "Title", "NVIDIA NEMOTRON DODGEBALL ARENA", defaultFont, 20, FontStyle.Bold, Color.yellow);
            RectTransform titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(0f, -15f);
            titleRect.sizeDelta = new Vector2(800f, 30f);
            titleGo.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

            // Score
            GameObject scoreGo = CreateTextObject(topPanel, "Score", "DUMMY 1 (BLUE): 0 HITS   |   DUMMY 2 (RED): 0 HITS", defaultFont, 16, FontStyle.Bold, Color.white);
            RectTransform scoreRect = scoreGo.GetComponent<RectTransform>();
            scoreRect.anchoredPosition = new Vector2(0f, -48f);
            scoreRect.sizeDelta = new Vector2(800f, 25f);
            _scoreText = scoreGo.GetComponent<Text>();
            _scoreText.alignment = TextAnchor.MiddleCenter;

            // Match Status
            GameObject statusGo = CreateTextObject(topPanel, "Status", "Arena Ready! Click 'Start Dodgeball Match'", defaultFont, 14, FontStyle.Normal, Color.cyan);
            RectTransform statusRect = statusGo.GetComponent<RectTransform>();
            statusRect.anchoredPosition = new Vector2(0f, -80f);
            statusRect.sizeDelta = new Vector2(800f, 25f);
            _statusText = statusGo.GetComponent<Text>();
            _statusText.alignment = TextAnchor.MiddleCenter;

            // Bottom Buttons Panel
            GameObject btnPanel = new GameObject("CourtButtonsPanel");
            btnPanel.transform.SetParent(parent.transform, false);
            RectTransform btnRect = btnPanel.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0f);
            btnRect.anchorMax = new Vector2(0.5f, 0f);
            btnRect.pivot = new Vector2(0.5f, 0f);
            btnRect.anchoredPosition = new Vector2(0f, 25f);
            btnRect.sizeDelta = new Vector2(760f, 75f);

            Image btnImg = btnPanel.AddComponent<Image>();
            btnImg.color = new Color(0.06f, 0.08f, 0.12f, 0.85f);

            // Start Match Button
            GameObject startBtnGo = CreateButtonObject(btnPanel, "StartMatchButton", "START DODGEBALL MATCH", defaultFont, 16, new Color(0.12f, 0.65f, 0.28f, 1f));
            RectTransform startBtnRect = startBtnGo.GetComponent<RectTransform>();
            startBtnRect.anchoredPosition = new Vector2(-230f, 0f);
            startBtnRect.sizeDelta = new Vector2(250f, 50f);
            _startMatchBtn = startBtnGo.GetComponent<Button>();
            _startMatchBtn.onClick.AddListener(StartDodgeballMatch);

            // Reset Match Button
            GameObject resetBtnGo = CreateButtonObject(btnPanel, "ResetButton", "RESET MATCH", defaultFont, 14, new Color(0.75f, 0.22f, 0.22f, 1f));
            RectTransform resetBtnRect = resetBtnGo.GetComponent<RectTransform>();
            resetBtnRect.anchoredPosition = new Vector2(40f, 0f);
            resetBtnRect.sizeDelta = new Vector2(170f, 50f);
            _resetBtn = resetBtnGo.GetComponent<Button>();
            _resetBtn.onClick.AddListener(ResetMatch);

            // Toggle Camera Button
            GameObject camBtnGo = CreateButtonObject(btnPanel, "CamToggleButton", "TOGGLE CAMERA [C]", defaultFont, 14, new Color(0.18f, 0.42f, 0.72f, 1f));
            RectTransform camBtnRect = camBtnGo.GetComponent<RectTransform>();
            camBtnRect.anchoredPosition = new Vector2(245f, 0f);
            camBtnRect.sizeDelta = new Vector2(190f, 50f);
            _camToggleBtn = camBtnGo.GetComponent<Button>();
            _camToggleBtn.onClick.AddListener(ToggleCamera);
        }

        private GameObject CreateTextObject(GameObject parent, string name, string text, Font font, int size, FontStyle style, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            Text t = go.AddComponent<Text>();
            t.text = text;
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            return go;
        }

        private GameObject CreateButtonObject(GameObject parent, string name, string label, Font font, int fontSize, Color bgColor)
        {
            GameObject btnGo = new GameObject(name);
            btnGo.transform.SetParent(parent.transform, false);

            Image img = btnGo.AddComponent<Image>();
            img.color = bgColor;

            Button btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = img;

            ColorBlock cb = btn.colors;
            cb.highlightedColor = bgColor * 1.25f;
            cb.pressedColor = bgColor * 0.8f;
            btn.colors = cb;

            GameObject textGo = CreateTextObject(btnGo, "Text", label, font, fontSize, FontStyle.Bold, Color.white);
            RectTransform tr = textGo.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.sizeDelta = Vector2.zero;
            tr.anchoredPosition = Vector2.zero;
            textGo.GetComponent<Text>().alignment = TextAnchor.MiddleCenter;

            return btnGo;
        }
    }
}
