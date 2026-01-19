using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class XTTSClient : MonoBehaviour
{
    [Header("Server")]
    public string ttsUrl = "http://127.0.0.1:8010/tts";
    public string language = "en";

    [Tooltip("Path to speaker WAV for XTTS voice cloning. Ex: speaker.wav or full path.")]
    public string speakerWav = "speaker.wav"; // <- IMPORTANT: default to speaker.wav

    [Tooltip("Optional speaker name/ID (rarely used for XTTS v2 cloning). Leave empty normally.")]
    public string speaker = "";

    [Header("Performance")]
    [Tooltip("If true, the old code tried to send speaker only once. This caused random failures. Keep OFF.")]
    public bool cacheSpeakerEmbedding = false; // <- keep false (safe)

    [Tooltip("Max characters per chunk request to TTS server.")]
    public int maxChunkLength = 200;

    [Tooltip("How many TTS generations can run in parallel (more = faster but uses more resources)")]
    public int maxConcurrentGenerations = 2;

    [Header("Audio Output")]
    public AudioSource audioSource;

    [Header("References")]
    public OllamaClient ollama;

    [Header("Debug")]
    public bool debugLogs = true;

    // Internal queue and playback
    private readonly Queue<TTSItem> pendingGeneration = new Queue<TTSItem>();
    private readonly Queue<AudioClip> readyToPlayQueue = new Queue<AudioClip>();
    private readonly Dictionary<int, AudioClip> generatedClips = new Dictionary<int, AudioClip>();
    
    private int activeGenerators = 0;
    private bool isPlaying = false;
    private int seqCounter = 0;
    private int nextPlaySeq = 1; // Start at 1 since seqCounter starts at 0 and gets incremented

    // Speaker caching flags (kept for compatibility; no longer used to remove speaker_wav)
    private bool speakerCached = false;

    void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {
            Debug.LogWarning("[XTTS] No AudioSource found! Adding one automatically.");
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        // Ensure AudioSource is properly configured for speech playback
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f; // 2D sound
            audioSource.volume = Mathf.Max(audioSource.volume, 0.5f); // Ensure reasonable volume
            
            if (debugLogs)
                Debug.Log($"[XTTS] AudioSource configured: volume={audioSource.volume}, spatialBlend={audioSource.spatialBlend}");
        }

        if (ollama == null)
            ollama = FindAnyObjectByType<OllamaClient>();
    }

    void OnEnable()
    {
        if (ollama != null)
            ollama.OnSentenceReady += HandleSentence;
    }

    void OnDisable()
    {
        if (ollama != null)
            ollama.OnSentenceReady -= HandleSentence;
    }

    void HandleSentence(string sentence, string emotion)
    {
        if (!XTTSServerManager.XTTSReady)
        {
            if (debugLogs)
                Debug.Log($"[XTTS] Server not ready, ignoring sentence: {sentence}");
            return;
        }

        Enqueue(sentence);
    }

    [Serializable]
    public class TTSItem
    {
        public int seq;
        public string text;
    }

    [Serializable]
    public class XTTSRequest
    {
        public string text;
        public string language;
        public string speaker_wav;
        public string speaker;
    }

    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // Remove sentences that are just punctuation
        text = text.Trim();
        if (text.Length < 2)
            return;

        // Split long text into chunks
        foreach (var chunk in SplitText(text, maxChunkLength))
        {
            // Skip empty or very short chunks
            if (string.IsNullOrWhiteSpace(chunk) || chunk.Length < 2)
                continue;

            var item = new TTSItem
            {
                seq = ++seqCounter,
                text = chunk
            };

            pendingGeneration.Enqueue(item);
            
            if (debugLogs)
                Debug.Log($"[XTTS] Enqueued #{item.seq}: {chunk}");
        }

        // Kick generators
        TryStartGenerators();
    }

    private void TryStartGenerators()
    {
        if (!XTTSServerManager.XTTSReady)
            return;

        // Start multiple generators in parallel
        while (activeGenerators < maxConcurrentGenerations && pendingGeneration.Count > 0)
        {
            var item = pendingGeneration.Dequeue();
            activeGenerators++;
            _ = GenerateClipAsync(item);
        }
    }

    private async Task GenerateClipAsync(TTSItem item)
    {
        try
        {
            byte[] wavBytes = await PostTTSAsync(item.text);

            if (debugLogs)
                Debug.Log($"[XTTS] Received {wavBytes?.Length ?? 0} bytes for seq {item.seq}");

            if (wavBytes == null || wavBytes.Length < 44)
            {
                if (debugLogs) Debug.LogWarning($"[XTTS] Empty or invalid WAV for seq {item.seq} (got {wavBytes?.Length ?? 0} bytes)");
                return;
            }

            // Decode WAV -> AudioClip
            // Your project probably already has this WAV decoder (WavUtility).
            // If not, tell me and I’ll include a full decoder too.
            AudioClip clip = WavUtility.ToAudioClip(wavBytes, $"xtts_{item.seq}");

            if (clip == null)
            {
                if (debugLogs) Debug.LogWarning($"[XTTS] WAV decode failed for seq {item.seq}");
                return;
            }
            if (debugLogs)
                Debug.Log($"[XTTS] Created AudioClip for seq {item.seq}: length={clip.length:F2}s, samples={clip.samples}, channels={clip.channels}, freq={clip.frequency}Hz");

            // Check if clip is silent (all samples near zero)
            if (clip.length < 1.0f && debugLogs)
            {
                float[] samples = new float[Mathf.Min(100, clip.samples)]; // Check first 100 samples
                clip.GetData(samples, 0);
                float maxAmplitude = 0f;
                for (int i = 0; i < samples.Length; i++)
                {
                    maxAmplitude = Mathf.Max(maxAmplitude, Mathf.Abs(samples[i]));
                }
                Debug.LogWarning($"[XTTS] Short clip detected! Length={clip.length:F2}s, MaxAmplitude={maxAmplitude:F6}");
            }
            // Store clip and flush in-order clips to play queue
            UnityMainThread(() =>
            {
                generatedClips[item.seq] = clip;
                FlushReadyClips();
                
                if (debugLogs)
                    Debug.Log($"[XTTS] Stored clip #{item.seq}, readyToPlayQueue has {readyToPlayQueue.Count} clips, isPlaying={isPlaying}");
                
                // Start player if not already playing AND we have clips ready
                if (!isPlaying && readyToPlayQueue.Count > 0)
                {
                    if (debugLogs)
                        Debug.Log("[XTTS] Starting playback coroutine");
                    StartCoroutine(PlayQueueCoroutine());
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[XTTS] GenerateClipAsync error: {ex}");
        }
        finally
        {
            UnityMainThread(() =>
            {
                activeGenerators--;
                TryStartGenerators();
            });
        }
    }

    private void FlushReadyClips()
    {
        if (debugLogs)
            Debug.Log($"[XTTS] FlushReadyClips: looking for seq {nextPlaySeq}, available clips: {string.Join(", ", generatedClips.Keys)}");
            
        // Move consecutive clips that are ready into the play queue
        while (generatedClips.TryGetValue(nextPlaySeq, out AudioClip clip))
        {
            generatedClips.Remove(nextPlaySeq);
            readyToPlayQueue.Enqueue(clip);
            
            if (debugLogs)
                Debug.Log($"[XTTS] Clip #{nextPlaySeq} ready for playback");
            
            nextPlaySeq++;
        }
    }

    private System.Collections.IEnumerator PlayQueueCoroutine()
    {
        if (debugLogs)
            Debug.Log($"[XTTS] PlayQueueCoroutine started, queue has {readyToPlayQueue.Count} clips");
            
        isPlaying = true;

        while (readyToPlayQueue.Count > 0)
        {
            AudioClip clip = readyToPlayQueue.Dequeue();
            if (clip == null) 
            {
                if (debugLogs)
                    Debug.LogWarning("[XTTS] Found null clip in queue, skipping");
                continue;
            }

            if (audioSource == null)
            {
                Debug.LogWarning("[XTTS] No AudioSource assigned.");
                break;
            }

            // Check AudioSource configuration
            if (debugLogs)
            {
                Debug.Log($"[XTTS] Playing clip: {clip.name}, length={clip.length:F2}s");
                Debug.Log($"[XTTS] AudioSource volume={audioSource.volume}, mute={audioSource.mute}, enabled={audioSource.enabled}");
                Debug.Log($"[XTTS] AudioSource spatialBlend={audioSource.spatialBlend}, priority={audioSource.priority}");
                
                // Check for AudioListener
                AudioListener listener = FindAnyObjectByType<AudioListener>();
                if (listener == null)
                {
                    Debug.LogError("[XTTS] NO AUDIIOLISTENER FOUND! You need an AudioListener in the scene to hear audio!");
                }
                else
                {
                    Debug.Log($"[XTTS] AudioListener found on {listener.gameObject.name}");
                }
            }

            audioSource.clip = clip;
            audioSource.Play();

            if (debugLogs)
                Debug.Log($"[XTTS] AudioSource.Play() called, isPlaying={audioSource.isPlaying}, time={audioSource.time}");

            // Wait until done playing with additional checks
            float startTime = Time.time;
            float timeout = clip.length + 5f; // Add 5 second timeout
            
            while (audioSource.isPlaying && (Time.time - startTime) < timeout)
            {
                yield return new WaitForSeconds(0.1f);
                
                if (debugLogs && Time.time - startTime > 1f && (Time.time - startTime) % 1f < 0.1f) // Log every second after first second
                {
                    Debug.Log($"[XTTS] Still playing... time={audioSource.time:F1}/{clip.length:F1}s, isPlaying={audioSource.isPlaying}");
                }
            }
            
            if (!audioSource.isPlaying)
            {
                if (debugLogs)
                    Debug.Log($"[XTTS] Clip finished playing normally");
            }
            else
            {
                if (debugLogs)
                    Debug.LogWarning($"[XTTS] Clip playback timed out after {timeout}s");
            }
        }

        isPlaying = false;
        
        if (debugLogs)
            Debug.Log($"[XTTS] PlayQueueCoroutine finished. Pending: {pendingGeneration.Count}, Generated: {generatedClips.Count}");

        // Reset sequence counters when playback is done and queue is empty
        if (pendingGeneration.Count == 0 && generatedClips.Count == 0)
        {
            seqCounter = 0;
            nextPlaySeq = 1; // Reset to 1, not 0
            if (debugLogs)
                Debug.Log("[XTTS] Playback complete, sequence counters reset");
        }
        else if (pendingGeneration.Count > 0)
        {
            // If there are more items pending, try to start generators again
            if (debugLogs)
                Debug.Log("[XTTS] More items pending, restarting generators");
            TryStartGenerators();
        }
    }

    private async Task<byte[]> PostTTSAsync(string text)
    {
        // IMPORTANT FIX:
        // Always send speaker_wav if you want cloning.
        // DO NOT send speaker_wav only once (server does not persist it unless coded to).
        string finalSpeakerWav = string.IsNullOrWhiteSpace(speakerWav) ? null : speakerWav;

        // Optional support for "cacheSpeakerEmbedding" (but we do NOT remove speaker_wav anymore)
        // because removing it causes random failures.
        if (cacheSpeakerEmbedding && !speakerCached)
        {
            speakerCached = true; // marked used once, but we still send it anyway
        }

        XTTSRequest payload = new XTTSRequest
        {
            text = text,
            language = language,
            speaker_wav = finalSpeakerWav,
            speaker = string.IsNullOrWhiteSpace(speaker) ? null : speaker
        };

        string json = JsonUtility.ToJson(payload);

        if (debugLogs)
        {
            Debug.Log($"[XTTS] POST {ttsUrl} | speaker_wav={(payload.speaker_wav ?? "NULL")} | len={text.Length}");
            Debug.Log($"[XTTS] Full text being sent: '{text}'");
            Debug.Log($"[XTTS] JSON payload: {json}");
        }

        using (UnityWebRequest req = new UnityWebRequest(ttsUrl, "POST"))
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(jsonBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[XTTS] Request failed: {req.error}\nResponse: {req.downloadHandler?.text}");
                return null;
            }

            byte[] data = req.downloadHandler.data;
            if (data == null || data.Length == 0)
            {
                Debug.LogWarning("[XTTS] Server returned empty audio.");
                return null;
            }

            return data;
        }
    }

    // --------------------------------------------------------------------
    // Manual testing functions
    // --------------------------------------------------------------------
    
    [ContextMenu("Test TTS with Hello World")]
    public void TestTTS()
    {
        Debug.Log("[XTTS] Manual TTS test started");
        Enqueue("Hello world, this is a test.");
    }
    
    [ContextMenu("Test AudioSource Directly")]
    public void TestAudioSource()
    {
        if (audioSource == null)
        {
            Debug.LogError("[XTTS] No AudioSource to test!");
            return;
        }
        
        Debug.Log($"[XTTS] AudioSource test - enabled={audioSource.enabled}, volume={audioSource.volume}, mute={audioSource.mute}");
        
        // Play a simple tone to test if AudioSource works at all
        AudioClip testClip = AudioClip.Create("test", 8000, 1, 8000, false);
        float[] samples = new float[8000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Mathf.Sin(2.0f * Mathf.PI * 440.0f * i / 8000) * 0.1f; // 440Hz tone at low volume
        }
        testClip.SetData(samples, 0);
        
        audioSource.clip = testClip;
        audioSource.Play();
        Debug.Log($"[XTTS] Test tone playing: {audioSource.isPlaying}");
    }

    // --------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------
    private IEnumerable<string> SplitText(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        text = text.Trim();

        if (text.Length <= maxLen)
        {
            yield return text;
            yield break;
        }

        int start = 0;
        while (start < text.Length)
        {
            int len = Math.Min(maxLen, text.Length - start);
            int end = start + len;

            // try cut at punctuation
            int cut = -1;
            for (int i = end - 1; i > start; i--)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?' || c == ',' || c == ';' || c == ':')
                {
                    cut = i + 1;
                    break;
                }
            }

            if (cut != -1)
                end = cut;

            string chunk = text.Substring(start, end - start).Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                yield return chunk;

            start = end;
        }
    }

    // --------------------------------------------------------------------
    // Unity main thread dispatcher (minimal)
    // --------------------------------------------------------------------
    private static readonly Queue<Action> mainThreadActions = new Queue<Action>();

    void Update()
    {
        while (mainThreadActions.Count > 0)
            mainThreadActions.Dequeue()?.Invoke();
    }

    void UnityMainThread(Action action)
    {
        if (action == null) return;
        mainThreadActions.Enqueue(action);
    }
}
