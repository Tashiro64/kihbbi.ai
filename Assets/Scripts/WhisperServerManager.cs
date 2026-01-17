using System.Diagnostics;
using System.IO;
using UnityEngine;

public class WhisperServerManager : MonoBehaviour
{
    private Process proc;

    void Start()
    {
        string sttFolder = Path.Combine(Application.streamingAssetsPath, "STT");
        string serverFile = Path.Combine(sttFolder, "whisper_server.py");

        if (!File.Exists(serverFile))
        {
            UnityEngine.Debug.LogError("whisper_server.py not found at: " + serverFile);
            return;
        }

        proc = new Process();
        proc.StartInfo.FileName = "python"; // IMPORTANT: start python directly
        proc.StartInfo.Arguments = "-m uvicorn whisper_server:app --host 127.0.0.1 --port 8007";
        proc.StartInfo.WorkingDirectory = sttFolder;

        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.UseShellExecute = false;

        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;

        proc.OutputDataReceived += (_, e) =>
		{
			if (!string.IsNullOrWhiteSpace(e.Data))
				UnityEngine.Debug.Log("[STT] " + e.Data);
		};

		proc.ErrorDataReceived += (_, e) =>
		{
			if (!string.IsNullOrWhiteSpace(e.Data))
				UnityEngine.Debug.Log("[STT] " + e.Data);
		};

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        UnityEngine.Debug.Log("✅ Whisper server started (python)");
    }

    void OnDisable() => StopServer();
    void OnDestroy() => StopServer();
    void OnApplicationQuit() => StopServer();

    private void StopServer()
    {
        try
        {
            if (proc != null && !proc.HasExited)
            {
                proc.Kill();
                proc.WaitForExit(2000);
                proc.Dispose();
                proc = null;
                UnityEngine.Debug.Log("🛑 Whisper server stopped");
            }
        }
        catch { }
    }
}
