using System.Diagnostics;
using System.IO;
using UnityEngine;

public class XTTSServerManager : MonoBehaviour
{
    [Header("Startup")]
    public bool autoStartOnLaunch = true;
	public static bool XTTSReady = false;

    [Header("Server")]
    public int port = 8010;

    [Header("Paths (inside StreamingAssets)")]
    public string ttsFolder = "TTS";              // StreamingAssets/TTS
    public string serverFileName = "xtts_server.py";

    private Process proc;

    void Start()
    {
        if (autoStartOnLaunch)
            StartServer();
    }

    public void StartServer()
    {
        if (proc != null && !proc.HasExited)
        {
            UnityEngine.Debug.Log("ℹ️ XTTS server already running.");
            return;
        }

        string folder = Path.Combine(Application.streamingAssetsPath, ttsFolder);
        string serverFile = Path.Combine(folder, serverFileName);

        UnityEngine.Debug.Log($"[XTTS] Looking for server at: {serverFile}");

        if (!File.Exists(serverFile))
        {
            UnityEngine.Debug.LogError("[XTTS] xtts_server.py not found: " + serverFile);
            return;
        }

        // Prefer venv python if shipped inside StreamingAssets/TTS/venv/
        string venvPython = Path.Combine(folder, "venv", "Scripts", "python.exe");
        string pythonExe = File.Exists(venvPython) ? venvPython : "python";

        UnityEngine.Debug.Log($"[XTTS] Using Python: {pythonExe}");
        UnityEngine.Debug.Log($"[XTTS] Working directory: {folder}");

        proc = new Process();
        proc.StartInfo.FileName = pythonExe;
        proc.StartInfo.Arguments = $"-m uvicorn xtts_server:app --host 127.0.0.1 --port {port}";
        proc.StartInfo.WorkingDirectory = folder;

        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                UnityEngine.Debug.Log("[XTTS] " + e.Data);
                string lowerData = e.Data.ToLower();
                if (lowerData.Contains("application startup complete") || lowerData.Contains("uvicorn running on"))
                {
                    XTTSReady = true;
                    UnityEngine.Debug.Log("✅ XTTS server is ready");
                }
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                UnityEngine.Debug.Log("[XTTS ERROR] " + e.Data);
                string lowerData = e.Data.ToLower();
                if (lowerData.Contains("application startup complete") || lowerData.Contains("uvicorn running on"))
                {
                    XTTSReady = true;
                    UnityEngine.Debug.Log("✅ XTTS server is ready");
                }
            }
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            UnityEngine.Debug.Log("✅ XTTS server process started");
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[XTTS] Failed to start server: {ex.Message}");
        }
    }

    public void StopServer()
    {
        try
        {
            if (proc != null && !proc.HasExited)
            {
                proc.Kill();
                proc.WaitForExit(2000);
                proc.Dispose();
                proc = null;

                XTTSReady = false;
                UnityEngine.Debug.Log("🛑 XTTS server stopped");
            }
        }
        catch { }
    }

    void OnDisable() => StopServer();
    void OnDestroy() => StopServer();
    void OnApplicationQuit() => StopServer();
}
