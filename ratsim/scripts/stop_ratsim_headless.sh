#!/usr/bin/env bash
# Kill a running ratsim Unity binary launched via start_ratsim_headless.sh.
#
# Usage:
#   ./stop_ratsim_headless.sh --port 9000        # kill via pidfile (preferred)
#   ./stop_ratsim_headless.sh --all              # kill every ratsim_*.pid instance
#   ./stop_ratsim_headless.sh /path/to/bin       # legacy: kill by basename match
#
# Reads $RATSIM_RUNDIR (default /tmp) for the pidfile and its sidecars:
#   ratsim_<port>.pid    the Unity pid
#   ratsim_<port>.pgid   process group of the launch (backstop)
#   ratsim_<port>.xpid   an Xvfb this script's launcher started, if any
set -u

PORT=""
ALL=0
BIN=""
RUNDIR="${RATSIM_RUNDIR:-/tmp}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="$2"; shift 2 ;;
    --all)  ALL=1;     shift ;;
    -h|--help)
      sed -n '2,14p' "$0"; exit 0 ;;
    *)
      if [[ -z "$BIN" ]]; then BIN="$1"; shift
      else echo "unknown arg: $1"; exit 2; fi ;;
  esac
done

# Does this process group still hold something recognisably ours? Guards the
# group kill below against a stale pgid whose number has since been recycled.
group_looks_like_ours() {
  local pgid="$1" port="$2" pid
  for pid in $(pgrep -g "$pgid" -u "$(id -u)" 2>/dev/null); do
    if tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null \
       | grep -qE -- "-port ${port} |Xvfb|xvfb-run"; then
      return 0
    fi
  done
  return 1
}

stop_port() {
  local port="$1"
  local pf="$RUNDIR/ratsim_${port}.pid"
  local pgf="$RUNDIR/ratsim_${port}.pgid"
  local xpf="$RUNDIR/ratsim_${port}.xpid"
  local pid pgid xpid

  if [[ -f "$pf" ]]; then
    pid="$(cat "$pf")"
    if kill -0 "$pid" 2>/dev/null; then
      echo "killing pid $pid (from $pf)"
      kill -9 "$pid" 2>/dev/null || true
    else
      echo "pid $pid not running (stale pidfile $pf)"
    fi
    rm -f "$pf"
  else
    echo "no pidfile: $pf"
  fi

  # Give xvfb-run a moment to notice its command exited and tear down its own
  # X server before we judge whether anything is left over.
  [[ -f "$pgf" || -f "$xpf" ]] && sleep 1

  # Backstop for the case where the launcher could not resolve the Unity pid
  # and stored the wrapper's instead: xvfb-run is a shell script and its Unity
  # child survives the wrapper, so kill the whole group.
  if [[ -f "$pgf" ]]; then
    pgid="$(cat "$pgf")"
    if [[ "$pgid" =~ ^[0-9]+$ ]] && group_looks_like_ours "$pgid" "$port"; then
      echo "reaping leftovers in process group $pgid"
      kill -9 -- "-$pgid" 2>/dev/null || true
    fi
    rm -f "$pgf"
  fi

  # An Xvfb we started ourselves (no xvfb-run available). It is launched with
  # -terminate so it usually exits on its own once Unity is gone; reap anyway.
  if [[ -f "$xpf" ]]; then
    xpid="$(cat "$xpf")"
    if [[ "$xpid" =~ ^[0-9]+$ ]] && kill -0 "$xpid" 2>/dev/null; then
      echo "reaping Xvfb pid $xpid"
      kill -9 "$xpid" 2>/dev/null || true
    fi
    rm -f "$xpf"
  fi
}

if [[ "$ALL" -eq 1 ]]; then
  shopt -s nullglob
  found=0
  for pf in "$RUNDIR"/ratsim_*.pid; do
    p="$(basename "$pf")"; p="${p#ratsim_}"; p="${p%.pid}"
    stop_port "$p"
    found=1
  done
  [[ "$found" -eq 0 ]] && echo "no pidfiles in $RUNDIR"
  exit 0
fi

if [[ -n "$PORT" ]]; then
  stop_port "$PORT"
  exit 0
fi

# Legacy path: kill by binary basename. Used when launched without pidfile or
# from a non-headless start (Editor, manual ./bin run).
if [[ -z "$BIN" ]]; then
  echo "usage: $0 (--port <p> | --all | <unity-binary-path-or-basename>)"
  exit 2
fi

BIN_BASE=$(basename "$BIN")
PATTERN="${BIN_BASE:0:15}"  # comm names are capped at 15 chars

if ! pgrep -x "$PATTERN" >/dev/null; then
  echo "no process matching '$PATTERN' running"
  exit 0
fi

echo "killing '$PATTERN'..."
pkill -9 -x "$PATTERN"
sleep 1

if pgrep -x "$PATTERN" >/dev/null; then
  echo "warning: process still present"
  pgrep -a -x "$PATTERN"
  exit 1
fi

echo "stopped."
