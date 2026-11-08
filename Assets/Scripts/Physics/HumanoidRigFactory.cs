using System;
using System.Collections.Generic;
using UnityEngine;
using Physics;

namespace Physics
{
    /// <summary>
    /// Runtime factory that procedurally builds articulated 12-bone humanoid ragdoll competitors
    /// on demand across WebGL and Standalone builds with zero pre-existing scene requirements.
    /// </summary>
    public static class HumanoidRigFactory
    {
        public static GameObject CreateHumanoidCompetitor(string rigName, Vector3 spawnPos, Quaternion spawnRot, Material limbMat, uint npcId, string serverUrl = "https://maincloud.spacetimedb.com", string dbName = "knet")
        {
            GameObject root = new GameObject(rigName);
            root.transform.position = spawnPos;
            root.transform.rotation = spawnRot;

            if (limbMat == null)
            {
                limbMat = new Material(Shader.Find("Standard"));
                bool isBlue = (npcId % 2 != 0);
                limbMat.color = isBlue ? new Color(0.20f, 0.48f, 0.88f) : new Color(0.88f, 0.28f, 0.22f);
            }

            // 1. Pelvis (Root Bone)
            GameObject pelvis = CreateBone("Pelvis", root.transform, spawnPos + spawnRot * new Vector3(0f, 1.05f, 0f), limbMat);
            BoxCollider pelvisCol = pelvis.AddComponent<BoxCollider>();
            pelvisCol.size = new Vector3(0.28f, 0.18f, 0.22f);
            Rigidbody pelvisRb = pelvis.GetComponent<Rigidbody>();
            pelvisRb.mass = 12f;

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
            lFaCol.radius = 0.045f;
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
            rFaCol.radius = 0.045f;
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
            IgnoreCollision(pelvisCol, spineCol);
            IgnoreCollision(spineCol, chestCol);
            IgnoreCollision(chestCol, headCol);
            IgnoreCollision(chestCol, lUaCol);
            IgnoreCollision(lUaCol, lFaCol);
            IgnoreCollision(chestCol, rUaCol);
            IgnoreCollision(rUaCol, rFaCol);
            IgnoreCollision(pelvisCol, lThighCol);
            IgnoreCollision(lThighCol, lCalfCol);
            IgnoreCollision(pelvisCol, rThighCol);
            IgnoreCollision(rThighCol, rCalfCol);

            // Attach Rig Controllers
            var goalCtrl = root.AddComponent<RagdollPoseGoalController>();
            var queueMgr = root.AddComponent<DynamicPoseQueueManager>();
            var pickupCtrl = root.AddComponent<ObjectPickupController>();
            var faceCtrl = root.AddComponent<RagdollFaceController>();
            var execAgent = root.AddComponent<RagdollExecutiveAgent>();
            var rigCtrl = root.AddComponent<SpacetimeRigController>();

            rigCtrl.serverUrl = serverUrl;
            rigCtrl.databaseName = dbName;
            rigCtrl.npcId = npcId;
            rigCtrl.telemetryInterval = 0.1f;
            rigCtrl.poseGoalController = goalCtrl;
            rigCtrl.queueManager = queueMgr;

            execAgent.npcId = npcId;
            faceCtrl.InitializeReferences();

            return root;
        }

        private static GameObject CreateBone(string name, Transform parent, Vector3 pos, Material mat)
        {
            GameObject bone = new GameObject(name);
            bone.transform.position = pos;
            bone.transform.SetParent(parent, true);

            Rigidbody rb = bone.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var bti = bone.AddComponent<BoneTorqueInterpreter>();
            bti.boneName = name;
            return bone;
        }

        private static void SetupJoint(GameObject bone, Rigidbody connectedBody, Vector3 anchor)
        {
            ConfigurableJoint joint = bone.AddComponent<ConfigurableJoint>();
            joint.connectedBody = connectedBody;
            joint.anchor = anchor;
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
                positionDamper = 1500f,
                maximumForce = 100000f
            };
            joint.projectionMode = JointProjectionMode.PositionAndRotation;
            joint.enablePreprocessing = false;
        }

        private static void CreateVisualBox(GameObject parent, Vector3 size, Material mat)
        {
            GameObject vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vis.name = "Visual";
            vis.transform.SetParent(parent.transform, false);
            vis.transform.localScale = size;
            UnityEngine.Object.Destroy(vis.GetComponent<Collider>());
            vis.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateVisualSphere(GameObject parent, float radius, Material mat)
        {
            GameObject vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "Visual";
            vis.transform.SetParent(parent.transform, false);
            vis.transform.localScale = Vector3.one * (radius * 2f);
            UnityEngine.Object.Destroy(vis.GetComponent<Collider>());
            vis.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateVisualCapsule(GameObject parent, float radius, float height, int direction, Material mat)
        {
            GameObject vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.name = "Visual";
            vis.transform.SetParent(parent.transform, false);
            vis.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            UnityEngine.Object.Destroy(vis.GetComponent<Collider>());
            vis.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateVisualOffsetCapsule(GameObject parent, Vector3 center, float radius, float height, int direction, Material mat)
        {
            GameObject vis = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            vis.name = "Visual";
            vis.transform.SetParent(parent.transform, false);
            vis.transform.localPosition = center;

            if (direction == 0) // X
            {
                vis.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                vis.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }
            else // Y
            {
                vis.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            }

            UnityEngine.Object.Destroy(vis.GetComponent<Collider>());
            vis.GetComponent<Renderer>().sharedMaterial = mat;
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
            var idleSpr = Resources.Load<Sprite>("idleface");
            if (idleSpr != null) sr.sprite = idleSpr;
        }

        private static void IgnoreCollision(Collider a, Collider b)
        {
            if (a != null && b != null)
            {
                UnityEngine.Physics.IgnoreCollision(a, b, true);
            }
        }
    }
}

