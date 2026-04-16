using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Component placed on the PlayerRow prefab. Holds references to all interactive
/// children so PlayersEditorPanel can wire them without doing Find() calls.
/// </summary>
public class PlayerRowUI : MonoBehaviour
{
    public TMP_InputField nameField;
    public TMP_Dropdown   allianceDropdown;
    public TMP_Dropdown   robotDropdown;
    public Button         removeButton;
}
