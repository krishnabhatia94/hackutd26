using UnityEngine;
using UnityEditor;
using Physics;

namespace Physics.Editor
{
    public static class HumanoidRigBuilder
    {
        [MenuItem("Tools/Build 12-Bone Humanoid Rig")]
        public static GameObject BuildRig()
        {
            // Clean up existing rig if present
            GameObject existingRig = GameObject.Find("HumanoidRig");
            if (existingRig != null)
            {
                Undo.DestroyObjectImmediate(existingRig);
            }

            // Also check for existing floor
            GameObject existingFloor = GameObject.Find("Environment_Floor");
            if (existingFloor == null)
            {
                CreateFloor();
            }

            // Root container
            GameObject root = new GameObject("HumanoidRig");
            root.transform.position = Vector3.zero;
            Undo.RegisterCreatedObjectUndo(root, "Create 12-Bone Humanoid Rig");

            // Material for visualization
            Material boneMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat");
            if (boneMaterial == null)
            {
                boneMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            }

            // 1. Pelvis (Root Bone)
            GameObject pelvis = CreateBone("Pelvis", root.transform, new Vector3(0f, 1.05f, 0f), boneMaterial);
            BoxCollider pelvisCol = pelvis.AddComponent<BoxCollider>();
            pelvisCol.size = new Vector3(0.28f, 0.18f, 0.22f);
            Rigidbody pelvisRb = pelvis.GetComponent<Rigidbody>();
            pelvisRb.mass = 12f;

            // Root Pelvis ConfigurableJoint (Anchored to world to suspend or stabilize character)
            ConfigurableJoint pelvisJoint = pelvis.AddComponent<ConfigurableJoint>();
            pelvisJoint.xMotion = ConfigurableJointMotion.Locked;
            pelvisJoint.yMotion = ConfigurableJointMotion.Locked;
            pelvisJoint.zMotion = ConfigurableJointMotion.Locked;
            pelvisJoint.angularXMotion = ConfigurableJointMotion.Free;
            pelvisJoint.angularYMotion = ConfigurableJointMotion.Free;
            pelvisJoint.angularZMotion = ConfigurableJointMotion.Free;
            pelvisJoint.rotationDriveMode = RotationDriveMode.Slerp;
            pelvisJoint.slerpDrive = new JointDrive
            {
                positionSpring = 35000f,
                positionDamper = 2000f,
                maximumForce = 150000f
            };
            pelvisJoint.projectionMode = JointProjectionMode.PositionAndRotation;
            pelvisJoint.enablePreprocessing = false;

            var pelvisBti = pelvis.GetComponent<BoneTorqueInterpreter>();
            pelvisBti.SetTargetAndGains(0f, 0f, 0f, 35000f, 2000f);

            CreateVisualBox(pelvis, pelvisCol.size, boneMaterial);

            // 2. Spine
            GameObject spine = CreateBone("Spine", pelvis.transform, new Vector3(0f, 1.23f, 0f), boneMaterial);
            CapsuleCollider spineCol = spine.AddComponent<CapsuleCollider>();
            spineCol.radius = 0.11f;
            spineCol.height = 0.20f;
            spineCol.direction = 1; // Y-axis
            Rigidbody spineRb = spine.GetComponent<Rigidbody>();
            spineRb.mass = 8f;
            SetupJoint(spine, pelvisRb, new Vector3(0f, -0.09f, 0f));
            spine.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualCapsule(spine, spineCol.radius, spineCol.height, 1, boneMaterial);

            // 3. Chest
            GameObject chest = CreateBone("Chest", spine.transform, new Vector3(0f, 1.45f, 0f), boneMaterial);
            BoxCollider chestCol = chest.AddComponent<BoxCollider>();
            chestCol.size = new Vector3(0.32f, 0.24f, 0.22f);
            Rigidbody chestRb = chest.GetComponent<Rigidbody>();
            chestRb.mass = 14f;
            SetupJoint(chest, spineRb, new Vector3(0f, -0.11f, 0f));
            chest.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualBox(chest, chestCol.size, boneMaterial);

            // 4. Head
            GameObject head = CreateBone("Head", chest.transform, new Vector3(0f, 1.73f, 0f), boneMaterial);
            SphereCollider headCol = head.AddComponent<SphereCollider>();
            headCol.radius = 0.12f;
            Rigidbody headRb = head.GetComponent<Rigidbody>();
            headRb.mass = 4.5f;
            SetupJoint(head, chestRb, new Vector3(0f, -0.14f, 0f));
            head.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 20000f, 1000f);
            CreateVisualSphere(head, headCol.radius, boneMaterial);

            // 5. Left Upper Arm (Horizontal in T-Pose)
            GameObject lUpperArm = CreateBone("LeftUpperArm", chest.transform, new Vector3(-0.26f, 1.51f, 0f), boneMaterial);
            CapsuleCollider lUaCol = lUpperArm.AddComponent<CapsuleCollider>();
            lUaCol.center = new Vector3(-0.14f, 0f, 0f);
            lUaCol.radius = 0.055f;
            lUaCol.height = 0.28f;
            lUaCol.direction = 0; // X-axis
            Rigidbody lUaRb = lUpperArm.GetComponent<Rigidbody>();
            lUaRb.mass = 3.5f;
            SetupJoint(lUpperArm, chestRb, Vector3.zero);
            lUpperArm.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 20000f, 1000f);
            CreateVisualOffsetCapsule(lUpperArm, lUaCol.center, lUaCol.radius, lUaCol.height, 0, boneMaterial);

            // 6. Left Forearm (Extended in T-Pose)
            GameObject lForearm = CreateBone("LeftForearm", lUpperArm.transform, new Vector3(-0.54f, 1.51f, 0f), boneMaterial);
            CapsuleCollider lFaCol = lForearm.AddComponent<CapsuleCollider>();
            lFaCol.center = new Vector3(-0.13f, 0f, 0f);
            lFaCol.radius = 0.045f;
            lFaCol.height = 0.26f;
            lFaCol.direction = 0; // X-axis
            Rigidbody lFaRb = lForearm.GetComponent<Rigidbody>();
            lFaRb.mass = 2.0f;
            SetupJoint(lForearm, lUaRb, Vector3.zero);
            lForearm.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 20000f, 1000f);
            CreateVisualOffsetCapsule(lForearm, lFaCol.center, lFaCol.radius, lFaCol.height, 0, boneMaterial);

            // 7. Right Upper Arm (Horizontal in T-Pose)
            GameObject rUpperArm = CreateBone("RightUpperArm", chest.transform, new Vector3(0.26f, 1.51f, 0f), boneMaterial);
            CapsuleCollider rUaCol = rUpperArm.AddComponent<CapsuleCollider>();
            rUaCol.center = new Vector3(0.14f, 0f, 0f);
            rUaCol.radius = 0.055f;
            rUaCol.height = 0.28f;
            rUaCol.direction = 0; // X-axis
            Rigidbody rUaRb = rUpperArm.GetComponent<Rigidbody>();
            rUaRb.mass = 3.5f;
            SetupJoint(rUpperArm, chestRb, Vector3.zero);
            rUpperArm.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 20000f, 1000f);
            CreateVisualOffsetCapsule(rUpperArm, rUaCol.center, rUaCol.radius, rUaCol.height, 0, boneMaterial);

            // 8. Right Forearm (Extended in T-Pose)
            GameObject rForearm = CreateBone("RightForearm", rUpperArm.transform, new Vector3(0.54f, 1.51f, 0f), boneMaterial);
            CapsuleCollider rFaCol = rForearm.AddComponent<CapsuleCollider>();
            rFaCol.center = new Vector3(0.13f, 0f, 0f);
            rFaCol.radius = 0.045f;
            rFaCol.height = 0.26f;
            rFaCol.direction = 0; // X-axis
            Rigidbody rFaRb = rForearm.GetComponent<Rigidbody>();
            rFaRb.mass = 2.0f;
            SetupJoint(rForearm, rUaRb, Vector3.zero);
            rForearm.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 20000f, 1000f);
            CreateVisualOffsetCapsule(rForearm, rFaCol.center, rFaCol.radius, rFaCol.height, 0, boneMaterial);

            // 9. Left Thigh (Straight down in T-Pose)
            GameObject lThigh = CreateBone("LeftThigh", pelvis.transform, new Vector3(-0.12f, 0.93f, 0f), boneMaterial);
            CapsuleCollider lThighCol = lThigh.AddComponent<CapsuleCollider>();
            lThighCol.center = new Vector3(0f, -0.21f, 0f);
            lThighCol.radius = 0.075f;
            lThighCol.height = 0.42f;
            lThighCol.direction = 1; // Y-axis
            Rigidbody lThighRb = lThigh.GetComponent<Rigidbody>();
            lThighRb.mass = 8.0f;
            SetupJoint(lThigh, pelvisRb, Vector3.zero);
            lThigh.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(lThigh, lThighCol.center, lThighCol.radius, lThighCol.height, 1, boneMaterial);

            // 10. Left Calf (Straight down in T-Pose)
            GameObject lCalf = CreateBone("LeftCalf", lThigh.transform, new Vector3(-0.12f, 0.51f, 0f), boneMaterial);
            CapsuleCollider lCalfCol = lCalf.AddComponent<CapsuleCollider>();
            lCalfCol.center = new Vector3(0f, -0.21f, 0f);
            lCalfCol.radius = 0.065f;
            lCalfCol.height = 0.42f;
            lCalfCol.direction = 1; // Y-axis
            Rigidbody lCalfRb = lCalf.GetComponent<Rigidbody>();
            lCalfRb.mass = 4.5f;
            SetupJoint(lCalf, lThighRb, Vector3.zero);
            lCalf.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(lCalf, lCalfCol.center, lCalfCol.radius, lCalfCol.height, 1, boneMaterial);

            // 11. Right Thigh (Straight down in T-Pose)
            GameObject rThigh = CreateBone("RightThigh", pelvis.transform, new Vector3(0.12f, 0.93f, 0f), boneMaterial);
            CapsuleCollider rThighCol = rThigh.AddComponent<CapsuleCollider>();
            rThighCol.center = new Vector3(0f, -0.21f, 0f);
            rThighCol.radius = 0.075f;
            rThighCol.height = 0.42f;
            rThighCol.direction = 1; // Y-axis
            Rigidbody rThighRb = rThigh.GetComponent<Rigidbody>();
            rThighRb.mass = 8.0f;
            SetupJoint(rThigh, pelvisRb, Vector3.zero);
            rThigh.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(rThigh, rThighCol.center, rThighCol.radius, rThighCol.height, 1, boneMaterial);

            // 12. Right Calf (Straight down in T-Pose)
            GameObject rCalf = CreateBone("RightCalf", rThigh.transform, new Vector3(0.12f, 0.51f, 0f), boneMaterial);
            CapsuleCollider rCalfCol = rCalf.AddComponent<CapsuleCollider>();
            rCalfCol.center = new Vector3(0f, -0.21f, 0f);
            rCalfCol.radius = 0.065f;
            rCalfCol.height = 0.42f;
            rCalfCol.direction = 1; // Y-axis
            Rigidbody rCalfRb = rCalf.GetComponent<Rigidbody>();
            rCalfRb.mass = 4.5f;
            SetupJoint(rCalf, rThighRb, Vector3.zero);
            rCalf.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(rCalf, rCalfCol.center, rCalfCol.radius, rCalfCol.height, 1, boneMaterial);

            // Ignore adjacent collider collisions to prevent jitter
            IgnoreAdjacentCollisions(pelvisCol, spineCol);
            IgnoreAdjacentCollisions(spineCol, chestCol);
            IgnoreAdjacentCollisions(chestCol, headCol);
            IgnoreAdjacentCollisions(chestCol, lUaCol);
            IgnoreAdjacentCollisions(lUaCol, lFaCol);
            IgnoreAdjacentCollisions(chestCol, rUaCol);
            IgnoreAdjacentCollisions(rUaCol, rFaCol);
            IgnoreAdjacentCollisions(pelvisCol, lThighCol);
            IgnoreAdjacentCollisions(lThighCol, lCalfCol);
            IgnoreAdjacentCollisions(pelvisCol, rThighCol);
            IgnoreAdjacentCollisions(rThighCol, rCalfCol);

            // Attach controllers to NPC root
            var goalController = root.AddComponent<RagdollPoseGoalController>();
            var queueManager = root.AddComponent<DynamicPoseQueueManager>();
            var rigController = root.AddComponent<SpacetimeRigController>();
            rigController.serverUrl = "https://maincloud.spacetimedb.com";
            rigController.databaseName = "knet";
            rigController.npcId = 1;
            rigController.telemetryInterval = 0.1f;
            rigController.poseGoalController = goalController;
            rigController.queueManager = queueManager;

            // Setup Main Camera for optimal framing
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.transform.position = new Vector3(0f, 1.35f, -3.2f);
                mainCam.transform.rotation = Quaternion.Euler(6f, 0f, 0f);
            }

            // Ensure AgentWorkflowUI is in scene
            var existingUI = Object.FindObjectOfType<UI.AgentWorkflowUI>();
            if (existingUI == null)
            {
                GameObject uiGo = new GameObject("AgentWorkflowOverlay");
                var ui = uiGo.AddComponent<UI.AgentWorkflowUI>();
                ui.rigController = rigController;
            }
            else
            {
                existingUI.rigController = rigController;
            }

            // Mark active scene dirty so changes persist
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene()
            );

            Selection.activeGameObject = root;
            Debug.Log("[HumanoidRigBuilder] Successfully generated 12-bone articulated humanoid rig.");
            return root;
        }

        private static GameObject CreateBone(string name, Transform parent, Vector3 worldPos, Material mat)
        {
            GameObject bone = new GameObject(name);
            bone.transform.position = worldPos;
            bone.transform.SetParent(parent, true);

            // Every bone gets Rigidbody
            Rigidbody rb = bone.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.maxAngularVelocity = 50f;

            // Every bone gets BoneTorqueInterpreter
            BoneTorqueInterpreter bti = bone.AddComponent<BoneTorqueInterpreter>();
            bti.boneName = name;
            bti.kp = 25000f;
            bti.kd = 1200f;
            bti.maxTorque = 100000f;
            bti.targetPitch = 0f;
            bti.targetYaw = 0f;
            bti.targetRoll = 0f;
            bti.applyTorque = true;

            return bone;
        }

        private static void SetupJoint(GameObject child, Rigidbody parentRb, Vector3 anchor)
        {
            ConfigurableJoint joint = child.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parentRb;
            joint.anchor = anchor;
            joint.autoConfigureConnectedAnchor = true;

            // Lock linear movement to keep bones connected
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

            // Angular movement free so PD torque controller can freely drive it
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;

            joint.rotationDriveMode = RotationDriveMode.Slerp;
            joint.slerpDrive = new JointDrive
            {
                positionSpring = 25000f,
                positionDamper = 1200f,
                maximumForce = 100000f
            };

            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.enablePreprocessing = false;
        }

        private static void IgnoreAdjacentCollisions(Collider a, Collider b)
        {
            if (a != null && b != null)
            {
                UnityEngine.Physics.IgnoreCollision(a, b, true);
            }
        }

        private static void CreateFloor()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Environment_Floor";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(5f, 1f, 5f);
        }

        private static void CreateVisualBox(GameObject parent, Vector3 size, Material mat)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual";
            visual.transform.SetParent(parent.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localScale = size;
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            if (mat != null) visual.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateVisualSphere(GameObject parent, float radius, Material mat)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "Visual";
            visual.transform.SetParent(parent.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localScale = Vector3.one * (radius * 2f);
            Object.DestroyImmediate(visual.GetComponent<Collider>());
            if (mat != null) visual.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateVisualCapsule(GameObject parent, float radius, float height, int direction, Material mat)
        {
            CreateVisualOffsetCapsule(parent, Vector3.zero, radius, height, direction, mat);
        }

        private static void CreateVisualOffsetCapsule(GameObject parent, Vector3 center, float radius, float height, int direction, Material mat)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Visual";
            visual.transform.SetParent(parent.transform, false);
            visual.transform.localPosition = center;

            if (direction == 0) // X-axis
            {
                visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                visual.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }
            else if (direction == 1) // Y-axis
            {
                visual.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }
            else // Z-axis
            {
                visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                visual.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }

            Object.DestroyImmediate(visual.GetComponent<Collider>());
            if (mat != null) visual.GetComponent<Renderer>().sharedMaterial = mat;
        }
    }
}

