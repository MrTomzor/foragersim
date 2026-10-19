"""
Run the simulator in human control mode for a single world/agent/task preset.

Usage:
    python -m ratsim.human_control_test
    python -m ratsim.human_control_test --world_preset default --agent_preset sphereagent_2d_lidar
    python -m ratsim.human_control_test --world_preset hilly_forest --task_preset default --seed 42

When imported, use run_human_session() directly.
"""

import argparse
import select
import sys
import termios
import time
import tty

from ratsim.roslike_unity_connector.connector import RoslikeUnityConnector
from ratsim.roslike_unity_connector.message_definitions import (
    BoolMessage,
    Float32Message,
    StringMessage,
)
from ratsim.config_blender import blend_presets, to_entries_json
from ratsim.config_blender.blender import flatten_config
from ratsim.task_tracker import TaskTracker


class _CbreakStdin:
    """Context manager that puts stdin in cbreak mode so single keypresses can be
    polled non-blockingly via read_key(). Restores terminal settings on exit.

    Falls back to a no-op when stdin is not a TTY (e.g. piped input, headless run).
    """

    def __init__(self):
        self._fd = None
        self._old_attrs = None

    def __enter__(self):
        if not sys.stdin.isatty():
            return self
        self._fd = sys.stdin.fileno()
        self._old_attrs = termios.tcgetattr(self._fd)
        tty.setcbreak(self._fd)
        return self

    def __exit__(self, exc_type, exc, tb):
        if self._fd is not None and self._old_attrs is not None:
            termios.tcsetattr(self._fd, termios.TCSADRAIN, self._old_attrs)

    def read_key(self):
        """Return one character if available, else None. Never blocks."""
        if self._fd is None:
            return None
        ready, _, _ = select.select([sys.stdin], [], [], 0)
        if not ready:
            return None
        return sys.stdin.read(1)


def run_human_session(
    conn: RoslikeUnityConnector,
    world_config: dict,
    agent_config: dict,
    task_config: dict,
    seed: int | None = None,
    rtf: float = 1.0,
    max_steps: int | None = None,
    record_trajectory: bool = False,
) -> dict:
    """Run a single human-controlled episode. Returns metrics dict when the episode ends.

    Assumes conn is already connected and scene is selected.
    Blocks until episode termination or truncation.
    If max_steps is None, uses the task config's episode_max_steps.
    If max_steps <= 0, the episode runs indefinitely (no truncation).
    With record_trajectory=True the returned dict also carries "trajectory"
    (TaskTracker.get_trajectory() arrays) for plotting.
    """
    flat_world = flatten_config(world_config)
    flat_agent = flatten_config(agent_config)
    agent_prefix = flat_agent.get("name_prefix", "rat1")
    tracker = TaskTracker(
        task_config,
        world_width=float(flat_world["world_bounds/width"]) if "world_bounds/width" in flat_world else None,
        world_height=float(flat_world["world_bounds/height"]) if "world_bounds/height" in flat_world else None,
        pose_topic=f"/{agent_prefix}/gt_pose",
        lidar_topic="/lidar2d",
        record_trajectory=record_trajectory,
    )
    tracker.reset()

    # Apply seed if given
    cfg = dict(world_config)
    if seed is not None:
        cfg["seed"] = seed

    # Send configs and reset
    conn.publish(StringMessage(data=to_entries_json(cfg)), "/sim_control/world_config")
    conn.publish(BoolMessage(data=True), "/sim_control/reset_episode")
    conn.send_messages_and_step(enable_physics_step=True)
    conn.read_messages_from_unity()
    conn.process_worldgen_status()

    # Let worldgen settle
    conn.publish(BoolMessage(data=True), "/enable_human_control")
    conn.send_messages_and_step(enable_physics_step=True)
    conn.read_messages_from_unity()
    conn.process_worldgen_status()

    # Episode loop — Python just ticks the sim and reads metrics,
    # Unity handles human input directly. In Unity's wait-for-input mode
    # (Twist2DActuator.humanControlWaitForInput) a tick with no key pressed is
    # held: no physics, no timers, and /sim_control/step_skipped comes back.
    # Held ticks don't count as episode steps.
    # Physics step is 0.02s (50Hz). RTF=1.0 means 50 ticks/s real-time.
    PHYSICS_DT = 0.02
    target_dt = PHYSICS_DT / rtf  # wall-clock seconds between ticks
    step_count = 0
    if max_steps is None:
        max_steps = tracker.episode_max_steps

    print(f"Human control active. Max steps: {max_steps if max_steps > 0 else 'unlimited'}, RTF: {rtf}")
    print("Keys (Unity window): WASD = move, Space = wait in place, hold Shift = slow. "
          "Time only advances while a key is held.")
    print("Hotkeys (in this terminal): R = reload world with seed+1, Q = quit")

    reload_requested = False
    quit_requested = False
    with _CbreakStdin() as kb:
        while True:
            frame_start = time.perf_counter()

            conn.send_messages_and_step(enable_physics_step=True)
            msgs = conn.read_messages_from_unity()
            step_held = bool(msgs.get("/sim_control/step_skipped"))

            if not step_held:
                step_count += 1
                tracker.update_with_unity_msgs(msgs)

                # Send step score back to Unity for UI visualization
                conn.publish(Float32Message(data=tracker.get_this_step_score()), "/step_score")

            if not step_held and step_count % 100 == 0:
                print(f"  step {step_count} | score={tracker.get_total_score():.3f} "
                      f"| pickups={tracker.get_num_reward_objs_picked_up()} "
                      f"| collisions={tracker.get_collision_count()}")

            # Drain any pending keypresses; latest non-noop wins this step.
            while True:
                ch = kb.read_key()
                if ch is None:
                    break
                if ch in ("r", "R"):
                    reload_requested = True
                elif ch in ("q", "Q"):
                    quit_requested = True

            terminated = tracker.is_terminated()
            truncated = max_steps > 0 and step_count >= max_steps

            if reload_requested:
                print(f"Reload requested at step {step_count} — reloading with seed+1")
                break
            if quit_requested:
                print(f"Quit requested at step {step_count}")
                break
            if terminated:
                print(f"Episode terminated at step {step_count}: {tracker.get_termination_reason()}")
                tracker.print_exploration_summary(prefix="end-of-episode")
                break
            if truncated:
                print(f"Episode truncated at step {step_count} (max steps reached)")
                tracker.print_exploration_summary(prefix="end-of-episode")
                break

            elapsed = time.perf_counter() - frame_start
            sleep_time = target_dt - elapsed
            if sleep_time > 0:
                time.sleep(sleep_time)

    # Disable human control
    conn.publish(BoolMessage(data=False), "/enable_human_control")
    conn.send_messages_and_step(enable_physics_step=False)
    conn.read_messages_from_unity()

    result = {
        "world_seed": cfg.get("seed"),
        "steps": step_count,
        "total_score": tracker.get_total_score(),
        "objects_found": tracker.get_num_reward_objs_picked_up(),
        "collisions": tracker.get_collision_count(),
        "explored_area_m2": tracker.get_explored_area_m2(),
        "terminated": terminated,
        "truncated": truncated,
        "termination_reason": tracker.get_termination_reason(),
        "reload_requested": reload_requested,
        "quit_requested": quit_requested,
    }
    if record_trajectory:
        result["trajectory"] = tracker.get_trajectory()
    return result


def main():
    parser = argparse.ArgumentParser(description="Run simulator in human control mode")
    parser.add_argument("--world", default="default")
    parser.add_argument("--agent", default="sphereagent_2d_lidar")
    parser.add_argument("--task", default="default")
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--rtf", type=float, default=1.0,
                        help="Real-time factor. 1.0 = real-time, 0.5 = half speed, 2.0 = double speed")
    args = parser.parse_args()

    world_config = blend_presets("world", [args.world])
    agent_config = blend_presets("agents", [args.agent])
    task_config = blend_presets("task", [args.task])

    conn = RoslikeUnityConnector(verbose=False)
    conn.connect()

    # Select scene and send agent config
    conn.publish(StringMessage(data="Wildfire"), "/sim_control/scene_select")
    conn.send_messages_and_step(enable_physics_step=False)
    conn.read_messages_from_unity()

    conn.publish(StringMessage(data=to_entries_json(agent_config)), "/sim_control/agent_config")
    conn.send_messages_and_step(enable_physics_step=False)
    conn.read_messages_from_unity()

    print(f"World: {args.world}, Agent: {args.agent}, Task: {args.task}")

    episode = 0
    current_seed = args.seed
    try:
        while True:
            episode += 1
            print(f"\n{'='*60}")
            print(f"Episode {episode} (seed={current_seed})")

            result = run_human_session(conn, world_config, agent_config, task_config, seed=current_seed, rtf=args.rtf, max_steps=0)
            print(f"\nResults: {result}")

            if result.get("quit_requested"):
                break
            if result.get("reload_requested"):
                # If no seed was provided, start the increment chain from 0.
                current_seed = 0 if current_seed is None else current_seed + 1
    except KeyboardInterrupt:
        print("\nExiting.")


if __name__ == "__main__":
    main()
