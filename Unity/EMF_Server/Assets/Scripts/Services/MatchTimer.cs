using System;
using UnityEngine;

/// <summary>
/// Counts down during the Playing phase.
/// Started by GameFlow.StartGame(), stopped on phase change.
/// </summary>
public class MatchTimer : MonoBehaviour
{
    public float Remaining { get; private set; }
    public bool Running    { get; private set; }

    public event Action<float> OnTick;     // Fired every frame while running (passes remaining seconds)
    public event Action        OnExpired;  // Fired once when timer reaches zero

    public void StartTimer(float durationSeconds)
    {
        Remaining = durationSeconds;
        Running   = true;
    }

    public void StopTimer()
    {
        Running = false;
    }

    private void Update()
    {
        if (!Running) return;

        Remaining -= Time.deltaTime;

        if (Remaining <= 0f)
        {
            Remaining = 0f;
            Running   = false;
            OnTick?.Invoke(0f);
            OnExpired?.Invoke();
        }
        else
        {
            OnTick?.Invoke(Remaining);
        }
    }
}
