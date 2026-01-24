using System.Diagnostics;
using System.IO;
using UnityEngine;

public class PiperServerManager : MonoBehaviour
{
    [Header("Startup")]
    public bool autoStartOnLaunch = true;
    public static bool PiperReady = false;

    [Header("Server")]
    public int port = 8011;

    [Header("Paths")]
    public string ttsFolder = "TTS";
    public string serverFileName = "piper_server.py";

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
            return;
        }

        string folder = Path.Combine(Application.streamingAssetsPath, ttsFolder);
        string serverFile = Path.Combine(folder, serverFileName);

        if (!File.Exists(serverFile))
        {
            return;
        }

        // Use system python (should have piper-tts installed)
        string pythonExe = "python";

        proc = new Process();
        proc.StartInfo.FileName = pythonExe;
        proc.StartInfo.Arguments = $"-m uvicorn piper_server:app --host 127.0.0.1 --port {port}";
        proc.StartInfo.WorkingDirectory = folder;

        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                string lowerData = e.Data.ToLower();
                if (lowerData.Contains("application startup complete") || lowerData.Contains("uvicorn running on"))
                {
                    PiperReady = true;
                }
            }
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                string lowerData = e.Data.ToLower();
                if (lowerData.Contains("application startup complete") || lowerData.Contains("uvicorn running on"))
                {
                    PiperReady = true;
                }
            }
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (System.Exception)
        {
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

                PiperReady = false;
            }
        }
        catch { }
    }

    void OnDisable() => StopServer();
    void OnDestroy() => StopServer();
    void OnApplicationQuit() => StopServer();
}