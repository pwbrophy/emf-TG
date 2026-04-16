using UnityEngine;
using UnityEngine.UI;

public class GameFlowPresenter : MonoBehaviour
{
    [Header("Top-level screens/panels")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject playingPanel;
    [SerializeField] private GameObject endedPanel;

    [Header("Buttons")]
    [SerializeField] private Button toLobbyButton;
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button backToMenuButton;
    [SerializeField] private Button endGameButton;

    private GameFlow _flow;

    void OnEnable()
    {
        _flow = ServiceLocator.GameFlow;
        if (_flow == null)
        {
            Debug.LogError("[GameFlowPresenter] GameFlow is null — AppBootstrap may not have run yet.");
            return;
        }

        if (toLobbyButton)    toLobbyButton.onClick.AddListener(() => _flow?.GoToLobby());
        if (startGameButton)  startGameButton.onClick.AddListener(() => _flow?.StartGame());
        if (backToMenuButton) backToMenuButton.onClick.AddListener(() => _flow?.BackToMenu());
        if (endGameButton)    endGameButton.onClick.AddListener(() => _flow?.EndGame());

        _flow.OnPhaseChanged += HandlePhaseChanged;

        HandlePhaseChanged(_flow.Phase);
    }

    void OnDisable()
    {
        if (_flow != null) _flow.OnPhaseChanged -= HandlePhaseChanged;

        if (toLobbyButton)    toLobbyButton.onClick.RemoveAllListeners();
        if (startGameButton)  startGameButton.onClick.RemoveAllListeners();
        if (backToMenuButton) backToMenuButton.onClick.RemoveAllListeners();
        if (endGameButton)    endGameButton.onClick.RemoveAllListeners();
    }

    void HandlePhaseChanged(GamePhase p)
    {
        if (mainMenuPanel) mainMenuPanel.SetActive(p == GamePhase.MainMenu);
        if (lobbyPanel)    lobbyPanel.SetActive(p == GamePhase.Lobby);
        if (playingPanel)  playingPanel.SetActive(p == GamePhase.Playing);
        if (endedPanel)    endedPanel.SetActive(p == GamePhase.Ended);

        if (startGameButton) startGameButton.interactable = _flow.CanStartGame();
    }
}
