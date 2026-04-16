using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using UnityEditor;
using UnityEngine;

public class InstallWebSocketSharp
{
    public static void Execute()
    {
        string pluginsDir = Path.Combine(Application.dataPath, "Plugins");
        string dllDest    = Path.Combine(pluginsDir, "websocket-sharp.dll");

        if (File.Exists(dllDest))
        {
            Debug.Log("[InstallWSS] websocket-sharp.dll already present at " + dllDest);
            AssetDatabase.Refresh();
            return;
        }

        Directory.CreateDirectory(pluginsDir);

        string tmpZip = Path.Combine(Path.GetTempPath(), "websocket-sharp.nupkg");

        Debug.Log("[InstallWSS] Downloading websocket-sharp NuGet package...");
        using (var wc = new WebClient())
        {
            wc.Headers.Add("User-Agent", "UnityEditor");
            wc.DownloadFile(
                "https://www.nuget.org/api/v2/package/WebSocketSharp/1.0.3-rc11",
                tmpZip);
        }

        Debug.Log("[InstallWSS] Download complete. Extracting DLL...");

        bool found = false;
        using (var zip = ZipFile.OpenRead(tmpZip))
        {
            foreach (var entry in zip.Entries)
            {
                // Look for lib/net45/websocket-sharp.dll (case-insensitive)
                if (entry.FullName.IndexOf("websocket-sharp.dll", StringComparison.OrdinalIgnoreCase) >= 0
                    && entry.FullName.IndexOf("net45", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    entry.ExtractToFile(dllDest, overwrite: true);
                    found = true;
                    Debug.Log("[InstallWSS] Extracted: " + entry.FullName + " → " + dllDest);
                    break;
                }
            }

            // Fallback: any websocket-sharp.dll in any lib folder
            if (!found)
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Equals("websocket-sharp.dll", StringComparison.OrdinalIgnoreCase)
                        && entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile(dllDest, overwrite: true);
                        found = true;
                        Debug.Log("[InstallWSS] Extracted (fallback): " + entry.FullName + " → " + dllDest);
                        break;
                    }
                }
            }
        }

        File.Delete(tmpZip);

        if (!found)
        {
            Debug.LogError("[InstallWSS] Could not find websocket-sharp.dll inside the NuGet package.");
            return;
        }

        AssetDatabase.Refresh();
        Debug.Log("[InstallWSS] Done. AssetDatabase refreshed.");
    }
}
