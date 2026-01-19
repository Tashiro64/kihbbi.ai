using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class OllamaClient : MonoBehaviour
{
    // Singleton protection
    private static OllamaClient instance;

    [Header("Ollama")]
    public string ollamaChatUrl = "http://127.0.0.1:11434/api/chat";
    public string model = "llama3.1";

    [Header("Persona & Memory File")]
    public string personaRelativePath = "AI/persona.json";
    public string memoryRelativePath = "AI/memory.json";

    [Header("Flow Control")]
    public AutoVADSTTClient sttClient;

    [Header("Whisper Server")]
    public WhisperServerManager whisperServerManager;
    public float whisperBootDelaySeconds = 0.75f;

    [Header("Chat Memory")]
    public int maxHistoryMessages = 12;

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
    private string memoryJson = "{}";
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
        // singleton guard
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;

        LoadPersona();
        LoadMemory();
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

    void LoadMemory()
    {
        string path = Path.Combine(Application.streamingAssetsPath, memoryRelativePath);

        if (!File.Exists(path))
        {
            Debug.LogWarning("[Ollama] memory.json not found: " + path);
            memoryJson = "{}";
            return;
        }

        memoryJson = File.ReadAllText(path);
    }

    void BuildInitialSystemMessage()
    {
        chatHistory.Clear();

        string systemPrompt =
@"CRITICAL RULES:
- You are Kihbbi and must ALWAYS respond in-character as Kihbbi in RP style.
- NEVER break character or mention you are an AI.
- You MUST follow persona + memory below.
- NEVER say you are reading a file, prompt, or JSON.
- NEVER refer to Final Fantasy XIV as a game. You live in Eorzea.
- DO NOT mention or summarize persona or memory JSON. Use silently as internal knowledge.
- Output MUST be plain English text ONLY.
- DO NOT output JSON, markdown, code blocks, or metadata.

PERSONA JSON:
" + personaJson + @"

MEMORY JSON:
" + memoryJson;

        chatHistory.Add(new ChatMessage
        {
            role = "system",
            content = systemPrompt
        });
    }

    public void ResetConversation()
    {
        BuildInitialSystemMessage();
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
            chatHistory.Add(new ChatMessage { role = "user", content = userText });
            TrimHistory();

            var reqObj = new OllamaChatRequest
            {
                model = model,
                stream = false,
                messages = chatHistory
            };

            string json = JsonUtility.ToJson(reqObj);

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
                Debug.LogError("[Ollama] Body: " + req.downloadHandler.text);
                yield break;
            }

            string responseJson = req.downloadHandler.text;

            OllamaChatResponse o = null;
            try
            {
                o = JsonUtility.FromJson<OllamaChatResponse>(responseJson);
            }
            catch
            {
                Debug.LogError("[Ollama] Failed to parse /api/chat JSON.");
                yield break;
            }

            if (o == null)
            {
                Debug.LogError("[Ollama] Null response object.");
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
                yield break;
            }

            string aiText = o.message.content.Trim();

            chatHistory.Add(new ChatMessage { role = "assistant", content = aiText });
            TrimHistory();

            var parsed = new KihbbiAIResponse
            {
                answer = aiText,
                emotion = InferEmotionFromText(aiText)
            };

            Debug.Log($"🤖 AI Answer: {parsed.answer}");
            Debug.Log($"🙂 Emotion: {parsed.emotion}");

            EmitAllSentences(aiText);

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
                // Normal unlock
                if (lockStt && sttClient != null)
                {
                    sttClient.canTalkAgain = true;
                    sttClient.allowSTTRequests = true;
                }
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
                    string emo = InferEmotionFromText(sentence);
                    OnSentenceReady?.Invoke(sentence, emo);
                }
            }
        }

        string leftover = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(leftover) && leftover.Length > 1)
        {
            string emo = InferEmotionFromText(leftover);
            OnSentenceReady?.Invoke(leftover, emo);
        }
    }

    string InferEmotionFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "neutral";

        string raw = text.Trim();
        string t = raw.ToLowerInvariant();

        int happy = 0, sad = 0, angry = 0, surprised = 0;

        // Extract and analyze action keywords between asterisks
        var actionMatches = System.Text.RegularExpressions.Regex.Matches(text, @"\*([^*]+)\*");
        foreach (System.Text.RegularExpressions.Match match in actionMatches)
        {
            string action = match.Groups[1].Value.ToLowerInvariant().Trim();
            
            // Happy actions
            if (action.Contains("wink") || action.Contains("smile") || action.Contains("grin") || 
                action.Contains("giggle") || action.Contains("laugh") || action.Contains("chuckle") ||
                action.Contains("beam") || action.Contains("bounce") || action.Contains("dance") ||
                action.Contains("hug") || action.Contains("kiss") || action.Contains("cuddle") ||
                action.Contains("nod") && action.Contains("excitedly"))
            {
                happy += 4;
            }
            
            // Sad actions
            else if (action.Contains("sigh") || action.Contains("frown") || action.Contains("tear") ||
                     action.Contains("cry") || action.Contains("sob") || action.Contains("droop") ||
                     action.Contains("slump") || action.Contains("mope") || action.Contains("whimper") ||
                     action.Contains("look down") || action.Contains("lower head"))
            {
                sad += 4;
            }
            
            // Angry actions  
            else if (action.Contains("glare") || action.Contains("scowl") || action.Contains("growl") ||
                     action.Contains("huff") || action.Contains("stomp") || action.Contains("clench") ||
                     action.Contains("cross arms") || action.Contains("roll eyes") || action.Contains("snap") ||
                     action.Contains("slam") || action.Contains("march"))
            {
                angry += 4;
            }
            
            // Surprised actions
            else if (action.Contains("gasp") || action.Contains("blink") || action.Contains("stare") ||
                     action.Contains("jump") || action.Contains("startle") || action.Contains("wide eyes") ||
                     action.Contains("raise eyebrow") || action.Contains("tilt head") || 
                     action.Contains("lean forward") || action.Contains("look surprised"))
            {
                surprised += 4;
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
        if (questions >= 2) surprised += 2;
        if (exclamations >= 3) { happy += 2; surprised += 1; angry += 1; }
        if (exclamations == 2) happy += 1;

        if (t.Contains("hehe") || t.Contains("haha") || t.Contains("lol") || t.Contains("lmao")) happy += 3;
        if (t.Contains("aww") || t.Contains("awww") || t.Contains("so cute") || t.Contains("adorable")) happy += 2;
        if (t.Contains("yay") || t.Contains("yaaay") || t.Contains("yesss")) happy += 2;
        if (raw.Contains("♡") || raw.Contains("❤") || raw.Contains("<3")) happy += 3;

        if (t.Contains("i'm sorry") || t.Contains("im sorry") || t.Contains("i apologize")) sad += 3;
        if (t.Contains("it's okay") || t.Contains("its okay") || t.Contains("it will be okay") || t.Contains("it’ll be okay")) sad += 2;
        if (t.Contains("poor thing") || t.Contains("oh no") || t.Contains("that's awful") || t.Contains("that’s awful")) sad += 2;
        if (raw.Contains("...")) sad += 1;

        if (t.Contains("tch") || t.Contains("hmph") || t.Contains("idiot") || t.Contains("moron")) angry += 3;
        if (t.Contains("shut up") || t.Contains("stop it") || t.Contains("enough")) angry += 4;
        if (t.Contains("annoying") || t.Contains("you're annoying")) angry += 3;

        if (t.Contains("no way") || t.Contains("what the") || t.Contains("wait") || t.Contains("seriously")) surprised += 3;
        if (t.Contains("oh my") || t.Contains("holy") || t.Contains("gods")) surprised += 2;

        int max = Mathf.Max(happy, sad, angry, surprised);
        if (max <= 1) return "neutral";

        if (angry == max) return "angry";
        if (sad == max) return "sad";
        if (surprised == max) return "surprised";
        if (happy == max) return "happy";

        return "neutral";
    }
}
