using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Physics;

namespace Physics.Editor
{
    [InitializeOnLoad]
    public static class DodgeballCourtSceneAutoRunner
    {
        static DodgeballCourtSceneAutoRunner()
        {
            EditorApplication.delayCall += CheckAndAutoBuild;
        }

        private static void CheckAndAutoBuild()
        {
            if (!System.IO.File.Exists("Assets/Scenes/DodgeballCourt.unity"))
            {
                Debug.Log("[DodgeballCourtSceneAutoRunner] Scene 'Assets/Scenes/DodgeballCourt.unity' not found. Auto-generating court...");
                DodgeballCourtSceneBuilder.BuildDodgeballCourtScene();
            }
        }
    }

    /// <summary>
    /// Programmatically generates the complete 'DodgeballCourt' scene:
    /// - Full basketball/dodgeball court arena with hardwood floor, half-court dividing line, attack lines, perimeter walls.
    /// - Row of 5 interactable dodgeballs along the center dividing line.
    /// - Two fully articulated 12-bone active ragdolls (Dummy 1 on South side facing +Z, Dummy 2 on North side facing -Z).
    /// - Retains the exact camera setup from SampleScene childed to Dummy 1's Pelvis, plus an arena broadcast spectator camera.
    /// - Modern on-screen UI with 'Start Dodgeball Match', 'Reset Match', and 'Toggle Camera' buttons.
    /// </summary>
    public static class DodgeballCourtSceneBuilder
    {
        private const string SCENE_PATH = "Assets/Scenes/DodgeballCourt.unity";

        [MenuItem("Dodgeball/Build DodgeballCourt Scene", false, 1)]
        public static void BuildDodgeballCourtScene()
        {
            Debug.Log("[DodgeballCourtSceneBuilder] Beginning automated generation of 'DodgeballCourt.unity'...");

            // Create or open scene
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Lighting & Environment
            SetupLighting();

            // 2. Materials
            Material floorMat = CreateOrGetCourtMaterial("CourtFloorMat", new Color(0.84f, 0.60f, 0.38f), 0.25f);
            Material borderMat = CreateOrGetCourtMaterial("CourtBorderMat", Color.white, 0.1f);
            Material attackLineMat = CreateOrGetCourtMaterial("CourtAttackLineMat", new Color(1f, 0.55f, 0.05f), 0.1f);
            Material wallMat = CreateOrGetCourtMaterial("ArenaWallMat", new Color(0.12f, 0.16f, 0.24f), 0.4f);
            Material ballMat = CreateOrGetCourtMaterial("DodgeballRedMat", new Color(0.92f, 0.18f, 0.14f), 0.5f);
            Material blueRigMat = CreateOrGetCourtMaterial("DummyBlueMat", new Color(0.20f, 0.48f, 0.88f), 0.2f);
            Material redRigMat = CreateOrGetCourtMaterial("DummyRedMat", new Color(0.88f, 0.28f, 0.22f), 0.2f);

            PhysicMaterial ballPhysMat = CreateOrGetPhysicMaterial("DodgeballBouncyMat", 0.70f, 0.35f);

            // 3. Court Arena Geometry
            GameObject arenaRoot = new GameObject("Arena_Court");
            BuildCourtArena(arenaRoot.transform, floorMat, borderMat, attackLineMat, wallMat);

            // 4. Center Line Dodgeballs
            List<InteractableObject> dodgeballs = SpawnCenterDodgeballs(arenaRoot.transform, ballMat, ballPhysMat);

            // 5. Dummy 1 (Team Blue, South end, z = -8)
            GameObject dummy1 = BuildHumanoidCompetitor("HumanoidRig_Team1", new Vector3(0f, 0f, -8f), Quaternion.identity, blueRigMat, 1);

            // Attach user's camera to Dummy 1's Pelvis (exact setup from SampleScene)
            Transform pelvis1 = dummy1.transform.Find("Pelvis");
            Camera dummy1Cam = SetupDummy1Camera(pelvis1);

            // 6. Dummy 2 (Team Red, North end, z = +8, facing south towards Dummy 1)
            GameObject dummy2 = BuildHumanoidCompetitor("HumanoidRig_Team2", new Vector3(0f, 0f, 8f), Quaternion.Euler(0f, 180f, 0f), redRigMat, 2);

            // 7. Court Spectator Broadcast Camera
            Camera spectatorCam = SetupSpectatorCamera();

            // Ensure Dummy 1 camera starts active, spectator camera is available on toggle
            if (dummy1Cam != null && spectatorCam != null)
            {
                dummy1Cam.enabled = true;
                spectatorCam.enabled = false;
            }

            // 8. Match Manager & UI
            GameObject managerGo = new GameObject("DodgeballMatchManager");
            var matchMgr = managerGo.AddComponent<DodgeballMatchManager>();
            matchMgr.dummy1 = dummy1;
            matchMgr.dummy2 = dummy2;
            matchMgr.dodgeballs = dodgeballs;
            matchMgr.dummy1Camera = dummy1Cam;
            matchMgr.spectatorCamera = spectatorCam;

            // Save Scene
            EditorSceneManager.SaveScene(scene, SCENE_PATH);
            Debug.Log($"[DodgeballCourtSceneBuilder] Successfully built and saved '{SCENE_PATH}'!");

            // Add scene to Build Settings if not already present
            AddSceneToBuildSettings(SCENE_PATH);
        }

        private static void SetupLighting()
        {
            GameObject lightGo = new GameObject("Directional Light");
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.intensity = 1.25f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(48f, -32f, 0f);

            RenderSettings.ambientLight = new Color(0.24f, 0.28f, 0.35f);
        }

        private static void BuildCourtArena(Transform parent, Material floorMat, Material borderMat, Material attackLineMat, Material wallMat)
        {
            // Court Floor: 28m long (Z), 16m wide (X)
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Court_Floor";
            floor.transform.SetParent(parent, false);
            floor.transform.position = new Vector3(0f, -0.15f, 0f);
            floor.transform.localScale = new Vector3(16f, 0.3f, 28f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // Half-Court Dividing Line (Z = 0)
            CreateLine(parent, "Center_Dividing_Line", new Vector3(0f, 0.01f, 0f), new Vector3(16f, 0.02f, 0.25f), borderMat);

            // Center Jump Circle (Radius 1.8m)
            GameObject centerCircle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            centerCircle.name = "Center_Jump_Circle";
            centerCircle.transform.SetParent(parent, false);
            centerCircle.transform.position = new Vector3(0f, 0.012f, 0f);
            centerCircle.transform.localScale = new Vector3(3.6f, 0.015f, 3.6f);
            UnityEngine.Object.DestroyImmediate(centerCircle.GetComponent<Collider>());
            centerCircle.GetComponent<Renderer>().sharedMaterial = borderMat;

            // Center Inner Disc
            GameObject centerDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            centerDisc.name = "Center_Jump_Disc";
            centerDisc.transform.SetParent(parent, false);
            centerDisc.transform.position = new Vector3(0f, 0.015f, 0f);
            centerDisc.transform.localScale = new Vector3(3.2f, 0.016f, 3.2f);
            UnityEngine.Object.DestroyImmediate(centerDisc.GetComponent<Collider>());
            centerDisc.GetComponent<Renderer>().sharedMaterial = floorMat;

            // Attack Lines (3-meter lines from center at z = -3m and z = +3m)
            CreateLine(parent, "Attack_Line_South", new Vector3(0f, 0.01f, -3f), new Vector3(16f, 0.02f, 0.15f), attackLineMat);
            CreateLine(parent, "Attack_Line_North", new Vector3(0f, 0.01f, 3f), new Vector3(16f, 0.02f, 0.15f), attackLineMat);

            // Perimeter Boundary Lines
            CreateLine(parent, "Perimeter_North", new Vector3(0f, 0.01f, 13.925f), new Vector3(16f, 0.02f, 0.15f), borderMat);
            CreateLine(parent, "Perimeter_South", new Vector3(0f, 0.01f, -13.925f), new Vector3(16f, 0.02f, 0.15f), borderMat);
            CreateLine(parent, "Perimeter_East", new Vector3(7.925f, 0.01f, 0f), new Vector3(0.15f, 0.02f, 28f), borderMat);
            CreateLine(parent, "Perimeter_West", new Vector3(-7.925f, 0.01f, 0f), new Vector3(0.15f, 0.02f, 28f), borderMat);

            // Free-throw keys (Basketball court feature)
            CreateLine(parent, "Key_Line_South", new Vector3(0f, 0.01f, -8.5f), new Vector3(4.8f, 0.02f, 0.15f), borderMat);
            CreateLine(parent, "Key_Line_North", new Vector3(0f, 0.01f, 8.5f), new Vector3(4.8f, 0.02f, 0.15f), borderMat);

            // Perimeter Arena Walls (Padded court barriers)
            CreateWall(parent, "Wall_North", new Vector3(0f, 1.0f, 14.15f), new Vector3(16.6f, 2.0f, 0.3f), wallMat);
            CreateWall(parent, "Wall_South", new Vector3(0f, 1.0f, -14.15f), new Vector3(16.6f, 2.0f, 0.3f), wallMat);
            CreateWall(parent, "Wall_East", new Vector3(8.15f, 1.0f, 0f), new Vector3(0.3f, 2.0f, 28.6f), wallMat);
            CreateWall(parent, "Wall_West", new Vector3(-8.15f, 1.0f, 0f), new Vector3(0.3f, 2.0f, 28.6f), wallMat);
        }

        private static void CreateLine(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            GameObject line = GameObject.CreatePrimitive(PrimitiveType.Cube);
            line.name = name;
            line.transform.SetParent(parent, false);
            line.transform.position = pos;
            line.transform.localScale = size;
            UnityEngine.Object.DestroyImmediate(line.GetComponent<Collider>());
            line.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateWall(Transform parent, string name, Vector3 pos, Vector3 size, Material mat)
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(parent, false);
            wall.transform.position = pos;
            wall.transform.localScale = size;
            wall.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static List<InteractableObject> SpawnCenterDodgeballs(Transform parent, Material ballMat, PhysicMaterial physMat)
        {
            var list = new List<InteractableObject>();
            float[] xOffsets = new float[] { -4.0f, -2.0f, 0.0f, 2.0f, 4.0f };

            for (int i = 0; i < xOffsets.Length; i++)
            {
                float x = xOffsets[i];
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ball.name = $"Interactable_Dodgeball_{i + 1}";
                ball.tag = "Interactable";
                ball.transform.SetParent(parent, true);
                ball.transform.position = new Vector3(x, 0.25f, 0f);
                ball.transform.localScale = Vector3.one * 0.5f;

                ball.GetComponent<Renderer>().sharedMaterial = ballMat;

                var col = ball.GetComponent<SphereCollider>();
                col.material = physMat;
                col.radius = 0.5f;

                var rb = ball.AddComponent<Rigidbody>();
                rb.mass = 1.8f;
                rb.drag = 0.25f;
                rb.angularDrag = 0.35f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                var interactable = ball.AddComponent<InteractableObject>();
                interactable.objectName = $"Red Dodgeball {i + 1}";
                interactable.interactType = "Pickup";
                interactable.description = $"Regulation red dodgeball at centerline position {x:F1}m.";
                interactable.interactionRadius = 0.85f;

                var proj = ball.AddComponent<ObstacleProjectile>();
                proj.impactImpulse = new Vector3(0f, 25f, 0f);

                list.Add(interactable);
            }

            return list;
        }

        private static GameObject BuildHumanoidCompetitor(string rigName, Vector3 spawnPos, Quaternion spawnRot, Material limbMat, uint npcId)
        {
            GameObject root = new GameObject(rigName);
            root.transform.position = spawnPos;
            root.transform.rotation = spawnRot;

            // 1. Pelvis (Root Bone)
            GameObject pelvis = CreateBone("Pelvis", root.transform, spawnPos + spawnRot * new Vector3(0f, 1.05f, 0f), limbMat);
            BoxCollider pelvisCol = pelvis.AddComponent<BoxCollider>();
            pelvisCol.size = new Vector3(0.28f, 0.18f, 0.22f);
            Rigidbody pelvisRb = pelvis.GetComponent<Rigidbody>();
            pelvisRb.mass = 12f;

            // ConfigurableJoint on Pelvis
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
            CreateVisualBox(pelvis, pelvisCol.size, limbMat);

            // 2. Spine
            GameObject spine = CreateBone("Spine", pelvis.transform, spawnPos + spawnRot * new Vector3(0f, 1.23f, 0f), limbMat);
            CapsuleCollider spineCol = spine.AddComponent<CapsuleCollider>();
            spineCol.radius = 0.11f;
            spineCol.height = 0.20f;
            spineCol.direction = 1;
            Rigidbody spineRb = spine.GetComponent<Rigidbody>();
            spineRb.mass = 8f;
            SetupJoint(spine, pelvisRb, new Vector3(0f, -0.09f, 0f));
            spine.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualCapsule(spine, spineCol.radius, spineCol.height, 1, limbMat);

            // 3. Chest
            GameObject chest = CreateBone("Chest", spine.transform, spawnPos + spawnRot * new Vector3(0f, 1.45f, 0f), limbMat);
            BoxCollider chestCol = chest.AddComponent<BoxCollider>();
            chestCol.size = new Vector3(0.32f, 0.24f, 0.22f);
            Rigidbody chestRb = chest.GetComponent<Rigidbody>();
            chestRb.mass = 14f;
            SetupJoint(chest, spineRb, new Vector3(0f, -0.11f, 0f));
            chest.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualBox(chest, chestCol.size, limbMat);

            // 4. Head
            GameObject head = CreateBone("Head", chest.transform, spawnPos + spawnRot * new Vector3(0f, 1.73f, 0f), limbMat);
            SphereCollider headCol = head.AddComponent<SphereCollider>();
            headCol.radius = 0.12f;
            Rigidbody headRb = head.GetComponent<Rigidbody>();
            headRb.mass = 4.5f;
            SetupJoint(head, chestRb, new Vector3(0f, -0.14f, 0f));
            head.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 20000f, 1000f);
            CreateVisualSphere(head, headCol.radius, limbMat);

            // Face visual on head
            SetupFaceVisual(head.transform);

            // 5. Left Upper Arm
            GameObject lUa = CreateBone("LeftUpperArm", chest.transform, spawnPos + spawnRot * new Vector3(-0.25f, 1.45f, 0f), limbMat);
            CapsuleCollider lUaCol = lUa.AddComponent<CapsuleCollider>();
            lUaCol.radius = 0.055f;
            lUaCol.height = 0.28f;
            lUaCol.direction = 0;
            lUaCol.center = new Vector3(-0.14f, 0f, 0f);
            Rigidbody lUaRb = lUa.GetComponent<Rigidbody>();
            lUaRb.mass = 3.2f;
            SetupJoint(lUa, chestRb, Vector3.zero);
            lUa.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(lUa, lUaCol.center, lUaCol.radius, lUaCol.height, 0, limbMat);

            // 6. Left Forearm
            GameObject lFa = CreateBone("LeftForearm", lUa.transform, spawnPos + spawnRot * new Vector3(-0.53f, 1.45f, 0f), limbMat);
            CapsuleCollider lFaCol = lFa.AddComponent<CapsuleCollider>();
            lFaCol.radius = 0.05f;
            lFaCol.height = 0.26f;
            lFaCol.direction = 0;
            lFaCol.center = new Vector3(-0.13f, 0f, 0f);
            Rigidbody lFaRb = lFa.GetComponent<Rigidbody>();
            lFaRb.mass = 2.2f;
            SetupJoint(lFa, lUaRb, Vector3.zero);
            lFa.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 22000f, 1200f);
            CreateVisualOffsetCapsule(lFa, lFaCol.center, lFaCol.radius, lFaCol.height, 0, limbMat);

            // 7. Right Upper Arm
            GameObject rUa = CreateBone("RightUpperArm", chest.transform, spawnPos + spawnRot * new Vector3(0.25f, 1.45f, 0f), limbMat);
            CapsuleCollider rUaCol = rUa.AddComponent<CapsuleCollider>();
            rUaCol.radius = 0.055f;
            rUaCol.height = 0.28f;
            rUaCol.direction = 0;
            rUaCol.center = new Vector3(0.14f, 0f, 0f);
            Rigidbody rUaRb = rUa.GetComponent<Rigidbody>();
            rUaRb.mass = 3.2f;
            SetupJoint(rUa, chestRb, Vector3.zero);
            rUa.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(rUa, rUaCol.center, rUaCol.radius, rUaCol.height, 0, limbMat);

            // 8. Right Forearm
            GameObject rFa = CreateBone("RightForearm", rUa.transform, spawnPos + spawnRot * new Vector3(0.53f, 1.45f, 0f), limbMat);
            CapsuleCollider rFaCol = rFa.AddComponent<CapsuleCollider>();
            rFaCol.radius = 0.05f;
            rFaCol.height = 0.26f;
            rFaCol.direction = 0;
            rFaCol.center = new Vector3(0.13f, 0f, 0f);
            Rigidbody rFaRb = rFa.GetComponent<Rigidbody>();
            rFaRb.mass = 2.2f;
            SetupJoint(rFa, rUaRb, Vector3.zero);
            rFa.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 22000f, 1200f);
            CreateVisualOffsetCapsule(rFa, rFaCol.center, rFaCol.radius, rFaCol.height, 0, limbMat);

            // 9. Left Thigh
            GameObject lThigh = CreateBone("LeftThigh", pelvis.transform, spawnPos + spawnRot * new Vector3(-0.11f, 0.95f, 0f), limbMat);
            CapsuleCollider lThighCol = lThigh.AddComponent<CapsuleCollider>();
            lThighCol.radius = 0.075f;
            lThighCol.height = 0.44f;
            lThighCol.direction = 1;
            lThighCol.center = new Vector3(0f, -0.22f, 0f);
            Rigidbody lThighRb = lThigh.GetComponent<Rigidbody>();
            lThighRb.mass = 8.5f;
            SetupJoint(lThigh, pelvisRb, Vector3.zero);
            lThigh.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 35000f, 2000f);
            CreateVisualOffsetCapsule(lThigh, lThighCol.center, lThighCol.radius, lThighCol.height, 1, limbMat);

            // 10. Left Calf
            GameObject lCalf = CreateBone("LeftCalf", lThigh.transform, spawnPos + spawnRot * new Vector3(-0.11f, 0.51f, 0f), limbMat);
            CapsuleCollider lCalfCol = lCalf.AddComponent<CapsuleCollider>();
            lCalfCol.radius = 0.065f;
            lCalfCol.height = 0.45f;
            lCalfCol.direction = 1;
            lCalfCol.center = new Vector3(0f, -0.22f, 0f);
            Rigidbody lCalfRb = lCalf.GetComponent<Rigidbody>();
            lCalfRb.mass = 5.0f;
            SetupJoint(lCalf, lThighRb, Vector3.zero);
            lCalf.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(lCalf, lCalfCol.center, lCalfCol.radius, lCalfCol.height, 1, limbMat);

            // 11. Right Thigh
            GameObject rThigh = CreateBone("RightThigh", pelvis.transform, spawnPos + spawnRot * new Vector3(0.11f, 0.95f, 0f), limbMat);
            CapsuleCollider rThighCol = rThigh.AddComponent<CapsuleCollider>();
            rThighCol.radius = 0.075f;
            rThighCol.height = 0.44f;
            rThighCol.direction = 1;
            rThighCol.center = new Vector3(0f, -0.22f, 0f);
            Rigidbody rThighRb = rThigh.GetComponent<Rigidbody>();
            rThighRb.mass = 8.5f;
            SetupJoint(rThigh, pelvisRb, Vector3.zero);
            rThigh.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 35000f, 2000f);
            CreateVisualOffsetCapsule(rThigh, rThighCol.center, rThighCol.radius, rThighCol.height, 1, limbMat);

            // 12. Right Calf
            GameObject rCalf = CreateBone("RightCalf", rThigh.transform, spawnPos + spawnRot * new Vector3(0.11f, 0.51f, 0f), limbMat);
            CapsuleCollider rCalfCol = rCalf.AddComponent<CapsuleCollider>();
            rCalfCol.radius = 0.065f;
            rCalfCol.height = 0.45f;
            rCalfCol.direction = 1;
            rCalfCol.center = new Vector3(0f, -0.22f, 0f);
            Rigidbody rCalfRb = rCalf.GetComponent<Rigidbody>();
            rCalfRb.mass = 5.0f;
            SetupJoint(rCalf, rThighRb, Vector3.zero);
            rCalf.GetComponent<BoneTorqueInterpreter>().SetTargetAndGains(0f, 0f, 0f, 25000f, 1500f);
            CreateVisualOffsetCapsule(rCalf, rCalfCol.center, rCalfCol.radius, rCalfCol.height, 1, limbMat);

            // Ignore adjacent collisions
            IgnoreAdjacent(pelvisCol, spineCol);
            IgnoreAdjacent(spineCol, chestCol);
            IgnoreAdjacent(chestCol, headCol);
            IgnoreAdjacent(chestCol, lUaCol);
            IgnoreAdjacent(lUaCol, lFaCol);
            IgnoreAdjacent(chestCol, rUaCol);
            IgnoreAdjacent(rUaCol, rFaCol);
            IgnoreAdjacent(pelvisCol, lThighCol);
            IgnoreAdjacent(lThighCol, lCalfCol);
            IgnoreAdjacent(pelvisCol, rThighCol);
            IgnoreAdjacent(rThighCol, rCalfCol);

            // Attach Rig Controllers
            var goalCtrl = root.AddComponent<RagdollPoseGoalController>();
            var queueMgr = root.AddComponent<DynamicPoseQueueManager>();
            var pickupCtrl = root.AddComponent<ObjectPickupController>();
            var faceCtrl = root.AddComponent<RagdollFaceController>();
            var rigCtrl = root.AddComponent<SpacetimeRigController>();

            rigCtrl.serverUrl = "https://maincloud.spacetimedb.com";
            rigCtrl.databaseName = "knet";
            rigCtrl.npcId = npcId;
            rigCtrl.telemetryInterval = 0.1f;
            rigCtrl.poseGoalController = goalCtrl;
            rigCtrl.queueManager = queueMgr;

            faceCtrl.InitializeReferences();

            return root;
        }

        private static void SetupFaceVisual(Transform headTransform)
        {
            Transform visual = headTransform.Find("Visual");
            if (visual == null) visual = headTransform;

            GameObject fv = new GameObject("face_visual");
            fv.transform.SetParent(visual, false);
            fv.transform.localPosition = new Vector3(0f, 0f, 0.125f);
            fv.transform.localRotation = Quaternion.identity;
            fv.transform.localScale = new Vector3(0.18f, 0.18f, 1f);

            var sr = fv.AddComponent<SpriteRenderer>();
            var idleSpr = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/idleface.png");
            if (idleSpr != null) sr.sprite = idleSpr;
        }

        private static Camera SetupDummy1Camera(Transform pelvisTransform)
        {
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            if (pelvisTransform != null)
            {
                camGo.transform.SetParent(pelvisTransform, false);
                // Exact local position and rotation added by user in SampleScene
                camGo.transform.localPosition = new Vector3(-1.75f, 0.621f, 1.64f);
                camGo.transform.localRotation = new Quaternion(0.020722548f, 0.91701233f, -0.048058536f, 0.3954104f);
            }
            else
            {
                camGo.transform.position = new Vector3(0f, 1.6f, -10f);
                camGo.transform.rotation = Quaternion.Euler(6f, 0f, 0f);
            }

            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;
            camGo.AddComponent<AudioListener>();

            return cam;
        }

        private static Camera SetupSpectatorCamera()
        {
            GameObject camGo = new GameObject("Spectator_Broadcast_Camera");
            camGo.transform.position = new Vector3(13.5f, 7.5f, 0f);
            camGo.transform.rotation = Quaternion.Euler(26f, -90f, 0f);

            Camera cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 1000f;

            return cam;
        }

        private static GameObject CreateBone(string name, Transform parent, Vector3 worldPos, Material mat)
        {
            GameObject bone = new GameObject(name);
            bone.transform.position = worldPos;
            bone.transform.SetParent(parent, true);

            Rigidbody rb = bone.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.maxAngularVelocity = 50f;

            BoneTorqueInterpreter bti = bone.AddComponent<BoneTorqueInterpreter>();
            bti.boneName = name;
            bti.kp = 25000f;
            bti.kd = 1200f;
            bti.maxTorque = 100000f;
            bti.applyTorque = true;

            return bone;
        }

        private static void SetupJoint(GameObject child, Rigidbody parentRb, Vector3 anchor)
        {
            ConfigurableJoint joint = child.AddComponent<ConfigurableJoint>();
            joint.connectedBody = parentRb;
            joint.anchor = anchor;
            joint.autoConfigureConnectedAnchor = true;

            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

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

        private static void CreateVisualBox(GameObject parent, Vector3 size, Material mat)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = "Visual";
            visual.transform.SetParent(parent.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localScale = size;
            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
            if (mat != null) visual.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateVisualSphere(GameObject parent, float radius, Material mat)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = "Visual";
            visual.transform.SetParent(parent.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localScale = Vector3.one * (radius * 2f);
            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
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

            if (direction == 0) // X
            {
                visual.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                visual.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }
            else if (direction == 1) // Y
            {
                visual.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }
            else // Z
            {
                visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                visual.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }

            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
            if (mat != null) visual.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void IgnoreAdjacent(Collider a, Collider b)
        {
            if (a != null && b != null) UnityEngine.Physics.IgnoreCollision(a, b, true);
        }

        private static Material CreateOrGetCourtMaterial(string matName, Color color, float roughness = 0.3f)
        {
            string matPath = $"Assets/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Diffuse");

                mat = new Material(shader);
                mat.color = color;
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f - roughness);
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 1f - roughness);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else
            {
                mat.color = color;
            }
            return mat;
        }

        private static PhysicMaterial CreateOrGetPhysicMaterial(string name, float bounciness, float friction)
        {
            string matPath = $"Assets/{name}.physicMaterial";
            PhysicMaterial pm = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(matPath);
            if (pm == null)
            {
                pm = new PhysicMaterial(name)
                {
                    bounciness = bounciness,
                    bounceCombine = PhysicMaterialCombine.Maximum,
                    dynamicFriction = friction,
                    staticFriction = friction,
                    frictionCombine = PhysicMaterialCombine.Average
                };
                AssetDatabase.CreateAsset(pm, matPath);
            }
            return pm;
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var s in scenes)
            {
                if (s.path == scenePath) return;
            }
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
