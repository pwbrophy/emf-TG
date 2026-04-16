using System;

// Which side are we running as? (Picked from the Main Menu)
public enum AppMode
{
    None,                // No mode chosen yet (at startup / main menu)
    Server,              // We act as the game server (host)
    Client               // We act as a client (phone/controller later)
}

// Simple service that stores the current mode and notifies listeners when it changes.
public class AppModeService
{
    // Current mode value (read-only from outside; change via SetMode)
    public AppMode Mode { get; private set; } = AppMode.None;

    // Event fired whenever Mode changes.
    public event Action<AppMode> OnModeChanged;

    // Set the mode (e.g., from a UI button) and notify listeners if it changed.
    public void SetMode(AppMode newMode)
    {
        if (newMode == Mode) return;  // No change -> do nothing
        Mode = newMode;               // Store the new mode
        OnModeChanged?.Invoke(Mode);  // Tell everyone that mode just changed
    }
}
