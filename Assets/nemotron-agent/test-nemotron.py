import os
import sys
import json
from openai import OpenAI

api_key = os.environ.get("NVIDIA_API_KEY")
if not api_key:
    print("[ERROR] NVIDIA_API_KEY is not set.")
    print("In Command Prompt, set it with: set NVIDIA_API_KEY=nvapi-your-key-here")
    sys.exit(1)

client = OpenAI(
    base_url="https://integrate.api.nvidia.com/v1",
    api_key=api_key
)

print("[*] Fetching model list from NVIDIA...")
try:
    models = client.models.list()
    all_model_ids = sorted([m.id for m in models.data])
    print(f"[OK] Authentication successful! Total models available: {len(all_model_ids)}")
except Exception as e:
    print(f"[ERROR] Failed to list models: {e}")
    sys.exit(1)

# Categorize models
nemotron_models = [m for m in all_model_ids if "nemotron" in m.lower()]
other_chat_models = [m for m in all_model_ids if ("instruct" in m.lower() or "chat" in m.lower()) and m not in nemotron_models]

print(f"\nNemotron models found ({len(nemotron_models)}):")
for m in nemotron_models:
    print(f"  - {m}")

# Prioritize testing Nemotron models first (especially 3.5 lightning and nano), then others
candidates_to_test = [
    "nvidia/nemotron-3.5-lightning-30b-a3b",
    "nvidia/nemotron-nano-3-30b-a3b",
    "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning",
    "nvidia/nemotron-3.5-bolt-e4b",
    "nvidia/nemotron-3.5-super-vl-120b-a12b",
    "nvidia/llama-3.1-nemotron-ultra-253b-v1",
    "nvidia/nemotron-3-ultra-550b-a55b",
    "nvidia/llama-3.1-nemotron-70b-instruct",
    "nvidia/llama-3.1-nemotron-51b-instruct",
    "nvidia/nemotron-4-340b-instruct"
]

# Append any other nemotron models not yet in the list
for nm in nemotron_models:
    if nm not in candidates_to_test:
        candidates_to_test.append(nm)

print("\n" + "="*60)
print("--- Testing Nemotron Chat Completions & Tool Calling ---")
print("="*60)

working_models = []
tool_supporting_models = []

sample_tools = [
    {
        "type": "function",
        "function": {
            "name": "get_body_telemetry",
            "description": "Returns current ragdoll COM height, tilt angle, and 12-bone positions.",
            "parameters": {"type": "object", "properties": {}}
        }
    }
]

for model_id in candidates_to_test:
    if model_id not in all_model_ids:
        print(f"[-] {model_id}: Not present in catalog")
        continue

    print(f"\n[*] Testing model: {model_id}")
    try:
        response = client.chat.completions.create(
            model=model_id,
            messages=[{"role": "user", "content": "Respond with only the single word: READY"}],
            max_tokens=64,
            temperature=0.1
        )
        msg = response.choices[0].message
        content = msg.content
        reasoning = getattr(msg, "reasoning_content", None) or getattr(msg, "reasoning", None)
        
        display_text = ""
        if content:
            display_text = content.strip().replace("\n", " ")
        elif reasoning:
            display_text = f"[Reasoning]: {reasoning.strip()[:60]}..."
        else:
            display_text = f"[Raw Message]: {msg}"

        print(f"    [OK] Completion succeeded! Output: \"{display_text}\"")
        working_models.append(model_id)

        # Test tool calling
        try:
            tool_resp = client.chat.completions.create(
                model=model_id,
                messages=[
                    {"role": "system", "content": "You are a humanoid controller. Call get_body_telemetry before acting."},
                    {"role": "user", "content": "What is the ragdoll's balance status?"}
                ],
                tools=sample_tools,
                tool_choice="auto",
                max_tokens=64,
                temperature=0.1
            )
            tool_msg = tool_resp.choices[0].message
            if tool_msg.tool_calls:
                fn_called = tool_msg.tool_calls[0].function.name
                print(f"    [OK] Tool calling verified! Model called: {fn_called}()")
                tool_supporting_models.append(model_id)
            else:
                print(f"    [-] Completed without invoking tool (content: {tool_msg.content[:40] if tool_msg.content else 'None'})")
        except Exception as te:
            print(f"    [-] Tool calling not supported or failed: {te}")

    except Exception as e:
        details = getattr(e, 'response', None)
        body = details.text if details is not None else getattr(e, 'body', str(e))
        print(f"    [FAILED] {e}")
        if "404" in str(e):
            print(f"    -> Not entitled on current account.")

print("\n" + "="*60)
print("[SUMMARY RESULTS]")
print("="*60)
if working_models:
    print(f"[+] Verified Responsive Models ({len(working_models)}):")
    for wm in working_models:
        has_tools = " [TOOLS SUPPORTED]" if wm in tool_supporting_models else " [NO TOOLS]"
        print(f"    - {wm}{has_tools}")
    
    recommended = tool_supporting_models[0] if tool_supporting_models else working_models[0]
    print(f"\nRecommended Model for Hackathon Track:")
    print(f"    {recommended}")
    print(f"\nIn Windows Command Prompt, run:")
    print(f"    set NVIDIA_MODEL={recommended}")
    print(f"    python nemotron-agent\\agent.py \"Transition from T-pose to a balanced right-hand wave\"")
else:
    print("[-] None of the tested Nemotron models responded with 200 OK.")
    if other_chat_models:
        print("\nOther available chat models on your account:")
        for om in other_chat_models[:10]:
            print(f"    - {om}")
print("="*60 + "\n")
