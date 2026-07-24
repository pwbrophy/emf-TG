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
    private bool      _running;

    /// <summary>
    /// True from the moment the pre-game countdown starts until StartGame() has been
    /// called. The lobby is closed for this window: PlayerWebSocketServer rejects new
    /// joins and freezes squad/robot/gunner changes, so the assignments that
    /// KickUnassignedPlayers() validated at the top of the countdown are still the
    /// assignments GameService.StartGame() snapshots at the bottom of it.
    /// </summary>
    public bool IsRunning => _running;

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
        _running = true;
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
            // A throwing robot/UI send must never kill the coroutine — that would strand
            // the countdown overlay on screen and the match would never start.
            try { OnCountdownTick?.Invoke(count, total); }
            catch (Exception ex) { Debug.LogException(ex); }

            // Robots: rising-pitch beep + LED bar shows remaining count
            if (robotServer != null && dir != null)
                foreach (var robot in dir.GetAll())
                {
                    try { robotServer.SendCountdownTick(robot.RobotId, count, total); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }

            // Web clients: update countdown number on phone and display
            try { playerServer?.BroadcastCountdownTick(count, total); }
            catch (Exception ex) { Debug.LogException(ex); }

            yield return new WaitForSeconds(1f);
        }

        // Game-start fanfare on all robots
        if (robotServer != null && dir != null)
            foreach (var robot in dir.GetAll())
            {
                try { robotServer.SendGameStartFanfare(robot.RobotId); }
                catch (Exception ex) { Debug.LogException(ex); }
            }

        // Everything below must run even if a step throws — a half-finished start
        // leaves the timer ticking with the robots switched off and no way back.
        try { OnCountdownDone?.Invoke(); }
        catch (Exception ex) { Debug.LogException(ex); }

        try { ServiceLocator.GameFlow?.StartGame(); }
        catch (Exception ex) { Debug.LogException(ex); }

        // Countdown is over — reopen the join/assignment path for the next lobby.
        _running = false;

        // Redirect any remaining connected players who have no robot (covers non-kick path)
        try { playerServer?.RedirectUnassignedPlayers(); }
        catch (Exception ex) { Debug.LogException(ex); }

        _current = null;
    }
}
