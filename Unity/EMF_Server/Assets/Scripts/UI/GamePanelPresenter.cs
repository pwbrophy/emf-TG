using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// GamePanelPresenter - simple realtime playing screen.
/// No turn management - all robots can be controlled freely.
/// </summary>
public class GamePanelPresenter : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button endGameButton;

    private void OnEnable()
    {
        if (endGameButton)
            endGameButton.onClick.AddListener(OnEndGameClicked);
    }

    private void OnDisable()
    {
        if (endGameButton)
            endGameButton.onClick.RemoveListener(OnEndGameClicked);
    }

    private void OnEndGameClicked()
    {
        ServiceLocator.GameFlow?.EndGame();
    }
}
