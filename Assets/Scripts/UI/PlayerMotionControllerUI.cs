using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Physics;

namespace UI
{
    /// <summary>
    /// User interface for controlling the local humanoid ragdoll competitor:
    /// 1. Text Entry Box: Allows entering custom motion sequence prompts (max 80 characters),
    ///    which are dispatched to Nemotron / agent.py to generate physical poses.
    /// 2. Three Quick Action Buttons directly below the text entry:
    ///    - "Go to nearest dodgeball"
    ///    - "Pick up dodgeball"
    ///    - "Throw dodgeball"
    /// 3. "Automate Ragdoll" Toggle:
    ///    - When toggled ON: Activates the autonomous executive agent loop and disables the
    ///      three action buttons and prompt input box.
    ///    - When toggled OFF: Deactivates autonomous mode and re-enables the manual inputs.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UI/Player Motion Controller UI")]
    public class PlayerMotionControllerUI : MonoBehaviour
    {
        [Header("Target Ragdoll Binding")]
        [Tooltip("The local player's ragdoll competitor.")]
        public GameObject localRagdoll;

        [Tooltip("Local player NPC identifier.")]
        public uint localNpcId = 1;

        [Header("UI Element References (Auto-Created if empty)")]
        public InputField promptInputField;
        public Button executeButton;
        public Text charCountText;
        public Button btnApproach;
        public Button btnPickup;
        public Button btnThrow;
        public Toggle automateToggle;
        public Text statusFeedbackText;
        public Text headerText;

        private SpacetimeRigController _rigController;
        private RagdollExecutiveAgent _executiveAgent;
        private DynamicPoseQueueManager _queueManager;
        private ObjectPickupController _pickupController;

        private Canvas _parentCanvas;
        private bool _isInitialized = false;

        public static PlayerMotionControllerUI Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            EnsureBindings();
            EnsureUIHierarchy();
        }

        private void Start()
        {
            EnsureBindings();
            RegisterEvents();
            UpdateUIState(automateToggle != null && automateToggle.isOn);
        }

        private void Update()
        {
            // Continuously update status feedback from ragdoll
            if (statusFeedbackText != null)
            {
                if (automateToggle != null && automateToggle.isOn)
                {
                    string execState = _executiveAgent != null ? _executiveAgent.currentState.ToString() : "Automating";
                    string execGoal = _executiveAgent != null ? _executiveAgent.currentActionGoal : "";
                    statusFeedbackText.text = $"<color=#00FFFF>AUTO EXECUTIVE:</color> {execState} <i>({execGoal})</i>";
                }
                else
                {
                    if (_queueManager != null && _queueManager.isExecuting)
                    {
                        statusFeedbackText.text = $"<color=#FFA500>EXECUTING:</color> {_queueManager.activeActionName}";
                    }
                    else
                    {
                        statusFeedbackText.text = "<color=#00FF66>READY:</color> Enter motion prompt or press action button";
                    }
                }
            }

            // Keyboard shortcut Enter to submit prompt if field is focused
            if (promptInputField != null && promptInputField.isFocused && Input.GetKeyDown(KeyCode.Return))
            {
                OnSubmitPrompt();
            }
        }

        public void SetTargetRagdoll(GameObject ragdoll, uint npcId)
        {
            localRagdoll = ragdoll;
            localNpcId = npcId;
            EnsureBindings();
            if (headerText != null)
            {
                string teamName = (npcId % 2 != 0) ? "TEAM BLUE" : "TEAM RED";
                headerText.text = $"PLAYER CONTROLLER [NPC {npcId} - {teamName}]";
            }
        }

        public void EnsureBindings()
        {
            if (localRagdoll == null)
            {
                // Find local ragdoll by npcId or fallback to Team 1
                var allRigs = FindObjectsOfType<SpacetimeRigController>();
                foreach (var rig in allRigs)
                {
                    if (rig.npcId == localNpcId)
                    {
                        localRagdoll = rig.gameObject;
                        break;
                    }
                }

                if (localRagdoll == null && allRigs.Length > 0)
                {
                    localRagdoll = allRigs[0].gameObject;
                    localNpcId = allRigs[0].npcId;
                }
            }

            if (localRagdoll != null)
            {
                _rigController = localRagdoll.GetComponent<SpacetimeRigController>();
                _executiveAgent = localRagdoll.GetComponent<RagdollExecutiveAgent>();
                _queueManager = localRagdoll.GetComponent<DynamicPoseQueueManager>();
                _pickupController = localRagdoll.GetComponent<ObjectPickupController>();
            }
        }

        private void RegisterEvents()
        {
            if (executeButton != null)
            {
                executeButton.onClick.RemoveAllListeners();
                executeButton.onClick.AddListener(OnSubmitPrompt);
            }

            if (promptInputField != null)
            {
                promptInputField.onValueChanged.RemoveAllListeners();
                promptInputField.onValueChanged.AddListener(OnPromptInputChanged);
            }

            if (btnApproach != null)
            {
                btnApproach.onClick.RemoveAllListeners();
                btnApproach.onClick.AddListener(() => OnActionButtonClicked("Approach nearest dodgeball"));
            }

            if (btnPickup != null)
            {
                btnPickup.onClick.RemoveAllListeners();
                btnPickup.onClick.AddListener(() => OnActionButtonClicked("Reach down and grasp dodgeball"));
            }

            if (btnThrow != null)
            {
                btnThrow.onClick.RemoveAllListeners();
                btnThrow.onClick.AddListener(() => OnActionButtonClicked("Wind up and throw dodgeball at opponent"));
            }

            if (automateToggle != null)
            {
                automateToggle.onValueChanged.RemoveAllListeners();
                automateToggle.onValueChanged.AddListener(OnAutomateToggled);
            }
        }

        private void OnPromptInputChanged(string text)
        {
            if (text.Length > 80)
            {
                promptInputField.text = text.Substring(0, 80);
                text = promptInputField.text;
            }

            if (charCountText != null)
            {
                charCountText.text = $"{text.Length}/80";
                charCountText.color = text.Length >= 80 ? Color.red : new Color(0.8f, 0.8f, 0.8f, 0.85f);
            }
        }

        public void OnSubmitPrompt()
        {
            if (promptInputField == null) return;
            string prompt = promptInputField.text.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            if (prompt.Length > 80)
            {
                prompt = prompt.Substring(0, 80);
            }

            Debug.Log($"[PlayerMotionControllerUI] User submitted prompt: '{prompt}' for NPC {localNpcId}");
            DispatchPromptToAgent(prompt);
            promptInputField.text = "";
            if (charCountText != null) charCountText.text = "0/80";
        }

        public void OnActionButtonClicked(string actionPrompt)
        {
            Debug.Log($"[PlayerMotionControllerUI] Quick action button clicked: '{actionPrompt}' for NPC {localNpcId}");
            DispatchPromptToAgent(actionPrompt);
        }

        private void DispatchPromptToAgent(string prompt)
        {
            EnsureBindings();

            if (_rigController != null)
            {
                _rigController.DispatchMotionPrompt(prompt, "motion_prompt");
            }
            else
            {
                Debug.LogWarning($"[PlayerMotionControllerUI] No SpacetimeRigController found on local ragdoll {localRagdoll?.name}!");
            }
        }

        public void OnAutomateToggled(bool isAutomated)
        {
            Debug.Log($"[PlayerMotionControllerUI] 'Automate Ragdoll' toggled: {isAutomated} for NPC {localNpcId}");
            UpdateUIState(isAutomated);

            EnsureBindings();
            if (_executiveAgent != null)
            {
                if (isAutomated)
                {
                    _executiveAgent.ActivateExecutive();
                }
                else
                {
                    _executiveAgent.DeactivateExecutive();
                }
            }
            else
            {
                Debug.LogWarning($"[PlayerMotionControllerUI] No RagdollExecutiveAgent found on local ragdoll {localRagdoll?.name}!");
            }
        }

        /// <summary>
        /// Updates interactability of manual controls based on automation toggle state.
        /// When automated, manual prompt input and the 3 action buttons are disabled.
        /// </summary>
        private void UpdateUIState(bool isAutomated)
        {
            bool manualAllowed = !isAutomated;

            if (promptInputField != null) promptInputField.interactable = manualAllowed;
            if (executeButton != null) executeButton.interactable = manualAllowed;
            if (btnApproach != null) btnApproach.interactable = manualAllowed;
            if (btnPickup != null) btnPickup.interactable = manualAllowed;
            if (btnThrow != null) btnThrow.interactable = manualAllowed;
        }

        // ======================================================================
        // Procedural UI Canvas Generation
        // ======================================================================

        public static Canvas GetOrCreateGameCanvas()
        {
            // Destroy any legacy duplicate canvases first
            var legacy1 = GameObject.Find("DodgeballCourtCanvas");
            if (legacy1 != null) DestroyImmediate(legacy1);
            var legacy2 = GameObject.Find("PlayerMotionControllerCanvas");
            if (legacy2 != null) DestroyImmediate(legacy2);
            var legacy3 = GameObject.Find("WorkflowCanvas");
            if (legacy3 != null) DestroyImmediate(legacy3);

            var canvasGo = GameObject.Find("DodgeballGameCanvas");
            Canvas canvas = null;
            if (canvasGo != null)
            {
                canvas = canvasGo.GetComponent<Canvas>();
            }

            if (canvas == null)
            {
                canvasGo = new GameObject("DodgeballGameCanvas");
                canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080);
                scaler.matchWidthOrHeight = 0.5f;
                canvasGo.AddComponent<GraphicRaycaster>();

                if (FindObjectOfType<UnityEngine.EventSystems.EventSystem>() == null)
                {
                    GameObject es = new GameObject("EventSystem");
                    es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                }
            }

            return canvas;
        }

        public void EnsureUIHierarchy()
        {
            if (_isInitialized) return;

            Canvas canvas = GetOrCreateGameCanvas();
            _parentCanvas = canvas;
            Font defaultFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (defaultFont == null) defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

            // Remove any existing duplicate panels
            Transform existingPanel = canvas.transform.Find("PlayerControlPanel");
            if (existingPanel != null)
            {
                DestroyImmediate(existingPanel.gameObject);
            }

            // Main Player Control Panel (Anchored to Bottom-Left / Center)
            GameObject panelGo = new GameObject("PlayerControlPanel");
            panelGo.transform.SetParent(canvas.transform, false);
            RectTransform panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
            panelRect.anchoredPosition = new Vector2(25f, 25f);
            panelRect.sizeDelta = new Vector2(620f, 210f);

            Image panelBg = panelGo.AddComponent<Image>();
            panelBg.color = new Color(0.08f, 0.10f, 0.16f, 0.94f);

            // Header Banner
            GameObject headerGo = new GameObject("Header");
            headerGo.transform.SetParent(panelGo.transform, false);
            RectTransform headerRect = headerGo.AddComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.anchoredPosition = new Vector2(0f, -6f);
            headerRect.sizeDelta = new Vector2(-20f, 30f);

            headerText = headerGo.AddComponent<Text>();
            headerText.font = defaultFont;
            headerText.fontSize = 15;
            headerText.fontStyle = FontStyle.Bold;
            string teamStr = (localNpcId % 2 != 0) ? "TEAM BLUE" : "TEAM RED";
            headerText.text = $"PLAYER CONTROLLER [NPC {localNpcId} - {teamStr}]";
            headerText.color = new Color(0.95f, 0.85f, 0.25f);
            headerText.alignment = TextAnchor.MiddleLeft;

            // Automate Ragdoll Toggle (Top Right of Panel)
            automateToggle = CreateToggle(panelGo, "AutomateToggle", "Automate Ragdoll", new Vector2(190f, 85f), defaultFont);

            // Text Entry Box (Row 1)
            promptInputField = CreateInputField(panelGo, "PromptInputField", new Vector2(0f, 35f), new Vector2(470f, 38f), defaultFont);
            promptInputField.characterLimit = 80;

            // Character Counter Text (next to input field)
            GameObject countGo = new GameObject("CharCounter");
            countGo.transform.SetParent(panelGo.transform, false);
            RectTransform countRect = countGo.AddComponent<RectTransform>();
            countRect.anchorMin = new Vector2(0f, 0.5f);
            countRect.anchorMax = new Vector2(0f, 0.5f);
            countRect.pivot = new Vector2(1f, 0.5f);
            countRect.anchoredPosition = new Vector2(475f, 58f);
            countRect.sizeDelta = new Vector2(60f, 20f);
            charCountText = countGo.AddComponent<Text>();
            charCountText.font = defaultFont;
            charCountText.fontSize = 11;
            charCountText.color = new Color(0.75f, 0.75f, 0.75f, 0.85f);
            charCountText.text = "0/80";
            charCountText.alignment = TextAnchor.MiddleRight;

            // Execute Button (Next to Input Field)
            executeButton = CreateButton(panelGo, "ExecuteButton", "EXECUTE", new Vector2(245f, 35f), new Vector2(105f, 38f), defaultFont, 13, new Color(0.15f, 0.65f, 0.35f));

            // Three Quick Action Buttons (Row 2, directly below text entry)
            btnApproach = CreateButton(panelGo, "BtnApproach", "Go to nearest dodgeball", new Vector2(-195f, -18f), new Vector2(190f, 40f), defaultFont, 12, new Color(0.20f, 0.45f, 0.75f));
            btnPickup   = CreateButton(panelGo, "BtnPickup", "Pick up dodgeball", new Vector2(5f, -18f), new Vector2(180f, 40f), defaultFont, 12, new Color(0.20f, 0.45f, 0.75f));
            btnThrow    = CreateButton(panelGo, "BtnThrow", "Throw dodgeball", new Vector2(195f, -18f), new Vector2(170f, 40f), defaultFont, 12, new Color(0.75f, 0.28f, 0.25f));

            // Status Feedback Text (Row 3, bottom of panel)
            GameObject statusGo = new GameObject("StatusFeedback");
            statusGo.transform.SetParent(panelGo.transform, false);
            RectTransform statusRect = statusGo.AddComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0f);
            statusRect.pivot = new Vector2(0.5f, 0f);
            statusRect.anchoredPosition = new Vector2(0f, 6f);
            statusRect.sizeDelta = new Vector2(-20f, 24f);

            statusFeedbackText = statusGo.AddComponent<Text>();
            statusFeedbackText.font = defaultFont;
            statusFeedbackText.fontSize = 12;
            statusFeedbackText.fontStyle = FontStyle.Normal;
            statusFeedbackText.color = Color.white;
            statusFeedbackText.alignment = TextAnchor.MiddleLeft;
            statusFeedbackText.text = "<color=#00FF66>READY:</color> Enter motion prompt or press action button";

            _isInitialized = true;
        }

        private InputField CreateInputField(GameObject parent, string name, Vector2 pos, Vector2 size, Font font)
        {
            GameObject ifGo = new GameObject(name);
            ifGo.transform.SetParent(parent.transform, false);
            RectTransform ifRect = ifGo.AddComponent<RectTransform>();
            ifRect.anchorMin = new Vector2(0f, 0.5f);
            ifRect.anchorMax = new Vector2(0f, 0.5f);
            ifRect.pivot = new Vector2(0f, 0.5f);
            ifRect.anchoredPosition = new Vector2(15f, pos.y);
            ifRect.sizeDelta = size;

            Image bg = ifGo.AddComponent<Image>();
            bg.color = new Color(0.14f, 0.17f, 0.24f, 0.98f);

            InputField inputField = ifGo.AddComponent<InputField>();

            // Text component
            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(ifGo.transform, false);
            RectTransform textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 2f);
            textRect.offsetMax = new Vector2(-10f, -2f);

            Text text = textGo.AddComponent<Text>();
            text.font = font;
            text.fontSize = 14;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            text.supportRichText = false;

            // Placeholder component
            GameObject phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(ifGo.transform, false);
            RectTransform phRect = phGo.AddComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(10f, 2f);
            phRect.offsetMax = new Vector2(-10f, -2f);

            Text placeholder = phGo.AddComponent<Text>();
            placeholder.font = font;
            placeholder.fontSize = 13;
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.color = new Color(0.6f, 0.65f, 0.75f, 0.70f);
            placeholder.text = "Enter motion sequence (e.g. 'kick left leg', max 80 chars)...";
            placeholder.alignment = TextAnchor.MiddleLeft;

            inputField.textComponent = text;
            inputField.placeholder = placeholder;
            return inputField;
        }

        private Button CreateButton(GameObject parent, string name, string label, Vector2 pos, Vector2 size, Font font, int fontSize, Color color)
        {
            GameObject btnGo = new GameObject(name);
            btnGo.transform.SetParent(parent.transform, false);
            RectTransform btnRect = btnGo.AddComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0.5f);
            btnRect.anchorMax = new Vector2(0.5f, 0.5f);
            btnRect.pivot = new Vector2(0.5f, 0.5f);
            btnRect.anchoredPosition = pos;
            btnRect.sizeDelta = size;

            Image img = btnGo.AddComponent<Image>();
            img.color = color;

            Button btn = btnGo.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = color;
            cb.highlightedColor = color * 1.25f;
            cb.pressedColor = color * 0.8f;
            cb.disabledColor = new Color(0.25f, 0.25f, 0.28f, 0.5f);
            btn.colors = cb;

            GameObject textGo = new GameObject("Text");
            textGo.transform.SetParent(btnGo.transform, false);
            RectTransform textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            Text txt = textGo.AddComponent<Text>();
            txt.text = label;
            txt.font = font;
            txt.fontSize = fontSize;
            txt.fontStyle = FontStyle.Bold;
            txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleCenter;

            return btn;
        }

        private Toggle CreateToggle(GameObject parent, string name, string label, Vector2 pos, Font font)
        {
            GameObject toggleGo = new GameObject(name);
            toggleGo.transform.SetParent(parent.transform, false);
            RectTransform toggleRect = toggleGo.AddComponent<RectTransform>();
            toggleRect.anchorMin = new Vector2(0.5f, 0.5f);
            toggleRect.anchorMax = new Vector2(0.5f, 0.5f);
            toggleRect.pivot = new Vector2(0.5f, 0.5f);
            toggleRect.anchoredPosition = pos;
            toggleRect.sizeDelta = new Vector2(190f, 30f);

            Toggle toggle = toggleGo.AddComponent<Toggle>();

            // Background box
            GameObject bgGo = new GameObject("Background");
            bgGo.transform.SetParent(toggleGo.transform, false);
            RectTransform bgRect = bgGo.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0f, 0.5f);
            bgRect.anchorMax = new Vector2(0f, 0.5f);
            bgRect.pivot = new Vector2(0f, 0.5f);
            bgRect.anchoredPosition = new Vector2(0f, 0f);
            bgRect.sizeDelta = new Vector2(22f, 22f);
            Image bgImg = bgGo.AddComponent<Image>();
            bgImg.color = new Color(0.18f, 0.22f, 0.32f);

            // Checkmark
            GameObject checkGo = new GameObject("Checkmark");
            checkGo.transform.SetParent(bgGo.transform, false);
            RectTransform checkRect = checkGo.AddComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.5f, 0.5f);
            checkRect.anchorMax = new Vector2(0.5f, 0.5f);
            checkRect.pivot = new Vector2(0.5f, 0.5f);
            checkRect.anchoredPosition = Vector2.zero;
            checkRect.sizeDelta = new Vector2(16f, 16f);
            Image checkImg = checkGo.AddComponent<Image>();
            checkImg.color = new Color(0.25f, 0.85f, 0.45f);

            // Label text
            GameObject labelGo = new GameObject("Label");
            labelGo.transform.SetParent(toggleGo.transform, false);
            RectTransform labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0f, 0.5f);
            labelRect.anchorMax = new Vector2(1f, 0.5f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = new Vector2(30f, 0f);
            labelRect.sizeDelta = new Vector2(-30f, 24f);

            Text lbl = labelGo.AddComponent<Text>();
            lbl.text = label;
            lbl.font = font;
            lbl.fontSize = 13;
            lbl.fontStyle = FontStyle.Bold;
            lbl.color = Color.white;
            lbl.alignment = TextAnchor.MiddleLeft;

            toggle.graphic = checkImg;
            toggle.targetGraphic = bgImg;
            toggle.isOn = false;

            return toggle;
        }
    }
}
