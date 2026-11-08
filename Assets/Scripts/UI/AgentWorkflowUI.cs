using System;
using UnityEngine;
using UnityEngine.UI;
using Physics;
using SpacetimeDB.Types;

namespace UI
{
    /// <summary>
    /// Live UI Overlay displaying SpacetimeDB AgentWorkflowStep table
    /// (what Nemotron is planning and why) and providing a 'Shove NPC / Spawn Obstacle'
    /// button that triggers the SpacetimeDB ShoveNpc reducer.
    /// </summary>
    [AddComponentMenu("UI/Agent Workflow UI")]
    public class AgentWorkflowUI : MonoBehaviour
    {
        [Header("References")]
        public SpacetimeRigController rigController;

        [Header("UI Elements (Auto-Created if empty)")]
        public Text goalText;
        public Text subtaskText;
        public Text statusText;
        public Text telemetryText;
        public Button shoveButton;

        private void Awake()
        {
            if (rigController == null)
            {
                rigController = FindObjectOfType<SpacetimeRigController>();
            }

            EnsureUIHierarchy();
        }

        private void Start()
        {
            if (rigController != null)
            {
                rigController.OnWorkflowUpdated += HandleWorkflowUpdated;
            }

            if (shoveButton != null)
            {
                shoveButton.onClick.AddListener(OnShoveButtonClicked);
            }
        }

        private void OnDestroy()
        {
            if (rigController != null)
            {
                rigController.OnWorkflowUpdated -= HandleWorkflowUpdated;
            }
        }

        private void Update()
        {
            if (rigController == null) return;

            // Refresh text displays continuously
            if (goalText != null)
            {
                goalText.text = $"<b>Goal:</b> {rigController.LatestGoal}";
            }

            if (subtaskText != null)
            {
                subtaskText.text = $"<b>Subtask:</b> {rigController.LatestSubtask}";
            }

            if (statusText != null)
            {
                string statusColor = rigController.LatestStatus == "In-Progress" ? "#FFA500" : "#00FF66";
                statusText.text = $"<b>Status:</b> <color={statusColor}>[{rigController.LatestStatus}]</color> (Step #{rigController.LatestStepId})";
            }

            if (telemetryText != null)
            {
                string fallColor = rigController.CurrentIsFallen ? "#FF3333" : (rigController.CurrentTiltAngle > 30f ? "#FFAA00" : "#55FF55");
                telemetryText.text = $"<b>COM Height:</b> {rigController.CurrentComHeight:F2}m  |  " +
                                     $"<b>Tilt:</b> <color={fallColor}>{rigController.CurrentTiltAngle:F1}°</color>  |  " +
                                     $"<b>Contact:</b> {rigController.CurrentActiveContact}";
            }
        }

        private void HandleWorkflowUpdated(AgentWorkflowStep step)
        {
            Debug.Log($"[AgentWorkflowUI] Received workflow update: Step {step.StepId} - {step.Goal}");
        }

        public void OnShoveButtonClicked()
        {
            Debug.Log("[AgentWorkflowUI] Shove button clicked!");
            if (rigController != null)
            {
                // Deliver random directional shove into the chest
                float fx = UnityEngine.Random.Range(-120f, 120f);
                float fy = UnityEngine.Random.Range(20f, 40f);
                float fz = UnityEngine.Random.Range(-160f, -60f); // Shove backwards

                rigController.TriggerShove(fx, fy, fz);
            }
        }

        private void EnsureUIHierarchy()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                GameObject canvasGo = new GameObject("WorkflowCanvas");
                canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvasGo.AddComponent<CanvasScaler>();
                canvasGo.AddComponent<GraphicRaycaster>();
                transform.SetParent(canvasGo.transform, false);

                // Add EventSystem if not present
                if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
                {
                    GameObject es = new GameObject("EventSystem");
                    es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                }
            }

            if (goalText == null || shoveButton == null)
            {
                BuildHUD(canvas.gameObject);
            }
        }

        private void BuildHUD(GameObject parent)
        {
            Font defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (defaultFont == null)
            {
                defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            // Main Panel
            GameObject panel = new GameObject("HUD_Panel");
            panel.transform.SetParent(parent.transform, false);
            RectTransform panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(20f, -20f);
            panelRect.sizeDelta = new Vector2(480f, 245f);

            Image panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.1f, 0.15f, 0.88f);

            // Title
            GameObject titleGo = CreateTextObject(panel, "Title", "NVIDIA NEMOTRON + SPACETIMEDB HUD", defaultFont, 16, FontStyle.Bold, Color.cyan);
            RectTransform titleRect = titleGo.GetComponent<RectTransform>();
            titleRect.anchoredPosition = new Vector2(15f, -15f);
            titleRect.sizeDelta = new Vector2(450f, 25f);

            // Goal Text
            GameObject goalGo = CreateTextObject(panel, "GoalText", "Goal: Stand by / Awaiting Nemotron", defaultFont, 13, FontStyle.Normal, Color.white);
            RectTransform gRect = goalGo.GetComponent<RectTransform>();
            gRect.anchoredPosition = new Vector2(15f, -45f);
            gRect.sizeDelta = new Vector2(450f, 35f);
            goalText = goalGo.GetComponent<Text>();

            // Subtask Text
            GameObject subtaskGo = CreateTextObject(panel, "SubtaskText", "Subtask: Idle", defaultFont, 13, FontStyle.Normal, new Color(0.9f, 0.9f, 0.9f));
            RectTransform sRect = subtaskGo.GetComponent<RectTransform>();
            sRect.anchoredPosition = new Vector2(15f, -85f);
            sRect.sizeDelta = new Vector2(450f, 30f);
            subtaskText = subtaskGo.GetComponent<Text>();

            // Status Text
            GameObject statusGo = CreateTextObject(panel, "StatusText", "Status: Ready", defaultFont, 13, FontStyle.Normal, Color.yellow);
            RectTransform stRect = statusGo.GetComponent<RectTransform>();
            stRect.anchoredPosition = new Vector2(15f, -120f);
            stRect.sizeDelta = new Vector2(450f, 25f);
            statusText = statusGo.GetComponent<Text>();

            // Telemetry Text
            GameObject telemGo = CreateTextObject(panel, "TelemetryText", "COM: 0.80m | Tilt: 0.0° | Contact: BothFeet", defaultFont, 12, FontStyle.Normal, Color.white);
            RectTransform tRect = telemGo.GetComponent<RectTransform>();
            tRect.anchoredPosition = new Vector2(15f, -150f);
            tRect.sizeDelta = new Vector2(450f, 25f);
            telemetryText = telemGo.GetComponent<Text>();

            // Shove Button
            GameObject btnGo = new GameObject("ShoveButton");
            btnGo.transform.SetParent(panel.transform, false);
            RectTransform btnRect = btnGo.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0f, 0f);
            btnRect.anchorMax = new Vector2(1f, 0f);
            btnRect.pivot = new Vector2(0.5f, 0f);
            btnRect.anchoredPosition = new Vector2(0f, 15f);
            btnRect.sizeDelta = new Vector2(-30f, 45f);

            Image btnImg = btnGo.AddComponent<Image>();
            btnImg.color = new Color(0.85f, 0.22f, 0.2f, 1f);

            shoveButton = btnGo.AddComponent<Button>();
            ColorBlock cb = shoveButton.colors;
            cb.highlightedColor = new Color(1f, 0.35f, 0.3f, 1f);
            cb.pressedColor = new Color(0.65f, 0.15f, 0.12f, 1f);
            shoveButton.colors = cb;

            GameObject btnTextGo = CreateTextObject(btnGo, "BtnText", "SHOOT PROJECTILE AT DUMMY", defaultFont, 14, FontStyle.Bold, Color.white);
            RectTransform btRect = btnTextGo.GetComponent<RectTransform>();
            btRect.anchorMin = Vector2.zero;
            btRect.anchorMax = Vector2.one;
            btRect.sizeDelta = Vector2.zero;
            Text btnTxt = btnTextGo.GetComponent<Text>();
            btnTxt.alignment = TextAnchor.MiddleCenter;
        }

        private GameObject CreateTextObject(GameObject parent, string name, string text, Font font, int fontSize, FontStyle style, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);

            Text txt = go.AddComponent<Text>();
            txt.text = text;
            txt.font = font;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.supportRichText = true;
            return go;
        }
    }
}

