using System;
using UnityEngine;

public enum SmokeOriginMode
{
    StaticDefaultSize
}

public class SmokeOrigin : MonoBehaviour
{
    public SmokeOriginMode mode = SmokeOriginMode.StaticDefaultSize;

    public static event Action<SmokeOrigin> OnOriginEnabled;
    public static event Action<SmokeOrigin> OnOriginDisabled;

    void OnEnable()  { OnOriginEnabled?.Invoke(this); }
    void OnDisable() { OnOriginDisabled?.Invoke(this); }
}
