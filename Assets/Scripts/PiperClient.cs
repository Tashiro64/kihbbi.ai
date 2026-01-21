using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/*
SPEAKER I LOVE
- 54 | 1.5 | 1.04 (original)
- 29 | 1.53 | 1.08
- 107 | 1.7 | 1.14
*/

public class PiperClient : MonoBehaviour
{
    [Header("Server")]
    public string ttsUrl = "http://127.0.0.1:8011/tts";
    
    [Header("Voice Settings")]
    [Tooltip("Speaker ID for libritts_r medium model (0-299). Different voices: Female clear: 5,15,25,45,67,89 | Male clear: 10,30,50,70,90,110")]
    public int speakerId = 67; // Default to a nice female voice
    
    [Tooltip("Speech rate: 1.0=normal speed, >1.0=slower, <1.0=faster (0.5-2.0 recommended)")]
    [Range(0.5f, 2.0f)]
    public float speechRate = 1.0f;

    [Header("Performance")]
    [Tooltip("Max characters per chunk request to TTS server.")]
    public int maxChunkLength = 800; // Piper can handle longer text much better than XTTS

    [Tooltip("How many TTS generations can run in parallel")]
    public int maxConcurrentGenerations = 3; // Piper is faster, can handle more

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
    private int nextPlaySeq = 1; // Start at 1

    void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {
            Debug.LogWarning("[Piper] No AudioSource found! Adding one automatically.");
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        // Ensure AudioSource is properly configured and protected
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f; // 2D sound
            audioSource.volume = Mathf.Max(audioSource.volume, 0.7f);
            audioSource.priority = 64; // Higher priority to avoid being interrupted
            audioSource.dopplerLevel = 0f; // Disable doppler for TTS
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 500f; // Ensure it can be heard
            
            // Ensure the AudioSource GameObject doesn't get destroyed
            if (audioSource.gameObject != this.gameObject)
            {
                Debug.LogWarning("[Piper] AudioSource is on different GameObject, this might cause issues");
            }
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
        if (!PiperServerManager.PiperReady)
        {
            if (debugLogs)
                Debug.Log($"[Piper] Server not ready, ignoring sentence: {sentence}");
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
    public class PiperRequest
    {
        public string text;
        public string language; // For API compatibility
        public string speaker_wav; // For API compatibility  
        public string speaker; // For API compatibility
        public int speaker_id; // Piper speaker ID (0-299)
        public float length_scale; // Speech rate (1.0=normal, >1.0=slower, <1.0=faster)
    }

    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();
        if (text.Length < 2)
            return;

        // Split long text into chunks
        foreach (var chunk in SplitText(text, maxChunkLength))
        {
            if (string.IsNullOrWhiteSpace(chunk) || chunk.Length < 2)
                continue;

            // Clean the chunk early to filter out action-only text
            string cleanedChunk = CleanTextForTTS(chunk);
            
            if (string.IsNullOrWhiteSpace(cleanedChunk) || cleanedChunk.Length < 2)
            {
                if (debugLogs)
                    Debug.Log($"[Piper] Skipping chunk that became empty after cleaning: '{chunk}'");
                continue;
            }

            var item = new TTSItem
            {
                seq = ++seqCounter,
                text = cleanedChunk // Store the already cleaned text
            };

            pendingGeneration.Enqueue(item);
        }

        TryStartGenerators();
    }

    private void TryStartGenerators()
    {
        if (!PiperServerManager.PiperReady)
            return;

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

            if (wavBytes == null || wavBytes.Length < 44)
            {
                Debug.LogWarning($"[Piper] Invalid audio data for text '{item.text.Substring(0, Math.Min(item.text.Length, 50))}...': {wavBytes?.Length ?? 0} bytes");
                return;
            }

            // Additional validation - check for silent audio
            bool isValidAudio = IsValidAudioData(wavBytes);
            if (!isValidAudio)
            {
                Debug.LogWarning($"[Piper] Generated silent/invalid audio for text '{item.text.Substring(0, Math.Min(item.text.Length, 50))}...'");
                return;
            }

            AudioClip clip = WavUtility.ToAudioClip(wavBytes, $"piper_{item.seq}");

            if (clip == null || clip.length < 0.01f)
            {
                Debug.LogWarning($"[Piper] Failed to create valid AudioClip for text '{item.text.Substring(0, Math.Min(item.text.Length, 50))}...'");
                return;
            }

            UnityMainThread(() =>
            {
                generatedClips[item.seq] = clip;
                
                if (debugLogs)
                    Debug.Log($"[Piper] Generated clip for seq {item.seq}, total clips: {generatedClips.Count}");
                
                FlushReadyClips();
                
                if (debugLogs)
                    Debug.Log($"[Piper] Ready queue has {readyToPlayQueue.Count} clips, isPlaying: {isPlaying}");
                
                if (!isPlaying && readyToPlayQueue.Count > 0)
                {
                    if (debugLogs)
                        Debug.Log("[Piper] Starting playback queue");
                    StartCoroutine(PlayQueueCoroutine());
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Piper] GenerateClipAsync error: {ex}");
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
        while (generatedClips.TryGetValue(nextPlaySeq, out AudioClip clip))
        {
            generatedClips.Remove(nextPlaySeq);
            readyToPlayQueue.Enqueue(clip);
            
            nextPlaySeq++;
        }
    }

    private System.Collections.IEnumerator PlayQueueCoroutine()
    {            
        isPlaying = true;
        
        if (debugLogs)
            Debug.Log($"[Piper] PlayQueue started with {readyToPlayQueue.Count} clips");

        while (readyToPlayQueue.Count > 0)
        {
            AudioClip clip = readyToPlayQueue.Dequeue();
            if (clip == null) 
            {
                if (debugLogs)
                    Debug.LogWarning("[Piper] Null clip in queue, skipping");
                continue;
            }

            if (audioSource == null)
            {
                Debug.LogWarning("[Piper] No AudioSource assigned.");
                break;
            }

            // Ensure AudioSource is in good state
            if (audioSource.isPlaying)
            {
                if (debugLogs)
                    Debug.LogWarning("[Piper] AudioSource already playing, stopping previous audio");
                audioSource.Stop();
                yield return new WaitForEndOfFrame(); // Give it a frame to stop
            }

            audioSource.clip = clip;
            
            if (debugLogs)
                Debug.Log($"[Piper] Playing clip: {clip.name}, length: {clip.length:F2}s, samples: {clip.samples}");
            
            audioSource.Play();

            // Enhanced monitoring during playback
            float startTime = Time.time;
            float timeout = clip.length + 5f; // Increased timeout
            float lastCheckTime = startTime;
            bool playbackInterrupted = false;
            
            while (audioSource.isPlaying && (Time.time - startTime) < timeout)
            {
                float currentTime = Time.time;
                
                // Check if playback seems stuck (audio source says it's playing but time isn't advancing)
                if (currentTime - lastCheckTime > 2f) // Check every 2 seconds
                {
                    if (audioSource.time == 0 && audioSource.isPlaying)
                    {
                        if (debugLogs)
                            Debug.LogWarning($"[Piper] Audio playback seems stuck at time 0, forcing restart");
                        audioSource.Stop();
                        audioSource.Play();
                    }
                    
                    if (debugLogs)
                        Debug.Log($"[Piper] Playback progress: {audioSource.time:F2}s / {clip.length:F2}s");
                    
                    lastCheckTime = currentTime;
                }
                
                // Check for interruption
                if (audioSource.clip != clip)
                {
                    Debug.LogWarning($"[Piper] Audio clip was changed during playback! Expected: {clip.name}, Current: {audioSource.clip?.name ?? "null"}");
                    playbackInterrupted = true;
                    break;
                }
                
                yield return new WaitForSeconds(0.1f);
            }
            
            if (playbackInterrupted)
            {
                Debug.LogError("[Piper] Playback was interrupted, stopping queue");
                break;
            }
            
            if (Time.time - startTime >= timeout)
            {
                Debug.LogWarning($"[Piper] Audio playback timeout for clip {clip.name} (played for {Time.time - startTime:F2}s, expected {clip.length:F2}s)");
                audioSource.Stop(); // Force stop on timeout
            }
            
            if (debugLogs)
                Debug.Log($"[Piper] Finished playing clip: {clip.name}");
        }

        isPlaying = false;
        
        if (debugLogs)
            Debug.Log($"[Piper] PlayQueue finished. Pending: {pendingGeneration.Count}, Generated: {generatedClips.Count}");
        
        // Clean up any remaining clips in ready queue (shouldn't happen but safety check)
        if (readyToPlayQueue.Count > 0)
        {
            Debug.LogWarning($"[Piper] {readyToPlayQueue.Count} clips still in ready queue after playback finished");
            readyToPlayQueue.Clear();
        }
        
        // Reset when everything is done
        if (pendingGeneration.Count == 0 && generatedClips.Count == 0)
        {
            if (debugLogs)
                Debug.Log("[Piper] Resetting sequence counters");
            seqCounter = 0;
            nextPlaySeq = 1;
        }
        else if (pendingGeneration.Count > 0)
        {
            if (debugLogs)
                Debug.Log("[Piper] More pending generation, restarting generators");
            TryStartGenerators();
        }
    }

    private string CleanTextForTTS(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        
        // Remove action/emotion annotations between asterisks
        string cleaned = Regex.Replace(text, @"\*[^*]*\*", "");
        
        // Also remove any remaining single asterisks
        cleaned = cleaned.Replace("*", "");
        
        // Clean up extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        
        // Remove common leftover punctuation that might be alone
        if (cleaned.Length <= 3 && Regex.IsMatch(cleaned, @"^[.,!?;:\s]*$"))
        {
            return "";
        }
        
        return cleaned;
    }

    private async Task<byte[]> PostTTSAsync(string text)
    {
        // Text is already cleaned in Enqueue, but double-check for safety
        if (string.IsNullOrWhiteSpace(text))
        {
            if (debugLogs)
                Debug.LogWarning($"[Piper] Empty text passed to PostTTSAsync: '{text}'");
            return null;
        }
        
        PiperRequest payload = new PiperRequest
        {
            text = text, // Text is already cleaned
            language = "en", // For compatibility
            speaker_wav = null, // Not used by Piper
            speaker = null, // For compatibility
            speaker_id = speakerId, // Use the selected speaker ID
            length_scale = speechRate // Speech rate control
        };

        string json = JsonUtility.ToJson(payload);
        
        // Retry logic for intermittent failures
        int maxRetries = 2;
        for (int retry = 0; retry <= maxRetries; retry++)
        {
            try
            {
                using (UnityWebRequest req = new UnityWebRequest(ttsUrl, "POST"))
                {
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                    req.uploadHandler = new UploadHandlerRaw(jsonBytes);
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 15; // 15 second timeout

                    if (debugLogs && retry > 0)
                        Debug.Log($"[Piper] Retry {retry} for text: '{text.Substring(0, Math.Min(text.Length, 30))}...'");

                    var op = req.SendWebRequest();
                    while (!op.isDone)
                        await Task.Yield();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        byte[] data = req.downloadHandler.data;
                        if (data != null && data.Length > 44)
                        {
                            if (debugLogs)
                                Debug.Log($"[Piper] TTS Success: {data.Length} bytes for '{text.Substring(0, Math.Min(text.Length, 30))}...'");
                            return data;
                        }
                        else
                        {
                            Debug.LogWarning($"[Piper] Server returned invalid audio data: {data?.Length ?? 0} bytes");
                        }
                    }
                    else
                    {
                        Debug.LogError($"[Piper] Request failed (attempt {retry + 1}): {req.error}\nResponse: {req.downloadHandler?.text}");
                        
                        if (retry < maxRetries)
                        {
                            // Wait before retry
                            await Task.Delay(500);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Piper] Exception in PostTTSAsync (attempt {retry + 1}): {ex}");
                
                if (retry < maxRetries)
                {
                    await Task.Delay(500);
                }
            }
        }

        Debug.LogError($"[Piper] All retry attempts failed for text: '{text.Substring(0, Math.Min(text.Length, 30))}...'");
        return null;
    }

    private bool IsValidAudioData(byte[] wavBytes)
    {
        if (wavBytes == null || wavBytes.Length < 44)
            return false;

        try
        {
            // Check WAV header
            if (wavBytes[0] != 'R' || wavBytes[1] != 'I' || wavBytes[2] != 'F' || wavBytes[3] != 'F')
                return false;

            // Get data size from header
            int dataSize = System.BitConverter.ToInt32(wavBytes, 40);
            int dataStartIndex = 44;
            
            if (dataSize <= 0 || dataStartIndex + dataSize > wavBytes.Length)
                return false;

            // Sample a few audio values to check if they're not all zero
            int sampleCount = Math.Min(100, dataSize / 2); // Check first 100 samples
            bool hasNonZeroSample = false;
            
            for (int i = 0; i < sampleCount; i++)
            {
                int sampleIndex = dataStartIndex + (i * 2);
                if (sampleIndex + 1 < wavBytes.Length)
                {
                    short sample = System.BitConverter.ToInt16(wavBytes, sampleIndex);
                    if (sample != 0)
                    {
                        hasNonZeroSample = true;
                        break;
                    }
                }
            }

            return hasNonZeroSample;
        }
        catch
        {
            return false;
        }
    }

    // Helper methods
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

            // First priority: Look for sentence endings (., !, ?)
            int cut = FindBestCutPoint(text, start, end, new char[] { '.', '!', '?' }, true);
            
            // Second priority: Look for clause separators
            if (cut == -1)
                cut = FindBestCutPoint(text, start, end, new char[] { ';', ':', ',' }, false);
            
            // Third priority: Look for natural word boundaries
            if (cut == -1)
                cut = FindWordBoundary(text, start, end);
            
            // Last resort: Cut at max length but try to avoid splitting words
            if (cut == -1)
            {
                cut = end;
                // If we're in the middle of a word, back up to the last space
                while (cut > start + 20 && cut < text.Length && !char.IsWhiteSpace(text[cut - 1]))
                {
                    cut--;
                }
            }

            string chunk = text.Substring(start, cut - start).Trim();
            if (!string.IsNullOrWhiteSpace(chunk) && chunk.Length > 1)
                yield return chunk;

            start = cut;
            
            // Skip any whitespace at the start of the next chunk
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }
    }
    
    private int FindBestCutPoint(string text, int start, int maxEnd, char[] separators, bool includeSeparator)
    {
        int bestCut = -1;
        int searchStart = Math.Max(start + 20, start); // Reduced from 50 to be less aggressive
        
        // Search backwards from maxEnd to find the best separator
        for (int i = Math.Min(maxEnd - 1, text.Length - 1); i >= searchStart; i--)
        {
            if (Array.IndexOf(separators, text[i]) >= 0)
            {
                // For sentences, include the punctuation
                // For clauses, cut after the punctuation
                bestCut = includeSeparator ? i + 1 : i + 1;
                
                // Make sure we don't leave tiny fragments
                int remainingLength = text.Length - bestCut;
                if (remainingLength > 0 && remainingLength < 10) // Reduced from 20 to 10
                {
                    // If what's left is very short, include it in this chunk
                    continue;
                }
                
                return bestCut;
            }
        }
        
        return bestCut;
    }
    
    private int FindWordBoundary(string text, int start, int maxEnd)
    {
        int searchStart = Math.Max(start + 50, start); // Keep this higher for word boundaries
        
        // Look for word boundaries (spaces) working backwards from maxEnd
        for (int i = Math.Min(maxEnd - 1, text.Length - 1); i >= searchStart; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                // Check if what's left after this cut is reasonable
                int remainingLength = text.Length - (i + 1);
                if (remainingLength > 15) // Reduced from 30 to 15
                {
                    return i + 1;
                }
            }
        }
        
        return -1;
    }

    // Unity main thread dispatcher
    private static readonly Queue<Action> mainThreadActions = new Queue<Action>();

    void Update()
    {
        while (mainThreadActions.Count > 0)
            mainThreadActions.Dequeue()?.Invoke();
            
        // Monitor for stuck audio playback
        if (isPlaying && audioSource != null)
        {
            CheckForStuckAudio();
        }
    }
    
    private float lastAudioCheckTime = 0f;
    private float lastAudioTime = 0f;
    private int stuckAudioCounter = 0;
    
    private void CheckForStuckAudio()
    {
        if (Time.time - lastAudioCheckTime < 1f) // Check every second
            return;
            
        lastAudioCheckTime = Time.time;
        
        if (audioSource.isPlaying)
        {
            float currentAudioTime = audioSource.time;
            
            // If audio time hasn't changed and we're supposedly playing, something is wrong
            if (Mathf.Approximately(currentAudioTime, lastAudioTime) && currentAudioTime > 0)
            {
                stuckAudioCounter++;
                
                if (stuckAudioCounter >= 3) // 3 seconds of stuck audio
                {
                    Debug.LogError($"[Piper] Audio playback stuck at time {currentAudioTime:F2}s, forcing restart");
                    
                    // Try to restart current audio
                    if (audioSource.clip != null)
                    {
                        audioSource.Stop();
                        audioSource.time = currentAudioTime; // Resume from where it was stuck
                        audioSource.Play();
                    }
                    
                    stuckAudioCounter = 0;
                }
            }
            else
            {
                stuckAudioCounter = 0; // Reset counter if audio is progressing
            }
            
            lastAudioTime = currentAudioTime;
        }
        else
        {
            stuckAudioCounter = 0;
        }
    }

    void UnityMainThread(Action action)
    {
        if (action == null) return;
        mainThreadActions.Enqueue(action);
    }

    // Manual test functions
    [ContextMenu("Test Piper TTS")]
    public void TestTTS()
    {
        Debug.Log("[Piper] Manual TTS test started");
        Enqueue("Hello world, this is Piper TTS test.");
    }
    
    [ContextMenu("Emergency Stop Audio")]
    public void EmergencyStopAudio()
    {
        Debug.Log("[Piper] Emergency stop triggered");
        
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
        
        // Clear all queues
        pendingGeneration.Clear();
        readyToPlayQueue.Clear();
        generatedClips.Clear();
        
        // Reset state
        isPlaying = false;
        activeGenerators = 0;
        seqCounter = 0;
        nextPlaySeq = 1;
        
        Debug.Log("[Piper] Emergency stop complete - all audio and queues cleared");
    }
    
    [ContextMenu("Debug Queue State")]
    public void DebugQueueState()
    {
        Debug.Log($"[Piper] Queue State:");
        Debug.Log($"  - Pending Generation: {pendingGeneration.Count}");
        Debug.Log($"  - Ready to Play: {readyToPlayQueue.Count}");
        Debug.Log($"  - Generated Clips: {generatedClips.Count}");
        Debug.Log($"  - Active Generators: {activeGenerators}");
        Debug.Log($"  - Is Playing: {isPlaying}");
        Debug.Log($"  - Seq Counter: {seqCounter}");
        Debug.Log($"  - Next Play Seq: {nextPlaySeq}");
        Debug.Log($"  - AudioSource Playing: {(audioSource != null ? audioSource.isPlaying.ToString() : "null")}");
        if (audioSource != null && audioSource.clip != null)
        {
            Debug.Log($"  - Current Clip: {audioSource.clip.name}, Time: {audioSource.time:F2}s / {audioSource.clip.length:F2}s");
        }
    }
}