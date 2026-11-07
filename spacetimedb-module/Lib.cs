using System;
using SpacetimeDB;

public static partial class Module
{
    // ==========================================
    // 1. Core Public Tables
    // ==========================================

    [SpacetimeDB.Table(Accessor = "BoneTarget", Public = true)]
    public partial struct BoneTarget
    {
        [PrimaryKey]
        public string BoneName;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public float Kp;
        public float Kd;
        public uint KeyframeId;
    }

    [SpacetimeDB.Table(Accessor = "BodyTelemetry", Public = true)]
    public partial struct BodyTelemetry
    {
        [PrimaryKey]
        public uint NpcId;
        public float ComHeight;
        public float TiltAngle;
        public bool IsFallen;
        public string ActiveContact;
    }

    [SpacetimeDB.Table(Accessor = "AgentWorkflowStep", Public = true)]
    public partial struct AgentWorkflowStep
    {
        [PrimaryKey]
        public uint StepId;
        public string Goal;
        public string CurrentSubtask;
        public string Status;
    }

    [SpacetimeDB.Table(Accessor = "EnvironmentState", Public = true)]
    public partial struct EnvironmentState
    {
        [PrimaryKey]
        public uint Id;
        public string Location;
        public float WindSpeed;
        public float WindDirection;
        public float WindForceX;
        public float WindForceZ;
    }

    [SpacetimeDB.Table(Accessor = "NpcImpulse", Public = true)]
    public partial struct NpcImpulse
    {
        [PrimaryKey]
        public uint NpcId;
        public float ForceX;
        public float ForceY;
        public float ForceZ;
        public uint ImpulseCount;
    }

    [SpacetimeDB.Table(Accessor = "BoneLiveState", Public = true)]
    public partial struct BoneLiveState
    {
        [PrimaryKey]
        public string BoneName;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public float AngVelX;
        public float AngVelY;
        public float AngVelZ;
    }

    // ==========================================
    // 2. Reducers
    // ==========================================

    /// <summary>
    /// Updates live physical state (position, rotation, angular velocity) for a bone.
    /// </summary>
    [SpacetimeDB.Reducer]
    public static void UpdateBoneLiveState(
        ReducerContext ctx,
        string boneName,
        float posX,
        float posY,
        float posZ,
        float pitch,
        float yaw,
        float roll,
        float angVelX,
        float angVelY,
        float angVelZ)
    {
        if (ctx.Db.BoneLiveState.BoneName.Find(boneName) is { } existing)
        {
            ctx.Db.BoneLiveState.BoneName.Update(existing with
            {
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                Pitch = pitch,
                Yaw = yaw,
                Roll = roll,
                AngVelX = angVelX,
                AngVelY = angVelY,
                AngVelZ = angVelZ
            });
        }
        else
        {
            ctx.Db.BoneLiveState.Insert(new BoneLiveState
            {
                BoneName = boneName,
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                Pitch = pitch,
                Yaw = yaw,
                Roll = roll,
                AngVelX = angVelX,
                AngVelY = angVelY,
                AngVelZ = angVelZ
            });
        }
    }

    /// <summary>
    /// Applies a target pose keyframe to a specific bone after validating and clamping
    /// joint rotations to realistic anatomical human limits.
    /// </summary>
    [SpacetimeDB.Reducer]
    public static void SetPoseKeyframe(
        ReducerContext ctx,
        string boneName,
        float pitch,
        float yaw,
        float roll,
        float kp,
        float kd,
        uint keyframeId)
    {
        // Clamp bone angles to anatomical limits
        var (clampedPitch, clampedYaw, clampedRoll) = ClampBoneRotation(boneName, pitch, yaw, roll);

        // Ensure non-negative PD gains
        float clampedKp = Math.Max(0f, kp);
        float clampedKd = Math.Max(0f, kd);

        if (ctx.Db.BoneTarget.BoneName.Find(boneName) is { } existing)
        {
            ctx.Db.BoneTarget.BoneName.Update(existing with
            {
                Pitch = clampedPitch,
                Yaw = clampedYaw,
                Roll = clampedRoll,
                Kp = clampedKp,
                Kd = clampedKd,
                KeyframeId = keyframeId
            });
        }
        else
        {
            ctx.Db.BoneTarget.Insert(new BoneTarget
            {
                BoneName = boneName,
                Pitch = clampedPitch,
                Yaw = clampedYaw,
                Roll = clampedRoll,
                Kp = clampedKp,
                Kd = clampedKd,
                KeyframeId = keyframeId
            });
        }
    }

    /// <summary>
    /// Updates body telemetry reported from Unity physics simulation.
    /// </summary>
    [SpacetimeDB.Reducer]
    public static void UpdateTelemetry(
        ReducerContext ctx,
        uint npcId,
        float comHeight,
        float tiltAngle,
        bool isFallen,
        string activeContact)
    {
        if (ctx.Db.BodyTelemetry.NpcId.Find(npcId) is { } existing)
        {
            ctx.Db.BodyTelemetry.NpcId.Update(existing with
            {
                ComHeight = comHeight,
                TiltAngle = tiltAngle,
                IsFallen = isFallen,
                ActiveContact = activeContact ?? ""
            });
        }
        else
        {
            ctx.Db.BodyTelemetry.Insert(new BodyTelemetry
            {
                NpcId = npcId,
                ComHeight = comHeight,
                TiltAngle = tiltAngle,
                IsFallen = isFallen,
                ActiveContact = activeContact ?? ""
            });
        }
    }

    /// <summary>
    /// Updates or inserts an agent workflow execution step.
    /// </summary>
    [SpacetimeDB.Reducer]
    public static void SetWorkflowStep(
        ReducerContext ctx,
        uint stepId,
        string goal,
        string currentSubtask,
        string status)
    {
        if (ctx.Db.AgentWorkflowStep.StepId.Find(stepId) is { } existing)
        {
            ctx.Db.AgentWorkflowStep.StepId.Update(existing with
            {
                Goal = goal ?? "",
                CurrentSubtask = currentSubtask ?? "",
                Status = status ?? ""
            });
        }
        else
        {
            ctx.Db.AgentWorkflowStep.Insert(new AgentWorkflowStep
            {
                StepId = stepId,
                Goal = goal ?? "",
                CurrentSubtask = currentSubtask ?? "",
                Status = status ?? ""
            });
        }
    }

    /// <summary>
    /// Sets environmental conditions and applies proportional external wind force.
    /// </summary>
    [SpacetimeDB.Reducer]
    public static void SetWindForce(
        ReducerContext ctx,
        string location,
        float windSpeed,
        float windDirection,
        float windForceX,
        float windForceZ)
    {
        if (ctx.Db.EnvironmentState.Id.Find(1) is { } existing)
        {
            ctx.Db.EnvironmentState.Id.Update(existing with
            {
                Location = location ?? "",
                WindSpeed = windSpeed,
                WindDirection = windDirection,
                WindForceX = windForceX,
                WindForceZ = windForceZ
            });
        }
        else
        {
            ctx.Db.EnvironmentState.Insert(new EnvironmentState
            {
                Id = 1,
                Location = location ?? "",
                WindSpeed = windSpeed,
                WindDirection = windDirection,
                WindForceX = windForceX,
                WindForceZ = windForceZ
            });
        }
    }

    /// <summary>
    /// Applies an external shove impulse to the NPC across all connected clients.
    /// </summary>
    [SpacetimeDB.Reducer]
    public static void ShoveNpc(
        ReducerContext ctx,
        uint npcId,
        float forceX,
        float forceY,
        float forceZ)
    {
        if (ctx.Db.NpcImpulse.NpcId.Find(npcId) is { } existing)
        {
            ctx.Db.NpcImpulse.NpcId.Update(existing with
            {
                ForceX = forceX,
                ForceY = forceY,
                ForceZ = forceZ,
                ImpulseCount = existing.ImpulseCount + 1
            });
        }
        else
        {
            ctx.Db.NpcImpulse.Insert(new NpcImpulse
            {
                NpcId = npcId,
                ForceX = forceX,
                ForceY = forceY,
                ForceZ = forceZ,
                ImpulseCount = 1
            });
        }
    }

    // ==========================================
    // 3. Anatomical Joint Limits Validation
    // ==========================================

    private static (float pitch, float yaw, float roll) ClampBoneRotation(
        string boneName,
        float pitch,
        float yaw,
        float roll)
    {
        float minPitch = -45f, maxPitch = 45f;
        float minYaw   = -45f, maxYaw   = 45f;
        float minRoll  = -45f, maxRoll  = 45f;

        switch (boneName)
        {
            case "Head":
                minPitch = -50f; maxPitch = 60f;
                minYaw   = -70f; maxYaw   = 70f;
                minRoll  = -40f; maxRoll  = 40f;
                break;

            case "Spine":
                minPitch = -30f; maxPitch = 45f;
                minYaw   = -35f; maxYaw   = 35f;
                minRoll  = -30f; maxRoll  = 30f;
                break;

            case "Chest":
                minPitch = -25f; maxPitch = 30f;
                minYaw   = -30f; maxYaw   = 30f;
                minRoll  = -20f; maxRoll  = 20f;
                break;

            case "Pelvis":
                minPitch = -45f; maxPitch = 45f;
                minYaw   = -45f; maxYaw   = 45f;
                minRoll  = -45f; maxRoll  = 45f;
                break;

            case "LeftUpperArm":
            case "RightUpperArm":
                minPitch = -120f; maxPitch = 135f;
                minYaw   = -90f;  maxYaw   = 90f;
                minRoll  = -120f; maxRoll  = 120f;
                break;

            case "LeftForearm":
            case "RightForearm":
                minPitch = -90f;  maxPitch = 150f;
                minYaw   = -90f;  maxYaw   = 90f;
                minRoll  = -130f; maxRoll  = 130f;
                break;

            case "LeftThigh":
            case "RightThigh":
                minPitch = -30f; maxPitch = 120f;
                minYaw   = -40f; maxYaw   = 40f;
                minRoll  = -40f; maxRoll  = 50f;
                break;

            case "LeftCalf":
            case "RightCalf":
                minPitch = -150f; maxPitch = 5f;
                minYaw   = -10f;  maxYaw   = 10f;
                minRoll  = -5f;   maxRoll  = 5f;
                break;
        }

        float cPitch = Math.Clamp(pitch, minPitch, maxPitch);
        float cYaw   = Math.Clamp(yaw, minYaw, maxYaw);
        float cRoll  = Math.Clamp(roll, minRoll, maxRoll);

        return (cPitch, cYaw, cRoll);
    }
}
