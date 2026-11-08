#!/usr/bin/env python3
"""
SpacetimeDB Continuous Backend Worker for WebGL Multiplayer Dodgeball
---------------------------------------------------------------------
Connects to SpacetimeDB Cloud ('knet' database) and listens for incoming motion
requests dispatched by WebGL clients (text prompts <= 80 chars, quick actions,
or autonomous executive decisions).

When a request arrives:
1. Queries live scene info (dodgeball positions, distances, holding status) from environment_state table.
2. Queries humanoid telemetry (COM height, tilt, contacts) from body_telemetry table.
3. Invokes NVIDIA Nemotron (or biomechanical trajectory synthesizer) with live scene context.
4. Commits verified physical keyframes back to SpacetimeDB AgentWorkflowStep with status 'Queued'.
5. WebGL clients receive the keyframe sequence via WebSocket and execute it in real time!
"""

import os
import sys
import json
import time
import subprocess
from typing import Dict, Any, List, Optional

# Ensure nemotron-agent directory is on sys.path
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)

from agent import (
    SPACETIME_BIN,
    SPACETIME_DB_NAME,
    NVIDIA_MODEL,
    NVIDIA_BASE_URL,
    NVIDIA_API_KEY,
    run_spacetime_sql,
    run_spacetime_call,
    get_available_objects,
    get_body_telemetry,
    synthesize_trajectory_for_goal,
    run_executive_turn,
    execute_spacetime_sql,
    OpenAI
)

POLL_INTERVAL = float(os.environ.get("STDB_POLL_INTERVAL", "0.4"))


def fetch_requested_workflow_steps() -> List[Dict[str, Any]]:
    """Fetches all steps from SpacetimeDB that need processing."""
    query = "SELECT step_id, goal, current_subtask, status FROM agent_workflow_step WHERE status = 'Requested'"
    rows = run_spacetime_sql(query)
    if not rows:
        return []
    return rows


def mark_step_status(step_id: int, goal: str, subtask_json: str, status: str):
    """Updates the status of a step in SpacetimeDB."""
    clean_goal = (goal or "").replace("'", "''")
    clean_subtask = (subtask_json or "").replace("'", "''")
    escaped_subtask = clean_subtask.replace('"', '\\"')
    run_spacetime_call("set_workflow_step", [str(step_id), clean_goal, escaped_subtask, status])


def process_workflow_request(step: Dict[str, Any], client: Optional[Any] = None):
    step_id = int(step.get("step_id", 0))
    goal = step.get("goal", "")
    raw_subtask = step.get("current_subtask", "")

    # Parse subtask parameters
    target_npc = 1
    request_type = "motion_prompt"
    prompt_text = goal

    if raw_subtask:
        try:
            # Unescape if double-encoded
            if raw_subtask.startswith('"') and raw_subtask.endswith('"') and len(raw_subtask) > 2:
                raw_subtask = json.loads(raw_subtask)
            payload = json.loads(raw_subtask) if isinstance(raw_subtask, str) else raw_subtask
            target_npc = int(payload.get("target_npc_id", 1))
            request_type = payload.get("request_type", "motion_prompt")
            prompt_text = payload.get("prompt", goal)
        except Exception:
            pass

    print(f"\n[Worker] >>> Processing Step #{step_id} for NPC {target_npc}: '{prompt_text}' (Type: {request_type})")

    # Mark as Processing immediately to avoid double-processing
    mark_step_status(step_id, goal, raw_subtask, "Processing")

    # Read live scene info from SpacetimeDB
    scene_objects = get_available_objects(target_npc)
    nearest_ball_dist = 8.0
    if scene_objects:
        for obj in scene_objects:
            d = float(obj.get("distance", 8.0))
            if d < nearest_ball_dist:
                nearest_ball_dist = d

    # Handle Executive Decision Request
    if request_type == "executive_turn" or "executive" in prompt_text.lower():
        print(f"[Worker] Running stateless executive turn for NPC {target_npc}...")
        turn_res = run_executive_turn(npc_id=target_npc, client=client, no_await=True)
        print(f"[Worker] Executive Goal decided: '{turn_res.get('action_goal')}'")
        return

    # Handle Motion Prompt Request (Text entry <= 80 chars or 3 quick action buttons)
    clean_prompt = prompt_text
    # Strip team prefix like "[NPC 1] "
    if clean_prompt.startswith("[NPC"):
        idx = clean_prompt.find("]")
        if idx != -1:
            clean_prompt = clean_prompt[idx + 1:].strip()

    print(f"[Worker] Synthesizing trajectory for '{clean_prompt}' with SpacetimeDB scene context (Target dist: {nearest_ball_dist:.2f}m)...")
    keyframes = synthesize_trajectory_for_goal(
        clean_prompt,
        npc_id=target_npc,
        target_dist=nearest_ball_dist
    )

    if not keyframes:
        print(f"[Worker] [!] Warning: No keyframes generated for '{clean_prompt}'. Generating default posture.")
        keyframes = [
            {
                "step_name": "Neutral Stance",
                "duration_seconds": 0.35,
                "bone_targets": {
                    "Spine": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 35000.0, "kd": 2000.0}
                }
            }
        ]

    # Package keyframes into subtask JSON payload
    out_payload = {
        "target_npc_id": target_npc,
        "prompt": clean_prompt,
        "step_name": f"{clean_prompt} Trajectory",
        "keyframes": keyframes
    }
    out_json = json.dumps(out_payload)

    # Post back to SpacetimeDB with status = "Queued"
    print(f"[Worker] Committing {len(keyframes)} keyframes to SpacetimeDB Step #{step_id} [Status: Queued]...")
    mark_step_status(step_id, goal, out_json, "Queued")
    print(f"[Worker] <<< Step #{step_id} successfully queued for NPC {target_npc}!")


def run_worker_loop():
    print("=======================================================")
    print("  SpacetimeDB WebGL Multiplayer Worker for Dodgeball  ")
    print("=======================================================")
    print(f"[*] Database: {SPACETIME_DB_NAME}")
    print(f"[*] Spacetime CLI: {SPACETIME_BIN}")
    print(f"[*] Poll Interval: {POLL_INTERVAL}s")

    client = None
    if NVIDIA_API_KEY and NVIDIA_API_KEY != "nvapi-placeholder":
        print(f"[*] NVIDIA Nemotron API active ({NVIDIA_MODEL})")
        client = OpenAI(base_url=NVIDIA_BASE_URL, api_key=NVIDIA_API_KEY, timeout=15.0)
    else:
        print("[*] Biomechanical Trajectory Synthesizer active (Zero-latency fallback mode)")

    print("[*] Worker listening for WebGL client requests... Press Ctrl+C to stop.\n")

    consecutive_errors = 0
    while True:
        try:
            steps = fetch_requested_workflow_steps()
            if steps:
                for step in steps:
                    process_workflow_request(step, client=client)
            consecutive_errors = 0
        except KeyboardInterrupt:
            print("\n[Worker] Shutting down.")
            break
        except Exception as e:
            consecutive_errors += 1
            if consecutive_errors <= 3 or consecutive_errors % 20 == 0:
                print(f"[Worker] Error checking requests: {e}")
            time.sleep(1.0)
            continue

        time.sleep(POLL_INTERVAL)


if __name__ == "__main__":
    run_worker_loop()

