import math

from ratsim.task_tracker.exploration_tracker import ExplorationTracker
from ratsim.task_tracker.exploration_viewer import ExplorationViewer
from ratsim.transforms import yaw_from_quat


class TaskTracker:
    """Computes per-step reward and episode termination from Unity messages.

    Driven by a task config dict (loaded from task_presets/default.json).

    Topics are matched by suffix so that both bare topics (e.g. ``/collisions``)
    and agent-namespaced topics (e.g. ``/rat1/collisions``) are handled
    automatically.  Reward pickups are summed across all agents; termination
    triggers if *any* agent hits the condition.

    Usage::

        tracker = TaskTracker(task_config)
        tracker.reset()
        # ...each step:
        tracker.update_with_unity_msgs(msgs)
        reward     = tracker.get_this_step_score()
        terminated = tracker.is_terminated()

    Volumetric exploration reward (optional):
        If ``task_config["volumetric_exploration_settings"]["reward_per_m2"]``
        is a nonzero float, TaskTracker builds an occupancy map from the
        agent's ground-truth pose + lidar scans and rewards newly-covered
        area (free + occupied) each step.  World dimensions default to
        values in the exploration settings; pass ``world_width`` /
        ``world_height`` to ``__init__`` to let the env override them from
        its worldgen config (e.g. ``world_bounds/width``).

    Trajectory recording (optional, off by default):
        With ``record_trajectory=True`` every ``update_with_unity_msgs`` call
        appends the agent's ground-truth pose (ROS frame: x forward, y left,
        z up, yaw CCW rad) and the step index to an in-memory buffer, and the
        steps at which reward pickups happened are noted.  ``get_trajectory()``
        returns it as numpy arrays; ``reset()`` clears it.  Cost is one tuple
        append per step, so it is safe to leave on for a whole eval, but it is
        meant for a handful of episodes, not training.
    """

    # Topic suffixes (matched against the end of every incoming topic name)
    _COLLISION_SUFFIX = "collisions"
    _REWARD_PICKUP_SUFFIX = "reward_pickup"
    _WRONG_WELL_SUFFIX = "wrong_activation"
    _HEALTH_SUFFIX = "health"
    _BATTERY_SUFFIX = "battery"
    _ALL_REWARDS_COLLECTED_SUFFIX = "all_rewards_collected"

    def __init__(
        self,
        task_config: dict,
        world_width: "float | None" = None,
        world_height: "float | None" = None,
        pose_topic: "str | None" = None,
        lidar_topic: "str | None" = None,
        record_trajectory: bool = False,
    ):
        self.episode_max_steps = task_config.get("episode_max_steps", 300)

        # Per-step pose buffer (opt-in — eval scripts turn it on for a few
        # episodes; training never does). Consumed via get_trajectory().
        self.record_trajectory = bool(record_trajectory)

        foraging = task_config.get("foraging_settings", {})
        self.pickup_reward_modifier = foraging.get("reward_object_pickup_modifier", 1.0)
        self.negative_pickup_modifier = foraging.get("negative_pickups_modifier", 1.0)
        # Wrong-well activations (wells task; Unity reports them when
        # well_schedule/wrong_well_detection=1). The penalty is a fraction of what a
        # correct activation would have paid: fraction * reward_at_stake *
        # reward_object_pickup_modifier. `trials` picks which trial types are
        # penalised: "home" | "random" | "both". Events are counted either way.
        self.wrong_well_penalty_fraction = float(foraging.get("wrong_well_penalty_fraction", 0.0))
        self.wrong_well_penalty_trials = str(foraging.get("wrong_well_penalty_trials", "home")).lower()
        if self.wrong_well_penalty_trials not in ("home", "random", "both"):
            raise ValueError(
                f"foraging_settings.wrong_well_penalty_trials must be home|random|both, "
                f"got {self.wrong_well_penalty_trials!r}")

        collision = task_config.get("collision_settings", {})
        self.penalize_collisions = collision.get("penalize_collisions", True)
        self.penalization_variable = collision.get("penalization_variable", "velocity")
        self.collision_penalty_modifier = collision.get("collision_penalty_modifier", 0.3)
        self.collision_flat_penalty = collision.get("collision_flat_penalty", 0.0)
        self.min_col_velocity = collision.get("min_col_velocity", 1.0)

        termination = task_config.get("termination_settings", {})
        self.terminate_on_collision = termination.get("terminate_on_collision", True)
        self.collision_termination_reward = termination.get("collision_termination_reward", 0.0)
        self.terminate_on_zero_battery = termination.get("terminate_on_zero_battery", True)
        self.zero_battery_termination_reward = termination.get("zero_battery_termination_reward", 0.0)
        self.terminate_on_zero_health = termination.get("terminate_on_zero_health", True)
        self.zero_health_termination_reward = termination.get("zero_health_termination_reward", 0.0)
        self.terminate_on_all_rewards_collected = termination.get("terminate_on_all_rewards_collected", True)
        self.all_rewards_collected_termination_reward = termination.get("all_rewards_collected_termination_reward", 0.0)

        # --- Volumetric exploration settings ---
        vol = task_config.get("volumetric_exploration_settings", {}) or {}
        self.vol_reward_per_m2 = float(vol.get("reward_per_m2", 0.0))
        self.vol_visualize = bool(vol.get("visualize", False))
        # Track whenever reward OR viz is requested — you can visualize
        # coverage without adding it to the reward signal.
        self.vol_enabled = self.vol_reward_per_m2 != 0.0 or self.vol_visualize
        self.vol_resolution = float(vol.get("grid_resolution", 1.0))
        # Explicit world dims in the config act as a fallback if the env
        # didn't forward worldgen bounds — gives a sensible default.
        cfg_w = vol.get("world_width", None)
        cfg_h = vol.get("world_height", None)
        self.vol_world_width = (
            float(world_width) if world_width is not None
            else float(cfg_w) if cfg_w is not None else None
        )
        self.vol_world_height = (
            float(world_height) if world_height is not None
            else float(cfg_h) if cfg_h is not None else None
        )
        self.vol_viz_draw_every = int(vol.get("viz_draw_every", 1))
        # Toggle per-step diagnostic prints.  Level 1 = one-line per-step,
        # level 2 = verbose (first few ranges + per-ray end points).
        self.vol_debug = int(vol.get("debug", 0))
        self.vol_debug_every = int(vol.get("debug_every", 20))
        # Topic hints (env knows these — agent's name_prefix can make the
        # suffix-matcher ambiguous for /rat1_pose vs /rat1_pose_from_start).
        self.pose_topic = pose_topic
        self.lidar_topic = lidar_topic

        self.exploration_tracker: "ExplorationTracker | None" = None
        self.exploration_viewer: "ExplorationViewer | None" = None
        if self.vol_enabled:
            if self.vol_world_width is None or self.vol_world_height is None:
                print(
                    "[TaskTracker] volumetric_exploration_settings requested "
                    "but world_width/world_height not provided — disabling exploration."
                )
                self.vol_enabled = False
            else:
                self.exploration_tracker = ExplorationTracker(
                    world_width=self.vol_world_width,
                    world_height=self.vol_world_height,
                    resolution=self.vol_resolution,
                )
                et = self.exploration_tracker
                print(
                    f"[TaskTracker] volumetric exploration enabled: "
                    f"world={self.vol_world_width}x{self.vol_world_height}m "
                    f"(width=Unity X=ROS y, height=Unity Z=ROS x), "
                    f"grid={et.cells_x}x{et.cells_y} cells (ROS x by ROS y) @ {et.resolution}m/cell, "
                    f"origin=({et.origin_x:.1f}, {et.origin_y:.1f}) [ROS frame], "
                    f"reward={self.vol_reward_per_m2}/m², "
                    f"pose_topic={self.pose_topic!r}, lidar_topic={self.lidar_topic!r}",
                    flush=True,
                )
                if self.vol_visualize:
                    self.exploration_viewer = ExplorationViewer(
                        title="Volumetric exploration",
                        draw_every=self.vol_viz_draw_every,
                    )

        self.reset()

    def reset(self):
        """Reset all episode state. Call at the start of each episode."""
        self._step_score = 0.0
        self._total_score = 0.0
        self._terminated = False
        self._termination_reason = None
        self._num_reward_objs_picked_up = 0
        self._wrong_well_activations = {"random": 0, "home": 0}
        self._collision_count = 0
        self._exploration_area_m2 = 0.0
        # Reset per-episode diagnostic state so the "first exploration update"
        # log fires on episode 1, 2, 3, … (easier to catch in long runs).
        self._logged_first_exploration_update = False
        self._warned_missing_topics = False
        self._step_update_count = 0
        self._rays_total = 0
        self._rays_out_of_bounds = 0
        self._rays_zero_len = 0
        self._update_count = 0
        self._traj_rows: list = []          # (step, x, y, z, yaw)
        self._traj_pickup_steps: list = []
        if self.exploration_tracker is not None:
            self.exploration_tracker.reset()

    def update_with_unity_msgs(self, msgs: dict):
        """Process one step's Unity messages, updating score and termination state.

        Args:
            msgs: dict mapping topic strings to lists of message objects,
                  as returned by RoslikeUnityConnector.read_messages_from_unity().
        """
        self._step_score = 0.0
        self._update_count += 1

        # --- Collisions (any agent) ---
        for col_vel in self._get_values_by_suffix(msgs, self._COLLISION_SUFFIX):
            if col_vel is None or abs(col_vel) < self.min_col_velocity:
                continue
            self._collision_count += 1
            if self.penalize_collisions:
                if self.penalization_variable == "velocity":
                    penalty = self.collision_penalty_modifier * abs(col_vel)
                else:
                    penalty = self.collision_penalty_modifier
                penalty += self.collision_flat_penalty
                self._step_score -= penalty
                print(f"[TaskTracker] Collision vel={col_vel:.2f}, penalty={penalty:.3f}")

            if self.terminate_on_collision and not self._terminated:
                self._step_score += self.collision_termination_reward
                self._terminated = True
                self._termination_reason = "collision"
                print("[TaskTracker] Terminating: collision")

        # --- Health from Unity (any agent) ---
        if self.terminate_on_zero_health and not self._terminated:
            for health in self._get_values_by_suffix(msgs, self._HEALTH_SUFFIX):
                if health is not None and health <= 0.0:
                    self._step_score += self.zero_health_termination_reward
                    self._terminated = True
                    self._termination_reason = "zero_health"
                    print("[TaskTracker] Terminating: zero health")
                    break

        # --- Reward object pickups (all agents summed) ---
        num_pickups = self._get_total_pickups(msgs)
        if num_pickups > 0:
            self._step_score += num_pickups * self.pickup_reward_modifier
            self._num_reward_objs_picked_up += num_pickups
            if self.record_trajectory:
                self._traj_pickup_steps.extend([self._update_count] * num_pickups)
            print(f"[TaskTracker] Picked up {num_pickups} objects, score += {num_pickups * self.pickup_reward_modifier:.2f}")

        # --- Wrong-well activations: [well_id, trial_type (0=random,1=home), reward_at_stake] ---
        for topic in self._topics_by_suffix(msgs, self._WRONG_WELL_SUFFIX):
            for msg in msgs[topic]:
                well_id, trial_type, at_stake = msg.data[:3]
                trial = "home" if int(trial_type) == 1 else "random"
                self._wrong_well_activations[trial] += 1
                penalty = 0.0
                if self.wrong_well_penalty_trials in (trial, "both"):
                    penalty = self.wrong_well_penalty_fraction * at_stake * self.pickup_reward_modifier
                    self._step_score -= penalty
                print(f"[TaskTracker] Wrong well {int(well_id)} on {trial} trial, penalty={penalty:.2f}")

        # --- Battery depletion (any agent) ---
        if self.terminate_on_zero_battery and not self._terminated:
            for battery in self._get_values_by_suffix(msgs, self._BATTERY_SUFFIX):
                if battery is not None and battery <= 0.0:
                    self._step_score += self.zero_battery_termination_reward
                    self._terminated = True
                    self._termination_reason = "zero_battery"
                    print("[TaskTracker] Terminating: zero battery")
                    break

        # --- All rewards collected ---
        if self.terminate_on_all_rewards_collected and not self._terminated:
            for topic in self._topics_by_suffix(msgs, self._ALL_REWARDS_COLLECTED_SUFFIX):
                if msgs[topic][0].data:
                    self._step_score += self.all_rewards_collected_termination_reward
                    self._terminated = True
                    self._termination_reason = "all_rewards_collected"
                    print("[TaskTracker] Terminating: all rewards collected")
                    break

        # --- Volumetric exploration reward ---
        if self.vol_enabled and self.exploration_tracker is not None:
            self._update_exploration(msgs)

        # --- Trajectory buffer (opt-in) ---
        if self.record_trajectory:
            self._record_pose(msgs)

        self._total_score += self._step_score

    def _record_pose(self, msgs: dict):
        pose_msg = self._find_pose_msg(msgs)
        if pose_msg is None:
            return
        yaw = yaw_from_quat(pose_msg.qx, pose_msg.qy, pose_msg.qz, pose_msg.qw)
        self._traj_rows.append((
            self._update_count,
            float(pose_msg.x), float(pose_msg.y), float(pose_msg.z or 0.0), float(yaw),
        ))

    def _update_exploration(self, msgs: dict):
        pose_msg = self._find_pose_msg(msgs)
        lidar_msg = self._find_lidar_msg(msgs)
        if pose_msg is None or lidar_msg is None:
            if not getattr(self, "_warned_missing_topics", False):
                self._warned_missing_topics = True
                print(
                    f"[TaskTracker] Exploration skipped — missing topics. "
                    f"Got pose={pose_msg is not None}, lidar={lidar_msg is not None}. "
                    f"Configured pose_topic={self.pose_topic!r}, lidar_topic={self.lidar_topic!r}. "
                    f"Available topics: {sorted(msgs.keys())}"
                )
            return

        agent_yaw = yaw_from_quat(pose_msg.qx, pose_msg.qy, pose_msg.qz, pose_msg.qw)

        # Unity's lidar publishes angles in Unity local frame
        # (angle=0 → Unity +Z = forward; angle=+90 → Unity +X = right).
        # ExplorationTracker expects standard ROS math convention
        # (angle=0 → local +x forward; angle grows CCW toward local +y left).
        # Unity angle α corresponds to ROS angle −α, so negate here. Without
        # this flip every ray is mirrored left↔right, which places occupied
        # cells on the wrong side of the agent.
        angle_start_rad = -math.radians(lidar_msg.angleStartDeg)
        angle_increment_rad = -math.radians(lidar_msg.angleIncrementDeg)

        stats = self.exploration_tracker.update_from_lidar(
            agent_x=pose_msg.x,
            agent_y=pose_msg.y,
            agent_yaw=agent_yaw,
            ranges=lidar_msg.ranges,
            angle_start_rad=angle_start_rad,
            angle_increment_rad=angle_increment_rad,
            max_range=lidar_msg.maxRange,
        )
        newly_known = stats["newly_known"]
        self._rays_total += stats["rays_total"]
        self._rays_out_of_bounds += stats["rays_out_of_bounds"]
        self._rays_zero_len += stats["rays_zero_len"]

        et = self.exploration_tracker
        self._step_update_count += 1

        # First-update diagnostic: fires unconditionally on every episode's
        # first exploration update so we can see pose/grid alignment.
        if not self._logged_first_exploration_update:
            self._logged_first_exploration_update = True
            agent_col, agent_row = et.world_to_cell(pose_msg.x, pose_msg.y)
            n_ranges = len(lidar_msg.ranges) if lidar_msg.ranges is not None else 0
            sample_ranges = list(lidar_msg.ranges[:5]) if n_ranges > 0 else []
            print(
                f"[TaskTracker] First exploration update: "
                f"pose=({pose_msg.x:.2f}, {pose_msg.y:.2f}), yaw={agent_yaw:.2f}rad, "
                f"agent cell=({agent_col}, {agent_row}), "
                f"in_bounds={stats['agent_in_bounds']}, "
                f"grid={et.cells_x}x{et.cells_y} @ {et.resolution}m/cell "
                f"(origin {et.origin_x:.1f}, {et.origin_y:.1f}), "
                f"lidar rays={n_ranges} (first 5: {sample_ranges}), "
                f"maxRange={lidar_msg.maxRange}, "
                f"angleStart={lidar_msg.angleStartDeg}deg, "
                f"angleInc={lidar_msg.angleIncrementDeg}deg → "
                f"newly_known={newly_known} cells, "
                f"rays_oob={stats['rays_out_of_bounds']}/{stats['rays_total']}",
                flush=True,
            )

        # Unconditional periodic health check — prints every N steps
        # regardless of debug flag, so a silent/broken config is still visible.
        HEALTH_EVERY = 100
        if self._step_update_count % HEALTH_EVERY == 0:
            oob_frac = (
                self._rays_out_of_bounds / self._rays_total
                if self._rays_total > 0 else 0.0
            )
            print(
                f"[TaskTracker] vol health step={self._step_update_count}: "
                f"pose=({pose_msg.x:.2f}, {pose_msg.y:.2f}) "
                f"agent_in_bounds={stats['agent_in_bounds']} "
                f"explored={self._exploration_area_m2:.1f}m² "
                f"rays_oob={self._rays_out_of_bounds}/{self._rays_total} "
                f"({oob_frac*100:.1f}%)",
                flush=True,
            )

        if self.vol_debug >= 1 and (
            self._step_update_count % max(1, self.vol_debug_every) == 0
            or newly_known > 0 and self._step_update_count <= 3
        ):
            agent_col, agent_row = et.world_to_cell(pose_msg.x, pose_msg.y)
            print(
                f"[TaskTracker][vol-debug] step={self._step_update_count}: "
                f"pose=({pose_msg.x:.2f}, {pose_msg.y:.2f}) yaw={agent_yaw:.2f} "
                f"cell=({agent_col},{agent_row}) in_bounds={stats['agent_in_bounds']} "
                f"newly_known={newly_known} total_area={et.known_area_m2:.1f}m² "
                f"rays_oob={stats['rays_out_of_bounds']}/{stats['rays_total']}"
            )
            if self.vol_debug >= 2:
                ranges = lidar_msg.ranges or []
                n = len(ranges)
                n_nonneg = sum(1 for r in ranges if r is not None and r > 0)
                n_hits = sum(
                    1 for r in ranges if r is not None and 0 < r < lidar_msg.maxRange
                )
                print(
                    f"[TaskTracker][vol-debug]   rays={n}, "
                    f"positive={n_nonneg}, hits(<maxRange)={n_hits}, "
                    f"maxRange={lidar_msg.maxRange}, "
                    f"ranges[:10]={list(ranges[:10])}"
                )

        if newly_known > 0:
            delta_area = newly_known * self.exploration_tracker.cell_area
            self._exploration_area_m2 += delta_area
            self._step_score += self.vol_reward_per_m2 * delta_area

        if self.exploration_viewer is not None:
            total_area = (
                self.exploration_tracker.cells_x
                * self.exploration_tracker.cells_y
                * self.exploration_tracker.cell_area
            )
            self.exploration_viewer.update(
                rgb_image=self.exploration_tracker.to_rgb_image(
                    agent_xy=(pose_msg.x, pose_msg.y)
                ),
                known_area_m2=self.exploration_tracker.known_area_m2,
                total_area_m2=total_area,
            )

    # --- Score / state accessors ---

    def get_this_step_score(self) -> float:
        """Return the reward accumulated in the most recent update_with_unity_msgs call."""
        return self._step_score

    def get_total_score(self) -> float:
        """Return the cumulative reward across the episode (for debugging)."""
        return self._total_score

    def is_terminated(self) -> bool:
        """Return True if a termination condition was triggered this episode."""
        return self._terminated

    def get_termination_reason(self) -> "str | None":
        """Return a string describing why the episode terminated, or None."""
        return self._termination_reason

    def get_num_reward_objs_picked_up(self) -> int:
        return self._num_reward_objs_picked_up

    def get_wrong_well_activations(self) -> dict:
        """Wrong-well activations this episode by trial type: {"random": n, "home": n}.
        Counts every event, penalised or not."""
        return dict(self._wrong_well_activations)

    def get_collision_count(self) -> int:
        return self._collision_count

    def get_explored_area_m2(self) -> float:
        """Return cumulative area explored (newly-known cells) this episode."""
        return self._exploration_area_m2

    def get_trajectory(self) -> "dict | None":
        """Return this episode's recorded poses, or None if recording is off.

        Keys: ``steps`` (N,) int32 — update index (1-based) of each pose;
        ``xyz`` (N, 3) float32 — ROS frame (x forward, y left, z up);
        ``yaw`` (N,) float32 — CCW radians; ``pickup_steps`` (K,) int32 — one
        entry per reward pickup, the update index it happened on.
        """
        if not self.record_trajectory:
            return None
        import numpy as np
        rows = np.asarray(self._traj_rows, dtype=np.float64).reshape(-1, 5)
        return {
            "steps": rows[:, 0].astype(np.int32),
            "xyz": rows[:, 1:4].astype(np.float32),
            "yaw": rows[:, 4].astype(np.float32),
            "pickup_steps": np.asarray(self._traj_pickup_steps, dtype=np.int32),
        }

    def get_total_explorable_area_m2(self) -> "float | None":
        """Total grid area in m² (None if exploration is not enabled)."""
        if self.exploration_tracker is None:
            return None
        et = self.exploration_tracker
        return et.cells_x * et.cells_y * et.cell_area

    def print_exploration_summary(self, prefix: str = ""):
        """Print cumulative explored area + % coverage of the tracked grid."""
        if not self.vol_enabled or self.exploration_tracker is None:
            return
        total = self.get_total_explorable_area_m2()
        explored = self.get_explored_area_m2()
        pct = 100.0 * explored / total if total and total > 0 else 0.0
        oob_frac = (
            self._rays_out_of_bounds / self._rays_total
            if self._rays_total > 0 else 0.0
        )
        tag = f"{prefix} " if prefix else ""
        print(
            f"[TaskTracker] {tag}Exploration: {explored:.1f} m² / {total:.0f} m² "
            f"({pct:.2f}%) — reward contribution: "
            f"{explored * self.vol_reward_per_m2:.3f} | "
            f"rays_oob={self._rays_out_of_bounds}/{self._rays_total} "
            f"({oob_frac*100:.1f}%), rays_zero_len={self._rays_zero_len}, "
            f"updates={self._step_update_count}"
        )

    # --- Topic matching helpers ---

    def _topics_by_suffix(self, msgs: dict, suffix: str) -> list:
        """Return all topic names whose last path component equals suffix."""
        return [t for t in msgs if t == f"/{suffix}" or t.endswith(f"/{suffix}")]

    def _get_values_by_suffix(self, msgs: dict, suffix: str) -> list:
        """Return the .data value from the first message on each matching topic."""
        return [msgs[t][0].data for t in self._topics_by_suffix(msgs, suffix)]

    def _get_total_pickups(self, msgs: dict) -> int:
        """Sum .data across all messages on all reward_pickup topics."""
        total = 0
        for topic in self._topics_by_suffix(msgs, self._REWARD_PICKUP_SUFFIX):
            total += sum(msg.data for msg in msgs[topic])
        return total

    def _find_pose_msg(self, msgs: dict):
        # Prefer the explicitly-configured topic (e.g. "/rat1_pose").
        if self.pose_topic is not None and self.pose_topic in msgs and msgs[self.pose_topic]:
            return msgs[self.pose_topic][0]
        # Fallback: any topic that looks like an absolute pose.  Skip
        # "_from_start" relative-pose topics.
        for t, msg_list in msgs.items():
            if not msg_list:
                continue
            if "from_start" in t:
                continue
            if t.endswith("_pose") or t.endswith("/pose"):
                return msg_list[0]
        return None

    def _find_lidar_msg(self, msgs: dict):
        if self.lidar_topic is not None and self.lidar_topic in msgs and msgs[self.lidar_topic]:
            return msgs[self.lidar_topic][0]
        for t, msg_list in msgs.items():
            if not msg_list:
                continue
            if t.endswith("lidar2d") or t.endswith("/lidar2d"):
                return msg_list[0]
        return None
