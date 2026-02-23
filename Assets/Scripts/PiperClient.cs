using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

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

	[Header("Playback Timing")]
	[Tooltip("Delay in seconds between each TTS clip playback")]
	[Range(0f, 2f)]
	public float delayBetweenClips = 0.25f;
    
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

    [Header("Chat History")]
    [Tooltip("TextMeshPro component to display chat history")]
    public TextMeshProUGUI chatHistoryText;
    
    [Tooltip("Maximum number of lines to keep in chat history. Oldest lines are removed when exceeded.")]
    public int maxChatHistoryLines = 10;

    [Header("Debug")]
    public bool debugLogs = true;

    // Helper struct to pair clips with their sequence numbers
    private struct ClipWithSeq
    {
        public AudioClip clip;
        public int seq;
    }

    // Internal queue and playback
    private readonly Queue<TTSItem> pendingGeneration = new Queue<TTSItem>();
    private readonly Queue<ClipWithSeq> readyToPlayQueue = new Queue<ClipWithSeq>();
    private readonly Dictionary<int, AudioClip> generatedClips = new Dictionary<int, AudioClip>();
    private readonly Dictionary<int, string> originalTextMap = new Dictionary<int, string>(); // Maps seq -> original text before cleaning
    private readonly Dictionary<int, string> emotionMap = new Dictionary<int, string>(); // Maps seq -> emotion for this sentence
    
    private int activeGenerators = 0;
    private bool isPlaying = false;
    private int seqCounter = 0;
    private int nextPlaySeq = 1; // Start at 1

    /// <summary>
    /// Returns true if TTS is currently generating or playing audio
    /// </summary>
    public bool IsBusy => pendingGeneration.Count > 0 || readyToPlayQueue.Count > 0 || isPlaying || activeGenerators > 0;

    void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        if (audioSource == null)
        {

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
            return;
        }

        Enqueue(sentence, emotion);
    }

    [Serializable]
    public class TTSItem
    {
        public int seq;
        public string text;
        public string emotion; // Emotion for this sentence
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

    public void Enqueue(string text, string emotion = "neutral")
    {
        Debug.Log($"[Piper] 📥 Enqueue called with text: '{text?.Substring(0, Math.Min(50, text?.Length ?? 0))}...' emotion: {emotion} at time {Time.time}");
        
        if (string.IsNullOrWhiteSpace(text))
            return;

        text = text.Trim();
        
        // ALWAYS normalize location names to proper capitalization (from STTClient)
        text = AutoVADSTTClient.NormalizeLocationForDetection(text);
        
        if (text.Length < 2)
            return;

        bool hasAnyContent = false;
        bool hasAnySpeakableContent = false;
        
        // Split long text into chunks
        foreach (var chunk in SplitText(text, maxChunkLength))
        {
            if (string.IsNullOrWhiteSpace(chunk) || chunk.Length < 2)
                continue;

            // Create chat version (with pink emotions) and TTS version (no emotions)
			Debug.Log("=======" + chunk);
            string chatVersion = PrepareForChat(chunk);
            string ttsVersion = PrepareForTTS(chunk);
            
            // If we have chat content (even if just actions), mark that we have content
            if (!string.IsNullOrWhiteSpace(chatVersion) && chatVersion.Length >= 2)
            {
                hasAnyContent = true;
            }
            
            // If TTS is empty (only actions/emotions), display in chat but don't speak
            if (string.IsNullOrWhiteSpace(ttsVersion) || ttsVersion.Length < 2)
            {
                // Still display the chat version if it has content
                if (!string.IsNullOrWhiteSpace(chatVersion) && chatVersion.Length >= 2)
                {
                    AppendToChatHistory(chatVersion);
                }
                continue;
            }

            hasAnySpeakableContent = true;
            int currentSeq = ++seqCounter;
            
            // Store chat version for chat history display
            originalTextMap[currentSeq] = chatVersion;
            emotionMap[currentSeq] = emotion; // Store emotion for this sentence

            var item = new TTSItem
            {
                seq = currentSeq,
                text = ttsVersion, // Use TTS version without emotions
                emotion = emotion
            };

            pendingGeneration.Enqueue(item);
        }

        // If we had content but nothing speakable (only actions), notify completion immediately
        if (hasAnyContent && !hasAnySpeakableContent)
        {
            Debug.Log("[Piper] 💬 Only non-speakable content (actions/emotions) - completing immediately");
            if (ollama != null)
            {
                ollama.OnTTSComplete();
            }
            return;
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
                return;
            }

            // Additional validation - check for silent audio
            bool isValidAudio = IsValidAudioData(wavBytes);
            if (!isValidAudio)
            {

                return;
            }

            AudioClip clip = WavUtility.ToAudioClip(wavBytes, $"piper_{item.seq}");

            if (clip == null || clip.length < 0.01f)
            {

                return;
            }

            UnityMainThread(() =>
            {
                generatedClips[item.seq] = clip;
                
                FlushReadyClips();
                
                if (!isPlaying && readyToPlayQueue.Count > 0)
                {
                    StartCoroutine(PlayQueueCoroutine());
                }
            });
        }
        catch (Exception)
        {
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
            readyToPlayQueue.Enqueue(new ClipWithSeq { clip = clip, seq = nextPlaySeq });
            
            nextPlaySeq++;
        }
    }

    private System.Collections.IEnumerator PlayQueueCoroutine()
    {            
        isPlaying = true;
        Debug.Log($"[Piper] ▶️ PLAYBACK STARTED - IsBusy={IsBusy} at time {Time.time}");
        
        while (readyToPlayQueue.Count > 0)
        {
            ClipWithSeq clipData = readyToPlayQueue.Dequeue();
            if (clipData.clip == null) 
            {
                continue;
            }

            if (audioSource == null)
            {
                break;
            }

            // Add text to chat history before playing
            if (originalTextMap.TryGetValue(clipData.seq, out string originalText))
            {
                AppendToChatHistory(originalText);
                originalTextMap.Remove(clipData.seq); // Clean up to prevent memory leak
            }
            
            // Set emotion for this sentence
            if (emotionMap.TryGetValue(clipData.seq, out string sentenceEmotion))
            {
                VTuberAnimationController.currentEmotion = sentenceEmotion;
                Debug.Log($"[Piper] 🎭 Set emotion to: {sentenceEmotion} for seq {clipData.seq} at time {Time.time}");
                emotionMap.Remove(clipData.seq); // Clean up
            }
            else
            {
                VTuberAnimationController.currentEmotion = "neutral";
            }

            // Ensure AudioSource is in good state
            if (audioSource.isPlaying)
            {
                audioSource.Stop();
                yield return new WaitForEndOfFrame(); // Give it a frame to stop
            }

            audioSource.clip = clipData.clip;
            
            audioSource.Play();

            // Enhanced monitoring during playback
            float startTime = Time.time;
            float timeout = clipData.clip.length + 5f; // Increased timeout
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
                        audioSource.Stop();
                        audioSource.Play();
                    }
                    
                    lastCheckTime = currentTime;
                }
                
                // Check for interruption
                if (audioSource.clip != clipData.clip)
                {
                    playbackInterrupted = true;
                    break;
                }
                
                yield return new WaitForSeconds(0.1f);
            }
            
            if (playbackInterrupted)
            {
                break;
            }
            
            if (Time.time - startTime >= timeout)
            {
                audioSource.Stop(); // Force stop on timeout
            }
            
            // Reset emotion to neutral after sentence completes
            VTuberAnimationController.currentEmotion = "neutral";
            Debug.Log($"[Piper] 😐 Reset emotion to neutral after sentence playback at time {Time.time}");

			if (delayBetweenClips > 0f)
			{
				yield return new WaitForSeconds(delayBetweenClips);
			}
            
        }

        isPlaying = false;
        
        // Clean up any remaining clips in ready queue (shouldn't happen but safety check)
        if (readyToPlayQueue.Count > 0)
        {
            readyToPlayQueue.Clear();
        }
        
        // Reset when everything is done
        if (pendingGeneration.Count == 0 && generatedClips.Count == 0)
        {
            seqCounter = 0;
            nextPlaySeq = 1;
            
            // Notify OllamaClient that TTS is completely finished
            if (ollama != null)
            {
                ollama.OnTTSComplete();
            }
        }
        else if (pendingGeneration.Count > 0)
        {
            TryStartGenerators();
        }
    }

    private void AppendToChatHistory(string text, string speakerPrefix = "<color=#00ccff>Kihbbi:</color>")
    {
        if (chatHistoryText == null || string.IsNullOrWhiteSpace(text))
            return;

        // Wrap location names with color
        text = WrapLocationNamesWithColor(text);

        // Add speaker prefix
        string messageWithPrefix = speakerPrefix + " " + text;

        // Append new text with newline
        string currentText = chatHistoryText.text;
        if (string.IsNullOrEmpty(currentText))
        {
            chatHistoryText.text = messageWithPrefix;
        }
        else
        {
            chatHistoryText.text = currentText + "\n" + messageWithPrefix;
        }

        // Trim to max lines if needed
        string[] lines = chatHistoryText.text.Split('\n');
        if (lines.Length > maxChatHistoryLines)
        {
            // Remove oldest lines (from the beginning)
            int linesToRemove = lines.Length - maxChatHistoryLines;
            string[] remainingLines = new string[maxChatHistoryLines];
            Array.Copy(lines, linesToRemove, remainingLines, 0, maxChatHistoryLines);
            chatHistoryText.text = string.Join("\n", remainingLines);
        }
    }

    /// <summary>
    /// Public method to append user messages to chat history
    /// </summary>
    public void AppendUserMessage(string text)
    {
        AppendToChatHistory(text, "<color=#00ccff>Tashiro:</color>");
    }
    
    /// <summary>
    /// Public method to append system messages (like location changes) to chat history.
    /// System messages are displayed exactly as provided without any prefix or modification.
    /// </summary>
    public void AppendSystemMessage(string text)
    {
        Debug.Log($"[PiperClient] AppendSystemMessage called with: {text}");
        
        if (chatHistoryText == null)
        {
            Debug.LogError("[PiperClient] AppendSystemMessage: chatHistoryText is NULL!");
            return;
        }
        
        if (string.IsNullOrWhiteSpace(text))
        {
            Debug.LogWarning("[PiperClient] AppendSystemMessage: text is null or whitespace!");
            return;
        }

        Debug.Log("[PiperClient] AppendSystemMessage: Adding to chat UI");
        
        // Don't wrap location names - system messages already have their own color formatting
        
        // Append system message directly without any prefix or modification
        string currentText = chatHistoryText.text;
        if (string.IsNullOrEmpty(currentText))
        {
            chatHistoryText.text = text;
        }
        else
        {
            chatHistoryText.text = currentText + "\n" + text;
        }

        // Trim to max lines if needed
        string[] lines = chatHistoryText.text.Split('\n');
        if (lines.Length > maxChatHistoryLines)
        {
            int linesToRemove = lines.Length - maxChatHistoryLines;
            string[] remainingLines = new string[maxChatHistoryLines];
            Array.Copy(lines, linesToRemove, remainingLines, 0, maxChatHistoryLines);
            chatHistoryText.text = string.Join("\n", remainingLines);
        }
    }

    private string PrepareForChat(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        
        string cleaned = text.Trim();
        
        // Remove only DOUBLE quotation marks (keep apostrophes for contractions like "you're")
        cleaned = cleaned.Replace("\"", "");
        cleaned = cleaned.Replace("\u201C", ""); // Left curly double quote
        cleaned = cleaned.Replace("\u201D", ""); // Right curly double quote
        
        // Remove leading orphaned asterisk ONLY if it's not part of a valid emotion pattern (*word*)
        // Valid: *giggles* Hello  (don't touch)
        // Invalid: *I chuckle... (remove the leading *)
        if (cleaned.StartsWith("*") && !Regex.IsMatch(cleaned, @"^\*[^*]+\*"))
        {
            cleaned = cleaned.Substring(1).Trim();
        }
        
        // NOW wrap action/emotion annotations (including asterisks) with pink color for chat display
        cleaned = Regex.Replace(cleaned, @"(\*[^*]+\*)", "<color=#FF69B4>$1</color>");
        
        // Clean up extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        
        // Remove common leftover punctuation that might be alone
        if (cleaned.Length <= 3 && Regex.IsMatch(cleaned, @"^[.,!?;:\s]*$"))
        {
            return "";
        }
        
        return cleaned;
    }
    
    private string PrepareForTTS(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        
        string cleaned = text.Trim();
        
        // Remove only DOUBLE quotation marks (keep apostrophes for contractions)
        cleaned = cleaned.Replace("\"", "");
        cleaned = cleaned.Replace("\u201C", "");
        cleaned = cleaned.Replace("\u201D", "");
        
        // Remove leading orphaned asterisk ONLY if it's not part of a valid emotion pattern
        if (cleaned.StartsWith("*") && !Regex.IsMatch(cleaned, @"^\*[^*]+\*"))
        {
            cleaned = cleaned.Substring(1).Trim();
        }
        
        // Remove action/emotion annotations between asterisks completely for TTS
        cleaned = Regex.Replace(cleaned, @"\*[^*]+\*", "");
        
        // Also remove any remaining single asterisks
        cleaned = cleaned.Replace("*", "");
        
        // Remove leading and trailing quotation marks (straight and curly quotes)
        cleaned = cleaned.Trim();
        cleaned = cleaned.Trim('"', '\u201C', '\u201D', '\'', '\u2018', '\u2019');
        
        // Clean up extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        
        // Remove common leftover punctuation that might be alone
        if (cleaned.Length <= 3 && Regex.IsMatch(cleaned, @"^[.,!?;:\s]*$"))
        {
            return "";
        }
        
        return cleaned;
    }

    /// <summary>
    /// Wraps all known location names in text with color tag for chat display
    /// </summary>
    private string WrapLocationNamesWithColor(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // List of all location names to colorize (ordered by length to match longer names first)
        string[] locationNames = new string[]
        {
            "Limsa Lominsa Upper Decks",
            "Limsa Lominsa Lower Decks",
            "Middle La Noscea",
            "Central Shroud",
            "Limsa Lominsa",
            "Solution Nine",
            "New Gridania",
            "Old Gridania",
            "Gold Saucer",
            "Tuliyollal",
            "La Noscea",
            "Lakeland",
            "Gridania",
            "Yak'Tel",
            "Il Mheg",
            "Eulmore",
            "Ul'dah",
            "Kugane",
            "Shroud",
            "Mist"
        };

        string result = text;
        foreach (string location in locationNames)
        {
            // Use word boundaries to avoid partial matches
            string pattern = $@"\b{Regex.Escape(location)}\b";
            string replacement = $"<color=#51db86>{location}</color>";
            result = Regex.Replace(result, pattern, replacement, RegexOptions.IgnoreCase);
        }

        return result;
    }

    private async Task<byte[]> PostTTSAsync(string text)
    {
        // Text is already cleaned in Enqueue, but double-check for safety
        if (string.IsNullOrWhiteSpace(text))
        {
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


                    var op = req.SendWebRequest();
                    while (!op.isDone)
                        await Task.Yield();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        byte[] data = req.downloadHandler.data;
                        if (data != null && data.Length > 44)
                        {
                            return data;
                        }
                        else
                        {
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
        Enqueue("Hello world, this is Piper TTS test.");
    }
    
    [ContextMenu("Emergency Stop Audio")]
    public void EmergencyStopAudio()
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }
        
        // Clear all queues
        pendingGeneration.Clear();
        readyToPlayQueue.Clear();
        generatedClips.Clear();
        originalTextMap.Clear();
        
        // Reset state
        isPlaying = false;
        activeGenerators = 0;
        seqCounter = 0;
        nextPlaySeq = 1;
    }
    
    [ContextMenu("Debug Queue State")]
    public void DebugQueueState()
    {
    }
}