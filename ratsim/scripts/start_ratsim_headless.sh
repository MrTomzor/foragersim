#!/usr/bin/env bash
# Launch a Unity ratsim build on a headless Linux box.
#
# Two display modes:
#
#   xvfb  Bring up a throwaway X server for this instance and run Unity with
#         -batchmode -nographics. Nothing to set up beforehand and no root
#         needed, so this is the only mode that works on an HPC compute node.
#         Also measured ~2.2x faster than rendering (the cluster notes).
#   gfx   Attach to a pre-existing X server (default :99, from
#         setup_headless_display.sh) and let Unity render normally. Use this if
#         the agent has a camera sensor (RGBD): -nographics gives Unity a null
#         graphics device, so cameras produce nothing.
#
# Mode: --xvfb / --gfx, or RATSIM_XVFB=1 / RATSIM_XVFB=0. Default is auto —
# xvfb when an xvfb binary is on PATH, else gfx.
#
# Usage:
#   ./start_ratsim_headless.sh <bin> [--port 9000] [--log /path/log] [--force]
#   ./start_ratsim_headless.sh --port 9001                    # uses $RATSIM_UNITY_BIN
#
# If <bin> is omitted the env var RATSIM_UNITY_BIN must be set.
# Default port is 9000. Default log is $RATSIM_RUNDIR/ratsim_<port>.log.
# A pidfile holding the *Unity* pid goes to $RATSIM_RUNDIR/ratsim_<port>.pid.
# $RATSIM_RUNDIR defaults to /tmp.
# --force kills the existing instance on this port (if any) before launching.
set -u

BIN=""
PORT=9000
LOG=""
FORCE=0
MODE=""                                     # "", "xvfb" or "gfx"
DISPLAY_NUM=${DISPLAY_NUM:-:99}

# Where pidfiles and Unity logs go. This precedence MUST stay identical to
# _rundir() in ratsim/unity_launcher.py -- if they disagree, Python reads a
# pidfile this script never wrote and silently fails to kill what it spawned.
# Bare /tmp is shared with other users' jobs on a cluster node, so a colliding
# ratsim_<port>.pid would let one job kill another's Unity.
if [[ -n "${RATSIM_RUNDIR:-}" ]]; then
  RUNDIR="$RATSIM_RUNDIR"
elif [[ -n "${SLURM_JOB_ID:-}" ]]; then
  if [[ -d "/mnt/job-${SLURM_JOB_ID}" ]]; then
    RUNDIR="/mnt/job-${SLURM_JOB_ID}"
  else
    RUNDIR="${TMPDIR:-/tmp/ratsim-job-${SLURM_JOB_ID}}"
  fi
else
  RUNDIR="/tmp"
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="$2"; shift 2 ;;
    --log)  LOG="$2";  shift 2 ;;
    --force) FORCE=1;  shift ;;
    --xvfb) MODE="xvfb"; shift ;;
    --gfx)  MODE="gfx";  shift ;;
    -h|--help)
      sed -n '2,28p' "$0"; exit 0 ;;
    *)
      if [[ -z "$BIN" ]]; then BIN="$1"; shift
      else echo "unknown arg: $1"; exit 2; fi ;;
  esac
done

if [[ -z "$BIN" ]]; then
  BIN="${RATSIM_UNITY_BIN:-}"
fi
if [[ -z "$BIN" ]]; then
  echo "no binary path: pass as first arg or set RATSIM_UNITY_BIN"
  exit 2
fi
if [[ ! -x "$BIN" ]]; then
  echo "binary not found or not executable: $BIN"
  exit 2
fi
# `ss` lives in /sbin, which an interactive shell has on PATH but a SLURM batch
# job does NOT (measured on the cluster: batch PATH is just /usr/local/bin:/usr/bin).
# So look for it by absolute path too before giving up.
SS_BIN=""
for _c in ss /sbin/ss /usr/sbin/ss; do
  if _p="$(command -v "$_c" 2>/dev/null)"; then SS_BIN="$_p"; break; fi
done
unset _c _p
if [[ -z "$SS_BIN" ]]; then
  echo "note: 'ss' not found (install iproute2 for a cleaner check) —"
  echo "      falling back to a bash /dev/tcp connect probe."
fi

# True if something is accepting TCP on $1. Prefer `ss`: it reads kernel state
# without touching Unity, whereas the fallback actually connects and hangs up,
# which Unity sees as a client appearing and vanishing.
port_open() {
  if [[ -n "$SS_BIN" ]]; then
    "$SS_BIN" -tln 2>/dev/null | grep -q ":${1} "
  else
    ( exec 3<>"/dev/tcp/127.0.0.1/${1}" ) 2>/dev/null
  fi
}

# Is pid $2 the process listening on port $1? Without root, `ss -tlnp` shows
# process details only for our *own* sockets -- which is exactly the case that
# matters here. Returns success ("can't rule it out") when ss is unavailable, so
# this never invents a failure on a box without iproute2.
port_held_by_pid() {
  [[ -n "$SS_BIN" ]] || return 0
  "$SS_BIN" -tlnp 2>/dev/null | grep ":${1} " | grep -q "pid=${2},"
}

mkdir -p "$RUNDIR" 2>/dev/null || true
LOG="${LOG:-$RUNDIR/ratsim_${PORT}.log}"
PIDFILE="$RUNDIR/ratsim_${PORT}.pid"
PGIDFILE="$RUNDIR/ratsim_${PORT}.pgid"
XPIDFILE="$RUNDIR/ratsim_${PORT}.xpid"

# --- mode resolution --------------------------------------------------------
HAVE_XVFB_RUN=0; command -v xvfb-run >/dev/null 2>&1 && HAVE_XVFB_RUN=1
HAVE_XVFB=0;     command -v Xvfb     >/dev/null 2>&1 && HAVE_XVFB=1

if [[ -z "$MODE" ]]; then
  case "${RATSIM_XVFB:-}" in
    1|true|yes) MODE="xvfb" ;;
    0|false|no) MODE="gfx" ;;
    *) if (( HAVE_XVFB_RUN || HAVE_XVFB )); then MODE="xvfb"; else MODE="gfx"; fi ;;
  esac
fi

if [[ "$MODE" == "xvfb" ]] && (( ! HAVE_XVFB_RUN && ! HAVE_XVFB )); then
  echo "xvfb mode requested but neither 'xvfb-run' nor 'Xvfb' is on PATH."
  echo "  Debian/Ubuntu: sudo apt-get install xvfb"
  echo "  HPC with Lmod: module load Xvfb   (must be loaded in the job, see the cluster job environment)"
  exit 2
fi

# The gfx path really does need a server already running — Unity segfaults if
# DISPLAY points at nothing (measured, the cluster notes). Only check it
# here; in xvfb mode we provide the display ourselves.
if [[ "$MODE" == "gfx" && ! -e "/tmp/.X11-unix/X${DISPLAY_NUM#:}" ]]; then
  echo "no X server on $DISPLAY_NUM — run sudo ./scripts/setup_headless_display.sh first,"
  echo "or use --xvfb to let this script start its own (needs xvfb installed)."
  exit 2
fi

# --- helpers ----------------------------------------------------------------
# Unity's comm name is the basename truncated to 15 chars (kernel limit).
COMM="$(basename "${BIN%.x86_64}" | cut -c1-15)"

# Find OUR Unity's pid. Needed because in xvfb mode $! is the xvfb-run wrapper,
# whose Unity child survives killing it — a pidfile holding the wrapper pid
# means stop_ratsim_headless.sh silently leaves an instance holding the port.
#
# Identified by ancestry, NOT by "any process with -port <PORT>" in its cmdline.
# The cmdline match looks equivalent and is not: when two launches race for the
# same port, the loser finds the WINNER's Unity, writes that pid into its own
# pidfile, and every ownership check downstream then passes while both clients
# talk to one simulator — and worse, the loser's cleanup then kills the winner's
# Unity. Measured, not theoretical: two trainings in one job both reported
# "TCP server up on port 9990 (pid 2770568)".
ppid_of() {
  # /proc/<pid>/stat field 4 is the ppid, but field 2 is "(comm)" and may itself
  # contain spaces or parens -- so cut everything up to the last ") " first,
  # after which field 1 is the state and field 2 is the ppid.
  local s
  s="$(cat "/proc/$1/stat" 2>/dev/null)" || return 1
  s="${s##*) }"
  printf '%s\n' "$s" | { read -r _state ppid _rest; printf '%s\n' "$ppid"; }
}

is_descendant_of() {
  local p="$1" up
  for _ in $(seq 1 12); do
    [[ "$p" == "$2" ]] && return 0
    up="$(ppid_of "$p")" || return 1
    [[ -n "$up" && "$up" != 0 && "$up" != 1 ]] || return 1
    p="$up"
  done
  return 1
}

our_unity_pid() {
  # Ancestry, not process group: the pgid has to be *read*, and right after `&`
  # the wrapper may not have called setsid yet, so an early read returns the
  # shell's old group and matches a stranger's Unity. Parentage is true from the
  # moment the process exists.
  local pid
  for pid in $(pgrep -x -u "$(id -u)" "$COMM" 2>/dev/null); do
    is_descendant_of "$pid" "$WRAPPER_PID" && { echo "$pid"; return 0; }
  done
  return 1
}

# /proc, not `kill -0`: the display we are probing may belong to another user
# (root owns the :99 Xorg from setup_headless_display.sh), and kill -0 fails
# with EPERM there — which would read as "dead".
pid_alive() { [[ -n "${1:-}" && -d "/proc/$1" ]]; }

display_free() {
  # Judge by the lock file, not by /tmp/.X11-unix/X<n>: an Xvfb killed with
  # SIGKILL leaves its socket behind, and those accumulate. A lock holding a
  # dead pid is likewise stale — the X server removes it itself on startup.
  local lock="/tmp/.X${1}-lock" sock="/tmp/.X11-unix/X${1}" pid
  if [[ -e "$lock" ]]; then
    pid="$(tr -dc '0-9' < "$lock" 2>/dev/null)"
    # Unreadable/garbled lock: assume someone owns it.
    [[ -n "$pid" ]] || return 1
    pid_alive "$pid" && return 1
  fi
  # No live server owns this number, so any socket here is a leftover. Clear it
  # (only if it is ours) so that the socket appearing later is real evidence
  # that *our* Xvfb came up, rather than a stale file we mistake for readiness.
  if [[ -e "$sock" ]]; then
    [[ -O "$sock" ]] || return 1
    rm -f "$sock" 2>/dev/null || return 1
  fi
  return 0
}

XPID=""
XDISPLAY=""
start_own_xvfb() {
  # Port-seeded start so two launches at once don't race for the same number.
  # Failure of one candidate just moves to the next -- the only reliable test
  # that a display is usable is starting a server on it.
  local start=$(( 90 + (PORT % 60) )) n extra i tries=0
  for (( n = start; n < start + 60 && tries < 8; n++ )); do
    display_free "$n" || continue
    tries=$(( tries + 1 ))
    # -terminate makes the server exit once its last client (Unity) is gone, so
    # killing Unity cleans up even if the stop path never runs. Retry without it
    # in case a given Xvfb build doesn't take the flag.
    for extra in "-terminate" ""; do
      # shellcheck disable=SC2086  # intentional split: $extra is one flag or none
      Xvfb ":$n" -screen 0 1024x768x24 -nolisten tcp $extra >/dev/null 2>&1 &
      XPID=$!
      for (( i = 0; i < 20; i++ )); do
        sleep 0.5
        if [[ -e "/tmp/.X11-unix/X${n}" ]] && pid_alive "$XPID"; then
          XDISPLAY="$n"; return 0
        fi
        pid_alive "$XPID" || break
      done
      kill -9 "$XPID" 2>/dev/null || true
      XPID=""
    done
  done
  return 1
}

# Atomically reserve $PIDFILE, or fail. `set -o noclobber` makes `>` fail when
# the file exists, which is the atomic create-exclusive we need.
#
# Without this, two launches racing for the same port both write this one path:
# the loser overwrites the winner's recorded pid and then deletes the file on
# its own failure, leaving the winner's Unity with no cleanup handle. Measured —
# it survived process exit as an orphan holding the port.
#
# This only separates launches that share a $RUNDIR (i.e. the same job). Across
# jobs the paths differ, and separation falls to the bind test plus the
# ownership check in the wait loop. The two layers cover different cases.
claim_pidfile() {
  # Write OUR OWN pid as the placeholder, not a word like "claiming": the
  # staleness check below extracts digits, and a non-numeric placeholder reads
  # as "no pid recorded" -- so the loser deleted the winner's fresh claim and
  # took the port anyway. $$ is alive by definition, so the loser now backs off.
  if ( set -o noclobber; echo "$$" > "$PIDFILE" ) 2>/dev/null; then
    return 0
  fi
  # A file is already there. Respect it if it still refers to something real,
  # otherwise treat it as debris from a crashed run and take it over.
  local old
  old="$(tr -dc '0-9' < "$PIDFILE" 2>/dev/null)"
  if [[ -n "$old" ]] && pid_alive "$old"; then return 1; fi
  if port_open "$PORT"; then return 1; fi
  rm -f "$PIDFILE" 2>/dev/null || return 1
  ( set -o noclobber; echo "claiming" > "$PIDFILE" ) 2>/dev/null
}

cleanup_failed_launch() {
  [[ -n "${UPID:-}" ]] && kill -9 "$UPID" 2>/dev/null
  [[ -n "${WRAPPER_PID:-}" ]] && kill -9 "$WRAPPER_PID" 2>/dev/null
  [[ -n "$XPID" ]] && { sleep 1; kill -9 "$XPID" 2>/dev/null; }
  rm -f "$PIDFILE" "$PGIDFILE" "$XPIDFILE"
}

# --- refuse to clobber a live instance on this port unless --force ----------
if port_open "$PORT"; then
  if [[ "$FORCE" -eq 1 ]]; then
    echo "port ${PORT} in use — --force given, killing previous instance"
    stopper="$(dirname "$0")/stop_ratsim_headless.sh"
    if [[ -x "$stopper" ]]; then
      "$stopper" --port "$PORT" || true
    elif [[ -f "$PIDFILE" ]]; then
      kill -9 "$(cat "$PIDFILE")" 2>/dev/null || true
      rm -f "$PIDFILE"
    fi
    sleep 1
  else
    echo "port ${PORT} already in use (pidfile: $(cat "$PIDFILE" 2>/dev/null || echo "?"))"
    echo "use --force to replace, or pick a different --port"
    exit 1
  fi
fi

if ! claim_pidfile; then
  echo "port ${PORT} is already claimed by $(cat "$PIDFILE" 2>/dev/null || echo "?")"
  echo "(pidfile $PIDFILE). Another launch is using this port — pick a different --port."
  exit 1
fi

# --- launch -----------------------------------------------------------------
if [[ "$MODE" == "xvfb" ]]; then
  if (( HAVE_XVFB_RUN )); then
    echo "launching $(basename "$BIN") under xvfb-run -nographics port=$PORT (log: $LOG)"
    setsid xvfb-run -a "$BIN" -batchmode -nographics \
      -port "$PORT" -logFile "$LOG" >/dev/null 2>&1 &
  else
    if ! start_own_xvfb; then
      echo "could not start an Xvfb on any free display number"
      exit 1
    fi
    echo "$XPID" > "$XPIDFILE"
    echo "launching $(basename "$BIN") on own Xvfb :$XDISPLAY -nographics port=$PORT (log: $LOG)"
    DISPLAY=":$XDISPLAY" setsid "$BIN" -batchmode -nographics \
      -port "$PORT" -logFile "$LOG" >/dev/null 2>&1 &
  fi
else
  echo "launching $(basename "$BIN") on DISPLAY=$DISPLAY_NUM port=$PORT (log: $LOG)"
  DISPLAY="$DISPLAY_NUM" setsid "$BIN" -port "$PORT" -logFile "$LOG" >/dev/null 2>&1 &
fi
WRAPPER_PID=$!
disown 2>/dev/null || true

# Process group of the launch. Only used by the stop path as a backstop for the
# case below where the Unity pid can't be resolved.
ps -o pgid= -p "$WRAPPER_PID" 2>/dev/null | tr -d ' ' > "$PGIDFILE" || rm -f "$PGIDFILE"

UPID=""
for _ in {1..10}; do
  UPID="$(our_unity_pid || true)"
  [[ -n "$UPID" ]] && break
  kill -0 "$WRAPPER_PID" 2>/dev/null || break
  sleep 1
done
if [[ -n "$UPID" ]]; then
  echo "$UPID" > "$PIDFILE"
else
  # Fall back to the launched pid so there is *something* to kill, and say so:
  # in xvfb-run mode this is the wrapper, and killing it leaves Unity behind.
  echo "$WRAPPER_PID" > "$PIDFILE"
  echo "warning: could not resolve Unity pid for port ${PORT} (comm '$COMM');"
  echo "         pidfile holds the launcher pid ${WRAPPER_PID} instead."
  UPID="$WRAPPER_PID"
fi

# 30s is generous for one instance (measured: ~1-2s on an idle node) but far too
# short during a launch storm. Packing 7 runs x 4 envs onto one node had ~20
# Unity processes already competing while the next ones booted, and boots
# started exceeding 30s -- every run in the job then failed, and the retries
# collided with ports still being torn down. Raise it for heavily packed jobs:
#   RATSIM_BOOT_TIMEOUT=180
# Keep the default at 30 so single-run and laptop behaviour is unchanged.
BOOT_TIMEOUT="${RATSIM_BOOT_TIMEOUT:-30}"
[[ "$BOOT_TIMEOUT" =~ ^[0-9]+$ ]] || BOOT_TIMEOUT=30
echo "waiting up to ${BOOT_TIMEOUT}s for port ${PORT}..."
for ((i=1; i<=BOOT_TIMEOUT; i++)); do
  sleep 1
  # Liveness is checked BEFORE the port, and that ordering is the point: an open
  # port proves *someone* is listening, not that it is ours. If our Unity lost a
  # bind race to another job on this node and died, declaring success here would
  # hand the caller a stranger's simulator and it would train against the wrong
  # world with no error anywhere. That is not hypothetical -- three
  # co-scheduled jobs all defaulting to :9000 did exactly this.
  if ! pid_alive "$UPID"; then
    echo "Unity process died before opening port ${PORT}. last 40 log lines:"
    tail -40 "$LOG" 2>/dev/null
    cleanup_failed_launch
    exit 1
  fi
  if port_open "$PORT"; then
    if port_held_by_pid "$PORT" "$UPID"; then
      echo "TCP server up on port ${PORT} after ${i}s (pid ${UPID})"
      exit 0
    fi
    echo "port ${PORT} is open but held by another process, not our Unity (${UPID}):"
    [[ -n "$SS_BIN" ]] && "$SS_BIN" -tlnp 2>/dev/null | grep ":${PORT} " | sed 's/^/  /'
    echo "refusing to attach to someone else's instance. pick a different --port."
    cleanup_failed_launch
    exit 1
  fi
done

echo "timed out. last 40 log lines:"
tail -40 "$LOG" 2>/dev/null
cleanup_failed_launch
exit 1
