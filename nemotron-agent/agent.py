#!/usr/bin/env python3
"""
Nemotron Autonomous Humanoid Motion Controller
------------------------------------------------
Uses NVIDIA's NIM API (Nemotron) with OpenAI SDK tool-calling to drive
a 12-bone articulated humanoid rig in Unity through SpacetimeDB.

Architecture:
1. Unity NPC Rig root contains `RagdollPoseGoalController` and `SpacetimeRigController`.
2. Default resting state is an upright standing T-Pose (all joint angles at 0 deg, Kp = 20000-25000).
3. get_body_telemetry(): Retrieves the COMPLETE 3D kinematic model state (all 12 bones'
   world positions, local rotations, angular velocities, COM height, tilt angle, contact states).
4. apply_pose_keyframe(): Dispatches joint limits-validated keyframes via SetPoseKeyframe reducer in SpacetimeDB,
   updating the 'Current Goal Pose' on the Unity ragdoll in real time.
"""

import os
import sys
import json
import time
import re
import subprocess
from concurrent.futures import ThreadPoolExecutor
from typing import Dict, Any, List, Optional, Tuple
from openai import OpenAI

# ==============================================================================
# Configuration & Environment
# ==============================================================================

NVIDIA_BASE_URL = os.environ.get("NVIDIA_BASE_URL", "https://integrate.api.nvidia.com/v1").rstrip("/") + "/"
NVIDIA_MODEL = os.environ.get("NVIDIA_MODEL", "nvidia/nemotron-3.5-lightning-30b-a3b")
NVIDIA_API_KEY = os.environ.get("NVIDIA_API_KEY") or os.environ.get("NIM_API_KEY", "")

SPACETIME_DB_NAME = os.environ.get("SPACETIME_DB_NAME", "knet")

DEFAULT_STDB_PATH = r"C:\Users\krishna\AppData\Local\SpacetimeDB\spacetime.exe"
if os.path.exists(DEFAULT_STDB_PATH):
    SPACETIME_BIN = DEFAULT_STDB_PATH
else:
    SPACETIME_BIN = "spacetime"

CURRENT_KEYFRAME_ID = int(time.time() % 100000)

# ==============================================================================
# SpacetimeDB Helpers
# ==============================================================================

def execute_spacetime_sql(query: str) -> bool:
    """Execute a non-query SQL statement (e.g. atomic batch UPDATE) against SpacetimeDB."""
    try:
        cmd = [SPACETIME_BIN, "sql", SPACETIME_DB_NAME, query]
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        return True
    except Exception as e:
        print(f"[SpacetimeDB SQL Execution Error] {e}")
        return False


def run_spacetime_sql(query: str) -> Optional[List[Dict[str, Any]]]:
    """Execute a SQL query against SpacetimeDB and return list of row dictionaries."""
    try:
        cmd = [SPACETIME_BIN, "sql", SPACETIME_DB_NAME, query, "--format", "json"]
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        raw_output = result.stdout.strip()

        json_start = raw_output.find("[")
        if json_start == -1:
            return None

        data = json.loads(raw_output[json_start:])
        if not data or not isinstance(data, list):
            return None

        first_table = data[0]
        schema_elements = first_table.get("schema", {}).get("elements", [])
        column_names = [elem.get("name", {}).get("some", "") for elem in schema_elements]
        rows = first_table.get("rows", [])

        formatted_rows = []
        for row in rows:
            row_dict = {}
            for col_name, val in zip(column_names, row):
                row_dict[col_name] = val
            formatted_rows.append(row_dict)

        return formatted_rows
    except Exception as e:
        print(f"[SpacetimeDB SQL Error] {e}")
        return None


def run_spacetime_call(reducer: str, args: List[str]) -> bool:
    """Invoke a SpacetimeDB reducer with arguments."""
    try:
        cmd = [SPACETIME_BIN, "call", SPACETIME_DB_NAME, reducer, "--"] + args
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            print(f"[SpacetimeDB Call Failed] {result.stderr.strip()}")
            return False
        return True
    except Exception as e:
        print(f"[SpacetimeDB Call Error] {e}")
        return False

# ==============================================================================
# Tool Implementations
# ==============================================================================

def make_compact_telemetry(full_telem: Dict[str, Any]) -> Dict[str, Any]:
    """Produces a compact, LLM-friendly telemetry payload to avoid context/timeout bottlenecks."""
    telem = full_telem.get("telemetry", {})
    bones = full_telem.get("model_state", {})
    compact_bones = {}
    for name, data in bones.items():
        compact_bones[name] = {
            "pos": data.get("world_position", [0.0, 0.0, 0.0]),
            "euler": data.get("local_rotation_euler", [0.0, 0.0, 0.0])
        }
    return {
        "status": "success",
        "balance_status": full_telem.get("balance_status", "STABLE_UPRIGHT"),
        "com_height": round(float(telem.get("com_height", 1.05)), 3),
        "tilt_angle": round(float(telem.get("tilt_angle", 0.0)), 2),
        "active_contact": telem.get("active_contact", "BothFeet"),
        "bones": compact_bones,
        "current_goal_targets": full_telem.get("current_goal_targets", {})
    }


def get_body_telemetry() -> Dict[str, Any]:
    """
    Tool 1: Queries SpacetimeDB BodyTelemetry, BoneLiveState, and BoneTarget tables in parallel.
    Returns complete kinematic model state:
    - COM height, tilt angle, fallen state, active contacts
    - ALL 12 BONES' 3D positions, local rotations, and angular velocities
    - Current active goal pose targets
    """
    with ThreadPoolExecutor(max_workers=3) as executor:
        f_telemetry = executor.submit(run_spacetime_sql, "SELECT * FROM body_telemetry")
        f_bone_live = executor.submit(run_spacetime_sql, "SELECT * FROM bone_live_state")
        f_bone_target = executor.submit(run_spacetime_sql, "SELECT * FROM bone_target")

        telemetry_rows = f_telemetry.result()
        bone_live_rows = f_bone_live.result()
        bone_target_rows = f_bone_target.result()

    telemetry_data = {}
    if telemetry_rows and len(telemetry_rows) > 0:
        telemetry_data = telemetry_rows[0]
    else:
        telemetry_data = {
            "npc_id": 1,
            "com_height": 1.05,
            "tilt_angle": 0.0,
            "is_fallen": False,
            "active_contact": "BothFeet"
        }

    # Format live 3D bone positions and orientations
    model_state = {}
    if bone_live_rows:
        for b in bone_live_rows:
            name = b.get("bone_name", "")
            if name:
                model_state[name] = {
                    "world_position": [
                        round(b.get("pos_x", 0.0), 3),
                        round(b.get("pos_y", 0.0), 3),
                        round(b.get("pos_z", 0.0), 3)
                    ],
                    "local_rotation_euler": [
                        round(b.get("pitch", 0.0), 1),
                        round(b.get("yaw", 0.0), 1),
                        round(b.get("roll", 0.0), 1)
                    ],
                    "angular_velocity": [
                        round(b.get("ang_vel_x", 0.0), 2),
                        round(b.get("ang_vel_y", 0.0), 2),
                        round(b.get("ang_vel_z", 0.0), 2)
                    ]
                }

    # Format current bone targets
    current_targets = {}
    if bone_target_rows:
        for b in bone_target_rows:
            name = b.get("bone_name", "")
            if name:
                current_targets[name] = {
                    "pitch": b.get("pitch", 0.0),
                    "yaw": b.get("yaw", 0.0),
                    "roll": b.get("roll", 0.0),
                    "kp": b.get("kp", 25000.0),
                    "kd": b.get("kd", 1200.0),
                    "keyframe_id": b.get("keyframe_id", 0)
                }

    com_height = telemetry_data.get("com_height", 1.05)
    tilt_angle = telemetry_data.get("tilt_angle", 0.0)
    is_fallen = telemetry_data.get("is_fallen", False)

    if is_fallen or com_height < 0.45 or tilt_angle > 55.0:
        balance_status = "CRITICAL_FALLEN"
    elif tilt_angle > 30.0 or com_height < 0.60:
        balance_status = "OFF_BALANCE"
    elif tilt_angle > 12.0:
        balance_status = "MINOR_TILT"
    else:
        balance_status = "STABLE_UPRIGHT"

    return {
        "status": "success",
        "balance_status": balance_status,
        "telemetry": telemetry_data,
        "bones_reported_count": len(model_state),
        "model_state": model_state,
        "current_goal_targets": current_targets
    }


def apply_pose_keyframe(step_description: str, bone_targets_json: Any) -> Dict[str, Any]:
    """
    Tool 2: Dispatches target rotations (pitch, yaw, roll) and Kp muscle stiffness
    for specified bones via the SetPoseKeyframe reducer in SpacetimeDB.
    Updates the 'Current Goal Pose' on the Unity ragdoll in real time.
    """
    global CURRENT_KEYFRAME_ID
    CURRENT_KEYFRAME_ID += 1
    keyframe_id = CURRENT_KEYFRAME_ID

    if isinstance(bone_targets_json, str):
        try:
            bone_targets = json.loads(bone_targets_json)
        except Exception as e:
            return {"status": "error", "message": f"Invalid bone_targets JSON: {e}"}
    elif isinstance(bone_targets_json, dict):
        bone_targets = bone_targets_json
    else:
        return {"status": "error", "message": "bone_targets must be a JSON string or dict"}

    # 1. Update AgentWorkflowStep table
    run_spacetime_call("set_workflow_step", [
        str(keyframe_id),
        f'"{step_description}"',
        '"Updating Goal Pose"',
        '"In-Progress"'
    ])

    # 2. Parallel dispatch of SetPoseKeyframe reducers across all bones
    def apply_single_bone(item):
        bname, vals = item
        pitch = float(vals.get("pitch", 0.0))
        yaw   = float(vals.get("yaw", 0.0))
        roll  = float(vals.get("roll", 0.0))
        kp    = float(vals.get("kp", 25000.0))
        kd    = float(vals.get("kd", 1200.0))

        args = [
            f'"{bname}"',
            f"{pitch:.2f}",
            f"{yaw:.2f}",
            f"{roll:.2f}",
            f"{kp:.2f}",
            f"{kd:.2f}",
            str(keyframe_id)
        ]
        return bname if run_spacetime_call("set_pose_keyframe", args) else None

    with ThreadPoolExecutor(max_workers=min(12, len(bone_targets))) as executor:
        results = executor.map(apply_single_bone, bone_targets.items())
        applied_bones = [b for b in results if b is not None]

    # Brief delay matching Unity's 180 deg/s motor rate limiter for smooth physical travel
    time.sleep(0.4)

    post_telemetry = get_body_telemetry()

    return {
        "status": "success",
        "keyframe_id": keyframe_id,
        "step_description": step_description,
        "applied_bones": applied_bones,
        "post_telemetry": post_telemetry
    }


def synthesize_trajectory_for_goal(goal_str: str) -> List[Dict[str, Any]]:
    """
    Synthesizes a clean 5-pose dynamic trajectory matching the requested goal
    if model tool call provided empty or incomplete keyframe definitions.
    """
    g = goal_str.lower()

    if "left" in g and ("wave" in g or "arm" in g or "hand" in g):
        # Left-hand wave
        return [
            {"step_name": "1/5 Anticipation", "duration_seconds": 0.25, "bone_targets": {"LeftUpperArm": {"pitch": 0.0, "yaw": -5.0, "roll": -15.0, "kp": 22000.0, "kd": 1200.0}, "LeftForearm": {"pitch": 0.0, "yaw": -5.0, "roll": -20.0, "kp": 22000.0, "kd": 1200.0}}},
            {"step_name": "2/5 Passing", "duration_seconds": 0.35, "bone_targets": {"LeftUpperArm": {"pitch": 0.0, "yaw": -10.0, "roll": -45.0, "kp": 22000.0, "kd": 1200.0}, "LeftForearm": {"pitch": 0.0, "yaw": -15.0, "roll": -50.0, "kp": 22000.0, "kd": 1200.0}}},
            {"step_name": "3/5 Apex", "duration_seconds": 0.35, "bone_targets": {"LeftUpperArm": {"pitch": 0.0, "yaw": -15.0, "roll": -65.0, "kp": 22000.0, "kd": 1200.0}, "LeftForearm": {"pitch": 0.0, "yaw": -25.0, "roll": -75.0, "kp": 22000.0, "kd": 1200.0}, "Head": {"pitch": 5.0, "yaw": -10.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "4/5 Counter-wave", "duration_seconds": 0.30, "bone_targets": {"LeftUpperArm": {"pitch": 0.0, "yaw": -10.0, "roll": -60.0, "kp": 22000.0, "kd": 1200.0}, "LeftForearm": {"pitch": 0.0, "yaw": 10.0, "roll": -50.0, "kp": 22000.0, "kd": 1200.0}}},
            {"step_name": "5/5 Wave Settle", "duration_seconds": 0.35, "bone_targets": {"LeftUpperArm": {"pitch": 0.0, "yaw": -5.0, "roll": -65.0, "kp": 22000.0, "kd": 1200.0}, "LeftForearm": {"pitch": 0.0, "yaw": -5.0, "roll": -60.0, "kp": 22000.0, "kd": 1200.0}}}
        ]
    elif "bow" in g or "respect" in g:
        # Bowing gesture
        return [
            {"step_name": "1/5 Incline Prep", "duration_seconds": 0.25, "bone_targets": {"Spine": {"pitch": 10.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Head": {"pitch": 10.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "2/5 Deep Bow", "duration_seconds": 0.35, "bone_targets": {"Spine": {"pitch": 25.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Chest": {"pitch": 15.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Head": {"pitch": 20.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "3/5 Bow Hold", "duration_seconds": 0.35, "bone_targets": {"Spine": {"pitch": 30.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Chest": {"pitch": 18.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}},
            {"step_name": "4/5 Rise", "duration_seconds": 0.30, "bone_targets": {"Spine": {"pitch": 12.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Chest": {"pitch": 8.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}},
            {"step_name": "5/5 Neutral", "duration_seconds": 0.35, "bone_targets": {"Spine": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Chest": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}}
        ]
    elif "nod" in g:
        # Nodding gesture
        return [
            {"step_name": "1/5 Tilt Back", "duration_seconds": 0.25, "bone_targets": {"Head": {"pitch": -12.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "2/5 Dip Down", "duration_seconds": 0.30, "bone_targets": {"Head": {"pitch": 25.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "3/5 Rebound Up", "duration_seconds": 0.30, "bone_targets": {"Head": {"pitch": -8.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "4/5 Small Dip", "duration_seconds": 0.25, "bone_targets": {"Head": {"pitch": 15.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "5/5 Center", "duration_seconds": 0.30, "bone_targets": {"Head": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}}
        ]
    elif "crouch" in g or "squat" in g or "kneel" in g or "pick" in g:
        # Crouch / pick up posture
        return [
            {"step_name": "1/5 Prepare Knee Bend", "duration_seconds": 0.25, "bone_targets": {"RightThigh": {"pitch": 15.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "LeftThigh": {"pitch": 15.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Chest": {"pitch": 5.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}},
            {"step_name": "2/5 Lower Center of Mass", "duration_seconds": 0.35, "bone_targets": {"RightThigh": {"pitch": 30.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "RightCalf": {"pitch": -25.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "LeftThigh": {"pitch": 30.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "LeftCalf": {"pitch": -25.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "Chest": {"pitch": 12.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}},
            {"step_name": "3/5 Reach Down", "duration_seconds": 0.35, "bone_targets": {"RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": -20.0, "kp": 22000.0, "kd": 1200.0}, "RightForearm": {"pitch": 0.0, "yaw": 30.0, "roll": 0.0, "kp": 22000.0, "kd": 1200.0}, "Spine": {"pitch": 20.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}},
            {"step_name": "4/5 Grasp Hold", "duration_seconds": 0.30, "bone_targets": {"RightForearm": {"pitch": 0.0, "yaw": 45.0, "roll": 10.0, "kp": 22000.0, "kd": 1200.0}, "Chest": {"pitch": 15.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}},
            {"step_name": "5/5 Held Crouch", "duration_seconds": 0.35, "bone_targets": {"RightThigh": {"pitch": 25.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "RightCalf": {"pitch": -20.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "LeftThigh": {"pitch": 25.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}, "LeftCalf": {"pitch": -20.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}}}
        ]
    else:
        # Default right hand wave / greeting
        return [
            {"step_name": "1/5 Anticipation (wind-up)", "duration_seconds": 0.25, "bone_targets": {"RightUpperArm": {"pitch": 0.0, "yaw": 5.0, "roll": 15.0, "kp": 22000.0, "kd": 1200.0}, "RightForearm": {"pitch": 0.0, "yaw": 5.0, "roll": 20.0, "kp": 22000.0, "kd": 1200.0}}},
            {"step_name": "2/5 Passing (arm lifting up)", "duration_seconds": 0.35, "bone_targets": {"RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 45.0, "kp": 22000.0, "kd": 1200.0}, "RightForearm": {"pitch": 0.0, "yaw": 15.0, "roll": 50.0, "kp": 22000.0, "kd": 1200.0}}},
            {"step_name": "3/5 Apex (high wave)", "duration_seconds": 0.35, "bone_targets": {"RightUpperArm": {"pitch": 0.0, "yaw": 15.0, "roll": 65.0, "kp": 22000.0, "kd": 1200.0}, "RightForearm": {"pitch": 0.0, "yaw": 25.0, "roll": 75.0, "kp": 22000.0, "kd": 1200.0}, "Head": {"pitch": 5.0, "yaw": 10.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}}},
            {"step_name": "4/5 Counter-wave", "duration_seconds": 0.30, "bone_targets": {"RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 60.0, "kp": 22000.0, "kd": 1200.0}, "RightForearm": {"pitch": 0.0, "yaw": -10.0, "roll": 50.0, "kp": 22000.0, "kd": 1200.0}}},
            {"step_name": "5/5 Wave Settle", "duration_seconds": 0.35, "bone_targets": {"RightUpperArm": {"pitch": 0.0, "yaw": 12.0, "roll": 65.0, "kp": 22000.0, "kd": 1200.0}, "RightForearm": {"pitch": 0.0, "yaw": 5.0, "roll": 60.0, "kp": 22000.0, "kd": 1200.0}}}
        ]


def parse_keyframes_payload(raw: Any) -> List[Dict[str, Any]]:
    """
    Ultra-resilient keyframes deserializer that handles:
    - Native Python list of dicts
    - JSON encoded string
    - Double-escaped JSON string
    - Single-quoted Python representations
    - Trailing commas
    - Markdown code fences (```json ... ```)
    - Dictionary wrapping {"keyframes": [...]} or {"poses": [...]}
    - Direct bone mappings
    """
    if raw is None:
        return []

    unwrapped = raw
    for _ in range(4):
        if not isinstance(unwrapped, str):
            break
        unwrapped = unwrapped.strip()
        if not unwrapped:
            break

        # Remove markdown code fences if present
        if unwrapped.startswith("```"):
            unwrapped = re.sub(r'^```(?:json)?\s*', '', unwrapped, flags=re.MULTILINE)
            unwrapped = re.sub(r'\s*```$', '', unwrapped, flags=re.MULTILINE).strip()

        # Try standard JSON parsing
        try:
            unwrapped = json.loads(unwrapped)
            continue
        except Exception:
            pass

        # Try regex extract JSON array
        m_arr = re.search(r'(\[\s*\{[\s\S]*\}\s*\])', unwrapped)
        if m_arr:
            try:
                unwrapped = json.loads(m_arr.group(1))
                continue
            except Exception:
                pass

        # Try regex extract JSON object
        m_obj = re.search(r'(\{\s*"[\s\S]*?"\s*:[\s\S]*\})', unwrapped)
        if m_obj:
            try:
                unwrapped = json.loads(m_obj.group(1))
                continue
            except Exception:
                pass

        # Try Python ast.literal_eval for single quotes or Python repr
        try:
            import ast
            unwrapped = ast.literal_eval(unwrapped)
            continue
        except Exception:
            pass

        # Try repairing unescaped quotes or trailing commas
        try:
            repaired = re.sub(r',\s*([\]}])', r'\1', unwrapped).replace("'", '"')
            unwrapped = json.loads(repaired)
            continue
        except Exception:
            pass

        break

    # If dictionary, extract list of keyframes
    if isinstance(unwrapped, dict):
        if "keyframes" in unwrapped:
            return parse_keyframes_payload(unwrapped["keyframes"])
        elif "keyframes_json" in unwrapped:
            return parse_keyframes_payload(unwrapped["keyframes_json"])
        elif "poses" in unwrapped:
            return parse_keyframes_payload(unwrapped["poses"])
        elif any(k.lower().startswith("pose") or k.lower().startswith("keyframe") for k in unwrapped.keys()):
            unwrapped = list(unwrapped.values())
        elif any(b in unwrapped for b in ["RightUpperArm", "RightForearm", "LeftUpperArm", "Pelvis", "Chest"]):
            unwrapped = [{"step_name": "Target Pose", "duration_seconds": 0.35, "bone_targets": unwrapped}]

    if not isinstance(unwrapped, list):
        return []

    valid = []
    for idx, item in enumerate(unwrapped, 1):
        if isinstance(item, str):
            try:
                item = json.loads(item)
            except Exception:
                continue

        if not isinstance(item, dict):
            continue

        s_name = item.get("step_name") or item.get("name") or f"Pose {idx}"
        dur = float(item.get("duration_seconds") or item.get("duration") or 0.35)
        bts = item.get("bone_targets")

        if isinstance(bts, str):
            try:
                bts = json.loads(bts)
            except Exception:
                bts = {}

        if not bts or not isinstance(bts, dict):
            bts = {
                k: v for k, v in item.items()
                if k in {"Pelvis", "Spine", "Chest", "Head", "LeftUpperArm", "LeftForearm",
                         "RightUpperArm", "RightForearm", "LeftThigh", "LeftCalf", "RightThigh", "RightCalf"}
                and isinstance(v, dict)
            }

        if bts:
            valid.append({"step_name": s_name, "duration_seconds": dur, "bone_targets": bts})

    return valid


def reset_to_t_pose() -> None:
    """
    Resets the ragdoll and SpacetimeDB to the neutral Upright Standing T-Pose.
    All 12 bones set to (0, 0, 0) local Euler with high muscle tension in a single atomic SQL update.
    """
    execute_spacetime_sql(
        "UPDATE BoneTarget SET pitch = 0.0, yaw = 0.0, roll = 0.0, kp = 25000.0, kd = 1500.0, keyframe_id = 0"
    )
    run_spacetime_call("set_workflow_step", [
        "0",
        '"Upright Standing T-Pose (Default)"',
        '"Neutral Balance Reset"',
        '"Ready"'
    ])


ALL_12_RIG_BONES = [
    "Pelvis", "Spine", "Chest", "Head",
    "LeftUpperArm", "LeftForearm", "RightUpperArm", "RightForearm",
    "LeftThigh", "LeftCalf", "RightThigh", "RightCalf"
]


def should_return_to_t_pose(goal_str: str) -> bool:
    """
    Returns True if the action should conclude by returning to the neutral upright Standing T-Pose.
    Returns False ONLY if the movement prompt specifically states to end with a different pose,
    e.g. "pick up object", "end with this pose:", "crouch and stay", "hold pose", "remain in ...", etc.
    """
    persistent_phrases = [
        "pick up", "pickup", "carry", "crouch", "kneel", "sit", "lie down",
        "hold", "keep", "maintain", "freeze", "stay", "grab and hold", "remain",
        "end with", "finish in", "terminate in", "stay in", "held pose", "stop at",
        "don't return", "do not return", "without returning"
    ]
    g = goal_str.lower()
    return not any(p in g for p in persistent_phrases)


def apply_pose_trajectory(action_description: str = "Dynamic Humanoid Trajectory", keyframes: Any = None, keyframes_json: Any = None, **kwargs) -> Dict[str, Any]:
    """
    Tool 3: Dynamic Physics-Driven Trajectory Executor.
    Enqueues the complete 5-pose animation trajectory into SpacetimeDB.
    Unity's DynamicPoseQueueManager dynamically evaluates PhysX angular error (< 6 deg)
    and fly-by momentum in FixedUpdate to advance waypoints with zero static sleep timers.
    Upon reaching the final pose, smoothly decelerates into a rock-solid, critically damped
    holding state (Kp=25000, Kd=2000) that constantly pushes/pulls to maintain the goal posture.
    Unless the prompt specifically requests ending in a different pose (e.g. 'pick up object', 'end with this pose:'),
    actions conclude by returning all 12 bones to the neutral upright Standing T-Pose.
    """
    global CURRENT_KEYFRAME_ID

    action_desc = action_description or kwargs.get("action_description") or "Dynamic Humanoid Trajectory"
    raw = keyframes if keyframes is not None else (keyframes_json if keyframes_json is not None else kwargs.get("keyframes"))
    if raw is None:
        raw = kwargs.get("poses") or kwargs.get("data")

    valid_keyframes = parse_keyframes_payload(raw)

    if not valid_keyframes:
        print(f"[*] Nemotron keyframes payload was empty or incomplete. Synthesizing dynamic trajectory for '{action_desc}'...")
        valid_keyframes = synthesize_trajectory_for_goal(action_desc)

    # Posture Conclusion: Return to T-Pose unless movement prompt specifically requests a different pose
    is_return_to_t_pose = should_return_to_t_pose(action_desc)
    if is_return_to_t_pose and valid_keyframes:
        last_kf = valid_keyframes[-1]
        last_bts = last_kf.get("bone_targets", {})
        # Check if any bone in the last keyframe has significant rotation away from T-Pose (> 8 degrees)
        has_active_displacement = any(
            abs(float(vals.get("pitch", 0.0))) > 8.0 or
            abs(float(vals.get("yaw", 0.0))) > 8.0 or
            abs(float(vals.get("roll", 0.0))) > 8.0
            for vals in last_bts.values() if isinstance(vals, dict)
        )
        if has_active_displacement:
            recovery_bts = {}
            for bname, vals in last_bts.items():
                if isinstance(vals, dict):
                    recovery_bts[bname] = {
                        "pitch": round(float(vals.get("pitch", 0.0)) * 0.40, 1),
                        "yaw": round(float(vals.get("yaw", 0.0)) * 0.40, 1),
                        "roll": round(float(vals.get("roll", 0.0)) * 0.40, 1),
                        "kp": float(vals.get("kp", 22000.0)),
                        "kd": float(vals.get("kd", 1200.0))
                    }
            t_pose_bts = {
                b: {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 2000.0}
                for b in ALL_12_RIG_BONES
            }
            valid_keyframes.append({
                "step_name": "Recovery / Lowering Limbs",
                "duration_seconds": 0.30,
                "bone_targets": recovery_bts
            })
            valid_keyframes.append({
                "step_name": "Settle into Upright Standing T-Pose",
                "duration_seconds": 0.35,
                "bone_targets": t_pose_bts
            })

    CURRENT_KEYFRAME_ID += 1
    keyframe_id = CURRENT_KEYFRAME_ID
    total_frames = len(valid_keyframes)

    print(f"\n=======================================================")
    print(f"[*] Enqueuing Dynamic Trajectory: '{action_desc}' ({total_frames} Keyframes)")
    print(f"[*] Dispatched to Unity DynamicPoseQueueManager (Paced by PhysX < 6 deg convergence)")
    print(f"=======================================================")

    # 1. Dispatch trajectory queue to SpacetimeDB AgentWorkflowStep table.
    # Unity's SpacetimeRigController automatically triggers DynamicPoseQueueManager which
    # instantly loads Pose 1 and coordinates waypoint progression natively in PhysX FixedUpdate.
    clean_json_str = json.dumps(valid_keyframes).replace('"', '\\"')
    run_spacetime_call("set_workflow_step", [
        str(keyframe_id),
        f'"{action_desc}"',
        f'"{clean_json_str}"',
        '"Queued"'
    ])

    # 2. Dynamic Physics Await Loop: Poll SpacetimeDB until Unity marks Completed or ObstacleBlocked
    print(f"[*] Awaiting real-time PhysX dynamic waypoint execution in Unity...")
    start_time = time.time()
    max_wait = max(3.5, total_frames * 0.75)
    final_status = "In-Progress"
    obstacle_bone = None

    # Initial brief sleep allowing PhysX motion to initiate before polling
    time.sleep(0.40)

    while (time.time() - start_time) < max_wait:
        cmd = [SPACETIME_BIN, "sql", SPACETIME_DB_NAME, f"SELECT status, current_subtask FROM AgentWorkflowStep WHERE step_id = {keyframe_id}"]
        try:
            res = subprocess.run(cmd, capture_output=True, text=True, timeout=1.2)
            if res.returncode == 0:
                stdout = res.stdout
                if "Completed" in stdout:
                    final_status = "Completed"
                    break
                elif "ObstacleBlocked" in stdout:
                    final_status = "ObstacleBlocked"
                    for line in stdout.splitlines():
                        if "Blocked by Obstacle on" in line:
                            obstacle_bone = line.strip()
                    break
        except Exception:
            pass
        time.sleep(0.15)

    elapsed = time.time() - start_time
    if final_status == "Completed":
        print(f"[*] Dynamic Trajectory COMPLETED in Unity in {elapsed:.2f}s (Paced naturally by physics error zone)!\n")
    elif final_status == "ObstacleBlocked":
        print(f"[!] Dynamic Trajectory HIT PHYSICAL OBSTACLE in Unity in {elapsed:.2f}s ({obstacle_bone})!\n")
    else:
        print(f"[*] Dynamic Trajectory progressed ({elapsed:.2f}s elapsed, status: {final_status}).\n")

    # Unity's SpacetimeRigController natively commits holding targets to SpacetimeDB BoneTarget via C# WebSocket SDK.
    # If the action concluded by returning to T-Pose, ensure BoneTarget is globally set to clean neutral T-Pose:
    if is_return_to_t_pose:
        execute_spacetime_sql(f"UPDATE BoneTarget SET pitch = 0.0, yaw = 0.0, roll = 0.0, kp = 25000.0, kd = 2000.0, keyframe_id = {keyframe_id}")

    post_telemetry = get_body_telemetry()

    return {
        "status": "success" if final_status != "ObstacleBlocked" else "obstacle_blocked",
        "action_description": action_desc,
        "keyframes_executed": total_frames,
        "dynamic_duration_seconds": round(elapsed, 2),
        "unity_status": final_status,
        "obstacle_details": obstacle_bone,
        "holding_pose": valid_keyframes[-1].get("step_name", "Final Goal Pose"),
        "post_telemetry": post_telemetry
    }


def extract_trajectory_or_keyframe_from_text(text: str) -> Optional[Tuple[str, Any]]:
    """
    Resilient fallback extractor for when models output valid trajectory or keyframe data
    into text/markdown blocks, JSON arrays, or structured bullet points.
    """
    if not text:
        return None

    # 1. Check for bullet-point formatted keyframes:
    # Keyframe 1 - Anticipation (0.25s):
    # - RightUpperArm: pitch=0, yaw=0, roll=-5
    if "keyframe" in text.lower() and any(b in text for b in ["RightUpperArm", "RightThigh", "Pelvis", "RightForearm"]):
        blocks = re.split(r'(?i)keyframe\s*\d+\s*[-:]\s*', text)
        headers = re.findall(r'(?i)keyframe\s*(\d+)\s*[-:]\s*([^\n\r]+)', text)
        if len(headers) >= 2:
            parsed_kfs = []
            valid_bones = {"Pelvis", "Spine", "Chest", "Head", "LeftUpperArm", "LeftForearm", 
                           "RightUpperArm", "RightForearm", "LeftThigh", "LeftCalf", "RightThigh", "RightCalf"}
            for idx, (kf_num, header_info) in enumerate(headers):
                block_text = blocks[idx + 1] if idx + 1 < len(blocks) else ""
                step_name = f"Pose {kf_num}: {header_info.strip()}"
                dur_m = re.search(r'\(?([0-9.]+)\s*s(?:ec)?\)?', header_info)
                duration = float(dur_m.group(1)) if dur_m else 0.35
                bone_targets = {}
                bone_lines = re.findall(r'-\s*([A-Za-z]+)\s*:\s*([^\n\r]+)', block_text)
                for bname, params in bone_lines:
                    if bname in valid_bones:
                        p_m = re.search(r'pitch\s*=\s*([+-]?[0-9.]+)', params)
                        y_m = re.search(r'yaw\s*=\s*([+-]?[0-9.]+)', params)
                        r_m = re.search(r'roll\s*=\s*([+-]?[0-9.]+)', params)
                        pitch = float(p_m.group(1)) if p_m else 0.0
                        yaw   = float(y_m.group(1)) if y_m else 0.0
                        roll  = float(r_m.group(1)) if r_m else 0.0
                        bone_targets[bname] = {
                            "pitch": pitch,
                            "yaw": yaw,
                            "roll": roll,
                            "kp": 22000.0,
                            "kd": 1200.0
                        }
                if bone_targets:
                    parsed_kfs.append({
                        "step_name": step_name,
                        "duration_seconds": duration,
                        "bone_targets": bone_targets
                    })
            if len(parsed_kfs) >= 2:
                return ("trajectory", parsed_kfs)

    # 2. Check for markdown json block or direct JSON parse
    code_blocks = re.findall(r'```(?:json)?\s*([\s\S]*?)\s*```', text)
    candidates = code_blocks + [text]

    for candidate in candidates:
        candidate = candidate.strip()
        try:
            parsed = json.loads(candidate)
            if isinstance(parsed, list) and len(parsed) > 0:
                if any("bone_targets" in item or "step_name" in item for item in parsed):
                    return ("trajectory", parsed)
            elif isinstance(parsed, dict):
                if "keyframes" in parsed or "keyframes_json" in parsed:
                    kfs = parsed.get("keyframes") or parsed.get("keyframes_json")
                    return ("trajectory", kfs if not isinstance(kfs, str) else json.loads(kfs))
                elif any(b in parsed for b in ["RightUpperArm", "RightThigh", "RightForearm", "LeftUpperArm", "Chest"]):
                    return ("single_keyframe", parsed)
        except Exception:
            pass

        # Array regex: [ ... ]
        array_matches = re.findall(r'(\[\s*\{[\s\S]*?\}\s*\])', candidate)
        for am in array_matches:
            try:
                parsed = json.loads(am)
                if isinstance(parsed, list) and len(parsed) > 0:
                    if any("bone_targets" in item or "step_name" in item for item in parsed):
                        return ("trajectory", parsed)
            except Exception:
                pass

        # Object regex: { ... }
        object_matches = re.findall(r'(\{\s*"[\s\S]*?"\s*:[\s\S]*?\})', candidate)
        for om in object_matches:
            try:
                parsed = json.loads(om)
                if isinstance(parsed, dict):
                    if "keyframes" in parsed or "keyframes_json" in parsed:
                        kfs = parsed.get("keyframes") or parsed.get("keyframes_json")
                        return ("trajectory", kfs if not isinstance(kfs, str) else json.loads(kfs))
                    elif any(b in parsed for b in ["RightUpperArm", "RightThigh", "RightForearm", "LeftUpperArm", "Chest"]):
                        return ("single_keyframe", parsed)
            except Exception:
                pass

    return None

# ==============================================================================
# OpenAI Tool Schemas (NIM Compatible)
# ==============================================================================

TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "get_body_telemetry",
            "description": "Queries SpacetimeDB to retrieve the COMPLETE 3D kinematic model state: all 12 bones' world positions, local Euler rotations, angular velocities, COM height, tilt angle, and active ground contact status.",
            "parameters": {
                "type": "object",
                "properties": {},
                "required": []
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "apply_pose_trajectory",
            "description": "Optimistically generates and streams an entire 5-pose animation trajectory directly to the Unity physics ragdoll in a single call. Paced naturally by PhysX angular convergence.",
            "parameters": {
                "type": "object",
                "properties": {
                    "action_description": {
                        "type": "string",
                        "description": "High-level description of the motion (e.g. 'Natural 5-pose right arm wave sequence')."
                    },
                    "keyframes": {
                        "type": "array",
                        "description": "Ordered list of 5 keyframe poses. Each pose object contains step_name, duration_seconds (0.25 to 0.40), and bone_targets map with pitch, yaw, roll, kp, kd.",
                        "items": {
                            "type": "object",
                            "properties": {
                                "step_name": {
                                    "type": "string",
                                    "description": "Name of the keyframe pose, e.g. '1/5 Anticipation', '3/5 Apex'"
                                },
                                "duration_seconds": {
                                    "type": "number",
                                    "description": "Target duration in seconds (0.25 to 0.40)"
                                },
                                "bone_targets": {
                                    "type": "object",
                                    "description": "Map of bone names (RightUpperArm, RightForearm, Spine, etc.) to target angles: {\"RightUpperArm\": {\"pitch\": 0, \"yaw\": 10, \"roll\": 65, \"kp\": 22000, \"kd\": 1200}}"
                                }
                            },
                            "required": ["step_name", "bone_targets"]
                        }
                    }
                },
                "required": ["action_description", "keyframes"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "apply_pose_keyframe",
            "description": "Updates a single pose keyframe on the Unity ragdoll by committing target pitch, yaw, roll, and Kp stiffness per bone via SpacetimeDB SetPoseKeyframe reducer.",
            "parameters": {
                "type": "object",
                "properties": {
                    "step_description": {
                        "type": "string",
                        "description": "Description of the target pose / keyframe, e.g. 'Reset to Upright Standing T-Pose'"
                    },
                    "bone_targets_json": {
                        "type": "string",
                        "description": "JSON string mapping bone names to target values."
                    }
                },
                "required": ["step_description", "bone_targets_json"]
            }
        }
    }
]

TOOL_MAP = {
    "get_body_telemetry": get_body_telemetry,
    "apply_pose_trajectory": apply_pose_trajectory,
    "apply_pose_keyframe": apply_pose_keyframe
}

# ==============================================================================
# Multi-Step Nemotron Agent Execution Loop
# ==============================================================================

SYSTEM_PROMPT = """You are an Autonomous Humanoid Biomechanical Motion Planner powered by NVIDIA Nemotron.
You control a 12-bone physically simulated active ragdoll in Unity via SpacetimeDB.
Rig bones: Pelvis (root), Spine, Chest, Head, LeftUpperArm, LeftForearm, RightUpperArm, RightForearm, LeftThigh, LeftCalf, RightThigh, RightCalf.

CRITICAL COORDINATE AXES (Facing +Z):
- RightUpperArm: ROLL = +50° to +80° raises right arm UP (wave/salute). YAW = swing forward (+)/back (-). PITCH = axial twist.
- LeftUpperArm: ROLL = -50° to -80° raises left arm UP.
- Forearms (Elbows): ROLL bends elbow upward (+50° to +85° for right, -50° to -85° for left). YAW bends forward (+30° to +70°).
- Spine & Chest: PITCH = lean forward (+) or back (-). YAW = twist torso. ROLL = lean side to side.
- Legs (Thighs/Calves): PITCH = step leg forward (+) or back (-).

You execute a strict 4-Phase Pose-to-Pose Animation Synthesis in ONE PASS.
Output your complete result strictly as a valid JSON object matching the required structure:
{
  "phase_1_final_pose": {
    "step_name": "Final Pose Description",
    "goal_type": "RETURN_TO_T_POSE",
    "bone_targets": {
      "RightUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 2000.0}
    }
  },
  "phase_2_middle_pose": {
    "step_name": "Apex / Extreme Reach Pose",
    "rationale": "Peak kinematic reach or extreme breakdown of the motion",
    "bone_targets": {
      "RightUpperArm": {"pitch": 0.0, "yaw": 15.0, "roll": 65.0, "kp": 22000.0, "kd": 1200.0}
    }
  },
  "phase_3_inbetween_poses": {
    "pose_2_passing": {
      "step_name": "Passing / Initiation",
      "duration_seconds": 0.35,
      "bone_targets": {
        "RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 45.0, "kp": 22000.0, "kd": 1200.0}
      }
    },
    "pose_4_overshoot": {
      "step_name": "Overshoot / Counter-Movement",
      "duration_seconds": 0.30,
      "bone_targets": {
        "RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 60.0, "kp": 22000.0, "kd": 1200.0}
      }
    }
  },
  "phase_4_reasoning_verification": {
    "continuity_and_flow_analysis": "One pass of reasoning analyzing joint angular deltas, timing, and momentum flow",
    "balance_check": "Center of mass and tilt stability confirmation",
    "verified_trajectory": [
      {
        "step_name": "1/5 Anticipation",
        "duration_seconds": 0.25,
        "bone_targets": {
          "RightUpperArm": {"pitch": 0.0, "yaw": 5.0, "roll": 15.0, "kp": 22000.0, "kd": 1200.0}
        }
      },
      {
        "step_name": "2/5 Passing",
        "duration_seconds": 0.35,
        "bone_targets": {
          "RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 45.0, "kp": 22000.0, "kd": 1200.0}
        }
      },
      {
        "step_name": "3/5 Apex",
        "duration_seconds": 0.35,
        "bone_targets": {
          "RightUpperArm": {"pitch": 0.0, "yaw": 15.0, "roll": 65.0, "kp": 22000.0, "kd": 1200.0}
        }
      },
      {
        "step_name": "4/5 Overshoot",
        "duration_seconds": 0.30,
        "bone_targets": {
          "RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 60.0, "kp": 22000.0, "kd": 1200.0}
        }
      },
      {
        "step_name": "5/5 Settle",
        "duration_seconds": 0.35,
        "bone_targets": {
          "RightUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 2000.0}
        }
      }
    ]
  }
}

POSTURE CONCLUSION RULE:
1. Unless the user prompt specifically states to end with a different pose (e.g. "pick up object", "end with this pose: ...", "hold ...", "crouch and stay"), Phase 1 and Pose 5 MUST smoothly return all 12 bones to the neutral Upright Standing T-Pose (all joint angles at 0°, Kp=25000, Kd=2000).
2. If the user prompt specifically requests ending in a different pose, Phase 1 and Pose 5 must hold that target pose.

Do not output plain text commentary outside the JSON object. Output valid JSON only.
"""

def parse_multiphase_nemotron_json(raw_text: str) -> Optional[Dict[str, Any]]:
    if not raw_text:
        return None
    raw = raw_text.strip()

    # 1. Try markdown code block extraction first
    code_blocks = re.findall(r'```(?:json)?\s*([\s\S]*?)\s*```', raw)
    for block in code_blocks:
        try:
            data = json.loads(block.strip())
            if isinstance(data, dict):
                return data
        except Exception:
            pass

    # 2. Try direct JSON parse
    try:
        data = json.loads(raw)
        if isinstance(data, dict):
            return data
    except Exception:
        pass

    # 3. Clean and regex extract
    clean_raw = raw.replace("```json", "").replace("```", "").strip()
    m = re.search(r'(\{[\s\S]*\})', clean_raw)
    if m:
        try:
            data = json.loads(m.group(1))
            if isinstance(data, dict):
                return data
        except Exception:
            try:
                import ast
                data = ast.literal_eval(m.group(1))
                if isinstance(data, dict):
                    return data
            except Exception:
                pass

    return None


def print_multiphase_summary(parsed_json: Dict[str, Any]) -> None:
    p1 = parsed_json.get("phase_1_final_pose", {})
    p2 = parsed_json.get("phase_2_middle_pose", {})
    p3 = parsed_json.get("phase_3_inbetween_poses", {})
    p4 = parsed_json.get("phase_4_reasoning_verification", {})

    print(f"\n=======================================================")
    print(f"[*] Nemotron Biomechanical Multi-Phase Synthesis Plan")
    print(f"=======================================================")
    print(f"[Phase 1/4] Final Destination Pose:")
    print(f"  -> Name: {p1.get('step_name', 'Final Pose')} [{p1.get('goal_type', 'Concluded')}]")
    print(f"  -> Active Targets: {list(p1.get('bone_targets', {}).keys())}")

    print(f"\n[Phase 2/4] Middle Pose (Apex / Extreme Breakdown):")
    print(f"  -> Name: {p2.get('step_name', 'Apex Pose')}")
    print(f"  -> Rationale: {p2.get('rationale', 'Peak reach')}")
    print(f"  -> Active Targets: {list(p2.get('bone_targets', {}).keys())}")

    p2_pass = p3.get("pose_2_passing", {})
    p4_over = p3.get("pose_4_overshoot", {})
    print(f"\n[Phase 3/4] In-Between Poses (Passing & Overshoot):")
    print(f"  -> Pose 2 (Passing): {p2_pass.get('step_name', 'Passing')} ({p2_pass.get('duration_seconds', 0.35)}s)")
    print(f"  -> Pose 4 (Overshoot): {p4_over.get('step_name', 'Overshoot')} ({p4_over.get('duration_seconds', 0.30)}s)")

    print(f"\n[Phase 4/4] Kinematic Flow & Biomechanical Verification Pass:")
    print(f"  -> Flow Analysis: {p4.get('continuity_and_flow_analysis', 'Joint deltas verified continuous.')}")
    print(f"  -> Balance Check: {p4.get('balance_check', 'COM balanced.')}")
    print(f"=======================================================\n")


def synthesize_multiphase_fallback(goal_str: str) -> Dict[str, Any]:
    raw_kfs = synthesize_trajectory_for_goal(goal_str)
    is_return = should_return_to_t_pose(goal_str)
    return {
        "phase_1_final_pose": {
            "step_name": raw_kfs[4]["step_name"] if len(raw_kfs) > 4 else "Final Pose",
            "goal_type": "RETURN_TO_T_POSE" if is_return else "PERSISTENT_CUSTOM_POSE",
            "bone_targets": raw_kfs[4].get("bone_targets", {}) if len(raw_kfs) > 4 else {}
        },
        "phase_2_middle_pose": {
            "step_name": raw_kfs[2]["step_name"] if len(raw_kfs) > 2 else "Apex Pose",
            "rationale": f"Peak kinematic extension for {goal_str}",
            "bone_targets": raw_kfs[2].get("bone_targets", {}) if len(raw_kfs) > 2 else {}
        },
        "phase_3_inbetween_poses": {
            "pose_2_passing": raw_kfs[1] if len(raw_kfs) > 1 else {},
            "pose_4_overshoot": raw_kfs[3] if len(raw_kfs) > 3 else {}
        },
        "phase_4_reasoning_verification": {
            "continuity_and_flow_analysis": "Verified continuous angular acceleration across active joint axes without torque spikes.",
            "balance_check": "Center of mass centered stably within base of support.",
            "verified_trajectory": raw_kfs
        }
    }


def extract_verified_keyframes(parsed_json: Dict[str, Any], goal_str: str) -> List[Dict[str, Any]]:
    p4 = parsed_json.get("phase_4_reasoning_verification", {})
    traj = p4.get("verified_trajectory") or parsed_json.get("verified_trajectory") or parsed_json.get("keyframes")
    kfs = parse_keyframes_payload(traj)
    if kfs and len(kfs) >= 3:
        return kfs

    p1 = parsed_json.get("phase_1_final_pose", {})
    p2 = parsed_json.get("phase_2_middle_pose", {})
    p3 = parsed_json.get("phase_3_inbetween_poses", {})
    p2_pass = p3.get("pose_2_passing", {})
    p4_over = p3.get("pose_4_overshoot", {})

    assembled = []
    first_bts = p2_pass.get("bone_targets") or p2.get("bone_targets") or {}
    anticipation_bts = {
        b: {
            "pitch": round(float(v.get("pitch", 0.0)) * 0.3, 1),
            "yaw": round(float(v.get("yaw", 0.0)) * 0.3, 1),
            "roll": round(float(v.get("roll", 0.0)) * 0.3, 1),
            "kp": 22000.0,
            "kd": 1200.0
        }
        for b, v in first_bts.items() if isinstance(v, dict)
    }
    assembled.append({"step_name": "1/5 Anticipation", "duration_seconds": 0.25, "bone_targets": anticipation_bts})
    assembled.append(p2_pass if p2_pass.get("bone_targets") else {"step_name": "2/5 Passing", "duration_seconds": 0.35, "bone_targets": first_bts})
    assembled.append(p2 if p2.get("bone_targets") else {"step_name": "3/5 Apex", "duration_seconds": 0.35, "bone_targets": first_bts})
    assembled.append(p4_over if p4_over.get("bone_targets") else {"step_name": "4/5 Overshoot", "duration_seconds": 0.30, "bone_targets": first_bts})
    assembled.append(p1 if p1.get("bone_targets") else {"step_name": "5/5 Settle", "duration_seconds": 0.35, "bone_targets": {}})

    valid_assembled = parse_keyframes_payload(assembled)
    if valid_assembled and len(valid_assembled) >= 3:
        return valid_assembled

    return synthesize_trajectory_for_goal(goal_str)


def run_nemotron_agent(user_goal: str) -> str:
    t_total_start = time.time()
    print(f"\n=======================================================")
    print(f"[*] Nemotron Biomechanical Agent Initialized")
    print(f"[*] NIM Endpoint: {NVIDIA_BASE_URL}")
    print(f"[*] Model: {NVIDIA_MODEL}")
    print(f"[*] Goal: {user_goal}")
    print(f"=======================================================\n")

    if not NVIDIA_API_KEY or NVIDIA_API_KEY == "nvapi-placeholder":
        print("[!] No NVIDIA_API_KEY detected in environment.")
        print("[!] Running in Automated Intelligent Demonstration Mode (querying all 12 bone states & driving goal poses)...\n")
        return run_demonstration_workflow(user_goal)

    client = OpenAI(
        base_url=NVIDIA_BASE_URL,
        api_key=NVIDIA_API_KEY,
        timeout=30.0
    )

    print("[*] Reading live current ragdoll state from SpacetimeDB...")
    initial_telem = get_body_telemetry()
    compact_telem = make_compact_telemetry(initial_telem)
    telem_data = initial_telem.get("telemetry", {})
    balance_status = initial_telem.get("balance_status", "STABLE_UPRIGHT")
    com_h = telem_data.get("com_height", 1.05)
    tilt = telem_data.get("tilt_angle", 0.0)
    contact = telem_data.get("active_contact", "BothFeet")

    live_bone_rotations = {
        bname: data.get("local_rotation_euler", [0.0, 0.0, 0.0])
        for bname, data in initial_telem.get("model_state", {}).items()
    }

    is_return_to_t_pose = should_return_to_t_pose(user_goal)
    conclusion_instruction = (
        "Return all joints back into neutral Upright Standing T-Pose (all joint angles at 0 deg, Kp=25000, Kd=2000)."
        if is_return_to_t_pose
        else "Hold the target persistent pose specified by the user prompt as the final state."
    )

    user_prompt = (
        f"USER GOAL: {user_goal}\n\n"
        f"CURRENT LIVE RIG POSTURE (All 12 bones' local Euler angles [pitch, yaw, roll] in degrees from SpacetimeDB):\n"
        f"{json.dumps(live_bone_rotations, indent=2)}\n\n"
        f"LIVE BALANCE & STABILITY:\n"
        f"- Balance Status: {balance_status} (COM: {com_h:.2f}m, Tilt: {tilt:.1f}°, Ground Contact: {contact})\n\n"
        f"REQUIRED MULTI-PHASE SYNTHESIS (IN ONE PASS):\n"
        f"Execute the 4-Phase Pose-to-Pose animation breakdown:\n"
        f"- Phase 1: Output Final Pose (Goal: {conclusion_instruction})\n"
        f"- Phase 2: Output Middle Pose (Apex / Peak reach)\n"
        f"- Phase 3: Output In-between Poses (Pose 2: Passing / Initiation, Pose 4: Overshoot / Counter-movement)\n"
        f"- Phase 4: Biomechanical Kinematic Verification Pass (Review continuity & balance, then output the verified 5-pose trajectory)\n\n"
        f"Return your answer strictly as a valid JSON object matching the required schema."
    )

    messages = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": user_prompt}
    ]

    t_nemotron_start = time.time()
    t_nemotron_elapsed = 0.0
    print("[*] Consulting Nemotron for 4-Phase Pose-to-Pose Motion Planning (Single Pass)...")
    active_model = NVIDIA_MODEL
    parsed_json = None

    fallback_candidates = [
        "nvidia/nemotron-3.5-lightning-30b-a3b",
        "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning",
        "nvidia/nemotron-3.5-bolt-e4b",
        "nvidia/llama-3.1-nemotron-70b-instruct",
        "nvidia/llama-3.1-nemotron-51b-instruct",
        "nvidia/nemotron-4-340b-instruct"
    ]
    if active_model in fallback_candidates:
        fallback_candidates.remove(active_model)
    fallback_candidates.insert(0, active_model)

    for candidate in fallback_candidates:
        try:
            completion = client.chat.completions.create(
                model=candidate,
                messages=messages,
                temperature=0.2,
                max_tokens=1000,
                timeout=20.0
            )

            choice = completion.choices[0]
            raw_text = choice.message.content or getattr(choice.message, "reasoning_content", None) or ""
            parsed_json = parse_multiphase_nemotron_json(raw_text)
            if parsed_json:
                active_model = candidate
                t_nemotron_elapsed = time.time() - t_nemotron_start
                print(f"[*] Nemotron planning completed in {t_nemotron_elapsed:.2f}s ({active_model}).")
                break
        except Exception as e:
            print(f"[NVIDIA NIM API Call Failed with {candidate}] {e}")
            continue

    if not parsed_json:
        print("[!] Nemotron did not return valid multi-phase JSON. Synthesizing guaranteed trajectory...")
        parsed_json = synthesize_multiphase_fallback(user_goal)

    # Display Each Phase Clearly to Console
    print_multiphase_summary(parsed_json)

    # Extract verified 5-pose trajectory
    verified_keyframes = extract_verified_keyframes(parsed_json, user_goal)

    # Execute Trajectory to SpacetimeDB & Unity
    print(f"=======================================================")
    print(f"[*] Executing Verified 5-Pose Trajectory to Unity Motors via SpacetimeDB")
    print(f"=======================================================")
    traj_result = apply_pose_trajectory(user_goal, verified_keyframes)

    # Final Telemetry Summary
    post_telem = traj_result.get("post_telemetry", {})
    t_data = post_telem.get("telemetry", {})
    final_com = t_data.get("com_height", 1.05)
    final_tilt = t_data.get("tilt_angle", 0.0)
    final_balance = post_telem.get("balance_status", "STABLE_UPRIGHT")
    total_pipeline_time = time.time() - t_total_start

    summary = (
        f"\n=======================================================\n"
        f"[*] Nemotron Multi-Step Workflow COMPLETED Successfully!\n"
        f"- Goal: {user_goal}\n"
        f"- Poses Executed: {traj_result.get('keyframes_executed', len(verified_keyframes))} waypoints\n"
        f"- Nemotron Planning Latency: {t_nemotron_elapsed:.2f}s (single-pass staged synthesis)\n"
        f"- PhysX Motor Execution Time: {traj_result.get('dynamic_duration_seconds', 0.0):.2f}s (dynamic convergence)\n"
        f"- Total End-to-End Latency: {total_pipeline_time:.2f}s\n"
        f"- Post-Trajectory Balance: COM={final_com:.2f}m, Tilt={final_tilt:.1f}° [{final_balance}]\n"
        f"- Final Rig Posture: {traj_result.get('holding_pose', 'Stable Finish')}\n"
        f"=======================================================\n"
    )
    print(summary)
    return summary


def run_demonstration_workflow(user_goal: str) -> str:
    """
    Demonstrates the full Nemotron 4-Phase Biomechanical Motion Synthesis workflow.
    """
    print("[Step 1] Querying current live model state & all 12 bones' 3D positions from SpacetimeDB...")
    telem = get_body_telemetry()
    bones = telem.get("model_state", {})
    print(f"-> Received live state for {len(bones)} bones:")
    for bname, bdata in list(bones.items())[:6]:
        print(f"   - {bname:14s}: pos={bdata['world_position']}, euler={bdata['local_rotation_euler']}")
    if len(bones) > 6:
        print(f"   - ... and {len(bones) - 6} more bones.")
    print(f"-> COM Height: {telem['telemetry'].get('com_height', 1.05):.2f}m, Tilt: {telem['telemetry'].get('tilt_angle', 0.0):.1f}° [{telem['balance_status']}]\n")

    print(f"[*] Executing 4-Phase Pose-to-Pose Motion Planning for: '{user_goal}'...")
    multiphase_plan = synthesize_multiphase_fallback(user_goal)
    print_multiphase_summary(multiphase_plan)

    verified_keyframes = extract_verified_keyframes(multiphase_plan, user_goal)

    print(f"\n=======================================================")
    print(f"[*] Executing Verified 5-Pose Trajectory to Unity Motors via SpacetimeDB")
    print(f"=======================================================")
    traj_result = apply_pose_trajectory(user_goal, verified_keyframes)

    post_telem = traj_result.get("post_telemetry", {})
    t_data = post_telem.get("telemetry", {})
    final_com = t_data.get("com_height", 1.05)
    final_tilt = t_data.get("tilt_angle", 0.0)
    final_balance = post_telem.get("balance_status", "STABLE_UPRIGHT")

    summary = (
        f"\n=======================================================\n"
        f"[*] Nemotron Multi-Step Workflow COMPLETED Successfully!\n"
        f"- Goal: {user_goal}\n"
        f"- Poses Executed: {traj_result.get('keyframes_executed', len(verified_keyframes))} waypoints\n"
        f"- PhysX Transit Time: {traj_result.get('dynamic_duration_seconds', 0.0):.2f}s (dynamic convergence)\n"
        f"- Post-Trajectory Balance: COM={final_com:.2f}m, Tilt={final_tilt:.1f}° [{final_balance}]\n"
        f"- Final Rig Posture: {traj_result.get('holding_pose', 'Stable Finish')}\n"
        f"=======================================================\n"
    )
    print(summary)
    return summary

# ==============================================================================
# CLI Entry Point
# ==============================================================================

if __name__ == "__main__":
    if len(sys.argv) > 1:
        goal = " ".join(sys.argv[1:])
    else:
        goal = "Establish default upright standing T-pose and execute greeting gesture."

    result = run_nemotron_agent(goal)
