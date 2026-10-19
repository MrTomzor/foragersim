using UnityEngine;

/// <summary>
/// Typed data on a well GameObject: stable identity + physical config + the gate
/// state that a schedule controller drives. A Well is a persistent landmark (a
/// <see cref="WorldStructure"/> spawned eagerly by <see cref="WellLoader"/>), not a
/// throwaway pickup — it *emits* reward when its gate opens (armed + agent present
/// + delay elapsed), rather than being consumed on touch.
///
/// In Phase 1 there is no scheduler, so wells default to an always-armed feeder
/// (<c>armed = true, cued = false</c>): the agent gets rewarded by dwelling within
/// <see cref="arrivalRadius"/> for <see cref="dispenseDelaySteps"/> steps, then the
/// well re-arms after <see cref="cooldownSteps"/>. A future WellScheduleController
/// (Phase 3) will drive armed/cued/depleted per trial to implement Home/Random
/// alternation and patch depletion.
/// </summary>
public class WellData : MonoBehaviour {

    [Header("Identity (assigned by WellLoader)")]
    public int wellId = -1;                     // deterministic index in the roster
    public Vector2Int gridCoord = new Vector2Int(-1, -1); // (col,row) if gridded, else (-1,-1)

    [Header("Physical config")]
    [Tooltip("Agent must be within this horizontal distance (world units) to count as present.")]
    public float arrivalRadius = 1.0f;
    [Tooltip("Steps the agent must dwell in range before reward is dispensed (the 5-15 s delay).")]
    public int dispenseDelaySteps = 25;
    [Tooltip("Steps after all dispensed rewards are collected before the well can dispense again.")]
    public int cooldownSteps = 50;
    [Tooltip("If true the well dispenses once then depletes; if false it re-arms after cooldown.")]
    public bool depleteOnDispense = false;

    [Header("Reward dispensing")]
    [Tooltip("Rewards are spawned at a random position in the annulus [min,max] around the well. Both 0 = spawn on the well.")]
    public float rewardSpawnMinDist = 0f;
    public float rewardSpawnMaxDist = 0f;
    [Tooltip("How many reward objects to spawn per dispense.")]
    public int rewardsPerDispense = 1;

    [Header("Gate state (schedule controller writes; Well reads)")]
    public bool armed = true;
    public bool cued = false;
    public bool depleted = false;

    [Header("Runtime telemetry")]
    public int visitCount = 0;
}
