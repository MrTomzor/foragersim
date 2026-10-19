using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Physical behaviour of a single well. Owns the arrive → dwell → dispense
/// micro-machine and its visuals; the *policy* of which well is armed/cued lives
/// in a schedule controller (Phase 3) or, absent one, defaults to an always-armed
/// feeder.
///
/// Dispensing spawns REAL reward objects (Pickupables) at random positions in an
/// annulus [rewardSpawnMinDist, rewardSpawnMaxDist] around the well (both 0 =
/// spawn on the well). The agent collects them by TOUCHING them — the normal
/// Pickupable → /reward_pickup path. The well NEVER credits reward on proximity;
/// arrival_radius only gates WHEN rewards are dispensed. The well waits until all
/// dispensed rewards are collected, then cools down and re-arms.
///
/// Wrong-well detection (opt-in, driven by the schedule controller): a DISARMED well
/// that is being watched runs the same dwell countdown and, instead of dispensing,
/// publishes a wrong-activation EVENT. The well never scores it — what (if anything)
/// the event costs is decided downstream by the Python TaskTracker.
///
/// Driven once per simulation step by WellLoader (one server timer ticks the whole
/// roster), so it stays in lockstep with the scripted Physics.Simulate loop.
/// Proximity is a horizontal distance test against the agent position the loader
/// supplies — no trigger collider, so a well never blocks the agent.
/// </summary>
public class Well : MonoBehaviour {

    /// <summary>How the perceptual cue (SignalSource on the 'cue' channel + optional cueLight)
    /// behaves on a CUED (Random) trial. Home trials arm uncued, so the cue never fires.</summary>
    public enum CueMode {
        None,          // cue disabled (pure-memory ablation)
        Timed,         // on for cueDurationSteps after the trial starts, then dark (harder)
        UntilDepleted  // on from trial start until the well dispenses (faithful steady LED)
    }

    public WellData data;

    // Wired by WellLoader.
    private GameObject _cueLight;
    private GameObject _rewardPrefab;
    private string _rewardTopic = "/reward_pickup";
    private int _rewardAmount = 1;
    private bool _verbose;
    private bool _drawGizmo = true;

    // Cue (wired by WellLoader). The SignalSource sits on this well and is toggled on
    // only while the cue is active; the SectorSignalSensor picks up enabled sources.
    private SignalSource _cueSignal;
    private CueMode _cueMode = CueMode.None;
    private int _cueDurationSteps;

    // Runtime state.
    private int _dwell;
    private int _cooldown;
    private bool _awaitingCollection;
    private readonly List<GameObject> _activeRewards = new List<GameObject>();

    // Cue runtime state.
    private bool _prevCuedArmed;        // for rising-edge detection (trial start)
    private int  _cueTimer;             // steps remaining (Timed mode)
    private bool _dispensedSinceArm;    // UntilDepleted: cue off once the well has dispensed

    // Wrong-well detection (driven by the schedule controller; off by default).
    private bool   _wrongWatch;         // count dwells at this (disarmed) well as wrong activations
    private int    _wrongDelaySteps;    // same dwell the armed well of this trial would need
    private int    _wrongTrialType;     // trial type reported with the event (0=random, 1=home)
    private string _wrongTopic;
    private int    _wrongDwell;
    private bool   _needsReentry;       // agent must leave the radius before a countdown may start

    public void Init(GameObject cueLight, GameObject rewardPrefab, string rewardTopic,
                     int rewardAmount, bool verbose, bool drawGizmo,
                     SignalSource cueSignal, CueMode cueMode, int cueDurationSteps) {
        _cueLight     = cueLight;
        _rewardPrefab = rewardPrefab;
        _rewardTopic  = rewardTopic;
        _rewardAmount = rewardAmount;
        _verbose      = verbose;
        _drawGizmo    = drawGizmo;
        _cueSignal    = cueSignal;
        _cueMode      = cueMode;
        _cueDurationSteps = cueDurationSteps;
        if (_cueLight != null) _cueLight.SetActive(false);
        if (_cueSignal != null) _cueSignal.enabled = false;
    }

    public void Tick(Vector3 agentPos, bool agentKnown) {
        UpdateCue();

        // While dispensed rewards are still out, wait — the agent must touch them.
        // (Pickupable destroys them on collection, so pruned entries mean collected.)
        if (PruneActiveRewards() > 0) return;

        // All dispensed rewards were just collected → start the cooldown once.
        if (_awaitingCollection) {
            _awaitingCollection = false;
            _cooldown = data.cooldownSteps;
        }

        // Independent of cooldown/depletion: those gate dispensing, not the agent's mistake.
        if (!data.armed && _wrongWatch) TickWrongDwell(agentPos, agentKnown);

        if (data.depleted) return;
        if (_cooldown > 0) { _cooldown--; return; }
        if (!data.armed || !agentKnown) { _dwell = 0; return; }

        // Horizontal (XZ) proximity: this only gates DISPENSING, not collection.
        if (!InArrivalRadius(agentPos)) { _dwell = 0; return; }

        if (++_dwell < data.dispenseDelaySteps) return;
        Dispense();
        _dwell = 0;
    }

    private bool InArrivalRadius(Vector3 agentPos) {
        float dx = agentPos.x - transform.position.x;
        float dz = agentPos.z - transform.position.z;
        return dx * dx + dz * dz <= data.arrivalRadius * data.arrivalRadius;
    }

    /// <summary>
    /// The mirror image of arrive → dwell → dispense for a well that is NOT the target:
    /// the agent enters the radius, dwells for the trial's delay, and a wrong-activation
    /// event is published — one per entry. The countdown only starts on an ENTRY: a well
    /// that was just disarmed under the agent (it is still collecting the rewards it
    /// dispensed) and a well that just fired both wait for the agent to leave first.
    /// </summary>
    private void TickWrongDwell(Vector3 agentPos, bool agentKnown) {
        if (!agentKnown || !InArrivalRadius(agentPos)) { _needsReentry = false; _wrongDwell = 0; return; }
        if (_needsReentry) return;
        if (++_wrongDwell < _wrongDelaySteps) return;

        _wrongDwell = 0;
        _needsReentry = true;
        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        if (conn != null) {
            // [ well_id, trial_type (0=random,1=home), reward_at_stake (pickup units a correct
            //   activation would have dispensed) ]
            float atStake = Mathf.Max(1, data.rewardsPerDispense) * _rewardAmount;
            conn.Publish(_wrongTopic, new FloatArrayMessage {
                data = new float[] { data.wellId, _wrongTrialType, atStake }
            });
        }
        if (_verbose)
            Debug.Log($"Well {data.wellId}: wrong activation (trial type {_wrongTrialType})");
    }

    /// <summary>
    /// Drives the perceptual cue (SignalSource + optional cueLight). The cue window opens on
    /// the rising edge of (armed &amp;&amp; cued) — i.e. the start of a CUED (Random) trial — and
    /// closes per <see cref="CueMode"/>. Home trials arm uncued, so this never turns the cue on.
    /// </summary>
    private void UpdateCue() {
        bool cuedArmed = data.armed && data.cued && !data.depleted;

        // Rising edge = a cued trial just started → (re)open the cue window.
        if (cuedArmed && !_prevCuedArmed) {
            _cueTimer = _cueDurationSteps;
            _dispensedSinceArm = false;
        }
        _prevCuedArmed = cuedArmed;

        bool active;
        switch (_cueMode) {
            case CueMode.Timed:
                active = cuedArmed && _cueTimer > 0;
                if (cuedArmed && _cueTimer > 0) _cueTimer--;
                break;
            case CueMode.UntilDepleted:
                active = cuedArmed && !_dispensedSinceArm;
                break;
            case CueMode.None:
            default:
                active = false;
                break;
        }

        if (_cueSignal != null && _cueSignal.enabled != active) _cueSignal.enabled = active;
        if (_cueLight != null) _cueLight.SetActive(active);
    }

    private int PruneActiveRewards() {
        for (int i = _activeRewards.Count - 1; i >= 0; i--)
            if (_activeRewards[i] == null) _activeRewards.RemoveAt(i);
        return _activeRewards.Count;
    }

    private void Dispense() {
        if (_rewardPrefab == null) return;
        data.visitCount++;

        int n = Mathf.Max(1, data.rewardsPerDispense);
        for (int i = 0; i < n; i++) {
            Vector3 pos = DispensePoint();
            // Parent to the well so rewards are cleaned up with the roster on episode reset.
            GameObject r = Instantiate(_rewardPrefab, pos, Quaternion.identity, transform);
            Pickupable p = r.GetComponentInChildren<Pickupable>();
            if (p != null) { p.topicName = _rewardTopic; p.publishedNumber = _rewardAmount; }
            _activeRewards.Add(r);
        }
        _awaitingCollection = true;
        _dispensedSinceArm = true; // UntilDepleted cue closes now (agent reached the well)

        if (_verbose)
            Debug.Log($"Well {data.wellId} dispensed {n} reward(s) (visit {data.visitCount}) — collect by touch");

        if (data.depleteOnDispense) data.depleted = true;
    }

    /// <summary>Random point in the annulus [min,max] around the well (area-uniform), snapped to terrain.</summary>
    private Vector3 DispensePoint() {
        float maxD = data.rewardSpawnMaxDist;
        if (maxD <= 0f) return transform.position;              // both 0 → spawn on the well
        float minD = Mathf.Clamp(data.rewardSpawnMinDist, 0f, maxD);
        float ang = Random.value * Mathf.PI * 2f;
        float r = Mathf.Sqrt(Mathf.Lerp(minD * minD, maxD * maxD, Random.value));
        float x = transform.position.x + Mathf.Cos(ang) * r;
        float z = transform.position.z + Mathf.Sin(ang) * r;
        float y = WorldServices.Has<IHeightProvider>()
            ? WorldServices.Get<IHeightProvider>().GetTerrainHeight(x, z)
            : transform.position.y;
        return new Vector3(x, y, z);
    }

    // ── Schedule API (Phase 3 WellScheduleController; harmless now) ──
    public void Arm(bool cued, int delaySteps) { data.armed = true; data.cued = cued; data.dispenseDelaySteps = delaySteps; _dwell = 0; }
    public void Disarm() {
        if (data.armed) _needsReentry = true; // it was the target: the agent may still be standing here
        data.armed = false;
        _dwell = 0;
    }

    /// <summary>Report dwells at this well while it is disarmed as wrong activations (see
    /// <see cref="TickWrongDwell"/>). Called per trial by the schedule controller.</summary>
    public void WatchWrongDwell(int delaySteps, int trialType, string topic) {
        _wrongWatch = true;
        _wrongDelaySteps = delaySteps;
        _wrongTrialType = trialType;
        _wrongTopic = topic;
        _wrongDwell = 0;
    }
    public void StopWrongWatch() { _wrongWatch = false; _wrongDwell = 0; }
    public void SetDepleted(bool value) { data.depleted = value; }

    /// <summary>Dispense right now, bypassing the arrive→dwell gate. Used to "prime" a well
    /// at trial start — e.g. the Home freebie that opens an episode so the agent is handed
    /// its first reward at Home before the alternation begins. No-op if depleted.</summary>
    public void ForceDispense() { if (data != null && !data.depleted) { Dispense(); _dwell = 0; } }

    /// <summary>Total number of times this well has dispensed (increments on each Dispense).</summary>
    public int DispenseCount => data != null ? data.visitCount : 0;

    /// <summary>True from the moment rewards are dispensed until the agent has collected them all.</summary>
    public bool AwaitingCollection => _awaitingCollection;

    // ── Debug gizmo (editor only; never in builds or agent cameras) ──
    // Circle above the well + the dispense-radius ring on the ground.
    // Green = active (armed, ready → will dispense when agent is near),
    // Yellow = closed (cooling down, disarmed, or rewards still out to collect),
    // Red = depleted.
    private void OnDrawGizmos() {
        if (!_drawGizmo || data == null) return;

        Color c = data.depleted ? Color.red
                : (!data.armed || _cooldown > 0 || _activeRewards.Count > 0) ? Color.yellow
                : Color.green;

        Vector3 p = transform.position;
        DrawGizmoCircle(p + Vector3.up * 0.05f, data.arrivalRadius, c); // dispense zone on the ground
        DrawGizmoCircle(p + Vector3.up * 1.5f, 0.35f, c);              // state indicator above
        Gizmos.color = c;
        Gizmos.DrawLine(p, p + Vector3.up * 1.5f);
    }

    private static void DrawGizmoCircle(Vector3 center, float radius, Color color, int segments = 32) {
        Gizmos.color = color;
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++) {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
