using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Runs a pre-game countdown before transitioning to Playing phase.
/// Sends countdown ticks to robots (rising-pitch beep + LED bar dimming)
/// and broadcasts them to web clients (phone overlay + display TTS).
/// </summary>
public class CountdownController : MonoBehaviour
{
    public event Action<int, int> OnCountdownTick;  // (current, total)
    public event Action           OnCountdownDone;

    private Coroutine _current;

    private void Awake()
    {
        ServiceLocator.Countdown = this;
    }

    private void OnDestroy()
    {
        if (ServiceLocator.Countdown == this)
            ServiceLocator.Countdown = null;
    }

    /// <summary>
    /// Begin the pre-game countdown. Called by GameFlowPresenter instead of
    /// GameFlow.StartGame(). kickUnassigned: kick players without a robot first.
    /// </summary>
    public void TriggerStart(bool kickUnassigned = false)
    {
        if (_current != null) StopCoroutine(_current);
        _current = StartCoroutine(CountdownCoroutine(kickUnassigned));
    }

    private IEnumerator CountdownCoroutine(bool kickUnassigned)
    {
        var settings = ServiceLocator.GameSettings;
        int total = settings != null ? settings.CountdownDuration : 5;
        if (total < 1) total = 1;

        var playerServer = ServiceLocator.PlayerServer;
        var robotServer  = ServiceLocator.RobotServer;
        var dir          = ServiceLocator.RobotDirectory;

        // Kick unassigned players before the countdown so they don't see it
        if (kickUnassigned)
            playerServer?.KickUnassignedPlayers();

        // Notify web clients the countdown is starting
        playerServer?.BroadcastCountdownStart(total);

        // Tick from total down to 1
        for (int count = total; count >= 1; count--)
        {
            OnCountdownTick?.Invoke(count, total);

            // Robots: rising-pitch beep + LED bar shows remaining count
            if (robotServer != null && dir != null)
                foreach (var robot in dir.GetAll())
                    robotServer.SendCountdownTick(robot.RobotId, count, total);

            // Web clients: update countdown number on phone and display
            playerServer?.BroadcastCountdownTick(count, total);

            yield return new WaitForSeconds(1f);
        }

        // Game-start fanfare on all robots
        if (robotServer != null && dir != null)
            foreach (var robot in dir.GetAll())
                robotServer.SendGameStartFanfare(robot.RobotId);

        OnCountdownDone?.Invoke();
        ServiceLocator.GameFlow?.StartGame();

        // Redirect any remaining connected players who have no robot (covers non-kick path)
        playerServer?.RedirectUnassignedPlayers();

        _current = null;
    }
}
