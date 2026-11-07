# Hackathon Architecture Blueprint (Nemotron + SpacetimeDB + Unity MCP)

## 1. Physics & Per-Bone Torque Architecture
- Every articulated bone on the NPC rig MUST have its own `BoneTorqueInterpreter.cs` component.
- Use `ArticulationBody` (or `ConfigurableJoint` + `Rigidbody`) per bone so joint limits and parent-relative rotations are stable.
- Do NOT use pre-baked Animator clips. In `FixedUpdate()`, each `BoneTorqueInterpreter` computes torque via a PD control law:
  `torque = Kp * (targetEuler - currentLocalEuler) - Kd * currentAngularVelocity`
- Expose `targetPitch`, `targetYaw`, `targetRoll`, `kp` (stiffness/muscle tension), and `kd` (damping) on each bone.

## 2. SpacetimeDB Reactive Sync (No Glue Code)
- Server logic lives in `/spacetimedb-module/Lib.cs`.
- Core Public Tables:
  1. `BoneTarget` (`[PrimaryKey] string bone_name`, `float pitch`, `float yaw`, `float roll`, `float kp`, `float kd`, `uint keyframe_id`)
  2. `BodyTelemetry` (`[PrimaryKey] uint npc_id`, `float com_height`, `float tilt_angle`, `bool is_fallen`, `string active_contact`)
  3. `AgentWorkflowStep` (`[PrimaryKey] uint step_id`, `string goal`, `string current_subtask`, `string status`)
- Reducers validate anatomical joint limits before committing to `BoneTarget`.
- After editing `Lib.cs`, always run `spacetime publish` and `spacetime generate --lang csharp --out-dir ./Assets/Scripts/SpacetimeBindings --project-path ./spacetimedb-module`.

## 3. NVIDIA Nemotron Track Compliance
- Must use multi-step workflows (plan -> apply keyframe -> read telemetry -> re-plan) and tool calling via the NVIDIA NIM API (`https://integrate.api.nvidia.com/v1`).