using System;
using System.Collections.Generic;
using UnityEngine;

namespace Physics
{
    [System.Serializable]
    public class BoneGoal
    {
        public string boneName;
        public float pitch;
        public float yaw;
        public float roll;
        public float kp = 25000f;
        public float kd = 1200f;
    }

    [System.Serializable]
    public class BoneState
    {
        public string boneName;
        public Vector3 worldPosition;
        public Vector3 localEuler;
        public Vector3 angularVelocity;
        public Vector3 orientationError;
    }

    /// <summary>
    /// Attached to the main NPC ragdoll root GameObject.
    /// Tracks the 'Current Goal Pose' that Nemotron is in charge of updating,
    /// and collects the live model state (all bones' 3D positions and rotations)
    /// for Nemotron queries and SpacetimeDB sync.
    /// </summary>
    [AddComponentMenu("Physics/Ragdoll Pose Goal Controller")]
    public class RagdollPoseGoalController : MonoBehaviour
    {
        [Header("Current Goal Pose (Updated by Nemotron)")]
        [Tooltip("High-level description of what Nemotron has planned")]
        public string currentGoalPoseName = "Upright Standing T-Pose (Default)";

        [Tooltip("Active keyframe step identifier from Nemotron workflow")]
        public uint currentStepId = 0;

        [Tooltip("Per-bone target orientations and muscle gains in active goal pose")]
        public List<BoneGoal> goalBoneTargets = new();

        [Header("Live Model State (All Bones - Read Only)")]
        [Tooltip("Live physical 3D positions, rotations, and velocities queried by Nemotron")]
        public List<BoneState> liveBoneStates = new();

        private Dictionary<string, BoneTorqueInterpreter> _boneMap = new();
        private Dictionary<string, BoneGoal> _goalMap = new();
        private Dictionary<string, BoneState> _stateMap = new();

        public IReadOnlyDictionary<string, BoneTorqueInterpreter> BoneMap => _boneMap;

        private void Awake()
        {
            IndexBones();
        }

        private void Start()
        {
            ApplyStandingTPose();
        }

        public void IndexBones()
        {
            _boneMap.Clear();
            _goalMap.Clear();
            _stateMap.Clear();

            var interpreters = GetComponentsInChildren<BoneTorqueInterpreter>(true);
            foreach (var bti in interpreters)
            {
                string key = string.IsNullOrEmpty(bti.boneName) ? bti.gameObject.name : bti.boneName;
                _boneMap[key] = bti;

                var goal = new BoneGoal
                {
                    boneName = key,
                    pitch = bti.targetPitch,
                    yaw = bti.targetYaw,
                    roll = bti.targetRoll,
                    kp = bti.kp,
                    kd = bti.kd
                };
                _goalMap[key] = goal;

                var state = new BoneState
                {
                    boneName = key,
                    worldPosition = bti.transform.position,
                    localEuler = bti.transform.localEulerAngles,
                    angularVelocity = Vector3.zero,
                    orientationError = Vector3.zero
                };
                _stateMap[key] = state;
            }

            SyncListsFromMaps();
        }

        private void Update()
        {
            // Update live telemetry of all bones continuously
            foreach (var kvp in _boneMap)
            {
                string name = kvp.Key;
                var bti = kvp.Value;
                if (bti == null) continue;

                if (_stateMap.TryGetValue(name, out var state))
                {
                    state.worldPosition = bti.transform.position;
                    state.localEuler = bti.transform.localEulerAngles;
                    state.orientationError = bti.CurrentErrorEuler;

                    if (bti.TryGetComponent<Rigidbody>(out var rb))
                    {
                        state.angularVelocity = rb.angularVelocity;
                    }
                }
            }
        }

        /// <summary>
        /// Applies or updates a single bone target within the current goal pose (dispatched from Nemotron/SpacetimeDB).
        /// </summary>
        public void UpdateGoalBoneTarget(string boneName, float pitch, float yaw, float roll, float kp, float kd, uint stepId)
        {
            currentStepId = stepId;

            if (_boneMap.TryGetValue(boneName, out var bti))
            {
                bti.SetTargetAndGains(pitch, yaw, roll, kp, kd);
            }

            if (_goalMap.TryGetValue(boneName, out var goal))
            {
                goal.pitch = pitch;
                goal.yaw = yaw;
                goal.roll = roll;
                goal.kp = kp;
                goal.kd = kd;
            }
            else
            {
                var newGoal = new BoneGoal
                {
                    boneName = boneName,
                    pitch = pitch,
                    yaw = yaw,
                    roll = roll,
                    kp = kp,
                    kd = kd
                };
                _goalMap[boneName] = newGoal;
            }

            SyncListsFromMaps();
        }

        /// <summary>
        /// Sets a new goal pose name and step ID.
        /// </summary>
        public void SetGoalPoseInfo(string poseName, uint stepId)
        {
            currentGoalPoseName = poseName;
            currentStepId = stepId;
        }

        /// <summary>
        /// Resets the ragdoll to the upright standing T-Pose default.
        /// </summary>
        [ContextMenu("Reset to Standing T-Pose")]
        public void ApplyStandingTPose()
        {
            currentGoalPoseName = "Upright Standing T-Pose (Default)";
            currentStepId = 0;

            var pickupCtrl = GetComponent<ObjectPickupController>();
            if (pickupCtrl != null)
            {
                pickupCtrl.ReleaseHeldObject();
            }

            foreach (var kvp in _boneMap)
            {
                string name = kvp.Key;
                var bti = kvp.Value;
                if (bti == null) continue;

                // Standing T-Pose: all angles at (0, 0, 0) with high muscle stiffness
                float kp = (name == "Pelvis" || name == "Spine" || name == "Chest") ? 25000f : 20000f;
                float kd = (name == "Pelvis" || name == "Spine" || name == "Chest") ? 1500f : 1000f;

                bti.maxTorque = 100000f;
                bti.SetTargetAndGains(0f, 0f, 0f, kp, kd);

                if (_goalMap.TryGetValue(name, out var goal))
                {
                    goal.pitch = 0f;
                    goal.yaw = 0f;
                    goal.roll = 0f;
                    goal.kp = kp;
                    goal.kd = kd;
                }
            }

            SyncListsFromMaps();
            Debug.Log("[RagdollPoseGoalController] Applied Upright Standing T-Pose.");
        }

        private void SyncListsFromMaps()
        {
            goalBoneTargets.Clear();
            goalBoneTargets.AddRange(_goalMap.Values);

            liveBoneStates.Clear();
            liveBoneStates.AddRange(_stateMap.Values);
        }

        /// <summary>
        /// Returns all 12 bones live states for SpacetimeDB sync and Nemotron queries.
        /// </summary>
        public IEnumerable<BoneState> GetAllBoneLiveStates()
        {
            return _stateMap.Values;
        }
    }
}

