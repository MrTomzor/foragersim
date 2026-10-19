#!/usr/bin/env bash
# Set up the Python side of the bundle on a fresh Linux machine.
#
# Creates two virtual environments (one for the SB3/PPO tooling, one for
# DreamerV3), installs their dependencies, and editable-installs the bundled
# packages. Nothing outside the bundle is modified except the venv directory.
#
# Usage:
#   ./install.sh            # auto-detect an NVIDIA GPU
#   ./install.sh --cpu      # force CPU-only JAX / torch
#   ./install.sh --gpu      # force CUDA JAX / torch
#   ./install.sh --force    # recreate the venvs from scratch
#
# Env overrides:
#   RATSIM_VENV_DIR   where the venvs go (default: <bundle>/.venvs)
#   RATSIM_TORCH_INDEX  pip index for torch in --gpu mode (default: cu126 wheels)
set -euo pipefail

BUNDLE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV_DIR="${RATSIM_VENV_DIR:-$BUNDLE/.venvs}"
MAIN_VENV="$VENV_DIR/venv"
DREAMER_VENV="$VENV_DIR/dreamer_venv"
DREAMERV3_REPO="https://github.com/danijar/dreamerv3.git"
DREAMERV3_COMMIT="b65cf81a6fb13625af8722127459283f899a35d9"   # upstream commit the paper runs used
THIRD_PARTY="$BUNDLE/third_party"

FORCE=0
GPU_MODE="auto"
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    --gpu) GPU_MODE="gpu" ;;
    --cpu) GPU_MODE="cpu" ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "Unknown arg: $arg" >&2; exit 1 ;;
  esac
done

# ----- preflight -------------------------------------------------------------
if ! python3 -c "import sys; sys.exit(0 if sys.version_info[:2] >= (3, 10) else 1)" 2>/dev/null; then
  echo "[install] ERROR: python3 >= 3.10 required (found $(python3 -V 2>&1))." >&2; exit 1
fi
if ! python3 -c "import venv, ensurepip" 2>/dev/null; then
  echo "[install] ERROR: python3 venv/ensurepip missing. On Debian/Ubuntu: sudo apt install python3-venv python3-pip" >&2; exit 1
fi
command -v git >/dev/null || { echo "[install] ERROR: git is required (to fetch DreamerV3)." >&2; exit 1; }

if [[ "$GPU_MODE" == "auto" ]]; then
  if command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi >/dev/null 2>&1; then GPU_MODE="gpu"; else GPU_MODE="cpu"; fi
fi
echo "[install] mode: $GPU_MODE   venvs: $VENV_DIR"

# ----- venvs -----------------------------------------------------------------
mkdir -p "$VENV_DIR" "$THIRD_PARTY"
create_venv() {
  local path="$1"
  if [[ -d "$path" && "$FORCE" == 1 ]]; then rm -rf "$path"; fi
  if [[ -d "$path" && ! -f "$path/bin/activate" ]]; then rm -rf "$path"; fi
  [[ -d "$path" ]] || python3 -m venv "$path"
}
create_venv "$MAIN_VENV"
create_venv "$DREAMER_VENV"

# ----- main venv (PPO / SB3, analysis, human control) ------------------------
echo "[install] === main venv ==="
# shellcheck disable=SC1091
source "$MAIN_VENV/bin/activate"
pip install --upgrade pip
if [[ "$GPU_MODE" == "gpu" ]]; then
  # torch from PyPI targets the newest CUDA; cu126 wheels cover driver >= 525
  # and GPU archs sm_50..sm_90, which is the safe default.
  TORCH_INDEX="${RATSIM_TORCH_INDEX:-https://download.pytorch.org/whl/cu126}"
  pip install --index-url "$TORCH_INDEX" torch
fi
pip install scipy scikit-learn gymnasium "stable_baselines3[extra]" sb3-contrib \
            tensorboard wandb pandas matplotlib pyyaml
pip install -e "$BUNDLE/ratsim"
pip install --no-deps -e "$BUNDLE/ratsim_wildfire_gym_env"
deactivate

# ----- dreamer venv ------------------------------------------------------------
echo "[install] === dreamer venv ==="
if [[ ! -d "$THIRD_PARTY/dreamerv3/.git" ]]; then
  git clone "$DREAMERV3_REPO" "$THIRD_PARTY/dreamerv3"
fi
git -C "$THIRD_PARTY/dreamerv3" checkout --quiet "$DREAMERV3_COMMIT"

# shellcheck disable=SC1091
source "$DREAMER_VENV/bin/activate"
pip install --upgrade pip
DREAMER_REQ="$BUNDLE/ratsim_experiments/methods/dreamerv3/requirements.txt"
FILTERED_REQ="$(mktemp)"
grep -viE '^(jax==|tensorflow-?cpu==|tensorflow==)' "$DREAMER_REQ" > "$FILTERED_REQ"
pip install -r "$FILTERED_REQ"
rm -f "$FILTERED_REQ"
if [[ "$GPU_MODE" == "gpu" ]]; then
  pip install "jax[cuda12]==0.4.33" "tensorflow==2.19.1"
else
  pip install "jax==0.4.33" "tensorflow-cpu==2.19.1"
fi
pip install --no-deps -e "$THIRD_PARTY/dreamerv3"
pip install --no-deps -e "$BUNDLE/ratsim"
pip install --no-deps -e "$BUNDLE/ratsim_wildfire_gym_env"
deactivate

# ----- env file ----------------------------------------------------------------
cat > "$BUNDLE/env.sh" <<ENV
# source this before running anything from the bundle
export PPO_PYTHON_PATH="$MAIN_VENV/bin/python"
export DREAMER_PYTHON_PATH="$DREAMER_VENV/bin/python"
_bin="$(ls "$BUNDLE"/build/*/*.x86_64 2>/dev/null | head -1)"
export RATSIM_UNITY_BIN="\${RATSIM_UNITY_BIN:-\$_bin}"
alias ppo_venv='source "$MAIN_VENV/bin/activate"'
alias dreamer_venv='source "$DREAMER_VENV/bin/activate"'
ENV

cat <<EOT

[install] done.
  main venv:     $MAIN_VENV
  dreamer venv:  $DREAMER_VENV
  mode:          $GPU_MODE

Next:  source $BUNDLE/env.sh   (sets PPO_PYTHON_PATH / DREAMER_PYTHON_PATH / RATSIM_UNITY_BIN)
EOT
