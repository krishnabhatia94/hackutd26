using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SpacetimeDB;
using SpacetimeDB.Types;
using Physics;
using UI;

namespace Physics
{
    /// <summary>
    /// Handles multiplayer client connection and dynamic ragdoll spawning via SpacetimeDB:
    /// - The scene starts completely bare with zero pre-placed dummies.
    /// - Whenever a player joins, they are automatically assigned to the Blue team first,
    ///   or the Red team if the Red team player count < Blue team player count.
    /// - Spawns an articulated 12-bone humanoid ragdoll for each connected player.
    /// - Sets the user's camera to follow their local ragdoll.
    /// - Connects the on-screen PlayerMotionControllerUI to the local ragdoll.
    /// - Synchronizes remote players' bone states and telemetry across the network.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Physics/Spacetime Network Spawner")]
    public class SpacetimeNetworkSpawner : MonoBehaviour
    {
        [Header("SpacetimeDB Connection")]
        public string serverUrl = "https://maincloud.spacetimedb.com";
        public string databaseName = "knet";

        [Header("Local Player Telemetry")]
        [Tooltip("Allocated NPC ID for the local client (1, 2, 3...).")]
        public uint localNpcId = 0;

        [Tooltip("True once connected and registered with SpacetimeDB.")]
        public bool isRegistered = false;

        [Header("Spawn Templates & Materials")]
        public GameObject blueRagdollTemplate;
        public GameObject redRagdollTemplate;
        public Material blueTorsoMat;
        public Material redTorsoMat;

        [Header("Cameras")]
        public Camera playerFollowCamera;
        public Camera spectatorCamera;

        [Header("UI Controller")]
        public PlayerMotionControllerUI playerUI;

        private DbConnection _conn;
        private SpacetimeDB.Identity _myIdentity;
        private readonly Dictionary<uint, GameObject> _spawnedRagdolls = new();
        private bool _isConnecting = false;

        public static SpacetimeNetworkSpawner Instance { get; private set; }

        public GameObject GetSpawnedRagdoll(uint npcId)
        {
            _spawnedRagdolls.TryGetValue(npcId, out GameObject rig);
            return rig;
        }

        private bool _hasTriggeredFallback = false;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            PreserveAndSetupCamera();
            CleanLegacyDummies();
            FindTemplateReferences();
            FindUIReferences();
        }

        private void PreserveAndSetupCamera()
        {
            // Unparent any camera attached inside legacy dummies BEFORE destroying them!
            var allDummies = new[] { "HumanoidRig_Team1", "HumanoidRig_Team2", "HumanoidRig", "HumanoidRig (1)" };
            foreach (var dummyName in allDummies)
            {
                var dGo = GameObject.Find(dummyName);
                if (dGo != null)
                {
                    var cams = dGo.GetComponentsInChildren<Camera>(true);
                    foreach (var c in cams)
                    {
                        c.transform.SetParent(null, true);
                        c.name = "Main Camera";
                        c.tag = "MainCamera";
                        c.enabled = true;
                        playerFollowCamera = c;
                    }
                }
            }

            if (playerFollowCamera == null)
            {
                var camGo = GameObject.Find("Main Camera");
                if (camGo != null) playerFollowCamera = camGo.GetComponent<Camera>();
                if (playerFollowCamera == null) playerFollowCamera = Camera.main;
            }

            if (spectatorCamera == null)
            {
                var specGo = GameObject.Find("Spectator_Broadcast_Camera");
                if (specGo == null) specGo = GameObject.Find("SpectatorCamera");
                if (specGo != null) spectatorCamera = specGo.GetComponent<Camera>();
            }

            if (playerFollowCamera == null)
            {
                if (spectatorCamera != null)
                {
                    playerFollowCamera = spectatorCamera;
                    playerFollowCamera.tag = "MainCamera";
                    playerFollowCamera.enabled = true;
                }
                else
                {
                    GameObject camGo = new GameObject("Main Camera");
                    camGo.tag = "MainCamera";
                    camGo.transform.position = new Vector3(0f, 8f, -14f);
                    camGo.transform.rotation = Quaternion.Euler(24f, 0f, 0f);
                    playerFollowCamera = camGo.AddComponent<Camera>();
                    playerFollowCamera.fieldOfView = 60f;
                    playerFollowCamera.nearClipPlane = 0.3f;
                    playerFollowCamera.farClipPlane = 1000f;
                    camGo.AddComponent<AudioListener>();
                    playerFollowCamera.enabled = true;
                }
            }
            else
            {
                playerFollowCamera.enabled = true;
                if (playerFollowCamera.transform.parent == null)
                {
                    playerFollowCamera.transform.position = new Vector3(0f, 8f, -14f);
                    playerFollowCamera.transform.rotation = Quaternion.Euler(24f, 0f, 0f);
                }
            }
        }

        private void CleanLegacyDummies()
        {
            var legacy1 = GameObject.Find("HumanoidRig_Team1");
            if (legacy1 != null) DestroyImmediate(legacy1);
            var legacy2 = GameObject.Find("HumanoidRig_Team2");
            if (legacy2 != null) DestroyImmediate(legacy2);
        }

        private void Start()
        {
            ConnectToSpacetimeDB();
        }

        private void Update()
        {
            // Crucial: Pump SpacetimeDB connection frame tick so WebSocket events and callbacks fire!
            if (_conn != null)
            {
                _conn.FrameTick();
            }

            // Fallback timer: If SpacetimeDB hasn't registered within 1.5 seconds, spawn local player ragdoll immediately
            if (!isRegistered && Time.timeSinceLevelLoad > 1.5f && !_hasTriggeredFallback)
            {
                _hasTriggeredFallback = true;
                RegisterLocalPlayerFallback();
            }
        }

        private void RegisterLocalPlayerFallback()
        {
            if (isRegistered) return;
            localNpcId = 1;
            isRegistered = true;
            Debug.Log($"[SpacetimeNetworkSpawner] Spawning local humanoid ragdoll NPC {localNpcId} (Team Blue)...");

            GameObject myRagdoll = EnsureRagdollSpawned(localNpcId, isLocal: true);
            if (playerUI != null && myRagdoll != null)
            {
                playerUI.SetTargetRagdoll(myRagdoll, localNpcId);
            }
            SetupPlayerCamera(myRagdoll);
        }

        private void OnDestroy()
        {
            if (_conn != null)
            {
                try { _conn.Disconnect(); } catch {}
                _conn = null;
            }
        }

        public void FindTemplateReferences()
        {
            if (blueRagdollTemplate == null) blueRagdollTemplate = GameObject.Find("HumanoidRig_Team1");
            if (blueRagdollTemplate == null) blueRagdollTemplate = GameObject.Find("HumanoidRig");

            if (redRagdollTemplate == null) redRagdollTemplate = GameObject.Find("HumanoidRig_Team2");
            if (redRagdollTemplate == null) redRagdollTemplate = GameObject.Find("HumanoidRig (1)");

            if (playerFollowCamera == null)
            {
                var camGo = GameObject.Find("Main Camera");
                if (camGo != null) playerFollowCamera = camGo.GetComponent<Camera>();
                if (playerFollowCamera == null) playerFollowCamera = Camera.main;
            }

            if (spectatorCamera == null)
            {
                var specGo = GameObject.Find("Spectator_Broadcast_Camera");
                if (specGo == null) specGo = GameObject.Find("SpectatorCamera");
                if (specGo != null) spectatorCamera = specGo.GetComponent<Camera>();
            }

            if (blueRagdollTemplate != null) _spawnedRagdolls[1] = blueRagdollTemplate;
            if (redRagdollTemplate != null) _spawnedRagdolls[2] = redRagdollTemplate;
        }

        public void FindUIReferences()
        {
            if (playerUI == null)
            {
                playerUI = FindObjectOfType<PlayerMotionControllerUI>();
                if (playerUI == null)
                {
                    GameObject uiGo = new GameObject("PlayerMotionControllerUI");
                    playerUI = uiGo.AddComponent<PlayerMotionControllerUI>();
                }
            }
        }

        public void ConnectToSpacetimeDB()
        {
            if (_isConnecting || (_conn != null && isRegistered)) return;
            _isConnecting = true;

            Debug.Log($"[SpacetimeNetworkSpawner] Connecting to SpacetimeDB '{databaseName}' at {serverUrl}...");

            try
            {
                _conn = DbConnection.Builder()
                    .WithUri(serverUrl)
                    .WithDatabaseName(databaseName)
                    .OnConnect(OnConnected)
                    .OnConnectError(OnConnectError)
                    .OnDisconnect(OnDisconnected)
                    .Build();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SpacetimeNetworkSpawner] Connection failed: {ex.Message}");
                _isConnecting = false;
                StartCoroutine(AutoReconnectCoroutine());
            }
        }

        private void OnConnected(DbConnection conn, SpacetimeDB.Identity identity, string authToken)
        {
            _myIdentity = identity;
            _isConnecting = false;
            Debug.Log($"[SpacetimeNetworkSpawner] Connected to SpacetimeDB! Identity: {identity}");

            // Subscribe to all tables
            conn.SubscriptionBuilder()
                .OnApplied(OnSubscriptionApplied)
                .OnError((ctx, err) => Debug.LogError($"[SpacetimeNetworkSpawner] Subscription error: {err}"))
                .SubscribeToAllTables();

            // Listen to BodyTelemetry for newly joined competitors
            conn.Db.BodyTelemetry.OnInsert += OnBodyTelemetryInserted;
            conn.Db.BodyTelemetry.OnUpdate += OnBodyTelemetryUpdated;
        }

        private void OnSubscriptionApplied(SubscriptionEventContext ctx)
        {
            Debug.Log("[SpacetimeNetworkSpawner] Subscription applied! Registering local player presence...");
            RegisterLocalPlayer();
        }

        private void RegisterLocalPlayer()
        {
            if (_conn == null) return;

            if (_hasTriggeredFallback && isRegistered)
            {
                string t = (localNpcId % 2 != 0) ? "Blue" : "Red";
                try
                {
                    _conn.Reducers.UpdateTelemetry(
                        localNpcId,
                        1.05f,
                        0f,
                        false,
                        $"Online|Team:{t}|Identity:{_myIdentity}"
                    );
                }
                catch {}
                return;
            }

            // Count active players on Blue and Red teams
            // User rule: automatically assigned to the blue team first, or the red team if redTeamCount < blueTeamCount.
            int blueTeamCount = 0;
            int redTeamCount = 0;
            var activeIds = new HashSet<uint>();

            if (_conn.Db.BodyTelemetry != null)
            {
                foreach (var row in _conn.Db.BodyTelemetry.Iter())
                {
                    activeIds.Add(row.NpcId);
                    if (row.NpcId % 2 != 0)
                    {
                        blueTeamCount++;
                    }
                    else
                    {
                        redTeamCount++;
                    }
                }
            }

            // Balanced team assignment: Red if redCount < blueCount, else Blue first
            bool assignToRed = (redTeamCount < blueTeamCount);
            uint chosenNpcId;

            if (assignToRed)
            {
                // Red team: lowest available even number (2, 4, 6, 8...)
                uint candidate = 2;
                while (activeIds.Contains(candidate))
                {
                    candidate += 2;
                }
                chosenNpcId = candidate;
            }
            else
            {
                // Blue team: lowest available odd number (1, 3, 5, 7...)
                uint candidate = 1;
                while (activeIds.Contains(candidate))
                {
                    candidate += 2;
                }
                chosenNpcId = candidate;
            }

            localNpcId = chosenNpcId;
            isRegistered = true;

            string team = (localNpcId % 2 != 0) ? "Blue" : "Red";
            Debug.Log($"[SpacetimeNetworkSpawner] Local player registered as NPC {localNpcId} on Team {team}! (Counts: Blue={blueTeamCount}, Red={redTeamCount})");

            // Spawn or configure the local player's ragdoll
            GameObject myRagdoll = EnsureRagdollSpawned(localNpcId, isLocal: true);

            // Bind UI to local ragdoll
            if (playerUI != null && myRagdoll != null)
            {
                playerUI.SetTargetRagdoll(myRagdoll, localNpcId);
            }

            // Attach follow camera to local ragdoll pelvis
            SetupPlayerCamera(myRagdoll);

            // Announce presence to SpacetimeDB
            try
            {
                _conn.Reducers.UpdateTelemetry(
                    localNpcId,
                    1.05f,
                    0f,
                    false,
                    $"Online|Team:{team}|Identity:{_myIdentity}"
                );
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SpacetimeNetworkSpawner] Failed to report initial presence: {ex.Message}");
            }

            // Synchronize any existing remote players that aren't yet in the scene
            foreach (uint id in activeIds)
            {
                if (id != localNpcId)
                {
                    EnsureRagdollSpawned(id, isLocal: false);
                }
            }
        }

        private void OnBodyTelemetryInserted(EventContext ctx, BodyTelemetry row)
        {
            if (row.NpcId != localNpcId)
            {
                Debug.Log($"[SpacetimeNetworkSpawner] Discovered player: NPC {row.NpcId} ({row.ActiveContact})");
                EnsureRagdollSpawned(row.NpcId, isLocal: false);
            }
        }

        private void OnBodyTelemetryUpdated(EventContext ctx, BodyTelemetry oldRow, BodyTelemetry newRow)
        {
            if (newRow.NpcId != localNpcId && !_spawnedRagdolls.ContainsKey(newRow.NpcId))
            {
                EnsureRagdollSpawned(newRow.NpcId, isLocal: false);
            }
        }

        /// <summary>
        /// Ensures a humanoid ragdoll exists for the given npcId.
        /// If not already spawned, creates it dynamically at the team's baseline position.
        /// </summary>
        public GameObject EnsureRagdollSpawned(uint npcId, bool isLocal)
        {
            if (_spawnedRagdolls.TryGetValue(npcId, out GameObject existing) && existing != null)
            {
                ConfigureRagdoll(existing, npcId, isLocal);
                return existing;
            }

            bool isTeamBlue = (npcId % 2 != 0);

            // Calculate team baseline spawn position
            // Team Blue (South): z = -8m, spread along X: 0, -2.8, +2.8, -5.6, +5.6...
            // Team Red (North):  z = +8m, spread along X: 0, +2.8, -2.8, +5.6, -5.6...
            int teamSlot = (int)((npcId - 1) / 2);
            float xOffset = 0f;
            if (teamSlot > 0)
            {
                int magnitude = (teamSlot + 1) / 2;
                int sign = (teamSlot % 2 != 0) ? -1 : 1;
                xOffset = sign * magnitude * 2.8f;
            }

            Vector3 spawnPos = isTeamBlue ? new Vector3(xOffset, 0f, -8f) : new Vector3(xOffset, 0f, 8f);
            Quaternion spawnRot = isTeamBlue ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);

            GameObject newRig = null;
            GameObject template = isTeamBlue ? blueRagdollTemplate : redRagdollTemplate;

            if (template != null)
            {
                newRig = Instantiate(template, spawnPos, spawnRot);
                newRig.name = $"HumanoidRig_NPC{npcId}_{(isTeamBlue ? "Blue" : "Red")}";
                newRig.transform.position = spawnPos;
                newRig.transform.rotation = spawnRot;
                var pelvis = newRig.transform.Find("Pelvis");
                if (pelvis != null)
                {
                    pelvis.position = spawnPos + Vector3.up * 1.05f;
                    pelvis.rotation = spawnRot;
                }
            }
            else
            {
                // Procedural runtime factory instantiation
                Material limbMat = isTeamBlue ? blueTorsoMat : redTorsoMat;
                newRig = HumanoidRigFactory.CreateHumanoidCompetitor(
                    $"HumanoidRig_NPC{npcId}_{(isTeamBlue ? "Blue" : "Red")}",
                    spawnPos,
                    spawnRot,
                    limbMat,
                    npcId,
                    serverUrl,
                    databaseName
                );
            }

            ConfigureRagdoll(newRig, npcId, isLocal);
            _spawnedRagdolls[npcId] = newRig;

            UpdateOpponentCrossTargeting();

            Debug.Log($"[SpacetimeNetworkSpawner] Humanoid Ragdoll spawned for NPC {npcId} at {spawnPos} (Team: {(isTeamBlue ? "Blue" : "Red")}, isLocal: {isLocal})!");
            return newRig;
        }

        private void ConfigureRagdoll(GameObject rig, uint npcId, bool isLocal)
        {
            var rigCtrl = rig.GetComponent<SpacetimeRigController>();
            if (rigCtrl == null) rigCtrl = rig.AddComponent<SpacetimeRigController>();
            rigCtrl.npcId = npcId;
            rigCtrl.serverUrl = serverUrl;
            rigCtrl.databaseName = databaseName;

            var queue = rig.GetComponent<DynamicPoseQueueManager>();
            if (queue == null) queue = rig.AddComponent<DynamicPoseQueueManager>();

            var exec = rig.GetComponent<RagdollExecutiveAgent>();
            if (exec == null) exec = rig.AddComponent<RagdollExecutiveAgent>();
            exec.npcId = npcId;

            var pickup = rig.GetComponent<ObjectPickupController>();
            if (pickup == null) pickup = rig.AddComponent<ObjectPickupController>();

            var face = rig.GetComponent<RagdollFaceController>();
            if (face == null) face = rig.AddComponent<RagdollFaceController>();

            // Apply Team Torso Color
            bool isBlue = (npcId % 2 != 0);
            var chest = rig.transform.Find("Pelvis/Spine/Chest");
            if (chest != null)
            {
                var rend = chest.GetComponent<Renderer>();
                if (rend != null)
                {
                    Material teamMat = isBlue ? blueTorsoMat : redTorsoMat;
                    if (teamMat != null)
                    {
                        rend.material = teamMat;
                    }
                    else
                    {
                        rend.material.color = isBlue ? new Color(0.2f, 0.48f, 0.88f) : new Color(0.88f, 0.28f, 0.22f);
                    }
                }
            }
        }

        private void UpdateOpponentCrossTargeting()
        {
            foreach (var kvp in _spawnedRagdolls)
            {
                uint id = kvp.Key;
                GameObject rig = kvp.Value;
                if (rig == null) continue;

                bool isBlue = (id % 2 != 0);
                var pickup = rig.GetComponent<ObjectPickupController>();
                var exec = rig.GetComponent<RagdollExecutiveAgent>();

                // Find closest competitor on opposing team
                GameObject bestOpponent = null;
                float bestDist = float.MaxValue;

                foreach (var oppKvp in _spawnedRagdolls)
                {
                    uint oppId = oppKvp.Key;
                    GameObject oppRig = oppKvp.Value;
                    if (oppRig == null) continue;

                    bool oppIsBlue = (oppId % 2 != 0);
                    if (isBlue != oppIsBlue)
                    {
                        float d = Vector3.Distance(rig.transform.position, oppRig.transform.position);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            bestOpponent = oppRig;
                        }
                    }
                }

                if (bestOpponent != null)
                {
                    if (exec != null) exec.opponentRig = bestOpponent;
                    if (pickup != null)
                    {
                        var chest = bestOpponent.transform.Find("Pelvis/Spine/Chest");
                        pickup.opponentTarget = (chest != null) ? chest : bestOpponent.transform;
                    }
                }
            }
        }

        private void SetupPlayerCamera(GameObject targetRagdoll)
        {
            if (targetRagdoll == null) return;
            Transform pelvis = targetRagdoll.transform.Find("Pelvis");
            if (pelvis == null) pelvis = targetRagdoll.transform;

            if (playerFollowCamera == null)
            {
                playerFollowCamera = Camera.main;
            }

            if (playerFollowCamera != null)
            {
                playerFollowCamera.transform.SetParent(pelvis, false);
                playerFollowCamera.transform.localPosition = new Vector3(0f, 1.25f, -3.2f);
                playerFollowCamera.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
                playerFollowCamera.enabled = true;
            }

            if (spectatorCamera != null)
            {
                spectatorCamera.enabled = false;
            }
        }

        private void OnConnectError(Exception err)
        {
            Debug.LogError($"[SpacetimeNetworkSpawner] Connect error: {err.Message}");
            _isConnecting = false;
            StartCoroutine(AutoReconnectCoroutine());
        }

        private void OnDisconnected(DbConnection conn, Exception err)
        {
            Debug.LogWarning($"[SpacetimeNetworkSpawner] Disconnected from SpacetimeDB: {err?.Message}");
            isRegistered = false;
            _isConnecting = false;
            StartCoroutine(AutoReconnectCoroutine());
        }

        private IEnumerator AutoReconnectCoroutine()
        {
            yield return new WaitForSeconds(3.0f);
            if (!isRegistered && !_isConnecting)
            {
                ConnectToSpacetimeDB();
            }
        }
    }
}
