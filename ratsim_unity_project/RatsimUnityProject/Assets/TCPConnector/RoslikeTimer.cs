using System;
using Unity.VisualScripting;
using UnityEngine;

public class TimerEvent
{
    public TimerEvent()
    {
        // TODO later
    }
}

public class RoslikeTimer
{
    // TimerEvent carries no data, so one shared instance serves every tick of every
    // timer. This used to allocate a fresh TimerEvent per tick; with a few hundred
    // registered timers that was hundreds of throwaway objects every simulation step,
    // for an object no callback reads. Safe to share as long as TimerEvent stays
    // stateless — if it ever gains fields, go back to allocating (or pool per timer).
    private static readonly TimerEvent SharedEvent = new TimerEvent();

    public RoslikeTimer(Action<TimerEvent> callback, bool discreteStepMode, float tickDelta)
    {
        this.callback = callback;
        if(discreteStepMode)
        {
            discreteMode = true;
            stepsPerTick = (uint)tickDelta;
        }
        else
        {
            discreteMode = false;
            secondsPerTick = tickDelta;
        }
    }

    public bool discreteMode = true;
    public float secondsPerTick = 0.1f;
    public float secondsPassed = 0.0f;
    public uint stepsPerTick = 1;
    public uint stepsPassed = 0;

    public void HandleSteps(uint elapsedPhysicsSteps, float elapsedSeconds)
    {
        if (discreteMode)
        {
            stepsPassed += elapsedPhysicsSteps;
            while (stepsPassed >= stepsPerTick)
            {
                stepsPassed -= stepsPerTick;
                callback(SharedEvent);
            }
        }
        else
        {
            secondsPassed += elapsedSeconds;
            while (secondsPassed >= secondsPerTick)
            {
                secondsPassed -= secondsPerTick;
                callback(SharedEvent);
            }
        }
    }

    public Action<TimerEvent> callback;
}
