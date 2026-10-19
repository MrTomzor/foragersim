using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase 3 of the wells task family: the Home/Random alternation scheduler, the
/// memory-demanding core of Widloski &amp; Foster 2022. Active when
/// <c>well_schedule/enabled = 1</c>; otherwise wells stay always-armed feeders
/// (Phase 1 behaviour) and this controller does nothing.
///
/// One well is designated the <b>Home</b> well for the episode (fixed, UNCUED — the
/// agent must RECALL its location, which is the memory demand). Trials then
/// alternate:
///   - <b>Random</b> trial: a randomly chosen non-home well is armed and CUED (LED —
///     reactive/easy). Reshuffled each Random trial.
///   - <b>Home</b> trial: the Home well is armed and UNCUED. The agent must return
///     to it from memory.
/// Exactly one well is armed at a time (all others disarmed), so the agent is driven
/// back and forth Home↔Random. A trial completes when its well has dispensed AND the
/// agent has collected the dispensed reward(s); the controller then flips to the
/// other trial type. Reward still flows via <c>Pickupable → /reward_pickup →</c>
/// TaskTracker — this controller only drives which well is armed/cued.
///
/// Each trial change (and each dispense) is published on <c>well_schedule/state_topic</c>
/// (default <c>/well/state</c>) as a <see cref="FloatArrayMessage"/>:
///   [ target_well_id, trial_type (0=random,1=home), cued (0/1), home_well_id, dispensed (0/1) ]
/// so Python obs/logging can read the current target and whether it is cued. (The
/// visual LED cue itself needs a "cueLight" child on the well prefab and cue
/// perception wiring — a separate follow-up; the state message already carries the
/// cued flag.)
///
/// Home-well selection is INDEPENDENT of where the agent spawns (that is pure
/// agents_spawn_pos config — point allowed_structures at "home_well_room" to start at
/// Home, "well_room" to start away from it, or both for anywhere).
///
/// Config params (under "well_schedule/"):
///   well_schedule/enabled            -- 0/1; master switch. Default 0.
///   well_schedule/home_source        -- how Home is picked: "structure" (well in the layout's
///                                       Home-labelled room) | "center" (centroid well) |
///                                       "random" (seeded per episode) | "id". Default "center".
///   well_schedule/home_structure_type-- structure type the layout labels the Home room (home_source=structure). Default "home_well_room".
///   well_schedule/home_well_id       -- fixed Home well id (home_source=id). Default -1.
///   well_schedule/first_trial        -- "random" | "home". Default "random".
///   well_schedule/prime_home_reward  -- 0/1; open the episode by immediately dispensing a reward at
///                                       Home (forces a Home first trial; the freebie grounds the agent
///                                       at Home before the alternation begins). Default 0.
///   well_schedule/random_delay_steps -- dwell steps before a Random well dispenses. Default 10.
///   well_schedule/home_delay_steps   -- dwell steps before the Home well dispenses. Default 10.
///   well_schedule/inter_trial_delay_min_steps / _max_steps -- steps with NO well armed after each
///                                       collection, drawn uniformly per trial (paper's 5-15 s gap
///                                       to encourage spatial coverage). Both 0 = immediate hand-off. Default 0/0.
///   well_schedule/state_topic        -- topic for the state message. Default "/well/state".
///   well_schedule/wrong_well_detection -- 0/1; report dwells at a NON-target well. While a trial is
///                                       running, an agent that enters a wrong well's arrival radius and
///                                       dwells as long as the target would need publishes one event per
///                                       entry on wrong_well_topic as a FloatArrayMessage
///                                       [ well_id, trial_type (0=random,1=home), reward_at_stake ].
///                                       Detection only — the sim is otherwise unchanged; whether an event
///                                       is penalised (and on which trial types) is the task config's call
///                                       (TaskTracker foraging_settings). Nothing fires during the
///                                       inter-trial gap (no well is armed, so none is "wrong"). Default 0.
///   well_schedule/wrong_well_delay_steps -- dwell before a wrong activation fires; -1 = the current
///                                       trial's dispense delay (keep them equal, or waiting out the
///                                       shorter one becomes a free probe). Default -1.
///   well_schedule/wrong_well_topic   -- topic for the events. Default "/well/wrong_activation".
/// </summary>
public class WellScheduleController : WorldDataProvider {

    public override WorldDataType[] Provides  => new[] { WorldDataType.WellSchedule };
    // Runs after WellLoader.Generate has spawned the roster.
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Wells };

    [Header("Debug")]
    public bool verbose = false;

    private enum TrialType { Random = 0, Home = 1 }

    // ── Params ──
    private bool   _enabled;
    private string _homeSource;         // structure | center | random | id
    private int    _homeWellIdParam;
    private string _homeStructureType;  // for home_source=structure
    private TrialType _firstTrial;
    private bool   _primeHomeReward;    // episode opens with an immediate Home dispense (freebie)
    private int    _randomDelaySteps;
    private int    _homeDelaySteps;
    private int    _interTrialDelayMin;   // steps with NO well armed after a collection
    private int    _interTrialDelayMax;   // both 0 = immediate hand-off
    private string _stateTopic;
    private bool   _wrongWellDetection;
    private int    _wrongWellDelaySteps;  // -1 = the current trial's dispense delay
    private string _wrongWellTopic;

    // ── Runtime ──
    private IReadOnlyList<Well> _wells;
    private Well _homeWell;
    private Well _target;
    private TrialType _currentTrial;
    private int  _trialStartDispenseCount;
    private bool _dispensedThisTrial;
    private int  _lastRandomWellId = -1;
    private System.Random _rng;
    private bool _ready;
    private bool _timerRegistered;

    // Inter-trial delay state (no well armed during the gap).
    private bool _inInterTrial;
    private int  _interTrialTimer;
    private TrialType _pendingTrial;

    // ─────────────────────────────────────────────

    public override void Generate() {
        LoadParams();
        _ready = false;
        if (!_enabled) {
            if (verbose) Debug.Log("WellScheduleController: well_schedule/enabled = 0, skipping");
            return;
        }

        IWellProvider provider = WorldServices.Has<IWellProvider>() ? WorldServices.Get<IWellProvider>() : null;
        _wells = provider?.GetWells();
        if (_wells == null || _wells.Count == 0) {
            WorldGenStatus.Warning("WellScheduleController",
                "well_schedule/enabled = 1 but no wells exist — enable wells/ and point them at a structure.");
            return;
        }

        _rng = new System.Random(WorldLoadingController.GetDerivedSeed("well_schedule"));
        _homeWell = ChooseHomeWell();
        _lastRandomWellId = -1;
        _inInterTrial = false;

        // prime_home_reward: open the episode by handing the agent a reward at Home. The first
        // trial is forced Home (so the freebie sits at the well the agent must later recall) and
        // its reward is dispensed immediately, bypassing arrive→dwell. Once the agent collects
        // it, the trial completes normally and the alternation flips to Random.
        _currentTrial = _primeHomeReward ? TrialType.Home : _firstTrial;
        if (_primeHomeReward && _firstTrial != TrialType.Home && verbose)
            Debug.Log("WellScheduleController: prime_home_reward=1 forces a Home first trial " +
                      "(overriding well_schedule/first_trial=random).");
        StartTrial(_currentTrial); // first trial: no preceding collection, so no inter-trial delay
        if (_primeHomeReward && _target != null) _target.ForceDispense();
        _ready = true;

        // Register the per-step tick exactly once — this provider persists across episodes.
        if (!_timerRegistered) {
            RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
            if (conn != null) { conn.RegisterTimerDiscrete(Tick, 1u); _timerRegistered = true; }
        }

        if (verbose)
            Debug.Log($"WellScheduleController: {_wells.Count} wells, home=well_{_homeWell.data.wellId}, " +
                      $"first trial={_firstTrial}");
    }

    public override void Clear() {
        // Wells are destroyed + re-spawned by WellLoader each episode; drop stale refs.
        _ready = false;
        _wells = null;
        _homeWell = null;
        _target = null;
        _inInterTrial = false;
    }

    private void LoadParams() {
        _enabled          = WorldLoadingController.GetParamInt("well_schedule/enabled", 0) != 0;
        _homeSource       = WorldLoadingController.GetParamString("well_schedule/home_source", "center").ToLowerInvariant();
        _homeWellIdParam  = WorldLoadingController.GetParamInt("well_schedule/home_well_id", -1);
        _homeStructureType= WorldLoadingController.GetParamString("well_schedule/home_structure_type", "home_well_room");
        _randomDelaySteps = WorldLoadingController.GetParamInt("well_schedule/random_delay_steps", 10);
        _homeDelaySteps   = WorldLoadingController.GetParamInt("well_schedule/home_delay_steps", 10);
        _interTrialDelayMin = WorldLoadingController.GetParamInt("well_schedule/inter_trial_delay_min_steps", 0);
        _interTrialDelayMax = WorldLoadingController.GetParamInt("well_schedule/inter_trial_delay_max_steps", 0);
        _stateTopic       = WorldLoadingController.GetParamString("well_schedule/state_topic", "/well/state");
        _wrongWellDetection  = WorldLoadingController.GetParamInt("well_schedule/wrong_well_detection", 0) != 0;
        _wrongWellDelaySteps = WorldLoadingController.GetParamInt("well_schedule/wrong_well_delay_steps", -1);
        _wrongWellTopic      = WorldLoadingController.GetParamString("well_schedule/wrong_well_topic", "/well/wrong_activation");
        string first      = WorldLoadingController.GetParamString("well_schedule/first_trial", "random");
        _firstTrial = first.Equals("home", System.StringComparison.OrdinalIgnoreCase)
            ? TrialType.Home : TrialType.Random;
        _primeHomeReward = WorldLoadingController.GetParamInt("well_schedule/prime_home_reward", 0) != 0;
    }

    // ─────────────────────────────────────────────
    //  Trial machine
    // ─────────────────────────────────────────────

    private void StartTrial(TrialType type) {
        _currentTrial = type;
        _target = (type == TrialType.Home) ? _homeWell : ChooseRandomWell();

        // Disarm everyone, then arm just the target. Home is uncued (recall);
        // Random is cued (LED).
        foreach (Well w in _wells) if (w != null) w.Disarm();
        if (_target != null) {
            bool cued = (type == TrialType.Random);
            int delay = (type == TrialType.Home) ? _homeDelaySteps : _randomDelaySteps;
            _target.Arm(cued, delay);
            _trialStartDispenseCount = _target.DispenseCount;

            // Every other well is "wrong" for this trial.
            int wrongDelay = _wrongWellDelaySteps >= 0 ? _wrongWellDelaySteps : delay;
            foreach (Well w in _wells) {
                if (w == null) continue;
                if (_wrongWellDetection && w != _target) w.WatchWrongDwell(wrongDelay, (int)type, _wrongWellTopic);
                else w.StopWrongWatch();
            }
        }
        _dispensedThisTrial = false;
        PublishState();

        if (verbose && _target != null)
            Debug.Log($"WellScheduleController: {type} trial → well_{_target.data.wellId} " +
                      $"(cued={_target.data.cued})");
    }

    private void Tick(TimerEvent evt) {
        if (!_ready) return;

        // Inter-trial gap: no well armed, just count down, then start the pending trial.
        if (_inInterTrial) {
            if (--_interTrialTimer <= 0) { _inInterTrial = false; StartTrial(_pendingTrial); }
            else PublishState();
            return;
        }

        if (_target != null) {
            if (!_dispensedThisTrial && _target.DispenseCount > _trialStartDispenseCount)
                _dispensedThisTrial = true;

            // Trial complete once the dispensed reward has been collected → advance (with delay).
            if (_dispensedThisTrial && !_target.AwaitingCollection) {
                TrialType next = (_currentTrial == TrialType.Home) ? TrialType.Random : TrialType.Home;
                AdvanceTo(next); // publishes the new trial's state
                return;
            }
        }

        // Publish current state every step so late-joining subscribers and per-step RL
        // observations always see the live target/trial/cued flags (like the sensors do).
        PublishState();
    }

    // Move to the next trial, optionally after an inter-trial delay with no well armed
    // (paper: 5-15 s after drinking before the next well fills, to encourage coverage).
    private void AdvanceTo(TrialType next) {
        int delay = SampleInterTrialDelay();
        if (delay <= 0) { StartTrial(next); return; }
        foreach (Well w in _wells) if (w != null) { w.Disarm(); w.StopWrongWatch(); }
        _target = null;
        _pendingTrial = next;
        _interTrialTimer = delay;
        _inInterTrial = true;
        PublishState();
        if (verbose) Debug.Log($"WellScheduleController: inter-trial delay {delay} steps before {next} trial");
    }

    private int SampleInterTrialDelay() {
        if (_interTrialDelayMax <= 0) return 0;
        int min = Mathf.Clamp(_interTrialDelayMin, 0, _interTrialDelayMax);
        return _rng.Next(min, _interTrialDelayMax + 1);
    }

    // ─────────────────────────────────────────────
    //  Well selection
    // ─────────────────────────────────────────────

    // Which well is Home. Independent of where the agent spawns. The default,
    // "structure", pairs with the layout's labelled Home room (home_cell_mode) so Home
    // location is chosen there; the other modes select Home purely here.
    private Well ChooseHomeWell() {
        switch (_homeSource) {
            case "id": {
                Well w = FindWellById(_homeWellIdParam);
                if (w != null) return w;
                WorldGenStatus.Warning("WellScheduleController",
                    $"home_source=id but well_schedule/home_well_id={_homeWellIdParam} not found; using centroid well.");
                return CentroidWell();
            }
            case "random":
                return _wells[_rng.Next(_wells.Count)];
            case "center":
                return CentroidWell();
            case "structure": {
                Well w = WellNearestHomeStructure();
                if (w != null) return w;
                WorldGenStatus.Warning("WellScheduleController",
                    $"home_source=structure but no '{_homeStructureType}' structure found " +
                    $"(set widloski_maze/home_cell_mode so the layout labels one); using centroid well.");
                return CentroidWell();
            }
            default:
                WorldGenStatus.Warning("WellScheduleController",
                    $"unknown well_schedule/home_source='{_homeSource}'; using centroid well.");
                return CentroidWell();
        }
    }

    private Well FindWellById(int id) {
        foreach (Well w in _wells) if (w != null && w.data.wellId == id) return w;
        return null;
    }

    // The well nearest the roster centroid — the central well in a symmetric grid.
    private Well CentroidWell() {
        Vector3 centroid = Vector3.zero;
        int n = 0;
        foreach (Well w in _wells) if (w != null) { centroid += w.transform.position; n++; }
        if (n == 0) return null;
        centroid /= n;
        return NearestWell(new Vector2(centroid.x, centroid.z));
    }

    // The well nearest the (single) Home-labelled room structure's center.
    private Well WellNearestHomeStructure() {
        WorldStructure home = null;
        foreach (WorldStructure s in WorldData.GetStructures()) {
            if (s != null && s.structureType.StartsWith(_homeStructureType, System.StringComparison.OrdinalIgnoreCase)) {
                home = s; break;
            }
        }
        return home == null ? null : NearestWell(home.GetCenter2D());
    }

    private Well NearestWell(Vector2 target) {
        Well best = null;
        float bestSq = float.MaxValue;
        foreach (Well w in _wells) {
            if (w == null) continue;
            float dx = w.transform.position.x - target.x;
            float dz = w.transform.position.z - target.y;
            float sq = dx * dx + dz * dz;
            if (sq < bestSq) { bestSq = sq; best = w; }
        }
        return best;
    }

    private Well ChooseRandomWell() {
        // Uniform over non-home wells; avoid immediately repeating the last random well
        // when there's a choice.
        List<Well> pool = new List<Well>();
        foreach (Well w in _wells)
            if (w != null && w != _homeWell) pool.Add(w);
        if (pool.Count == 0) return _homeWell; // degenerate: single well → keep using it

        Well pick = pool[_rng.Next(pool.Count)];
        if (pool.Count > 1 && pick.data.wellId == _lastRandomWellId)
            pick = pool[(pool.IndexOf(pick) + 1) % pool.Count];
        _lastRandomWellId = pick.data.wellId;
        return pick;
    }

    // ─────────────────────────────────────────────
    //  State publishing
    // ─────────────────────────────────────────────

    private void PublishState() {
        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        if (conn == null) return;
        int homeId = _homeWell != null ? _homeWell.data.wellId : -1;
        int targetId = _target != null ? _target.data.wellId : -1;
        bool cued = _target != null && _target.data.cued;
        conn.Publish(_stateTopic, new FloatArrayMessage {
            data = new float[] {
                targetId,
                (float)_currentTrial,
                cued ? 1f : 0f,
                homeId,
                _dispensedThisTrial ? 1f : 0f
            }
        });
    }
}
