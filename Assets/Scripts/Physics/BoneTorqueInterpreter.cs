using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Per-bone PD controller driving bone orientation toward target local Euler rotation
    /// (pitch, yaw, roll) using configurable Kp (stiffness) and Kd (damping).
    /// Leverages PhysX ConfigurableJoint slerpDrive for constraint-level implicit PD integration
    /// (eliminating flopping, instability, and oscillation) with fallback to torque.
    /// Complies with AGENTS.md physics architecture blueprint.
    /// </summary>
    [AddComponentMenu("Physics/Bone Torque Interpreter")]
    public class BoneTorqueInterpreter : MonoBehaviour
    {
        [Header("Bone Identification")]
        [Tooltip("Identifier matching SpacetimeDB BoneTarget.bone_name")]
        public string boneName;

        [Header("Target Local Euler Angles (Degrees)")]
        [Tooltip("Pitch: Rotation around local X axis")]
        public float targetPitch = 0f;

        [Tooltip("Yaw: Rotation around local Y axis")]
        public float targetYaw = 0f;

        [Tooltip("Roll: Rotation around local Z axis")]
        public float targetRoll = 0f;

        [Header("PD Controller Gains (Muscle Springs)")]
        [Tooltip("Kp: Muscle stiffness / spring drive")]
        public float kp = 25000f;

        [Tooltip("Kd: Muscle damping / damper drive")]
        public float kd = 1200f;

        [Header("Limits & Settings")]
        [Tooltip("Maximum drive force/torque magnitude. Clamps extreme spikes.")]
        public float maxTorque = 300000f;

        [Tooltip("Maximum angular velocity of setpoint transition in degrees/second. Prevents instant mechanical snapping.")]
        public float maxAngularSpeed = 360f;

        [Tooltip("Enable or disable active drive.")]
        public bool applyTorque = true;

        [Header("Telemetry / Debug")]
        [SerializeField] private Vector3 currentErrorEuler;

        private ConfigurableJoint _joint;
        private Rigidbody _rb;
        private Quaternion _currentDriveRot = Quaternion.identity;
        private bool _hasInitDriveRot = false;

        public Vector3 CurrentErrorEuler => currentErrorEuler;
        public float CurrentAngularError => Quaternion.Angle(transform.localRotation, Quaternion.Euler(targetPitch, targetYaw, targetRoll));
        public float CurrentAngularSpeed => _rb != null ? _rb.angularVelocity.magnitude : 0f;

        private void Reset()
        {
            if (string.IsNullOrEmpty(boneName))
            {
                boneName = gameObject.name;
            }
        }

        private void Awake()
        {
            if (string.IsNullOrEmpty(boneName))
            {
                boneName = gameObject.name;
            }

            _joint = GetComponent<ConfigurableJoint>();
            _rb = GetComponent<Rigidbody>();

            if (_joint != null)
            {
                _joint.rotationDriveMode = RotationDriveMode.Slerp;
                UpdateJointDrive();
            }

            if (_rb != null)
            {
                if (_rb.maxAngularVelocity < 50f)
                {
                    _rb.maxAngularVelocity = 50f;
                }
            }
        }

        private void FixedUpdate()
        {
            if (!applyTorque) return;

            // 1. Calculate orientation error for telemetry
            Vector3 currentLocalEuler = transform.localEulerAngles;
            float errorX = Mathf.DeltaAngle(currentLocalEuler.x, targetPitch);
            float errorY = Mathf.DeltaAngle(currentLocalEuler.y, targetYaw);
            float errorZ = Mathf.DeltaAngle(currentLocalEuler.z, targetRoll);
            currentErrorEuler = new Vector3(errorX, errorY, errorZ);

            // 2. Apply drive via PhysX ConfigurableJoint solver
            if (_joint != null)
            {
                UpdateJointDrive();
                Quaternion desiredRot = Quaternion.Euler(targetPitch, targetYaw, targetRoll);

                if (!_hasInitDriveRot)
                {
                    _currentDriveRot = desiredRot;
                    _hasInitDriveRot = true;
                }
                else if (maxAngularSpeed > 0f)
                {
                    _currentDriveRot = Quaternion.RotateTowards(_currentDriveRot, desiredRot, maxAngularSpeed * Time.fixedDeltaTime);
                }
                else
                {
                    _currentDriveRot = desiredRot;
                }

                // In PhysX, targetRotation is the conjugate/inverse of the desired rotation
                _joint.targetRotation = Quaternion.Inverse(_currentDriveRot);
            }
            else if (_rb != null && !_rb.isKinematic)
            {
                // Fallback direct torque if joint is absent
                Vector3 errorRad = currentErrorEuler * Mathf.Deg2Rad;
                Vector3 localAngVel = transform.InverseTransformDirection(_rb.angularVelocity);
                Vector3 torque = (kp * errorRad) - (kd * localAngVel);
                if (maxTorque > 0f) torque = Vector3.ClampMagnitude(torque, maxTorque);
                _rb.AddRelativeTorque(torque, ForceMode.Force);
            }
        }

        private void UpdateJointDrive()
        {
            if (_joint == null) return;

            float effectiveMaxForce = Mathf.Max(maxTorque, kp * 3f);
            JointDrive drive = new JointDrive
            {
                positionSpring = kp,
                positionDamper = kd,
                maximumForce = effectiveMaxForce
            };
            _joint.slerpDrive = drive;
        }

        public void SetTarget(float pitch, float yaw, float roll)
        {
            targetPitch = pitch;
            targetYaw = yaw;
            targetRoll = roll;
        }

        public void SetGains(float stiffness, float damping)
        {
            kp = stiffness;
            kd = damping;
            UpdateJointDrive();
        }

        public void SetTargetAndGains(float pitch, float yaw, float roll, float stiffness, float damping)
        {
            targetPitch = pitch;
            targetYaw = yaw;
            targetRoll = roll;
            kp = stiffness;
            kd = damping;
            UpdateJointDrive();
        }
    }
}
