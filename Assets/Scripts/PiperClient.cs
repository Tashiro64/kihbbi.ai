using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

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
    public int maxChunkLength = 300; // Piper can handle longer text better than XTTS

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

        // Ensure AudioSource is properly configured
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f; // 2D sound
            audioSource.volume = Mathf.Max(audioSource.volume, 0.7f);
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

            var item = new TTSItem
            {
                seq = ++seqCounter,
                text = chunk
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
                return;
            }

            AudioClip clip = WavUtility.ToAudioClip(wavBytes, $"piper_{item.seq}");

            if (clip == null)
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

        while (readyToPlayQueue.Count > 0)
        {
            AudioClip clip = readyToPlayQueue.Dequeue();
            if (clip == null) 
            {
                continue;
            }

            if (audioSource == null)
            {
                Debug.LogWarning("[Piper] No AudioSource assigned.");
                break;
            }

            audioSource.clip = clip;
            audioSource.Play();

            // Wait until done playing
            float startTime = Time.time;
            float timeout = clip.length + 3f;
            
            while (audioSource.isPlaying && (Time.time - startTime) < timeout)
                yield return new WaitForSeconds(0.1f);
        }

        isPlaying = false;
        
        // Reset when everything is done
        if (pendingGeneration.Count == 0 && generatedClips.Count == 0)
        {
            seqCounter = 0;
            nextPlaySeq = 1;
        }
        else if (pendingGeneration.Count > 0)
        {
            TryStartGenerators();
        }
    }

    private string CleanTextForTTS(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        
        // Remove action/emotion annotations between asterisks
        string cleaned = Regex.Replace(text, @"\*[^*]*\*", "");
        
        // Clean up extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        
        return cleaned;
    }

    private async Task<byte[]> PostTTSAsync(string text)
    {
        // Clean text to remove action/emotion annotations like *winks*
        string cleanedText = CleanTextForTTS(text);
        
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            if (debugLogs)
                Debug.LogWarning($"[Piper] Text became empty after cleaning: '{text}'");
            return null;
        }
        
        PiperRequest payload = new PiperRequest
        {
            text = cleanedText,
            language = "en", // For compatibility
            speaker_wav = null, // Not used by Piper
            speaker = null, // For compatibility
            speaker_id = speakerId, // Use the selected speaker ID
            length_scale = speechRate // Speech rate control
        };

        string json = JsonUtility.ToJson(payload);

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
                Debug.LogError($"[Piper] Request failed: {req.error}\nResponse: {req.downloadHandler?.text}");
                return null;
            }

            return req.downloadHandler.data;
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

            // Cut at sentence boundaries
            int cut = -1;
            for (int i = end - 1; i > start; i--)
            {
                char c = text[i];
                if (c == '.' || c == '!' || c == '?')
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

    // Unity main thread dispatcher
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

    // Manual test functions
    [ContextMenu("Test Piper TTS")]
    public void TestTTS()
    {
        Debug.Log("[Piper] Manual TTS test started");
        Enqueue("Hello world, this is Piper TTS test.");
    }
}