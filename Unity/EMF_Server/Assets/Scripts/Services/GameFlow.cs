using System;

public enum GamePhase { MainMenu, Lobby, Playing, Ended }

public sealed class GameFlow
{
    public GamePhase Phase    { get; private set; } = GamePhase.MainMenu;
    public bool      IsPaused { get; private set; } = false;

    private readonly LobbyService _lobby;
    private readonly GameService  _game;

    public event Action<GamePhase> OnPhaseChanged;
    public event Action<bool>      OnPausedChanged;

    public GameFlow(LobbyService lobby, GameService game)
    {
        _lobby = lobby;
        _game  = game;
    }

    private void SetPhase(GamePhase p)
    {
        if (Phase == p) return;
        Phase = p;
        OnPhaseChanged?.Invoke(Phase);
    }

    public bool CanGoToLobby()  => Phase == GamePhase.MainMenu;
    public bool CanStartGame()  => Phase == GamePhase.Lobby && _game.CanStart();
    public bool CanEndGame()    => Phase == GamePhase.Playing;

    public void GoToLobby()
    {
        if (!CanGoToLobby()) return;
        SetPhase(GamePhase.Lobby);
    }

    public void StartGame()
    {
        if (!CanStartGame()) return;

        _game.StartGame();

        // Start the countdown timer
        var timer    = ServiceLocator.MatchTimer;
        var settings = ServiceLocator.GameSettings;
        if (timer != null && settings != null)
        {
            timer.OnExpired -= OnTimerExpired;   // guard against double-subscribe
            timer.OnExpired += OnTimerExpired;
            timer.StartTimer(settings.MatchDurationSeconds);
        }

        SetPhase(GamePhase.Playing);
    }

    public bool CanPause()    => Phase == GamePhase.Playing && !IsPaused;
    public bool CanResume()   => Phase == GamePhase.Playing && IsPaused;

    public void PauseGame()
    {
        if (!CanPause()) return;
        IsPaused = true;
        ServiceLocator.MatchTimer?.Pause();
        OnPausedChanged?.Invoke(true);
    }

    public void ResumeGame()
    {
        if (!CanResume()) return;
        IsPaused = false;
        ServiceLocator.MatchTimer?.Resume();
        OnPausedChanged?.Invoke(false);
    }

    /// <summary>Called by the operator End Game button or automatically by game logic.</summary>
    public void EndGame()
    {
        if (!CanEndGame()) return;

        // Clear pause state
        if (IsPaused)
        {
            IsPaused = false;
            OnPausedChanged?.Invoke(false);
        }

        // Stop the timer if still running
        var timer = ServiceLocator.MatchTimer;
        if (timer != null)
        {
            timer.OnExpired -= OnTimerExpired;
            timer.StopTimer();
        }

        SetPhase(GamePhase.Ended);
    }

    public void BackToMenu()
    {
        var timer = ServiceLocator.MatchTimer;
        if (timer != null)
        {
            timer.OnExpired -= OnTimerExpired;
            timer.StopTimer();
        }
        SetPhase(GamePhase.MainMenu);
    }

    // ── private ──────────────────────────────────────────────────────

    private void OnTimerExpired()
    {
        var game = _game;
        var players = ServiceLocator.Players;
        var dir     = ServiceLocator.RobotDirectory;
        game?.TimeExpired(players, dir);
        // TimeExpired calls DeclareWinner which calls EndGame(), so no explicit call here
    }
}
