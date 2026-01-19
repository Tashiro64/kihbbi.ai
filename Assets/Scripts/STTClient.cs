using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class AutoVADSTTClient : MonoBehaviour
{
    [Header("AI")]
    public OllamaClient ollama;

    [Header("STT Server")]
    public string sttUrl = "http://127.0.0.1:8007/stt";

    [Header("Microphone")]
    public int selectedMicIndex = 0;
    public int sampleRate = 16000;

    [Header("VAD (Voice Activity Detection)")]
    [Tooltip("RMS threshold above which we consider the user is speaking.")]
    public float startThreshold = 0.02f;

    [Tooltip("If RMS stays below threshold for this duration, we stop recording + send.")]
    public float stopAfterSilenceSeconds = 2.0f;

    [Tooltip("Audio kept before speech starts so you don't miss first word.")]
    public float preRollSeconds = 0.35f;

    [Tooltip("Hard cap so you don't record forever.")]
    public float maxSpeechSeconds = 20f;

    [Header("Flow Control")]
    public bool canTalkAgain = true;

    [Tooltip("HARD GATE: if false, we will NOT send anything to Whisper, even if VAD triggers.")]
    public bool allowSTTRequests = true;

    [Header("Debug")]
    public bool showDebugLogs = true;
    public bool showOnScreenStatus = true;

    private string micDevice;
    private AudioClip micClip;

    private const int MIC_BUFFER_SECONDS = 60; // mic recording ring buffer length
    private int lastMicPos = 0;

    private bool isSpeaking = false;
    private float silenceTimer = 0f;
    private float speakingTimer = 0f;

    private float lastRms = 0f;

    // Rolling pre-roll buffer
    private Queue<float> preRollQueue = new();
    private int preRollSamplesMax;

    // Current utterance buffer
    private List<float> utterance = new();

    [Serializable]
    public class STTResponse
    {
        public string text;
        public string lang;
        public float time_sec;
        public string model;
        public string device;
    }

    void Awake()
    {
        Debug.Log("[AutoVADSTTClient] Awake instance id = " + GetInstanceID());
    }

    void Start()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone devices found.");
            enabled = false;
            return;
        }

        selectedMicIndex = Mathf.Clamp(selectedMicIndex, 0, Microphone.devices.Length - 1);
        micDevice = Microphone.devices[selectedMicIndex];

        preRollSamplesMax = Mathf.CeilToInt(preRollSeconds * sampleRate);

        StartMic();

        if (showDebugLogs)
        {
            Debug.Log($"[AutoVAD] Mic = {micDevice}");
            Debug.Log("[AutoVAD] Listening...");
        }
    }

    void OnDisable()
    {
        StopMic();
    }

    void StartMic()
    {
        micClip = Microphone.Start(micDevice, true, MIC_BUFFER_SECONDS, sampleRate);
        lastMicPos = 0;
    }

    void StopMic()
    {
        if (!string.IsNullOrEmpty(micDevice))
            Microphone.End(micDevice);
    }

    void Update()
    {
        if (micClip == null) return;

        int micPos = Microphone.GetPosition(micDevice);
        if (micPos < 0 || micPos == lastMicPos) return;

        // Handle wrap-around (ring buffer)
        int samplesToRead = micPos - lastMicPos;
        if (samplesToRead < 0)
            samplesToRead += micClip.samples;

        if (samplesToRead <= 0) return;

        float[] chunk = new float[samplesToRead];
        micClip.GetData(chunk, lastMicPos);

        lastMicPos = micPos;

        // Convert this chunk to actual time length (THIS is key)
        float chunkSeconds = (float)samplesToRead / sampleRate;

        // Compute RMS volume of this chunk
        float rms = ComputeRMS(chunk);
        lastRms = rms;

        // Always feed pre-roll buffer
        PushPreRoll(chunk);

        // Speech state machine
        if (!isSpeaking)
        {
            // ✅ HARD gate: don't even start a "speech session" if STT or XTTS not allowed
            if (!allowSTTRequests || !WhisperServerManager.STTReady || !PiperServerManager.PiperReady)
                return;

            // Start speaking when we cross threshold
            if (canTalkAgain && rms >= startThreshold)
            {
                isSpeaking = true;
                silenceTimer = 0f;
                speakingTimer = 0f;

                utterance.Clear();

                // Pre-roll so we don't miss first word
                utterance.AddRange(preRollQueue);

                // Add current chunk
                utterance.AddRange(chunk);

                if (showDebugLogs)
                    Debug.Log($"[AutoVAD] 🎤 Speech START (rms={rms:0.0000})");
            }
        }
        else
        {
            // IMPORTANT: speakingTimer must use AUDIO TIME, not deltaTime
            speakingTimer += chunkSeconds;

            // Always record while speaking
            utterance.AddRange(chunk);

            // Silence logic: use a lower threshold so muting / quiet counts as silence
            float silenceThreshold = startThreshold * 0.5f;

            if (rms < silenceThreshold)
            {
                // IMPORTANT: silenceTimer must use AUDIO TIME
                silenceTimer += chunkSeconds;

                if (silenceTimer >= stopAfterSilenceSeconds)
                {
                    if (showDebugLogs)
                        Debug.Log($"[AutoVAD] 🛑 Speech STOP (silence {silenceTimer:0.00}s)");

                    isSpeaking = false;
                    silenceTimer = 0f;

                    FinalizeAndSendUtterance();
                }
            }
            else
            {
                silenceTimer = 0f;
            }

            // Safety max cap (also based on AUDIO TIME)
            if (speakingTimer >= maxSpeechSeconds)
            {
                if (showDebugLogs)
                    Debug.Log("[AutoVAD] 🛑 Speech STOP (maxSpeechSeconds reached)");

                isSpeaking = false;
                silenceTimer = 0f;

                FinalizeAndSendUtterance();
            }
        }
    }

    void FinalizeAndSendUtterance()
    {
        // reset speaking timer now
        speakingTimer = 0f;

        // ✅ HARD gate: never send if blocked
        if (!allowSTTRequests || !WhisperServerManager.STTReady || !PiperServerManager.PiperReady)
        {
            if (showDebugLogs)
                Debug.Log("[AutoVAD] Utterance captured but STT/XTTS is blocked. Dropping it.");
            utterance.Clear();
            return;
        }

        if (!canTalkAgain)
        {
            if (showDebugLogs)
                Debug.Log("[AutoVAD] Utterance captured but canTalkAgain=false. Dropping it.");
            utterance.Clear();
            return;
        }

        if (utterance.Count <= sampleRate / 4)
        {
            if (showDebugLogs) Debug.Log("[AutoVAD] Utterance too short, ignoring.");
            utterance.Clear();
            return;
        }

        // Create AudioClip from float buffer
        var clip = AudioClip.Create(
            "utterance",
            utterance.Count,
            1,
            sampleRate,
            false
        );

        clip.SetData(utterance.ToArray(), 0);

        // Convert to WAV bytes and send
        byte[] wavBytes = WavUtility.FromAudioClip(clip);

        StartCoroutine(SendWavToSTT(wavBytes));

        utterance.Clear();
    }

    IEnumerator SendWavToSTT(byte[] wavBytes)
    {
        // ✅ HARD gate (most important spot)
        if (!allowSTTRequests)
        {
            if (showDebugLogs)
                Debug.Log("[AutoVAD] STT request BLOCKED at send stage (allowSTTRequests=false).");
            yield break;
        }

        if (!canTalkAgain)
        {
            if (showDebugLogs)
                Debug.Log("[AutoVAD] STT request BLOCKED at send stage (canTalkAgain=false).");
            yield break;
        }

        WWWForm form = new WWWForm();
        form.AddBinaryData("audio", wavBytes, "audio.wav", "audio/wav");

        using UnityWebRequest req = UnityWebRequest.Post(sttUrl, form);
        req.timeout = 120;

        if (showDebugLogs) Debug.Log("[AutoVAD] Sending audio to STT...");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[AutoVAD] STT request failed: " + req.error);
            Debug.LogError("[AutoVAD] Server says: " + req.downloadHandler.text);
            yield break;
        }

        string json = req.downloadHandler.text;
        if (showDebugLogs) Debug.Log("[AutoVAD] Raw STT JSON: " + json);

        STTResponse res = JsonUtility.FromJson<STTResponse>(json);

        Debug.Log($"✅ STT TEXT: {res.text}");
        Debug.Log($"🌐 LANG: {res.lang}");

        if (ollama != null && !string.IsNullOrWhiteSpace(res.text))
        {
            canTalkAgain = false; // 🔒 lock
            allowSTTRequests = false; // 🔒 HARD LOCK until Ollama finishes (Ollama must re-enable!)

            ollama.Ask(res.text);
        }
    }

    float ComputeRMS(float[] samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            sum += s * s;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    void PushPreRoll(float[] chunk)
    {
        for (int i = 0; i < chunk.Length; i++)
        {
            preRollQueue.Enqueue(chunk[i]);
            while (preRollQueue.Count > preRollSamplesMax)
                preRollQueue.Dequeue();
        }
    }

    void OnGUI()
    {
        if (!showOnScreenStatus || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(10, 10, 520, 320), GUI.skin.box);
        GUILayout.Label("AutoVAD STT");
        GUILayout.Label("Mic: " + micDevice);
        GUILayout.Label("Status: " + (isSpeaking ? "🎤 speaking" : "listening..."));
        GUILayout.Label($"RMS: {lastRms:0.0000}");
        GUILayout.Label($"canTalkAgain: {(canTalkAgain ? "✅ TRUE" : "⛔ FALSE (waiting AI)")}");
        GUILayout.Label($"allowSTTRequests: {(allowSTTRequests ? "✅ TRUE" : "⛔ FALSE (HARD BLOCK)")}");
        GUILayout.Label($"Whisper Ready: {(WhisperServerManager.STTReady ? "✅ TRUE" : "⛔ FALSE")}");
        GUILayout.Label($"Piper Ready: {(PiperServerManager.PiperReady ? "✅ TRUE" : "⛔ FALSE")}");
        GUILayout.Label($"Threshold: {startThreshold:0.0000} | SilenceStop: {stopAfterSilenceSeconds:0.0}s | PreRoll: {preRollSeconds:0.00}s");
        GUILayout.EndArea();
    }
}
