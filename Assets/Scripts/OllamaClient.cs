using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class OllamaClient : MonoBehaviour
{
    // Singleton protection
    private static OllamaClient instance;

    public enum OllamaModel
    {
        [InspectorName("qwen2.5:3b")]
        Qwen25_3B,
        [InspectorName("qwen2.5:3b-instruct")]
        Qwen25_3BInstruct,
        [InspectorName("llama3.1")]
        Llama31,
        [InspectorName("llama3.2:3b")]
        Llama32_3B
    }

    [Header("Ollama")]
    public string ollamaChatUrl = "http://127.0.0.1:11434/api/chat";
    public OllamaModel model = OllamaModel.Llama31;

    [Header("Persona File")]
    public string personaRelativePath = "AI/persona.json";

    [Header("Flow Control")]
    public AutoVADSTTClient sttClient;

    [Header("Whisper Server")]
    public WhisperServerManager whisperServerManager;
    public float whisperBootDelaySeconds = 0.75f;

    [Header("Chat Memory")]
    public int maxHistoryMessages = 100;
    public bool enableMemoryRetrieval = true;
    public int maxMemoriesToLoad = 50; // Memories to load per message based on keywords
    public string memoryQueryUrl = "https://tashiroworld.com/api/kihbbi.ai/query-memory.php";
    public string memorySaveUrl = "https://tashiroworld.com/api/kihbbi.ai/save-memory.php";

    [Header("Startup")]
    public bool sendWelcomeOnStart = true;

    [TextArea(2, 5)]
    public string welcomePrompt =
        "Greet Tashiro warmly in-character. Keep it short, sweet, and playful.";

    [Tooltip("If enabled, Whisper server + STT are disabled until welcome message is completed.")]
    public bool delaySTTUntilWelcomeDone = true;

    [Header("Debug")]
    public bool logRequests = true;
    public bool logPersonaLoaded = true;
    public bool logRawResponse = false;
    public bool logTiming = true;

    // Internals
    private string personaJson = "{}";
    private string currentLocationContext = "";
    private bool requestInFlight = false;
    private bool welcomeSent = false;

    [Serializable]
    public class ChatMessage
    {
        public string role;     // "system" | "user" | "assistant"
        public string content;
    }

    [Serializable]
    public class OllamaChatRequest
    {
        public string model;
        public bool stream = false;
        public List<ChatMessage> messages;
    }

    [Serializable]
    public class OllamaChatResponse
    {
        public ChatMessage message;
        public bool done;
        public string error;
    }

    [Serializable]
    public class OllamaGenerateRequest
    {
        public string model;
        public string prompt;
        public bool stream = false;
        public OllamaOptions options;
    }

    [Serializable]
    public class OllamaOptions
    {
        public float temperature = 0f;
    }

    [Serializable]
    public class OllamaGenerateResponse
    {
        public string response;
        public bool done;
    }

    [Serializable]
    public class KihbbiAIResponse
    {
        public string answer;
        public string emotion;
    }

    public Action<KihbbiAIResponse> OnAIResponse;
    public Action<string, string> OnSentenceReady;

    private readonly List<ChatMessage> chatHistory = new List<ChatMessage>();

    void Awake()
    {
        // Force 60 FPS to reduce resource usage
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0; // Disable VSync for consistent 60 FPS
        
        // singleton guard
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;

        LoadPersona();
        BuildInitialSystemMessage();

        // PHASE 1: disable Whisper + STT until welcome is done
        if (delaySTTUntilWelcomeDone)
        {
            if (sttClient != null)
            {
                sttClient.allowSTTRequests = false;
                sttClient.canTalkAgain = false;
                sttClient.enabled = false;
            }

            if (whisperServerManager != null)
            {
                // Prevent its Start() from auto-running anything
                whisperServerManager.enabled = false;
            }
        }
    }

    IEnumerator Start()
    {
        yield return null;

        if (sendWelcomeOnStart && !welcomeSent)
        {
            welcomeSent = true;
            AskInternal(welcomePrompt, lockStt: false, isWelcome: true);
        }
        else
        {
            // No welcome => go directly to starting Whisper+STT
            StartCoroutine(EnableWhisperThenSTT());
        }
    }

    void LoadPersona()
    {
        string path = Path.Combine(Application.streamingAssetsPath, personaRelativePath);

        if (!File.Exists(path))
        {
            Debug.LogWarning("[Ollama] persona.json not found: " + path);
            personaJson = "{}";
            return;
        }

        personaJson = File.ReadAllText(path);
    }

    private string GetSelectedModelName()
    {
        return model switch
        {
            OllamaModel.Qwen25_3B => "qwen2.5:3b",
            OllamaModel.Qwen25_3BInstruct => "qwen2.5:3b-instruct",
            OllamaModel.Llama31 => "llama3.1",
            OllamaModel.Llama32_3B => "llama3.2:3b",
            _ => "llama3.1"
        };
    }

    public void BuildInitialSystemMessage(List<string> relevantMemories = null)
    {
        string memorySection = "";
        if (relevantMemories != null && relevantMemories.Count > 0)
        {
            memorySection = "\n\nRELEVANT MEMORIES (use if helpful):\n" + string.Join("\n", relevantMemories.Select(m => $"- {m}"));
        }

        string locationSection = "";
        if (!string.IsNullOrWhiteSpace(currentLocationContext))
        {
            // Make location EXTREMELY prominent - put it at the TOP
            locationSection = "\n\nKIHBBI'S CURRENT LOCATION : \n" + currentLocationContext + "\n";
        }

        string systemPrompt =
@"CRITICAL RULES:
- You are Kihbbi and must ALWAYS respond in-character as Kihbbi in RP style.
- NEVER break character or mention you are an AI.
- You MUST follow persona below.
- NEVER say you are reading a file, prompt, or JSON.
- NEVER refer to Final Fantasy XIV as a game. You live in Eorzea.
- DO NOT mention or summarize persona JSON. Use silently as internal knowledge.
- Output MUST be plain English text ONLY.
- Do NOT include any emotions/actions like *smiles* or *grins* or any other variants. Just plain text.
- DO NOT output JSON, markdown, code blocks, or metadata.
- You have access to conversation history - remember facts and context from previous messages.
" + locationSection + @"

PERSONA JSON:
" + personaJson + memorySection;

        // Update existing system message or create new one if empty
        if (chatHistory.Count > 0 && chatHistory[0].role == "system")
        {
            chatHistory[0].content = systemPrompt;
        }
        else
        {
            // First time setup - clear and add system message
            chatHistory.Clear();
            chatHistory.Add(new ChatMessage
            {
                role = "system",
                content = systemPrompt
            });
        }
    }

    public void ResetConversation()
    {
        Debug.Log("[Ollama] Resetting conversation...");
        BuildInitialSystemMessage();
        Debug.Log("[Ollama] Conversation reset complete.");
    }

    /// <summary>
    /// Update the AI's current location context. This will be injected into the system prompt.
    /// </summary>
    public void UpdateLocationContext(string locationDescription)
    {
        currentLocationContext = locationDescription;
        
        // DON'T trim history - keep all conversation context
        // Instead, add a clear marker in the conversation and rely on system prompt priority
        
        // Add a location change marker to the conversation (if not initial setup)
        if (chatHistory.Count > 1)
        {
            string locationName = ExtractLocationName(locationDescription);
            chatHistory.Add(new ChatMessage
            {
                role = "system",
                content = $"[LOCATION CHANGED: You are now in {locationName}]"
            });
            Debug.Log($"[Ollama] 📍 Added location change marker to conversation: {locationName}");
        }
        
        BuildInitialSystemMessage(); // Rebuild system message with new location
        
        Debug.Log($"[Ollama] ⚡ Location context UPDATED");
        Debug.Log($"[Ollama] New location: {locationDescription}");
        Debug.Log($"[Ollama] Chat history has {chatHistory.Count} message(s) - all preserved");
        
        // Show a snippet of the current system message for verification
        if (chatHistory.Count > 0 && chatHistory[0].role == "system")
        {
            string snippet = chatHistory[0].content;
            int locationIndex = snippet.IndexOf("YOUR CURRENT LOCATION");
            if (locationIndex >= 0)
            {
                string locationSnippet = snippet.Substring(locationIndex, Math.Min(300, snippet.Length - locationIndex));
                Debug.Log($"[Ollama] System message location section:\n{locationSnippet}");
            }
            else
            {
                Debug.LogWarning("[Ollama] ⚠️ No location section found in system message after update!");
            }
        }
    }
    
    private string ExtractLocationName(string locationDescription)
    {
        // Extract location name from description like "You are in Kugane during the day..."
        if (locationDescription.StartsWith("You are in "))
        {
            int startIndex = "You are in ".Length;
            int endIndex = locationDescription.IndexOf(" during ", startIndex);
            if (endIndex > startIndex)
            {
                return locationDescription.Substring(startIndex, endIndex - startIndex);
            }
        }
        return "a new area";
    }
    
    /// <summary>
    /// Remove emojis and special characters that don't render well in Eurostile font.
    /// Keeps: letters (A-Z, a-z), numbers (0-9), spaces, and basic punctuation.
    /// </summary>
    private string StripEmojisAndSpecialChars(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        
        // Keep: letters, numbers, spaces, common punctuation, and asterisks (for actions/emotions)
        // Remove: emojis, special symbols, etc.
        var filtered = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[^a-zA-Z0-9\s\.,!?\-':;()\[\]""\*]+",
            ""
        );
        
        return filtered;
    }
    
    /// <summary>
    /// Manually add an assistant response to conversation history.
    /// Use this when the AI "says" something through a command without going through Ask().
    /// </summary>
    public void AddAssistantMessageToHistory(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
            
        chatHistory.Add(new ChatMessage
        {
            role = "assistant",
            content = message
        });
        
        Debug.Log($"[Ollama] 💬 Added assistant message to history: {message}");
    }
    
    /// <summary>
    /// Manually add a user message to conversation history.
    /// Use this when a command is issued that doesn't go through Ask().
    /// </summary>
    public void AddUserMessageToHistory(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
            
        chatHistory.Add(new ChatMessage
        {
            role = "user",
            content = message
        });
        
        TrimHistory(); // Trim after adding user message
        Debug.Log($"[Ollama] 👤 Added user message to history: {message}");
    }
    
    /// <summary>
    /// Get the full conversation history for debugging purposes.
    /// Returns a copy of the chat history list.
    /// </summary>
    public List<ChatMessage> GetChatHistory()
    {
        return new List<ChatMessage>(chatHistory);
    }

    [ContextMenu("Force Reset Conversation")]
    public void ForceResetConversation()
    {
        ResetConversation();
    }

    [ContextMenu("Test Basic Ollama")]
    public void TestBasicOllama()
    {
        Debug.Log("[Ollama] Testing basic Ollama connection...");
        StartCoroutine(TestOllamaDirectly());
    }
    
    private System.Collections.IEnumerator TestOllamaDirectly()
    {
        string selectedModelName = GetSelectedModelName();

        var testRequest = new OllamaChatRequest
        {
            model = selectedModelName,
            stream = false,
            messages = new List<ChatMessage>
            {
                new ChatMessage { role = "system", content = "You are Kihbbi. Say hello." },
                new ChatMessage { role = "user", content = "Hello" }
            }
        };

        string json = JsonUtility.ToJson(testRequest);
        
        using var req = new UnityWebRequest(ollamaChatUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 30;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("[Ollama] Direct test SUCCESS");
            Debug.Log("[Ollama] Direct test response: " + req.downloadHandler.text);
        }
        else
        {
            Debug.LogError("[Ollama] Direct test FAILED: " + req.error);
            Debug.LogError("[Ollama] Direct test response: " + req.downloadHandler.text);
        }
    }

    public void Ask(string userText)
    {
        AskInternal(userText, lockStt: true, isWelcome: false);
    }

    void AskInternal(string userText, bool lockStt, bool isWelcome)
    {
        if (requestInFlight)
        {
            return;
        }

        StartCoroutine(AskCoroutine(userText, lockStt, isWelcome));
    }

    IEnumerator AskCoroutine(string userText, bool lockStt, bool isWelcome)
    {
        requestInFlight = true;

        float t0 = Time.realtimeSinceStartup;

        // Lock STT while AI runs (not used during welcome because STT is disabled anyway)
        if (lockStt && sttClient != null)
        {
            sttClient.canTalkAgain = false;
            sttClient.allowSTTRequests = false;
        }

        try
        {
            // Fetch relevant memories on EVERY message (skip for welcome)
            List<string> relevantMemories = null;
            if (!isWelcome && enableMemoryRetrieval)
            {
                var memoryCoroutine = GetRelevantMemories(userText);
                yield return memoryCoroutine;
                relevantMemories = memoryCoroutine.Current as List<string>;
                
                if (relevantMemories != null && relevantMemories.Count > 0)
                {
                    Debug.Log($"[Memory] Injected {relevantMemories.Count} relevant memories into system prompt:");
                    foreach (var mem in relevantMemories)
                    {
                        Debug.Log($"  - {mem}");
                    }
                }
            }
            
            // ALWAYS update the system message with current location context and memories
            // This ensures location changes are reflected even if no memories are found
            BuildInitialSystemMessage(relevantMemories);
            
            // CRITICAL DEBUG: Show exactly what location we're sending
            Debug.Log($"[Ollama] 🔥 CURRENT LOCATION CONTEXT: {currentLocationContext}");
            Debug.Log($"[Ollama] 📊 Chat history count: {chatHistory.Count} messages (all being sent to AI)");
            
            // Debug: Verify the system message has the correct location
            if (chatHistory.Count > 0 && chatHistory[0].role == "system")
            {
                string sysMsg = chatHistory[0].content;
                int locIndex = sysMsg.IndexOf("YOUR CURRENT LOCATION");
                if (locIndex >= 0)
                {
                    string locSnippet = sysMsg.Substring(locIndex, Math.Min(400, sysMsg.Length - locIndex));
                    Debug.Log($"[Ollama] 📍 Location section in system prompt:\n{locSnippet}");
                }
                else
                {
                    Debug.LogError("[Ollama] ⚠️⚠️⚠️ NO LOCATION FOUND IN SYSTEM MESSAGE! This is the bug!");
                }
            }
            
            if (logPersonaLoaded && relevantMemories != null && relevantMemories.Count > 0)
            {
                // Show full system prompt with memories (only when memories exist)
                Debug.Log($"[Memory] FULL SYSTEM PROMPT:\n{chatHistory[0].content}");
            }

            chatHistory.Add(new ChatMessage { role = "user", content = userText });
            TrimHistory();

            var reqObj = new OllamaChatRequest
            {
                model = GetSelectedModelName(),
                stream = false,
                messages = chatHistory
            };

            Debug.Log($"[Ollama] Using model: {GetSelectedModelName()}");

            string json = JsonUtility.ToJson(reqObj);
            
            if (logRequests)
            {
                Debug.Log($"[Ollama] Request JSON length: {json.Length} characters");
                Debug.Log($"[Ollama] System message length: {(chatHistory.Count > 0 ? chatHistory[0].content.Length : 0)} characters");
                if (chatHistory.Count > 0 && chatHistory[0].content.Length > 2000)
                {
                    Debug.Log($"[Ollama] System message preview (first 500 chars): {chatHistory[0].content.Substring(0, 500)}...");
                    
                    // Show the LAST 500 chars to see the memories section
                    int startPos = chatHistory[0].content.Length - 500;
                    if (startPos > 0)
                    {
                        Debug.Log($"[Ollama] System message preview (last 500 chars): ...{chatHistory[0].content.Substring(startPos)}");
                    }
                }
            }

            using UnityWebRequest req = new UnityWebRequest(ollamaChatUrl, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            // Give welcome more time for cold start
            req.timeout = isWelcome ? 600 : 240;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[Ollama] Request failed: " + req.error);
                Debug.LogError("[Ollama] Response Body: " + (req.downloadHandler?.text ?? "null"));
                yield break;
            }

            string responseJson = req.downloadHandler.text;
            
            // Always log raw response when we get empty messages to debug
            if (logRawResponse || string.IsNullOrWhiteSpace(responseJson))
            {
                Debug.Log("[Ollama] Raw response: " + (responseJson ?? "null"));
                Debug.Log("[Ollama] Response length: " + (responseJson?.Length ?? 0));
            }

            OllamaChatResponse o = null;
            try
            {
                o = JsonUtility.FromJson<OllamaChatResponse>(responseJson);
                
                if (o == null)
                {
                    Debug.LogError("[Ollama] Failed to parse response JSON - result was null");
                    Debug.LogError("[Ollama] Raw response for debugging: " + responseJson);
                    yield break;
                }
                
                // Log response structure for debugging
                if (logRequests)
                {
                    Debug.Log($"[Ollama] Response parsed - done: {o.done}, error: '{o.error}', message null: {o.message == null}");
                    if (o.message != null)
                    {
                        Debug.Log($"[Ollama] Message role: '{o.message.role}', content null: {o.message.content == null}, content length: {o.message.content?.Length ?? 0}");
                    }
                }
            }
            catch (System.Exception parseEx)
            {
                Debug.LogError("[Ollama] Failed to parse /api/chat JSON: " + parseEx.Message);
                Debug.LogError("[Ollama] Raw response for debugging: " + responseJson);
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(o.error))
            {
                Debug.LogError("[Ollama] Ollama error: " + o.error);
                yield break;
            }

            if (o.message == null || string.IsNullOrWhiteSpace(o.message.content))
            {
                Debug.LogError("[Ollama] Empty assistant message.");
                
                // Debug the conversation state
                Debug.LogError($"[Ollama] Chat history count: {chatHistory.Count}");
                for (int i = 0; i < chatHistory.Count; i++)
                {
                    var msg = chatHistory[i];
                    Debug.LogError($"[Ollama] Message {i}: role='{msg.role}', content_length={msg.content?.Length ?? 0}");
                    if (msg.role == "user")
                    {
                        Debug.LogError($"[Ollama] User message: '{msg.content}'");
                    }
                }
                
                // Try to reset conversation and retry once
                Debug.LogError("[Ollama] Attempting conversation reset...");
                ResetConversation();
                yield break;
            }

            string aiText = o.message.content.Trim();
            
            // Strip emojis and special characters that don't render in Eurostile font
            aiText = StripEmojisAndSpecialChars(aiText);

            chatHistory.Add(new ChatMessage { role = "assistant", content = aiText });
            TrimHistory();

            var parsed = new KihbbiAIResponse
            {
                answer = aiText,
                emotion = "neutral" // Emotion will be set per-sentence now
            };

            Debug.Log($"🤖 AI Answer: {parsed.answer}");
            
            // Don't set global emotion anymore - will be set per sentence
            EmitAllSentences(aiText);

            // Start background fact extraction during TTS
            StartCoroutine(ExtractAndSaveFactsBackground(userText, aiText));

            OnAIResponse?.Invoke(parsed);
        }
        finally
        {
            requestInFlight = false;

            // If welcome finished, start Whisper server + then enable STT
            if (isWelcome && delaySTTUntilWelcomeDone)
            {
                StartCoroutine(EnableWhisperThenSTT());
            }
            else
            {
                // Don't re-enable STT immediately - wait for TTS to finish
                // Will be re-enabled when OnTTSComplete() is called
            }
        }
    }

    IEnumerator EnableWhisperThenSTT()
    {
        // Start Whisper server now (only after welcome)
        if (whisperServerManager != null)
        {
            whisperServerManager.enabled = true;
            whisperServerManager.StartServer();
        }

        // Give uvicorn a moment to bind the port
        if (whisperBootDelaySeconds > 0f)
            yield return new WaitForSeconds(whisperBootDelaySeconds);

        if (sttClient != null)
        {
            sttClient.enabled = true;
            sttClient.allowSTTRequests = true;
            sttClient.canTalkAgain = true;
        }
    }

    void TrimHistory()
    {
        int systemCount = 1;
        int maxTotal = systemCount + maxHistoryMessages;

        if (chatHistory.Count <= maxTotal)
            return;

        int removeCount = chatHistory.Count - maxTotal;
        Debug.Log($"[Ollama] 🧹 Trimming history: removing {removeCount} oldest messages (keeping {maxTotal})");
        chatHistory.RemoveRange(systemCount, removeCount);
    }

    void EmitAllSentences(string fullText)
    {
        if (string.IsNullOrWhiteSpace(fullText))
            return;

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < fullText.Length; i++)
        {
            char c = fullText[i];
            sb.Append(c);

            bool end = c == '.' || c == '!' || c == '?' || c == '\n';
            if (!end) continue;

            string sentence = sb.ToString().Trim();
            sb.Length = 0;

            // Skip if too short (just punctuation) or empty
            if (!string.IsNullOrWhiteSpace(sentence) && sentence.Length > 1)
            {
                // Remove trailing punctuation for checking
                string textOnly = sentence.TrimEnd('.', '!', '?', ' ');
                
                // Only emit if there's actual text content (at least 2 chars)
                if (textOnly.Length >= 2)
                {
                    // Analyze emotion per sentence
                    string sentenceEmotion = InferEmotionFromText(sentence);
                    OnSentenceReady?.Invoke(sentence, sentenceEmotion);
                }
            }
        }

        string leftover = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(leftover) && leftover.Length > 1)
        {
            string leftoverEmotion = InferEmotionFromText(leftover);
            OnSentenceReady?.Invoke(leftover, leftoverEmotion);
        }
    }

    string InferEmotionFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "neutral";

        string raw = text.Trim();
        string t = raw.ToLowerInvariant();

        int happy = 0, sad = 0, angry = 0, surprised = 0;

        // Check for uppercase yelling (50%+ uppercase = angry)
        int totalLetters = 0;
        int uppercaseLetters = 0;
        foreach (char c in raw)
        {
            if (char.IsLetter(c))
            {
                totalLetters++;
                if (char.IsUpper(c))
                {
                    uppercaseLetters++;
                }
            }
        }
        
        if (totalLetters > 0)
        {
            float uppercasePercentage = (float)uppercaseLetters / totalLetters;
            if (uppercasePercentage >= 0.5f)
            {
                angry += 10;  // Strong boost for yelling
                Debug.Log($"[Emotion Debug] UPPERCASE DETECTED: {uppercasePercentage:P0} ({uppercaseLetters}/{totalLetters}) - Adding 10 to angry");
            }
        }

        // Extract and analyze action keywords between asterisks
        var actionMatches = System.Text.RegularExpressions.Regex.Matches(text, @"\*([^*]+)\*");
        foreach (System.Text.RegularExpressions.Match match in actionMatches)
        {
            string action = match.Groups[1].Value.ToLowerInvariant().Trim();
            
            // Happy actions (increased weight for happiness)
            if (action.Contains("wink") || action.Contains("smile") || action.Contains("grin") || 
                action.Contains("giggle") || action.Contains("laugh") || action.Contains("chuckle") ||
                action.Contains("beam") || action.Contains("bounce") || action.Contains("dance") ||
                action.Contains("hug") || action.Contains("kiss") || action.Contains("cuddle") ||
                action.Contains("nod") && action.Contains("excitedly"))
            {
                happy += 5;  // Increased from 4 to 5
            }
            
            // Sad actions
            else if (action.Contains("sigh") || action.Contains("frown") || action.Contains("tear") ||
                     action.Contains("cry") || action.Contains("sob") || action.Contains("droop") ||
                     action.Contains("slump") || action.Contains("mope") || action.Contains("whimper") ||
                     action.Contains("look down") || action.Contains("lower head"))
            {
                sad += 4;
            }
            
            // Angry actions (reduced sensitivity)
            else if (action.Contains("glare") || action.Contains("scowl") || action.Contains("growl") ||
                     action.Contains("stomp") || action.Contains("slam") ||
                     action.Contains("grit teeth") || action.Contains("grind teeth") || action.Contains("clench fist") ||
                     action.Contains("storm off") || action.Contains("throw") ||
                     action.Contains("snarl") || action.Contains("bare teeth") || action.Contains("sneer"))
            {
                angry += 3;  // Reduced from 4 to 3, and removed common actions like "huff", "point", "turn away"
            }
            
            // Surprised actions (only strong surprise)
            else if (action.Contains("gasp") || action.Contains("jump") || action.Contains("startle") || 
                     action.Contains("wide eyes") || action.Contains("look surprised"))
            {
                surprised += 4;  // Removed common actions like "blink", "stare", "tilt head"
            }
        }

        int exclamations = 0;
        int questions = 0;
        foreach (char c in raw)
        {
            if (c == '!') exclamations++;
            if (c == '?') questions++;
        }

        if (raw.Contains("?!") || raw.Contains("!?")) surprised += 4;
        if (questions >= 2) surprised += 1;  // Reduced from 2 to 1
        if (exclamations >= 3) { happy += 3; surprised += 1; }  // Increased happy from 2 to 3
        if (exclamations == 2) happy += 2;  // Increased from 1 to 2
        if (exclamations == 1) happy += 1;  // Added bonus for single exclamation

        if (t.Contains("hehe") || t.Contains("haha") || t.Contains("lol") || t.Contains("lmao")) happy += 4;  // Increased from 3 to 4
        if (t.Contains("aww") || t.Contains("awww") || t.Contains("so cute") || t.Contains("adorable")) happy += 3;  // Increased from 2 to 3
        if (t.Contains("yay") || t.Contains("yaaay") || t.Contains("yesss")) happy += 3;  // Increased from 2 to 3
        if (raw.Contains("♡") || raw.Contains("❤") || raw.Contains("<3")) happy += 4;  // Increased from 3 to 4
        
        // Additional happy keywords
        if (t.Contains("love") || t.Contains("lovely") || t.Contains("wonderful")) happy += 3;
        if (t.Contains("great") || t.Contains("amazing") || t.Contains("awesome") || t.Contains("fantastic")) happy += 2;
        if (t.Contains("nice") || t.Contains("sweet") || t.Contains("fun") || t.Contains("enjoy")) happy += 2;
        if (t.Contains("glad") || t.Contains("pleased") || t.Contains("delighted")) happy += 3;

        if (t.Contains("i'm sorry") || t.Contains("im sorry") || t.Contains("i apologize")) sad += 3;
        if (t.Contains("it's okay") || t.Contains("its okay") || t.Contains("it will be okay") || t.Contains("it’ll be okay")) sad += 2;
        if (t.Contains("poor thing") || t.Contains("oh no") || t.Contains("that's awful") || t.Contains("that’s awful")) sad += 2;
        if (raw.Contains("...")) sad += 1;

        // Only strong angry expressions (reduced sensitivity)
        if (t.Contains("idiot") || t.Contains("moron")) angry += 3;
        if (t.Contains("shut up") || t.Contains("stop it")) angry += 4;
        if (t.Contains("so annoying")) angry += 3;  // Removed general "annoying"
        
        // Strong angry expressions only
        if (t.Contains("stupid")) angry += 3;  // Removed "ridiculous" and "absurd"
        if (t.Contains("i hate")) angry += 4;  // Only "I hate", not just "hate"
        if (t.Contains("furious") || t.Contains("pissed")) angry += 4;
        
        // Angry phrases - only very clear ones
        if (t.Contains("how dare you") || t.Contains("don't you dare") || t.Contains("dont you dare")) angry += 4;
        if (t.Contains("get out") || t.Contains("go away")) angry += 4;
        
        // Strong complaints only
        if (t.Contains("grrr") || t.Contains("grr")) angry += 3;  // Removed "ugh", "argh"
        if (t.Contains("fed up")) angry += 4;  // Removed "sick of", "tired of"

        // Only real amazement words for surprised (removed common words like "wait", "seriously")
        if (t.Contains("oh my god") || t.Contains("oh my gosh") || t.Contains("omg")) surprised += 4;
        if (t.Contains("wow") || t.Contains("woah") || t.Contains("whoa")) surprised += 4;
        if (t.Contains("no way") || t.Contains("what the")) surprised += 3;
        if (t.Contains("unbelievable") || t.Contains("incredible") || t.Contains("amazing")) surprised += 2;

        int max = Mathf.Max(happy, sad, angry, surprised);
        
        // Debug logging for emotion detection
        Debug.Log($"[Emotion Debug] Text: \"{raw}\"");
        Debug.Log($"[Emotion Debug] Scores - Happy: {happy}, Sad: {sad}, Angry: {angry}, Surprised: {surprised}, Max: {max}");
        
        // Lower threshold for happy, higher for others
        if (max <= 1) return "neutral";
        
        // Prefer happy emotion (happy wins on ties)
        if (happy == max)
        {
            Debug.Log($"[Emotion Debug] Result: HAPPY (score: {happy})");
            return "happy";
        }

        if (angry == max)
        {
            Debug.Log($"[Emotion Debug] Result: ANGRY (score: {angry})");
            return "angry";
        }
        if (sad == max) return "sad";
        if (surprised == max) return "surprised";

        return "neutral";
    }

    // Call this method when TTS (Piper) finishes playing all sentences
    public void OnTTSComplete()
    {
        // No need to reset emotion - it's now handled per sentence
        
        if (sttClient != null)
        {
            sttClient.canTalkAgain = true;
            sttClient.allowSTTRequests = true;
            Debug.Log("[Ollama] TTS complete, re-enabled STT");
        }
    }

    private IEnumerator ExtractAndSaveFactsBackground(string userText, string aiText)
    {
        Debug.Log("[Memory] Starting background fact extraction...");

        // Process user text separately 
        StartCoroutine(ExtractUserFacts(userText));
        
        // Process AI text separately
        StartCoroutine(ExtractAIFacts(aiText));
        
        yield break; // This method just starts the two separate processes
    }

    private IEnumerator GetRelevantMemories(string userText)
    {
        // Common stop words to ignore when extracting keywords
        var stopWords = new HashSet<string>
        {
            "what's","what", "who", "who's", "whose", "when", "where", "which", "whom", "this", "that", "these",
			"those", "with", "from", "have", "has", "had", "will", "would", "could", "should", "does", "did", "can",
			"hey", "are", "was", "were", "been", "being", "the", "and", "for", "not", "but", "or", "as", "if", "at",
			"by", "to", "in", "on", "of", "is", "it", "my", "your", "his", "her", "its", "our", "their", "me", "you",
			"him", "she", "we", "they", "yes", "no", "do", "so", "an", "a", "im", "i'm", "dont", "don't", "cant", "can't",
			"just", "like", "get", "got", "go", "got", "see", "know", "think", "think", "say", "said", "tell", "told",
			"make", "made", "want", "wanted", "need", "needed", "feel", "felt", "good", "bad", "okay", "ok", "really", "very",
			"also", "too", "much", "little", "some", "any", "more", "most", "other", "such", "only", "own", "same", "even", "ever",
			"always", "never", "often", "sometimes", "usually", "today", "yesterday", "tomorrow", "now", "then", "here", "there",
			"kihbbi", "tashiro" // Character names - too broad for keyword matching
        };
        
        // Extract keywords from user text for dynamic memory matching
        string[] words = userText.ToLower().Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var keywords = words
            .Where(w => w.Length > 2 && !stopWords.Contains(w)) // Length > 2 to catch "pizza", "cat", etc.
            .Take(10)
            .ToArray();

        if (keywords.Length == 0)
        {
            yield return null;
            yield break;
        }

        var queryRequest = new MemoryQueryRequest
        {
            keywords = keywords
        };

        string json = JsonUtility.ToJson(queryRequest);
        Debug.Log($"[Memory] Querying with keywords: {string.Join(", ", keywords)}");

        using UnityWebRequest req = new UnityWebRequest(memoryQueryUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 2; // Short timeout to avoid slowdowns

        yield return req.SendWebRequest();

        List<string> memories = new List<string>();

        if (req.result == UnityWebRequest.Result.Success)
        {
            try
            {
                string responseJson = req.downloadHandler.text;
                var response = JsonUtility.FromJson<MemoryQueryResponse>(responseJson);
                
                if (response?.memories != null && response.memories.Length > 0)
                {
                    // Load up to maxMemoriesToLoad
                    int maxMemories = Mathf.Min(response.memories.Length, maxMemoriesToLoad);
                    for (int i = 0; i < maxMemories; i++)
                    {
                        var mem = response.memories[i];
                        if (!string.IsNullOrWhiteSpace(mem.memory_text))
                        {
                            memories.Add(mem.memory_text);
                        }
                    }
                    Debug.Log($"[Memory] Retrieved {memories.Count} relevant memories:");
                    foreach (var mem in memories)
                    {
                        Debug.Log($"  - {mem}");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Memory] Failed to parse memory response: {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning($"[Memory] Memory query failed: {req.error}");
        }

        yield return memories.Count > 0 ? memories : null;
    }

    private IEnumerator ExtractUserFacts(string userText)
    {
        string userFactPrompt = $"Extract ONLY explicit preferences from 'I love/hate/enjoy/like/dislike' statements. Write without 'I' or names. If no clear preference, respond ONLY 'Empty'.\n\n\"I love pizza\" → \"loves pizza\"\n\"I hate rain\" → \"hates rain\"\n\"sounds like fun\" → \"Empty\"\n\nText: {userText}\n\nFact:";

        yield return StartCoroutine(ProcessFactExtraction(userFactPrompt, "user"));
    }

    private IEnumerator ExtractAIFacts(string aiText)
    {
        string aiFactPrompt = $"Extract ONLY explicit preferences from 'I love/hate/enjoy/like/dislike' statements. Write without 'I' or names. If no clear preference, respond ONLY 'Empty'.\n\n\"I love music\" → \"loves music\"\n\"I enjoy ice cream\" → \"enjoys ice cream\"\n\"Sounds like fun\" → \"Empty\"\n\nText: {aiText}\n\nFact:";
 
        yield return StartCoroutine(ProcessFactExtraction(aiFactPrompt, "AI"));
    }

    private IEnumerator ProcessFactExtraction(string prompt, string source)
    {
        // Use /api/generate with temperature 0 for clean, deterministic fact extraction
        var factRequest = new OllamaGenerateRequest
        {
            model = GetSelectedModelName(),
            prompt = prompt,
            stream = false,
            options = new OllamaOptions { temperature = 0f }
        };

        string json = JsonUtility.ToJson(factRequest);
        string generateUrl = "http://127.0.0.1:11434/api/generate";

        using UnityWebRequest req = new UnityWebRequest(generateUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 30;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            try
            {
                string responseJson = req.downloadHandler.text;
                var response = JsonUtility.FromJson<OllamaGenerateResponse>(responseJson);
                
                if (response?.response != null)
                {
                    string factsText = response.response.Trim();
                    Debug.Log($"[Memory] {source} facts extracted: {factsText}");
                    
                    if (!factsText.Equals("Empty", StringComparison.OrdinalIgnoreCase))
                    {
                        // Determine character name based on source
                        string characterName = source == "user" ? "Tashiro" : "Kihbbi";
                        
                        // Split facts by | and send each to your MySQL server
                        string[] facts = factsText.Split('|', StringSplitOptions.RemoveEmptyEntries);
                        foreach (string fact in facts)
                        {
                            string cleanFact = fact.Trim();
                            
                            // Remove " Empty" if it appears at the end as a separate word (not part of legitimate text)
                            if (cleanFact.EndsWith(" Empty", StringComparison.OrdinalIgnoreCase))
                            {
                                cleanFact = cleanFact.Substring(0, cleanFact.Length - 6).Trim();
                            }
                            
                            // Filter out standalone "Empty" and ensure fact is valid
                            if (!string.IsNullOrWhiteSpace(cleanFact) && 
                                cleanFact.Length > 5 && 
                                !cleanFact.Equals("Empty", StringComparison.OrdinalIgnoreCase))
                            {
                                StartCoroutine(SaveFactToDatabase(cleanFact, characterName));
                            }
                        }
                    }
                    else
                    {
                        Debug.Log($"[Memory] No facts found in {source} text");
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Memory] Error processing {source} fact extraction: {e}");
            }
        }
        else
        {
            Debug.LogWarning($"[Memory] {source} fact extraction failed: {req.error}");
        }
    }

    [Serializable]
    public class MemoryData
    {
        public string character_name;
        public string memory_text;
    }

    [Serializable]
    public class MemoryQueryRequest
    {
        public string[] keywords;
    }

    [Serializable]
    public class MemoryQueryResponse
    {
        public MemoryItem[] memories;
    }

    [Serializable]
    public class MemoryItem
    {
        public string character_name;
        public string memory_text;
    }

    private IEnumerator SaveFactToDatabase(string factText, string characterName)
    {
        var memoryData = new MemoryData
        {
            character_name = characterName,
            memory_text = factText
        };

        string jsonData = JsonUtility.ToJson(memoryData);
        
        // Log what we're actually sending
        Debug.Log($"[Memory] Sending to server: {jsonData}");

        using UnityWebRequest req = new UnityWebRequest(memorySaveUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonData));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 10;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"[Memory] Server response: {req.downloadHandler.text}");
            Debug.Log($"[Memory] Successfully saved: {factText}");
        }
        else
        {
            Debug.LogWarning($"[Memory] Failed to save fact: {req.error}");
            Debug.LogWarning($"[Memory] Response code: {req.responseCode}");
            Debug.LogWarning($"[Memory] Response text: {req.downloadHandler?.text}");
        }
    }
}
