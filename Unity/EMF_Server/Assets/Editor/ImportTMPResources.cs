using UnityEditor;
using UnityEngine;

/// <summary>
/// Imports TMP Essential Resources so TextMeshProUGUI components have a default font.
/// Only needs to run once.
/// </summary>
public static class ImportTMPResources
{
    public static void Execute()
    {
        // Find the ugui package in the package cache
        string[] guids = AssetDatabase.FindAssets("TMP Essential Resources", new[] { "Packages" });

        if (guids.Length > 0)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            Debug.Log("[ImportTMPResources] Found at: " + path);
            AssetDatabase.ImportPackage(path, false);
            Debug.Log("[ImportTMPResources] Import triggered.");
            return;
        }

        // Fallback: try direct path from PackageCache
        string cachePath = "Library/PackageCache/com.unity.ugui@d8a2716f3013/Package Resources/TMP Essential Resources.unitypackage";
        if (System.IO.File.Exists(cachePath) || System.IO.File.Exists(System.IO.Path.GetFullPath(cachePath)))
        {
            AssetDatabase.ImportPackage(cachePath, false);
            Debug.Log("[ImportTMPResources] Imported from cache path.");
        }
        else
        {
            Debug.LogError("[ImportTMPResources] Could not find TMP Essential Resources. Import manually via: Window > TextMeshPro > Import TMP Essential Resources");
        }
    }
}
