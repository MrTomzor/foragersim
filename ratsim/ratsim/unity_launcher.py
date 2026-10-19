"""Helpers for launching/discovering Unity ratsim instances.

Supports two operational tiers:

1. **Manual / interactive**: user launches Unity themselves (Editor Play mode
   or `start_ratsim_headless.sh`). Scripts default to port 9000, find the
   running instance, attach. Only works for n_envs=1.
2. **Auto-spawn**: `RATSIM_UNITY_BIN` env var points at the build. Scripts can
   spawn additional instances on demand. n_envs=1 still reuses port 9000 if
   alive (debug-friendly); n_envs>1 always spawns fresh on a separate range
   so it can't clobber the persistent debug instance.

Under SLURM (`$SLURM_JOB_ID` set) the node is shared with other users, so
tier 1 is disabled — port 9000 is never reused, the default port window is
derived from the job id, every port is bind-tested, and pidfiles live in
per-job scratch rather than `/tmp`. None of that applies off SLURM.

The intended usage from a training script::

    from ratsim.unity_launcher import allocate_unity_instances

    instances = allocate_unity_instances(n_envs=8, fresh=False)
    # instances is a list of UnityInstance(port=..., owned=...) — one per env.
    # Owned instances are killed at process exit; reused ones are left alone.

This module never assumes a port is alive just because it answers — callers
that need a heartbeat should send one over TCP after connecting.
"""

from __future__ import annotations

import atexit
import os
import shutil
import signal
import socket
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional


PERSISTENT_PORT = 9000
"""Port reserved for the long-running interactive instance.

n_envs=1 reuses this; n_envs>1 always spawns fresh elsewhere.
"""

FRESH_PORT_BASE = 9100
"""Starting port for auto-spawned training instances. Range 9100..9999."""

FRESH_PORT_WINDOW = 10
"""Ports per job. Matches the 10-port gap convention in scheduler/ports.py."""

FRESH_PORT_SPAN = 900
"""Size of the 9100..9999 band the job-derived base port is spread across."""


def _slurm_job_id() -> Optional[int]:
    """This job's SLURM id, or None when not running under SLURM.

    Presence of this variable is what switches the launcher into "shared
    machine" mode: derive a per-job port window, scope pidfiles to per-job
    scratch, and never reuse the persistent instance. Absent — the laptop
    case — nothing below changes behaviour.
    """
    for var in ("SLURM_JOB_ID", "SLURM_JOBID"):
        raw = os.environ.get(var, "")
        if raw.isdigit():
            return int(raw)
    return None


def _rundir() -> Path:
    """Directory holding pidfiles and Unity logs.

    Precedence — this MUST stay identical to ``rundir`` in
    ``scripts/start_ratsim_headless.sh``. If the two disagree, this module
    reads a pidfile the launcher never wrote and silently fails to kill the
    instance it spawned:

    1. ``$RATSIM_RUNDIR`` if set — always wins.
    2. Under SLURM: ``/mnt/job-<id>`` if it exists, else ``$TMPDIR``, else
       ``/tmp/ratsim-job-<id>``. Bare ``/tmp`` is shared with other users' jobs
       on the same node, so a colliding ``ratsim_<port>.pid`` would let one job
       kill another's Unity.
    3. ``/tmp`` — fine on a single-user box.
    """
    explicit = os.environ.get("RATSIM_RUNDIR")
    if explicit:
        return Path(explicit)
    jid = _slurm_job_id()
    if jid is None:
        return Path("/tmp")
    node_scratch = Path(f"/mnt/job-{jid}")
    if node_scratch.is_dir():
        return node_scratch
    tmpdir = os.environ.get("TMPDIR")
    path = Path(tmpdir) if tmpdir else Path(f"/tmp/ratsim-job-{jid}")
    try:
        path.mkdir(parents=True, exist_ok=True)
    except OSError:
        return Path("/tmp")
    return path


def _pidfile(port: int) -> Path:
    return _rundir() / f"ratsim_{port}.pid"


@dataclass
class UnityInstance:
    """A handle to a Unity instance the launcher chose for an env.

    Attributes:
        port: TCP port the instance listens on.
        owned: If True, the launcher spawned this and is responsible for
            killing it at exit. If False, it was reused (don't touch lifecycle).
    """

    port: int
    owned: bool


def _port_open(port: int, host: str = "127.0.0.1", timeout: float = 0.5) -> bool:
    """Return True if something is accepting TCP connections on port."""
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    try:
        s.connect((host, port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def _port_bindable(port: int) -> bool:
    """True if we could bind ``port`` right now on all interfaces.

    Stronger than ``_port_open()``: that only notices a socket already
    *accepting*, so it misses a process that has bound but not yet started
    accepting — and it is what made the old guard a check-then-act race. Two
    jobs could both see "free", both launch, and the loser's client would then
    attach to the *winner's* Unity and read another job's world. (Observed for
    real: three co-scheduled jobs on one node all defaulting to 9000.)

    Binding still leaves a millisecond window before Unity takes the port, so
    this narrows the race rather than closing it. What actually closes it is the
    launcher refusing to report success unless *our own* Unity pid is alive —
    see ``start_ratsim_headless.sh``.
    """
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        # No SO_REUSEADDR on purpose -- the strictest test available, so a port we
        # accept is one Unity can definitely bind. (Measured: SO_REUSEADDR makes no
        # difference against a live listener anyway; it only bypasses TIME_WAIT.)
        s.bind(("", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def _job_base_port(n_envs: int) -> int:
    """Per-job starting port, so co-scheduled jobs don't fight over 9100.

    Deterministic from the job id rather than probed, because SLURM ids are
    sequential: concurrent jobs land in different windows without any shared
    state. It is only a *starting point* — the caller still bind-tests every
    port and moves on if one is taken, which is what handles foreign listeners
    (the node probe found two already sitting in 9000-9999).
    """
    jid = _slurm_job_id()
    assert jid is not None, "_job_base_port called outside SLURM"
    stride = max(FRESH_PORT_WINDOW, -(-n_envs // FRESH_PORT_WINDOW) * FRESH_PORT_WINDOW)
    n_windows = max(1, FRESH_PORT_SPAN // stride)
    return FRESH_PORT_BASE + (jid % n_windows) * stride


def default_base_port(n_envs: int = 1) -> int:
    """Base port to start from when the caller has no preference.

    ``FRESH_PORT_BASE`` off SLURM (unchanged laptop behaviour), a job-derived
    window under it. Public so the scheduler's ``PortAllocator`` can start from
    the same place instead of re-deriving it and drifting.
    """
    return FRESH_PORT_BASE if _slurm_job_id() is None else _job_base_port(n_envs)


def _wait_port_bindable(port: int, timeout_s: float = 20.0) -> bool:
    """Wait for ``port`` to become bindable, up to ``timeout_s``.

    Exists for the scheduler's port recycling: it releases a run's window and
    can hand the same ports to the next stage immediately, while the previous
    stage's Unity is still shutting down. Measured — stage 1 of a run failed
    twice on 9630 then 9620, which were precisely that job's stage-0 ports; a
    later scan confirmed both were free and nothing foreign was listening.

    So the right response to "busy" on a *requested* port is to wait for the
    known previous holder to go, not to fail (which kills the run) and not to
    move to another port (which desyncs the scheduler's accounting).

    This is now a BACKSTOP, not the primary defence. 20 s turned out to be too
    short for dreamer teardown on a loaded node — job 11325473 lost 4 of 4
    dreamer re-dispatches this way, while 0 of 4 PPO ones failed. Rather than
    guess a bigger number, the scheduler's PortAllocator now quarantines a
    released window and tests it before reuse (scheduler/ports.py), so a run
    normally gets a different window instead of waiting on this one. What is
    left here is the residual race: the allocator tests a port, then the child
    binds it a moment later.
    """
    deadline = time.monotonic() + timeout_s
    while True:
        if _port_bindable(port):
            return True
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.5)


def _next_candidate_port(port: int, taken: set) -> int:
    """First port at or after ``port`` that looks usable right now."""
    while port <= 65535:
        if port not in taken and port != PERSISTENT_PORT and _port_bindable(port):
            return port
        port += 1
    raise RuntimeError("ran out of ports")


def _spawn_first_free(binary: Path, start_port: int, taken: set,
                      attempts: int = 8) -> int:
    """Spawn one Unity on the first port that actually works; return that port.

    A bind test can only ever say "free a moment ago" — two processes can pass
    it simultaneously and both launch on the same port. So the bind test is the
    hint and *this retry loop is the actual fix*: the launcher script refuses to
    report success unless our own Unity owns the port, so a loser gets a non-zero
    exit here and simply moves to the next port instead of quietly sharing the
    winner's simulator.
    """
    port = start_port
    last: Optional[Exception] = None
    for attempt in range(attempts):
        port = _next_candidate_port(port, taken)
        try:
            _spawn_via_script(binary, port)
            return port
        except RuntimeError as e:
            last = e
            print(f"[unity_launcher] port {port} did not come up cleanly "
                  f"(attempt {attempt + 1}/{attempts}); trying the next one")
            port += 1
    raise RuntimeError(
        f"could not get a Unity instance up after {attempts} ports starting at "
        f"{start_port}. Last error: {last}"
    )


def _read_pidfile(port: int) -> Optional[int]:
    p = _pidfile(port)
    if not p.exists():
        return None
    try:
        return int(p.read_text().strip())
    except (ValueError, OSError):
        return None


def _pid_alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except (ProcessLookupError, PermissionError):
        return False
    except OSError:
        return False


def _instance_alive(port: int) -> bool:
    """Liveness: something is accepting TCP connections on ``port``.

    An open port is the authoritative signal — if a client can connect, there
    is a Unity to attach to (Editor Play mode, a manually-launched binary, or
    one we spawned). The pidfile written by start_ratsim_headless.sh is only
    used for cleanup of instances *we* spawned, NOT for liveness: a stale
    pidfile (e.g. an old headless build's, left behind while the Editor now
    holds the port) must not mask a live listener, or we'd wrongly spawn a
    fresh build on top of the running Editor instead of attaching to it.
    """
    if not _port_open(port):
        return False
    # Port is live. If a stale pidfile points at a dead pid, drop it so it
    # doesn't linger and confuse cleanup later — but the port is what counts.
    pid = _read_pidfile(port)
    if pid is not None and not _pid_alive(pid):
        try:
            _pidfile(port).unlink(missing_ok=True)
        except OSError:
            pass
    return True


def _resolve_binary() -> Optional[Path]:
    raw = os.environ.get("RATSIM_UNITY_BIN")
    if not raw:
        return None
    p = Path(raw).expanduser()
    if not p.exists() or not os.access(p, os.X_OK):
        raise RuntimeError(
            f"RATSIM_UNITY_BIN points at {p} but it isn't an executable file"
        )
    return p


def _launcher_script() -> Path:
    """Find start_ratsim_headless.sh next to this module's repo."""
    here = Path(__file__).resolve()
    candidate = here.parent.parent / "scripts" / "start_ratsim_headless.sh"
    if candidate.exists():
        return candidate
    raise RuntimeError(f"could not locate start_ratsim_headless.sh (looked at {candidate})")


def _boot_timeout() -> float:
    """Seconds to allow one Unity instance to open its TCP port.

    Must agree with BOOT_TIMEOUT in start_ratsim_headless.sh, which reads the
    same variable — if this one is shorter, subprocess.run kills the script
    mid-wait and the failure looks like a crash rather than a timeout.

    Default 30 matches a single instance on an idle node (~1-2s measured, so a
    wide margin). It is NOT enough during a launch storm: 7 concurrent runs at
    n_envs=4 put ~20 Unity processes on the node while the rest were still
    booting, boots crossed 30s, and all 7 runs failed. Raise it for packed jobs.
    """
    raw = os.environ.get("RATSIM_BOOT_TIMEOUT", "")
    try:
        v = float(raw)
        return v if v > 0 else 30.0
    except ValueError:
        return 30.0


def _spawn_via_script(binary: Path, port: int, log_path: Optional[str] = None,
                      timeout_s: Optional[float] = None) -> None:
    """Launch a Unity instance via start_ratsim_headless.sh and block until ready.

    The script handles the X display, pidfile, and per-port log. It exits 0 once
    the TCP listener is up.
    """
    if timeout_s is None:
        timeout_s = _boot_timeout()
    script = _launcher_script()
    cmd = [str(script), str(binary), "--port", str(port)]
    if log_path:
        cmd.extend(["--log", log_path])
    print(f"[unity_launcher] spawning Unity on port {port} via {script.name}")
    res = subprocess.run(cmd, timeout=timeout_s + 5)
    if res.returncode != 0:
        raise RuntimeError(f"start_ratsim_headless.sh failed for port {port} (rc={res.returncode})")


def _kill_owned(port: int) -> None:
    """Best-effort: kill the instance we spawned on this port.

    The pidfile holds the *Unity* pid even when the launcher wrapped it in
    ``xvfb-run``, so killing it is enough: xvfb-run tears down its own X server
    once the command exits. The ``.xpid`` sidecar only exists on the fallback
    path where the launcher started ``Xvfb`` itself; that server is started with
    ``-terminate`` and should exit on its own, but reap it here too.
    """
    pid = _read_pidfile(port)
    if pid is not None:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        except OSError as e:
            print(f"[unity_launcher] failed to kill pid {pid} on port {port}: {e}")
    for suffix in ("pid", "xpid", "pgid"):
        path = _rundir() / f"ratsim_{port}.{suffix}"
        if suffix == "xpid":
            try:
                xpid = int(path.read_text().strip())
            except (ValueError, OSError):
                xpid = None
            if xpid is not None:
                try:
                    os.kill(xpid, signal.SIGKILL)
                except (ProcessLookupError, OSError):
                    pass
        try:
            path.unlink(missing_ok=True)
        except OSError:
            pass


def _register_cleanup(port: int) -> None:
    """Ensure spawned instances die when the parent Python process exits."""
    atexit.register(_kill_owned, port)


DEFAULT_ATTACH_TIMEOUT = 300.0
"""Seconds ``attach_instance`` waits for a Unity to appear. 0 = wait forever."""


def attach_instance(
    port: int = PERSISTENT_PORT,
    timeout_s: Optional[float] = None,
    poll_s: float = 1.0,
) -> UnityInstance:
    """Attach to a Unity that somebody else is running. NEVER spawns one.

    This is the "I want to watch the agent in the Editor" entry point, and it
    exists because ``allocate_unity_instances(n_envs=1)`` is the wrong default
    for that job: if :9000 is not accepting *at that instant* it silently
    spawns a headless build instead. With ``RATSIM_UNITY_BIN`` exported (the
    normal state on a dev box) that turns "I forgot to press Play" into a run
    against an invisible simulator — and since the launcher gained xvfb mode
    the spawned instance renders to a throwaway X server, so there is nothing
    to see anywhere and the Editor is left unable to bind :9000 afterwards.

    Waiting is the right response instead: the Editor takes a few seconds to
    enter Play mode, and a human is by definition present.

    Args:
        port: port to attach to. Default :9000, the persistent/Editor slot.
        timeout_s: how long to wait. None reads ``$RATSIM_ATTACH_TIMEOUT``,
            falling back to ``DEFAULT_ATTACH_TIMEOUT``. <= 0 waits forever.
        poll_s: interval between probes.

    Raises:
        RuntimeError: if nothing is listening before the timeout.
    """
    if timeout_s is None:
        raw = os.environ.get("RATSIM_ATTACH_TIMEOUT", "")
        try:
            timeout_s = float(raw) if raw else DEFAULT_ATTACH_TIMEOUT
        except ValueError:
            timeout_s = DEFAULT_ATTACH_TIMEOUT

    if _instance_alive(port):
        print(f"[unity_launcher] attached to existing instance on port {port}")
        return UnityInstance(port=port, owned=False)

    forever = timeout_s <= 0
    deadline = None if forever else time.monotonic() + timeout_s
    waited = "indefinitely" if forever else f"up to {timeout_s:.0f}s"
    print(f"[unity_launcher] nothing listening on port {port} — waiting {waited}. "
          f"Press Play in the Unity Editor (or run scripts/start_ratsim_headless.sh). "
          f"Nothing will be spawned.")
    last_note = time.monotonic()
    while True:
        time.sleep(poll_s)
        if _instance_alive(port):
            print(f"[unity_launcher] attached to existing instance on port {port}")
            return UnityInstance(port=port, owned=False)
        now = time.monotonic()
        if now - last_note >= 15.0:
            left = "" if forever else f" ({deadline - now:.0f}s left)"
            print(f"[unity_launcher] still waiting for port {port}{left}...")
            last_note = now
        if deadline is not None and now >= deadline:
            raise RuntimeError(
                f"no Unity accepting connections on port {port} after {timeout_s:.0f}s.\n"
                f"Either press Play in the Editor, or start one yourself "
                f"(scripts/start_ratsim_headless.sh <build> --port {port}), or ask the "
                f"caller to spawn a build instead (eval scripts: --spawn; raise the wait "
                f"with RATSIM_ATTACH_TIMEOUT=<seconds>, 0 to wait forever)."
            )


def allocate_unity_instances(
    n_envs: int = 1,
    fresh: bool = False,
    base_port: Optional[int] = None,
) -> List[UnityInstance]:
    """Pick (and spawn if needed) Unity instances for ``n_envs`` envs.

    Rules:
      * n_envs == 1, no ``base_port``, not ``fresh``: probe :9000. Reuse if
        alive; else spawn fresh on :9000 (requires RATSIM_UNITY_BIN). Useful
        for debug runs alongside a manually-launched Unity.
      * n_envs == 1 with ``fresh=True`` or an explicit ``base_port``: always
        spawn on ``base_port`` (default :9100 if ``fresh=True`` and no
        base_port given). Use when you don't want to touch the persistent
        instance — e.g. running a second training process in parallel.
      * n_envs > 1: always spawn fresh on ``base_port..base_port+n-1``
        (default base_port :9100). Refuses to use port 9000. Requires
        RATSIM_UNITY_BIN.

    **Under SLURM** (``$SLURM_JOB_ID`` set) two rules change, because the node
    is shared with other users' jobs:
      * the default base port is derived from the job id instead of being 9100
        for everybody, and every port is bind-tested before use;
      * :9000 is never reused. An open :9000 on a cluster node is far more
        likely to be a stranger's process than your Editor, and attaching to it
        would silently train against someone else's simulator.
    Both are no-ops off SLURM, so laptop behaviour is unchanged.

    Returns one UnityInstance per env. ``.owned`` is True for spawned ones
    (cleaned up at process exit) and False for the reused persistent instance.
    """
    if n_envs < 1:
        raise ValueError(f"n_envs must be >= 1, got {n_envs}")

    binary = _resolve_binary()
    jid = _slurm_job_id()
    base_port_specified = base_port is not None
    if base_port is None:
        base_port = default_base_port(n_envs)

    # n_envs=1 reuse path: only when the caller didn't ask for a specific port,
    # and never on a shared cluster node (see docstring).
    if n_envs == 1 and not fresh and not base_port_specified and jid is None:
        if _instance_alive(PERSISTENT_PORT):
            print(f"[unity_launcher] reusing existing instance on port {PERSISTENT_PORT}")
            return [UnityInstance(port=PERSISTENT_PORT, owned=False)]
        if binary is None:
            raise RuntimeError(
                f"no Unity instance on port {PERSISTENT_PORT} and RATSIM_UNITY_BIN is unset.\n"
                f"either: (a) launch Unity manually (Editor Play / start_ratsim_headless.sh) "
                f"so it listens on {PERSISTENT_PORT}, or (b) export RATSIM_UNITY_BIN to enable auto-spawn."
            )
        _spawn_via_script(binary, PERSISTENT_PORT)
        _register_cleanup(PERSISTENT_PORT)
        return [UnityInstance(port=PERSISTENT_PORT, owned=True)]

    # Fresh-spawn path (n_envs > 1, fresh=True, explicit base_port, or SLURM)
    if binary is None:
        extra = ""
        if jid is not None:
            extra = ("\nNote: under SLURM the persistent :9000 instance is never reused, "
                     "so a build path is required even for n_envs=1.")
        raise RuntimeError(
            "auto-spawn requested but RATSIM_UNITY_BIN is unset.\n"
            "export RATSIM_UNITY_BIN=/path/to/ForagerSimBuild.x86_64 to enable multi-env training."
            + extra
        )

    if base_port_specified:
        # The caller named exact ports — most importantly the scheduler, which
        # tracks the window it handed out. Silently shifting would desync its
        # bookkeeping, so fail loudly instead.
        ports = list(range(base_port, base_port + n_envs))
        for p in ports:
            if p == PERSISTENT_PORT:
                raise ValueError(
                    f"fresh-spawn range {ports} overlaps the persistent port {PERSISTENT_PORT}; "
                    f"pick a different base_port"
                )
            if not _port_bindable(p):
                print(f"[unity_launcher] port {p} busy, waiting for it to free up "
                      f"(likely the previous stage's Unity shutting down)")
                if not _wait_port_bindable(p):
                    raise RuntimeError(
                        f"port {p} still in use after waiting; kill it first "
                        f"(./scripts/stop_ratsim_headless.sh --port {p}) "
                        f"or pick a different base_port"
                    )
        instances: List[UnityInstance] = []
        for p in ports:
            _spawn_via_script(binary, p)
            _register_cleanup(p)
            instances.append(UnityInstance(port=p, owned=True))
    else:
        # Spawn one at a time and let each retry past a lost race, rather than
        # picking all n ports up front: by the time instance k launches, the
        # ports chosen for k+1.. may well have been taken by someone else.
        instances = []
        taken: set = set()
        next_start = base_port
        for _ in range(n_envs):
            p = _spawn_first_free(binary, next_start, taken)
            taken.add(p)
            next_start = p + 1
            _register_cleanup(p)
            instances.append(UnityInstance(port=p, owned=True))

    ports = [i.port for i in instances]
    print(f"[unity_launcher] spawned {n_envs} fresh instance(s) on ports {ports}")
    return instances


__all__ = [
    "UnityInstance",
    "PERSISTENT_PORT",
    "FRESH_PORT_BASE",
    "DEFAULT_ATTACH_TIMEOUT",
    "default_base_port",
    "allocate_unity_instances",
    "attach_instance",
]
