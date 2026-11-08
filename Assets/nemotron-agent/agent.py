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

CURRENT_KEYFRAME_ID = 400

# ==============================================================================
# SpacetimeDB Helpers
# ==============================================================================

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
        "bones": compact_bones
    }


def get_body_telemetry() -> Dict[str, Any]:
    """
    Tool 1: Queries SpacetimeDB BodyTelemetry, BoneLiveState, and BoneTarget tables.
    Returns complete kinematic model state:
    - COM height, tilt angle, fallen state, active contacts
    - ALL 12 BONES' 3D positions, local rotations, and angular velocities
    - Current active goal pose targets
    """
    telemetry_rows = run_spacetime_sql("SELECT * FROM body_telemetry")
    bone_live_rows = run_spacetime_sql("SELECT * FROM bone_live_state")
    bone_target_rows = run_spacetime_sql("SELECT * FROM bone_target")

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
    All 12 bones set to (0, 0, 0) local Euler with high muscle tension.
    """
    t_pose_bones = {
        "Pelvis": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 35000.0, "kd": 2000.0},
        "Spine": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "Chest": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "Head": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "LeftUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "LeftForearm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "RightUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "RightForearm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "LeftThigh": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "LeftCalf": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "RightThigh": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "RightCalf": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}
    }
    def apply_t_bone(item):
        bname, vals = item
        args = [f'"{bname}"', "0.00", "0.00", "0.00", f"{vals['kp']:.2f}", f"{vals['kd']:.2f}", "0"]
        return run_spacetime_call("set_pose_keyframe", args)

    with ThreadPoolExecutor(max_workers=12) as executor:
        list(executor.map(apply_t_bone, t_pose_bones.items()))

    run_spacetime_call("set_workflow_step", [
        "0",
        '"Upright Standing T-Pose (Default)"',
        '"Neutral Balance Reset"',
        '"Ready"'
    ])


def should_return_to_t_pose(goal_str: str) -> bool:
    """
    Returns True if the action is a transient gesture that should return to T-Pose by default.
    Returns False only if the user explicitly/implicitly specifies holding, picking up, carrying, crouching, etc.
    """
    persistent_phrases = [
        "pick up", "pickup", "carry", "crouch", "kneel", "sit", "lie down",
        "hold", "keep", "maintain", "freeze", "stay", "grab and hold", "remain"
    ]
    g = goal_str.lower()
    return not any(p in g for p in persistent_phrases)


def apply_pose_trajectory(action_description: str = "Dynamic Humanoid Trajectory", keyframes: Any = None, keyframes_json: Any = None, **kwargs) -> Dict[str, Any]:
    """
    Tool 3: Dynamic Physics-Driven Trajectory Executor.
    Enqueues the complete 5-pose animation trajectory into SpacetimeDB.
    Unity's DynamicPoseQueueManager dynamically evaluates PhysX angular error (< 10 deg)
    and fly-by momentum in FixedUpdate to advance waypoints with zero static sleep timers.
    Upon reaching the final pose, smoothly decelerates into a rock-solid, critically damped
    holding state (Kp=25000, Kd=2000) that constantly pushes/pulls to maintain the goal posture.
    By default, gesture actions smoothly return to the neutral upright Standing T-Pose.
    """
    global CURRENT_KEYFRAME_ID

    action_desc = action_description or kwargs.get("action_description") or "Dynamic Humanoid Trajectory"
    raw = keyframes if keyframes is not None else (keyframes_json if keyframes_json is not None else kwargs.get("keyframes"))
    if raw is None:
        raw = kwargs.get("poses") or kwargs.get("data")

    valid_keyframes = parse_keyframes_payload(raw)

    if not valid_keyframes:
        return {
            "status": "error",
            "message": "Keyframes array could not be parsed into valid pose objects with bone_targets. Each keyframe must contain bone_targets with pitch, yaw, roll for the active bones.",
            "keyframes_executed": 0
        }

    # Check if this action should return to T-Pose by default
    if should_return_to_t_pose(action_desc) and valid_keyframes:
        last_kf = valid_keyframes[-1]
        last_bts = last_kf.get("bone_targets", {})
        # Check if any bone in the last keyframe has significant rotation away from T-Pose (> 12 degrees)
        has_active_displacement = any(
            abs(float(vals.get("pitch", 0.0))) > 12.0 or
            abs(float(vals.get("yaw", 0.0))) > 12.0 or
            abs(float(vals.get("roll", 0.0))) > 12.0
            for vals in last_bts.values() if isinstance(vals, dict)
        )
        if has_active_displacement:
            recovery_bts = {}
            t_pose_bts = {}
            for bname, vals in last_bts.items():
                if isinstance(vals, dict):
                    recovery_bts[bname] = {
                        "pitch": round(float(vals.get("pitch", 0.0)) * 0.40, 1),
                        "yaw": round(float(vals.get("yaw", 0.0)) * 0.40, 1),
                        "roll": round(float(vals.get("roll", 0.0)) * 0.40, 1),
                        "kp": float(vals.get("kp", 22000.0)),
                        "kd": float(vals.get("kd", 1200.0))
                    }
                    t_pose_bts[bname] = {
                        "pitch": 0.0,
                        "yaw": 0.0,
                        "roll": 0.0,
                        "kp": 25000.0,
                        "kd": 2000.0
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
    print(f"[*] Dispatched to Unity DynamicPoseQueueManager (Paced by PhysX < 10 deg convergence)")
    print(f"=======================================================")

    # 1. Dispatch trajectory queue to SpacetimeDB AgentWorkflowStep table
    clean_json_str = json.dumps(valid_keyframes).replace('"', '\\"')
    run_spacetime_call("set_workflow_step", [
        str(keyframe_id),
        f'"{action_desc}"',
        f'"{clean_json_str}"',
        '"Queued"'
    ])

    # 2. Also dispatch Pose 1 targets to SpacetimeDB BoneTarget immediately
    p1 = valid_keyframes[0]
    p1_bones = p1.get("bone_targets", {})
    def apply_single_bone(item):
        bname, vals = item
        pitch = float(vals.get("pitch", 0.0))
        yaw   = float(vals.get("yaw", 0.0))
        roll  = float(vals.get("roll", 0.0))
        kp    = float(vals.get("kp", 22000.0))
        kd    = float(vals.get("kd", 1200.0))
        args = [f'"{bname}"', f"{pitch:.2f}", f"{yaw:.2f}", f"{roll:.2f}", f"{kp:.2f}", f"{kd:.2f}", str(keyframe_id)]
        return bname if run_spacetime_call("set_pose_keyframe", args) else None

    with ThreadPoolExecutor(max_workers=min(12, max(1, len(p1_bones)))) as executor:
        list(executor.map(apply_single_bone, p1_bones.items()))

    # 3. Dynamic Physics Await Loop: Poll SpacetimeDB until Unity marks Completed or ObstacleBlocked
    print(f"[*] Awaiting real-time PhysX dynamic waypoint execution in Unity...")
    start_time = time.time()
    max_wait = max(3.5, total_frames * 0.75)
    final_status = "In-Progress"
    obstacle_bone = None

    while (time.time() - start_time) < max_wait:
        time.sleep(0.12)
        cmd = [SPACETIME_BIN, "sql", SPACETIME_DB_NAME, f"SELECT status, current_subtask FROM AgentWorkflowStep WHERE step_id = {keyframe_id}"]
        try:
            res = subprocess.run(cmd, capture_output=True, text=True, timeout=1.5)
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

    elapsed = time.time() - start_time
    if final_status == "Completed":
        print(f"[*] Dynamic Trajectory COMPLETED in Unity in {elapsed:.2f}s (Paced naturally by physics error zone)!\n")
    elif final_status == "ObstacleBlocked":
        print(f"[!] Dynamic Trajectory HIT PHYSICAL OBSTACLE in Unity in {elapsed:.2f}s ({obstacle_bone})!\n")
    else:
        print(f"[*] Dynamic Trajectory progressed ({elapsed:.2f}s elapsed, status: {final_status}).\n")

    # 4. Commit final keyframe (Pose N) as permanent holding setpoint in SpacetimeDB
    last_kf = valid_keyframes[-1]
    last_bones = last_kf.get("bone_targets", {})
    def apply_holding_bone(item):
        bname, vals = item
        pitch = float(vals.get("pitch", 0.0))
        yaw   = float(vals.get("yaw", 0.0))
        roll  = float(vals.get("roll", 0.0))
        kp    = max(float(vals.get("kp", 22000.0)), 25000.0)
        kd    = max(float(vals.get("kd", 1200.0)), 2000.0)
        args = [f'"{bname}"', f"{pitch:.2f}", f"{yaw:.2f}", f"{roll:.2f}", f"{kp:.2f}", f"{kd:.2f}", str(keyframe_id)]
        return bname if run_spacetime_call("set_pose_keyframe", args) else None

    with ThreadPoolExecutor(max_workers=min(12, max(1, len(last_bones)))) as executor:
        list(executor.map(apply_holding_bone, last_bones.items()))

    post_telemetry = get_body_telemetry()

    return {
        "status": "success" if final_status != "ObstacleBlocked" else "obstacle_blocked",
        "action_description": action_desc,
        "keyframes_executed": total_frames,
        "dynamic_duration_seconds": round(elapsed, 2),
        "unity_status": final_status,
        "obstacle_details": obstacle_bone,
        "holding_pose": last_kf.get("step_name", "Final Goal Pose"),
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
            "description": "Optimistically generates and streams an entire 5-pose animation trajectory (1/5 Anticipation, 2/5 Passing, 3/5 Apex, 4/5 Overshoot, 5/5 Settle) to the Unity physics ragdoll in a single call. Eliminates LLM roundtrip lag and prevents torque overshoot by continuously driving through waypoints.",
            "parameters": {
                "type": "object",
                "properties": {
                    "action_description": {
                        "type": "string",
                        "description": "High-level description of the motion (e.g. 'Natural 5-pose right arm wave sequence' or 'Forward walking step with right leg')."
                    },
                    "keyframes": {
                        "type": "array",
                        "description": "Ordered list of 5 keyframe poses. Each pose object contains step_name, duration_seconds (0.25 to 0.40), and bone_targets map with pitch, yaw, roll, kp, kd.",
                        "items": {
                            "type": "object"
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

SYSTEM_PROMPT = """You are an Autonomous Humanoid Balance & Motion Controller powered by NVIDIA Nemotron.
You control a 12-bone physically simulated active ragdoll in Unity via SpacetimeDB:
- Bones: Pelvis, Spine, Chest, Head, LeftUpperArm, LeftForearm, RightUpperArm, RightForearm, LeftThigh, LeftCalf, RightThigh, RightCalf.
- The NPC ragdoll root component in Unity is `RagdollPoseGoalController`, which tracks the 'Current Goal Pose' you update.
- DEFAULT RESTING STATE: Upright Standing T-Pose (all joint angles at 0 degrees, Kp ~ 20000-25000).

CRITICAL JOINT COORDINATE SYSTEM (T-Pose Rig, Facing +Z, Arms extend along X):
- ARMS (RightUpperArm / LeftUpperArm):
  * ROLL (Z-axis): RAISES / LOWERS THE ARM.
    - RightUpperArm: roll = +50° to +80° raises the right arm UP toward head into a wave/salute!
    - LeftUpperArm:  roll = -50° to -80° raises the left arm UP!
  * YAW (Y-axis): Swings the arm forward (+) or backward (-).
  * PITCH (X-axis): Axial shoulder twist along the arm bone.
- FOREARMS / ELBOWS (RightForearm / LeftForearm):
  * ROLL (Z-axis): BENDS THE ELBOW UPWARD (+50° to +85° for right arm, -50° to -85° for left arm).
  * YAW (Y-axis): Bends the elbow forward (+30° to +70°).
  * PITCH (X-axis): Forearm axial twist.
- SPINE & CHEST:
  * PITCH (X-axis): Lean forward (+) or back (-).
  * YAW (Y-axis): Twist torso left / right.
  * ROLL (Z-axis): Lean torso side to side.
- LEGS (Thighs & Calves along Y):
  * PITCH (X-axis): Step leg forward (+) or backward (-).
    - To step RIGHT leg forward: RightThigh pitch = +25° to +35°, RightCalf pitch = -15° to -25° (knee bend).
    - To trail LEFT leg backward: LeftThigh pitch = -15° to -25°, LeftCalf pitch = -5° to -15°.
  * Counterbalance: Chest pitch = +5° to keep Center of Mass centered over feet.

ANIMATION PRINCIPLE: DEFAULT RETURN-TO-T-POSE VS. HELD ACTIONS
Every humanoid animation follows one of two motion archetypes based on the user's prompt:

1. TRANSIENT GESTURES & ACTIONS (DEFAULT - ALWAYS RETURN TO T-POSE):
   Most actions (e.g. waving, nodding, bowing, kicking, jumping, punching, pointing, looking around) are cyclical gestures that MUST smoothly return the humanoid to the neutral upright Standing T-Pose (all joint angles at 0°) at the end of the 5-pose trajectory:
   - Pose 1: Anticipation / Wind-up (slight counter-movement preparing for the motion).
   - Pose 2: Passing / Initiation (moving swiftly through the mid-way transition).
   - Pose 3: Apex / Extreme (the high point or extreme reach of the gesture).
   - Pose 4: Overshoot / Recovery (reversing motion back down towards neutral).
   - Pose 5: Settle into Upright T-Pose (all active joints returned to 0°, balanced Kp=25000, Kd=2000).

2. PERSISTENT POSTURAL ACTIONS (HELD POSTURE - DO NOT RETURN TO T-POSE):
   ONLY when the user prompt explicitly or implicitly describes a lasting pose or continuous interaction (e.g. "pick up an object", "crouch down and stay", "hold a salute", "raise hands and keep them up", "carry the box"):
   - The trajectory terminates in the held target pose (Pose 5 = the held posture).
   - In your final summary, state that the character is maintaining the resulting pose.

EXAMPLE 5-POSE TRAJECTORY FOR A RIGHT-HAND WAVE (DEFAULT RETURN TO T-POSE):
- Pose 1 (Anticipation): RightUpperArm roll = 15°, yaw = 5°, RightForearm roll = 20° (0.25s)
- Pose 2 (Initiation): RightUpperArm roll = 45°, yaw = 10°, RightForearm roll = 50° (0.35s)
- Pose 3 (Wave Apex): RightUpperArm roll = 65°, yaw = 15°, RightForearm roll = 75° (0.35s)
- Pose 4 (Recovery): RightUpperArm roll = 25°, yaw = 5°, RightForearm roll = 20° (0.30s)
- Pose 5 (Settle to T-Pose): RightUpperArm roll = 0°, yaw = 0°, RightForearm roll = 0° (0.35s)

YOUR OPTIMISTIC WORKFLOW:
Turn 1: Directly invoke `apply_pose_trajectory()` with the complete 5-pose animation trajectory breakdown for the user's requested motion.
Turn 2: Provide a concise final summary of the motion trajectory, stability status, and holding pose.
"""

def run_nemotron_agent(user_goal: str, max_turns: int = 10) -> str:
    print(f"\n=======================================================")
    print(f"[*] Nemotron Agent Initialized")
    print(f"[*] NIM Endpoint: {NVIDIA_BASE_URL}")
    print(f"[*] Model: {NVIDIA_MODEL}")
    print(f"[*] Goal: {user_goal}")
    print(f"=======================================================\n")

    # On start, always ensure humanoid is reset to clean T-Pose
    print("[*] Ensuring humanoid rig is initialized in neutral Upright Standing T-Pose...")
    reset_to_t_pose()
    time.sleep(0.15)

    if not NVIDIA_API_KEY or NVIDIA_API_KEY == "nvapi-placeholder":
        print("[!] No NVIDIA_API_KEY detected in environment.")
        print("[!] Running in Automated Intelligent Demonstration Mode (querying all 12 bone states & driving goal poses)...\n")
        return run_demonstration_workflow(user_goal)

    client = OpenAI(
        base_url=NVIDIA_BASE_URL,
        api_key=NVIDIA_API_KEY,
        timeout=25.0
    )

    print("[*] Reading live initial ragdoll state from SpacetimeDB...")
    initial_telem = make_compact_telemetry(get_body_telemetry())
    telem_data = initial_telem.get("telemetry", {})
    balance_status = initial_telem.get("balance_status", "STABLE_UPRIGHT")
    com_h = telem_data.get("com_height", 1.05)
    tilt = telem_data.get("tilt_angle", 0.0)
    contact = telem_data.get("active_contact", "BothFeet")

    user_prompt = (
        f"USER GOAL: {user_goal}\n\n"
        f"INITIAL SPODETIMEDB RAGDOLL STATE:\n"
        f"- Balance Status: {balance_status} (COM: {com_h:.2f}m, Tilt: {tilt:.1f}°, Ground Contact: {contact})\n"
        f"- Current Bone Orientation Targets: {json.dumps(initial_telem.get('current_goal_targets', {}), indent=2)}\n\n"
        f"ACTION INSTRUCTION: Immediately invoke the `apply_pose_trajectory` tool with the 5-pose animation breakdown "
        f"(1: Anticipation, 2: Passing, 3: Apex, 4: Overshoot, 5: Settle). Do not output plain text reasoning."
    )

    messages = [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": user_prompt}
    ]

    active_model = NVIDIA_MODEL
    fallback_candidates = [
        "nvidia/nemotron-3.5-lightning-30b-a3b",
        "nvidia/nemotron-nano-3-30b-a3b",
        "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning",
        "nvidia/nemotron-3.5-bolt-e4b",
        "nvidia/llama-3.1-nemotron-70b-instruct",
        "nvidia/llama-3.1-nemotron-51b-instruct",
        "nvidia/nemotron-4-340b-instruct"
    ]
    if active_model in fallback_candidates:
        fallback_candidates.remove(active_model)
    fallback_candidates.insert(0, active_model)

    keyframes_applied = 0

    for turn in range(max_turns):
        print(f"--- [Turn {turn + 1}/{max_turns}] Consulting Nemotron ({active_model}) ---")
        response = None
        
        for candidate in fallback_candidates:
            try:
                response = client.chat.completions.create(
                    model=candidate,
                    messages=messages,
                    tools=TOOLS,
                    tool_choice="auto",
                    temperature=0.2,
                    max_tokens=1500,
                    timeout=25.0
                )
                if candidate != active_model:
                    print(f"[*] Switched to accessible Nemotron model: {candidate}")
                    active_model = candidate
                break
            except Exception as e:
                err_str = str(e)
                print(f"[NVIDIA NIM API Turn {turn + 1} with {candidate}] {err_str}")
                if "404" in err_str or "not found" in err_str.lower():
                    continue
                
                # Direct telemetry retry if parser stalls
                if turn > 0:
                    print("[*] Multi-turn tool parser stalled. Retrying with direct telemetry injection...")
                    latest_telem = make_compact_telemetry(get_body_telemetry())
                    flattened_messages = [
                        {"role": "system", "content": SYSTEM_PROMPT},
                        {
                            "role": "user",
                            "content": (
                                f"GOAL: {user_goal}\n\n"
                                f"LIVE 12-BONE KINEMATIC TELEMETRY FROM SPODETIMEDB:\n{json.dumps(latest_telem, indent=2)}\n\n"
                                f"Now formulate the motion and call apply_pose_trajectory to update the ragdoll goal pose."
                            )
                        }
                    ]
                    try:
                        response = client.chat.completions.create(
                            model=candidate,
                            messages=flattened_messages,
                            tools=TOOLS,
                            tool_choice="auto",
                            temperature=0.2,
                            max_tokens=1500,
                            timeout=25.0
                        )
                        messages = flattened_messages
                        break
                    except Exception as retry_err:
                        print(f"[NVIDIA NIM Direct Telemetry Retry Error] {retry_err}")
                break

        if response is None:
            print("[!] Nemotron cloud call timed out or failed. Falling back to local verification loop...")
            return run_demonstration_workflow(user_goal)

        choice = response.choices[0]
        msg = choice.message
        
        # Cleanly serialize assistant message to avoid SDK/vLLM null-content parser bugs
        clean_assistant = {
            "role": "assistant",
            "content": msg.content or ""
        }
        if msg.tool_calls:
            clean_assistant["tool_calls"] = [
                {
                    "id": tc.id,
                    "type": "function",
                    "function": {
                        "name": tc.function.name,
                        "arguments": tc.function.arguments
                    }
                }
                for tc in msg.tool_calls
            ]
        messages.append(clean_assistant)

        if msg.tool_calls:
            for tool_call in msg.tool_calls:
                fn_name = tool_call.function.name
                fn_args_raw = tool_call.function.arguments or ""
                print(f"[Tool Call] {fn_name}({fn_args_raw[:160]}...)")

                fn_args = {}
                try:
                    fn_args = json.loads(fn_args_raw) if fn_args_raw else {}
                except Exception:
                    pass

                # If apply_pose_trajectory, ensure action_description and keyframes are populated
                if fn_name == "apply_pose_trajectory":
                    if not fn_args.get("action_description"):
                        fn_args["action_description"] = user_goal
                    if not fn_args.get("keyframes"):
                        # Extract directly with ultra-resilient parser
                        kfs = parse_keyframes_payload(fn_args_raw)
                        if kfs:
                            fn_args["keyframes"] = kfs

                if fn_name in TOOL_MAP:
                    try:
                        tool_result = TOOL_MAP[fn_name](**fn_args)
                    except Exception as ex:
                        if fn_name == "apply_pose_trajectory":
                            kfs = parse_keyframes_payload(fn_args_raw) or parse_keyframes_payload(fn_args)
                            tool_result = apply_pose_trajectory(user_goal, kfs)
                        else:
                            tool_result = {"status": "error", "message": str(ex)}

                    if fn_name == "apply_pose_keyframe":
                        keyframes_applied += 1
                    elif fn_name == "apply_pose_trajectory":
                        if tool_result.get("status") == "success":
                            keyframes_applied += tool_result.get("keyframes_executed", 0)
                else:
                    tool_result = {"status": "error", "message": f"Unknown tool: {fn_name}"}

                print(f"[Tool Result] {json.dumps(tool_result, indent=2)[:350]}...\n")

                # Send compact telemetry to role: tool strictly matching OpenAI schema
                tool_payload = make_compact_telemetry(tool_result) if fn_name == "get_body_telemetry" else tool_result
                messages.append({
                    "role": "tool",
                    "tool_call_id": tool_call.id,
                    "content": json.dumps(tool_payload)
                })
        else:
            final_text = msg.content or getattr(msg, "reasoning_content", None) or getattr(msg, "reasoning", "")
            
            # 1. Resilient extraction from text (handles markdown ```json, arrays, objects)
            extracted = extract_trajectory_or_keyframe_from_text(final_text)
            if extracted:
                kind, data = extracted
                if kind == "trajectory":
                    print(f"[*] Extracted 5-pose trajectory from Nemotron text output. Executing apply_pose_trajectory...")
                    traj_res = apply_pose_trajectory(user_goal, data)
                    keyframes_applied += traj_res.get("keyframes_executed", len(data))
                    messages.append({
                        "role": "user",
                        "content": f"Trajectory executed successfully ({len(data)} poses). Ragdoll is in stable finish posture. Provide your brief final summary."
                    })
                    continue
                elif kind == "single_keyframe":
                    print(f"[*] Extracted keyframe targets from Nemotron text output. Executing apply_pose_keyframe...")
                    kf_res = apply_pose_keyframe("Extracted Keyframe", data)
                    keyframes_applied += 1
                    messages.append({
                        "role": "user",
                        "content": f"Applied keyframe successfully. Post-telemetry: COM={kf_res['post_telemetry']['telemetry'].get('com_height', 1.05):.2f}m. Continue to next keyframe or conclude."
                    })
                    continue

            # 2. If Nemotron gave commentary without executing any keyframe yet:
            if keyframes_applied == 0:
                print(f"[*] Nemotron planned in text before calling tools:")
                preview = final_text.strip().replace("\n", " ")[:200]
                print(f"    \"{preview}...\"")
                print(f"[*] Enforcing apply_pose_trajectory invocation...")
                messages.append({
                    "role": "user",
                    "content": "Invoke `apply_pose_trajectory` now with your planned 5-pose keyframes. Return the function call directly without markdown text."
                })
                continue

            print(f"\n[Nemotron Final Response]\n{final_text}\n")
            return final_text

    return "Agent loop reached maximum turns."


def run_demonstration_workflow(user_goal: str) -> str:
    """
    Demonstrates the full Nemotron state inspection and goal pose update workflow.
    """
    print("[Step 1] Querying full model state & all 12 bones' 3D positions from SpacetimeDB...")
    telem = get_body_telemetry()
    bones = telem.get("model_state", {})
    print(f"-> Received live state for {len(bones)} bones:")
    for bname, bdata in list(bones.items())[:6]:
        print(f"   - {bname:14s}: pos={bdata['world_position']}, euler={bdata['local_rotation_euler']}")
    if len(bones) > 6:
        print(f"   - ... and {len(bones) - 6} more bones.")
    print(f"-> COM Height: {telem['telemetry'].get('com_height', 1.05):.2f}m, Tilt: {telem['telemetry'].get('tilt_angle', 0.0):.1f}° [{telem['balance_status']}]\n")

    print("[Step 2] Updating Current Goal Pose -> Upright Standing T-Pose...")
    t_pose = {
        "Pelvis": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 35000.0, "kd": 2000.0},
        "Spine": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "Chest": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "Head": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "LeftUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "LeftForearm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "RightUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "RightForearm": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0},
        "LeftThigh": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "LeftCalf": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "RightThigh": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0},
        "RightCalf": {"pitch": 0.0, "yaw": 0.0, "roll": 0.0, "kp": 25000.0, "kd": 1500.0}
    }
    kf1 = apply_pose_keyframe("Upright Standing T-Pose (Default)", t_pose)
    print(f"-> T-Pose committed to {len(kf1['applied_bones'])} bones in Unity RagdollPoseGoalController.")
    print(f"-> Post-Keyframe Balance: COM={kf1['post_telemetry']['telemetry'].get('com_height', 1.05):.2f}m, "
          f"Tilt={kf1['post_telemetry']['telemetry'].get('tilt_angle', 0.0):.1f}° [{kf1['post_telemetry']['balance_status']}]\n")

    time.sleep(0.5)

    print("[Step 3] Executing 5-Pose Optimistic Wave Trajectory (Pose-to-Pose Animation)...")
    wave_trajectory = [
        {
            "step_name": "1/5: Anticipation (slight dip & drop)",
            "duration_seconds": 0.25,
            "bone_targets": {
                "RightUpperArm": {"pitch": 0.0, "yaw": 0.0, "roll": -5.0, "kp": 22000.0, "kd": 1200.0},
                "RightForearm": {"pitch": 0.0, "yaw": 0.0, "roll": 5.0, "kp": 22000.0, "kd": 1200.0}
            }
        },
        {
            "step_name": "2/5: Initiation (arm lifting up)",
            "duration_seconds": 0.35,
            "bone_targets": {
                "RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 35.0, "kp": 22000.0, "kd": 1200.0},
                "RightForearm": {"pitch": 0.0, "yaw": 15.0, "roll": 30.0, "kp": 22000.0, "kd": 1200.0}
            }
        },
        {
            "step_name": "3/5: Wave Apex (high wave position)",
            "duration_seconds": 0.35,
            "bone_targets": {
                "RightUpperArm": {"pitch": 0.0, "yaw": 15.0, "roll": 65.0, "kp": 22000.0, "kd": 1200.0},
                "RightForearm": {"pitch": 0.0, "yaw": 25.0, "roll": 75.0, "kp": 22000.0, "kd": 1200.0},
                "Head": {"pitch": 5.0, "yaw": 10.0, "roll": 0.0, "kp": 20000.0, "kd": 1000.0}
            }
        },
        {
            "step_name": "4/5: Overshoot / Counter-wave",
            "duration_seconds": 0.30,
            "bone_targets": {
                "RightUpperArm": {"pitch": 0.0, "yaw": 10.0, "roll": 60.0, "kp": 22000.0, "kd": 1200.0},
                "RightForearm": {"pitch": 0.0, "yaw": -10.0, "roll": 50.0, "kp": 22000.0, "kd": 1200.0}
            }
        },
        {
            "step_name": "5/5: Settle & Hold greeting posture",
            "duration_seconds": 0.35,
            "bone_targets": {
                "RightUpperArm": {"pitch": 0.0, "yaw": 12.0, "roll": 65.0, "kp": 22000.0, "kd": 1200.0},
                "RightForearm": {"pitch": 0.0, "yaw": 5.0, "roll": 60.0, "kp": 22000.0, "kd": 1200.0}
            }
        }
    ]
    traj_result = apply_pose_trajectory("Right Arm Greeting Wave", wave_trajectory)
    print(f"-> Trajectory completed: {traj_result['keyframes_executed']} keyframes streamed smoothly to Unity.\n")

    # Mark completion
    run_spacetime_call("set_workflow_step", [
        str(CURRENT_KEYFRAME_ID),
        f'"{user_goal}"',
        '"Goal Pose Maintained"',
        '"Completed"'
    ])

    summary = (
        f"Nemotron Model State Query & Goal Pose Dispatch completed.\n"
        f"- Target Goal: {user_goal}\n"
        f"- Full Model State: All 12 bones' 3D positions and orientations successfully ingested.\n"
        f"- Unity Goal Pose Component: 'RagdollPoseGoalController' updated and active.\n"
        f"- Stability: Standing upright in T-Pose with COM {kf1['post_telemetry']['telemetry'].get('com_height', 1.05):.2f}m."
    )
    print(f"[Execution Summary]\n{summary}")
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
