using UnityEngine;

/// <summary>
/// Capsule-style NPC that wanders with a deterministic seeded RNG.
///
/// Ticking is driven by <see cref="ChaoticWalkersLoader"/>, which owns a single timer
/// for the whole walker population and decides who gets stepped on any given simulation
/// step. Walkers deliberately do NOT register their own timer: one timer per walker
/// meant hundreds of registrations on the server, and it made every chunk unload pay
/// RoslikeTCPServer.CleanupDestroyedTimersAndSubscribers() over that whole list.
///
/// Because the loader may step a walker less often than every simulation step, Tick()
/// takes the number of steps elapsed since this walker's previous tick and advances the
/// walk/pause state machine by that much. Motion itself is not affected by the tick
/// rate: velocity is handed to the Rigidbody and PhysX integrates it every physics step
/// regardless of when we last touched it, so a walker stepped every Nth step still
/// glides continuously. Only the rate at which it makes *decisions* drops.
///
/// Behaviour per tick:
///   1. If avoidant/aggressive and an agent is within reactionRadius, set velocity
///      radially away from (or toward) that agent, overriding the walk/pause state
///      machine. The loader decides which walkers are close enough to be candidates
///      and passes the relevant agent in, so distant walkers skip the test entirely.
///   2. Otherwise advance the walk/pause cycle. During walk, apply the stored speed
///      along the currently sampled direction; during pause, zero horizontal velocity.
///   3. When a walk leg finishes, sample a new direction for the next leg. If bounded
///      and close to the radius edge, directions are weighted toward the boundary centre.
///
/// The Rigidbody keeps gravity enabled so the capsule stays glued to uneven terrain;
/// rotation around X/Z is frozen so it never falls over. Horizontal velocity is set
/// directly each tick (vertical velocity is preserved so gravity still works).
/// </summary>
public class ChaoticWalker : MonoBehaviour {

    public enum Mode { Default, Avoidant, Aggressive }

    // ── Config (populated by ChaoticWalkersLoader on spawn) ──
    public float speed;
    public Mode mode;
    public float reactionRadius;
    public float reactionVelocity;
    public int walkMinSteps;
    public int walkMaxSteps;
    public int pauseMinSteps;
    public int pauseMaxSteps;
    public bool bounded;
    public Vector2 boundCenter;
    public float boundRadius;
    public float inwardBiasStrength;

    // ── Scheduler bookkeeping (owned by ChaoticWalkersLoader, not by this class) ──
    // NonSerialized: pure runtime state. Without it Unity would bake these into the
    // walker prefab and show them in the Inspector as if they were tunable.
    /// <summary>Simulation step index at which this walker last ticked.</summary>
    [System.NonSerialized] public int lastTickStep;
    /// <summary>Index into the loader's flat walker list, maintained across swap-removes.</summary>
    [System.NonSerialized] public int schedulerIndex = -1;

    /// <summary>
    /// Position on the XZ plane as of this walker's last tick. The loader reads this to
    /// decide who is near an agent without paying a transform interop call per walker;
    /// it can be up to one tick interval stale, which the loader compensates for with a
    /// margin on the reaction radius.
    /// </summary>
    public Vector2 PosXZ { get; private set; }

    // ── Runtime state ──
    private System.Random _rng;
    private Rigidbody _rb;

    private enum Phase { Walking, Paused }
    private Phase _phase;
    private int _phaseStepsLeft;
    private Vector2 _dir; // xz-plane unit vector

    /// <summary>True while the Rigidbody is parked asleep by the loader's distance policy.</summary>
    public bool IsAsleep { get; private set; }

    /// <summary>Re-read the transform into <see cref="PosXZ"/>. One interop read; the
    /// loader calls this only for walkers it is visiting this step.</summary>
    public void RefreshPosition() {
        Vector3 p = transform.position;
        PosXZ = new Vector2(p.x, p.z);
    }

    /// <summary>
    /// Park or un-park the Rigidbody. Far-away walkers are put to sleep so PhysX stops
    /// doing per-step work for them — with a zero-friction material they never come to
    /// rest on their own, so nothing else would ever let them sleep. Velocity is zeroed
    /// before sleeping, otherwise the stored velocity would resume the instant something
    /// wakes the body. Guarded on the current state so a walker that is already in the
    /// right state costs no interop at all.
    /// </summary>
    public void SetSleeping(bool sleep) {
        if (sleep == IsAsleep) return;
        IsAsleep = sleep;
        if (_rb == null) return;
        if (sleep) {
            _rb.linearVelocity = Vector3.zero;
            _rb.Sleep();
        } else {
            _rb.WakeUp();
        }
    }

    /// <summary>Call once right after Instantiate, before the first tick.</summary>
    public void Init(int seed) {
        _rng = new System.Random(seed);
        _rb = GetComponent<Rigidbody>();
        if (_rb != null) {
            _rb.constraints = RigidbodyConstraints.FreezeRotationX |
                              RigidbodyConstraints.FreezeRotationZ;
        }

        RefreshPosition();

        _phaseStepsLeft = 0;
        StartWalkPhase();
    }

    /// <summary>
    /// Advance this walker by <paramref name="elapsedSteps"/> simulation steps (>= 1).
    /// <paramref name="reactionAgentXZ"/> is only meaningful when
    /// <paramref name="hasReactionAgent"/> is true — the loader sets that for walkers
    /// close enough to an agent to possibly react this step.
    /// </summary>
    public void Tick(int elapsedSteps, bool hasReactionAgent, Vector2 reactionAgentXZ) {
        // PosXZ is refreshed by the loader before this call (it needs the position for
        // the sleep-distance test anyway), so we don't pay a second transform read here.

        // 1. Reaction override (avoidant flees, aggressive chases)
        if (hasReactionAgent && mode != Mode.Default) {
            Vector2 d = PosXZ - reactionAgentXZ;
            float dist = d.magnitude;
            if (dist > 1e-4f && dist < reactionRadius) {
                Vector2 unit = d / dist;
                Vector2 reactDir = (mode == Mode.Avoidant) ? unit : -unit;
                ApplyHorizontalVelocity(reactDir * reactionVelocity);
                return;
            }
        }

        // 2. Advance the phase state machine by however many steps we missed. The loop
        //    matters once elapsedSteps > 1: a long gap can span an entire leg, and the
        //    RNG has to be drawn once per boundary crossed so the sequence of leg
        //    durations and directions stays identical to ticking every step. Phase
        //    starts accumulate (+=) rather than assign so the leftover carries over and
        //    legs don't silently stretch to a multiple of the tick interval.
        _phaseStepsLeft -= elapsedSteps;
        while (_phaseStepsLeft <= 0) {
            if (_phase == Phase.Walking) StartPausePhase();
            else StartWalkPhase();
        }

        // 3. Apply velocity for current phase
        if (_phase == Phase.Walking)
            ApplyHorizontalVelocity(_dir * speed);
        else
            ApplyHorizontalVelocity(Vector2.zero);
    }

    private void StartWalkPhase() {
        _phase = Phase.Walking;
        _phaseStepsLeft += RandRange(walkMinSteps, walkMaxSteps);
        _dir = SampleDirection();
    }

    private void StartPausePhase() {
        _phase = Phase.Paused;
        _phaseStepsLeft += RandRange(pauseMinSteps, pauseMaxSteps);
    }

    /// <summary>
    /// Uniform random unit 2D direction, optionally rotated toward the bound centre
    /// with strength that grows as the walker approaches the bound radius.
    /// </summary>
    private Vector2 SampleDirection() {
        float angle = (float)(_rng.NextDouble() * 2.0 * Mathf.PI);
        Vector2 d = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));

        if (!bounded || boundRadius <= 0f) return d;

        Vector2 pos = PosXZ;
        Vector2 toCenter = boundCenter - pos;
        float r = toCenter.magnitude;
        if (r < 1e-4f) return d;
        Vector2 inward = toCenter / r;

        // Weight: 0 near centre, grows to 1 at/past bound. inwardBiasStrength scales it.
        float t = Mathf.Clamp01(r / boundRadius);
        float w = Mathf.Clamp01(t * t * inwardBiasStrength);

        // Hard redirect if the next step would leave the circle.
        if (r >= boundRadius) w = 1f;

        Vector2 mixed = Vector2.Lerp(d, inward, w);
        float m = mixed.magnitude;
        return m > 1e-4f ? mixed / m : inward;
    }

    private void ApplyHorizontalVelocity(Vector2 vxz) {
        if (_rb == null) return;
        Vector3 v = _rb.linearVelocity;

        // Skip the write when the body already carries exactly this horizontal velocity.
        // Nothing but a collision changes x/z here (linear damping is 0 and gravity is
        // vertical), so during a walk leg this is a redundant write on every step but
        // the first. If PhysX did perturb the velocity the compare fails and we
        // re-impose it, so the state entering Physics.Simulate is identical either way.
        if (v.x == vxz.x && v.z == vxz.y) return;

        v.x = vxz.x;
        v.z = vxz.y;
        _rb.linearVelocity = v;
    }

    private int RandRange(int minInclusive, int maxInclusive) {
        if (maxInclusive <= minInclusive) return Mathf.Max(1, minInclusive);
        return _rng.Next(minInclusive, maxInclusive + 1);
    }
}
