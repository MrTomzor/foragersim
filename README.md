# ForagerSim
ForagerSim is a multidisciplinary simulation environment built for investigating map-based reasoning in large-scale dynamic worlds with a unified scenario definition allowing evaluation of both ROS2 and Gym-based agents on the same tasks (volumetric exploration, multi-object search, rat-study home-return task etc.). This repo contains the code and data for replicating the results in the ForagerSim paper, and development of the framework will continue in this repo in the future.

Accompanying paper preprint - https://arxiv.org/abs/2610.26395

This repo contains the simulator, the training and evaluation tooling, the
experiment definitions of every scenario in the paper, and the episode logs
and trajectories behind the paper's results table and figures.

The Linux build and the paper's results are attached to the
[`arxiv-v1` release](https://github.com/MrTomzor/foragersim/releases/tag/arxiv-v1).

## Requirements

- Linux x86_64, Python >= 3.10 with `venv`, git, `rsync`.
- An X server for the simulator. Headless machines: `xvfb` (`apt install xvfb`);
  the launch scripts start a throwaway X server automatically when it is present.
- For the frontier baseline only: a ROS 2 distribution (tested with Jazzy) with
  `rclpy`, `nav_msgs`, `sensor_msgs`, `tf2_ros`, `launch_ros`, `colcon`, and
  `numpy`, `scipy` and `pyyaml` for the system python (`apt install python3-numpy python3-scipy python3-yaml`).
- An NVIDIA GPU is optional. PPO trains on CPU; DreamerV3 is much faster on a GPU.
- Windowed use (keyboard control) needs a desktop session; everything else runs headless.

## Install

```bash
./install.sh            # or ./install.sh --cpu / --gpu
source env.sh           # exports PPO_PYTHON_PATH, DREAMER_PYTHON_PATH, RATSIM_UNITY_BIN
```

`install.sh` creates two virtual environments under `.venvs/` (SB3/PPO tooling
and DreamerV3), fetches the pinned upstream DreamerV3 commit into
`third_party/`, and editable-installs the bundled packages. `env.sh` points the
tooling at those interpreters and at the bundled build.

If you have not downloaded the build into `build/`, open `ratsim_unity_project/RatsimUnityProject`
in Unity 6000.1, build a Linux player, and export `RATSIM_UNITY_BIN=/path/to/player.x86_64`.

## Run

All commands below assume `source env.sh` was done and are run from
`ratsim_experiments/` unless noted. The simulator listens on TCP port 9000.
`train.py`, `train_dreamerv3.py` and the scheduler start and stop their own
simulator instances through `RATSIM_UNITY_BIN`. `test.py` and the frontier
baseline attach to an instance that is already running on port 9000; start one
with either the windowed player or, on a headless machine, with

```bash
../ratsim/scripts/start_ratsim_headless.sh "$RATSIM_UNITY_BIN" --port 9000   # xvfb, -batchmode -nographics
../ratsim/scripts/stop_ratsim_headless.sh --port 9000                        # when done
```

### Drive the agent yourself (desktop session)

```bash
source ../.venvs/venv/bin/activate
"$RATSIM_UNITY_BIN" &                                   # windowed simulator on :9000
python -m ratsim.human_control_test --world_preset maze_sectored_orthogonal \
       --agent_preset sphereagent_2d_lidar --task_preset volumetric_exploration_5000_collision_penalty
```

WASD moves the agent, Space steps the world while the agent is stationary, Shift can be held alongside WASD to move more slowly. 
In the terminal, R reloads the world with the next seed and Q quits. The task tracker prints score and termination like it does for the agents.

### Train one run of a paper scenario

```bash
source ../.venvs/venv/bin/activate
python train.py def=memorymaze_15x15 method=ppo                   # PPO, CPU
source ../.venvs/dreamer_venv/bin/activate
python train_dreamerv3.py def=memorymaze_15x15 method.jax.platform=cpu   # DreamerV3 (drop the platform override on a GPU)
```

Runs land under `results/experiments/<def>/runs/<variation>__<method>__seed<k>/`
with `train_episodes.jsonl`, checkpoints per stage, and `run_config.json`.
`total_steps=200_000 n_stages=1` on the command line shortens a run for a smoke
test.

### Reproduce a whole paper row (all methods and seeds)

```bash
python scheduler_run.py memorymaze_15x15                      # uses scheduler/machines/default.yaml
python scheduler_run.py memorymaze_15x15 --step-multiplier 0.01   # 1 % of the steps, for a smoke test
python scheduler_status.py memorymaze_15x15
```

The scheduler packs runs onto one machine according to the machine profile
(CPU slots, GPUs), one simulator instance per run, and resumes at stage
boundaries if interrupted. See `scheduler/README.md` and
`scheduler/machines/gpu_example.yaml` for a GPU profile.

### Evaluate

```bash
# simulator running on :9000 (see above)
python test.py def=memorymaze_15x15 model=results/experiments/memorymaze_15x15/runs/baseline__ppo__seed0/checkpoints/final.zip
python test.py def=memorymaze_15x15 method=human rtf=1.0        # at the keyboard, windowed simulator
python test.py def=memorymaze_15x15 method=frontier             # ROS 2 sourced in this shell
```

Held-out evaluation uses world seeds drawn from `eval_metaseed` (42 in the
paper, except for 43 in the foraging variant of memory_orthomaze chosen so the human cannot reuse memorized maze layouts from the multi-object search variant), so every method is scored on the same worlds.

### Reproduce the paper's evaluation protocol

`test.py` evaluates one checkpoint. The paper's numbers come from
`record_trajectories.py`, which evaluates one run per method of an experiment
on world seeds drawn from `--eval-metaseed` (42 in the paper), so every method
is scored on the same held-out worlds, then runs the frontier and human
baselines on exactly those seeds and writes the `trajectories.json` manifest
the trajectory figure reads:

```bash
# simulator running on :9000, ROS 2 sourced for the frontier method
python record_trajectories.py memorymaze_15x15 --local --methods ppo,dreamer,frontier --n-worldseeds 5
python record_trajectories.py memorymaze_15x15 --local --methods human      # at the keyboard
python plot_trajectories.py memorymaze_15x15 --local --background ortho
```

`--local` points at runs you trained here (`results/experiments/<def>/`);
without it the scripts read the shipped `results/paper/<def>/`, whose
manifests are already recorded and which have no checkpoints to re-evaluate.
`--spawn-unity` lets the script start the simulator itself.

### Frontier-exploration baseline (ROS 2)

```bash
source /opt/ros/jazzy/setup.bash
export PYTHONPATH="$PWD/../ratsim:$PYTHONPATH"      # the ROS nodes run under the system python
cd ../ratsim_ros2 && colcon build --symlink-install && source install/setup.bash && cd ../ratsim_experiments
ros2 launch ratsim_ros2 frontier_exploration.launch.py world_preset:=maze_sectored_orthogonal \
     agent_preset:=sphereagent_2d_lidar_20m seeds:=3 rtf:=0.0 lockstep:=true
```

`test.py method=frontier` wraps this launch file with the def's presets and
the paper's evaluation seeds; `frontier.<key>=<value>` on its command line is
forwarded as a launch argument.

### Regenerate the paper's table and figures from the shipped results

```bash
source ../.venvs/venv/bin/activate
python make_results_table.py            # -> results/analysis/paper/tables/results.tex
python make_training_figure.py          # training curves, one panel per scenario
python make_final_boxplots.py --grid    # final performance per scenario and method
python plot_trajectories.py memory_orthomaze longyard --background ortho   # trajectory figure
```

These read `results/paper/<def>/`: `runs/*/train_episodes.jsonl` and
`eval_episodes.jsonl` for PPO and DreamerV3, `external/{human,frontier}/episodes.jsonl`
for the two non-learning baselines, `trajectories.json` and the `.npz` files
it indexes for the trajectory figure, and `snapshots/` for the overhead
renders. Checkpoints are not included. Paths inside `trajectories.json` are
relative to `ratsim_experiments/`, so run the plotting scripts from there.

## Layout of a result directory

```
results/paper/<def>/
  experiment.yaml                 the def as it ran
  runs/<variation>__<method>__seed<k>/
    run_config.json               presets, method config, metaseed
    train_episodes.jsonl          one line per training episode
    eval_episodes.jsonl           held-out evaluation episodes
    eval_world_config.json        the world config those evals used
    eval_episodes_trajectories/   one .npz per eval episode (pose per step)
  external/<human|frontier>/      the same for the non-learning baselines
  trajectories.json               manifest for the trajectory figure
  snapshots/                      overhead renders of the eval worlds
```

Each JSONL line carries `total_score`, `objects_found`, `collisions`,
`termination_reason`, `steps` and `world_seed`.

## Simulator details

The simulator is a Unity application driven over TCP with a topic-based
protocol (`ratsim/ratsim/roslike_unity_connector/`). Each episode reset sends a
world config (procedural generation parameters, from a world preset) and the
agent config (sensors, actuators, their parameters, from an agent preset); the
simulator generates the world, spawns the agent, and streams sensor messages
while accepting velocity commands. All data crossing the TCP boundary uses the
ROS convention (x forward, y left, z up). `ratsim/scripts/start_ratsim_headless.sh`
launches a build headless (with xvfb) and `stop_ratsim_headless.sh` tears it
down; the Python tooling does the same automatically.

## Contents

| Directory                  | What it is                                                                                                                                                                                                                                                        |
|----------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `ratsim/`                  | Python SDK: General-purpose ROS-like TCP connector between Python and Unity, message definitions, world / agent / task presets, task tracker (rewards, termination, exploration map), keyboard control, headless launch scripts                                                                                                                                                                                               |
| `ratsim_wildfire_gym_env/` | Gymnasium environment wrapping the simulator through the SDK                                                                                                                                                                                                      |
| `ratsim_ros2/`             | ROS 2 package with the frontier-exploration baseline                                                                                                                                                                                                              |
| `ratsim_experiments/`      | Training (`train.py`, `train_dreamerv3.py`), evaluation (`test.py`), the single-machine experiment scheduler, the paper's experiment defs (`defs/`), the shipped results (`results/paper/`), and the scripts that regenerate the results table and figures |
| `ratsim_unity_project/`    | Unity project of the simulator (Unity 6000.1). Only needed to modify the simulator or to build it for another platform                                                                                                                                            |

The eight paper scenarios and their defs:

| Paper name | Def | World preset |
|---|---|---|
| memorymaze15x15 | `memorymaze_15x15` | `maze_memorymaze_15x15` |
| 10houses-fenced | `longyard` | `longyard_search` |
| 4arm-submazes | `memory_orthomaze` | `maze_sectored_orthogonal` |
| 5houses-dynamic | `memory_dynahouses_2k` | `dyna_houses` |
| 5houses-sar | `hardsar` | `superdyna_houses` |
| ratmaze-foraging | `wellmaze_tight_cue` | `widloski_maze_3x3_tight` |
| forest-foraging | `forest_wells_cue_unb` | `forest_landmarks_wells_unb` |
| 4arm-submazes-foraging | `ortho_wells_2k_nopose` | `maze_ortho_wells_nohomeprime` |

Def and preset names are the working names the scenarios had when they were
designed and do not always match the paper's names; the mapping above is the
one in `ratsim_experiments/paper/results_table.yaml`, which every analysis
script reads.

A def names an agent preset, a task preset and a world preset
(`ratsim/ratsim/config_blender/*_presets/`), the training length and the
methods and seeds to run. The paper table (`ratsim_experiments/paper/results_table.yaml`)
maps defs to paper row names.


## License

MIT, see `LICENSE`.
