using UnityEngine;

public static class NavToLobby
{
    public static void Execute()
    {
        var flow = ServiceLocator.GameFlow;
        if (flow != null) flow.GoToLobby();
        else Debug.LogError("[NavToLobby] GameFlow is null");
    }
}
