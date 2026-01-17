using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class OllamaClient : MonoBehaviour
{
    [Header("Ollama")]
    public string ollamaUrl = "http://127.0.0.1:11434/api/generate";
    public string model = "llama3.1";

    [Header("Persona & Memory File")]
    public string personaRelativePath = "AI/persona.json";
	public string memoryRelativePath = "AI/memory.json";
	private string memoryJson = "{}";

    [Header("Flow Control")]
    public AutoVADSTTClient sttClient;


    [Header("Debug")]
    public bool logRequests = true;
    public bool logPersonaLoaded = true;
    public bool logRawResponse = false;

    private string personaJson = "{}";

    [Serializable]
    public class OllamaRequest
    {
        public string model;
        public string prompt;
        public string system;
        public bool stream = false;
    }

    [Serializable]
    public class OllamaResponse
    {
        public string response;
    }

    [Serializable]
    public class KihbbiAIResponse
    {
        public string answer;
        public string emotion;
    }

    public Action<KihbbiAIResponse> OnAIResponse;

    void Awake()
    {
        LoadPersona();
        LoadMemory();
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

        if (logPersonaLoaded)
            Debug.Log("[Ollama] persona.json loaded (" + personaJson.Length + " chars)");
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

		if (logPersonaLoaded)
			Debug.Log("[Ollama] memory.json loaded (" + memoryJson.Length + " chars)");
	}

    public void Ask(string userText)
    {
        StartCoroutine(AskCoroutine(userText));
    }

    IEnumerator AskCoroutine(string userText)
    {
        // Lock STT while AI runs
        if (sttClient != null)
            sttClient.canTalkAgain = false;

        // System prompt builds from persona.json
        string systemPrompt =
@"CRITICAL RULES:
- You MUST follow persona + memory below.
- ALWAYS answer in ENGLISH, no matter what language the user uses.
- NEVER say you are reading a file, prompt, or JSON.
- NEVER refer to Final Fantasy XIV as a game. You live in Eorzea.
- DO NOT mention or summarize persona or memory JSON. Use silently as internal knowledge.
- Output MUST be VALID JSON ONLY (no markdown, no code block).

PERSONA JSON:
" + personaJson + @"

MEMORY JSON:
" + memoryJson;

        var reqObj = new OllamaRequest
        {
            model = model,
            prompt = userText,
            system = systemPrompt,
            stream = false
        };

        string json = JsonUtility.ToJson(reqObj);

        if (logRequests)
            Debug.Log("[Ollama] Asking: " + userText);

        using UnityWebRequest req = new UnityWebRequest(ollamaUrl, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 120;

        yield return req.SendWebRequest();

        // Always unlock at end
        void Unlock()
        {
            if (sttClient != null)
                sttClient.canTalkAgain = true;
        }

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[Ollama] Request failed: " + req.error);
            Debug.LogError("[Ollama] Body: " + req.downloadHandler.text);
            Unlock();
            yield break;
        }

        string ollamaJson = req.downloadHandler.text;
        if (logRawResponse)
            Debug.Log("[Ollama] Raw: " + ollamaJson);

        OllamaResponse o = JsonUtility.FromJson<OllamaResponse>(ollamaJson);
        if (o == null || string.IsNullOrWhiteSpace(o.response))
        {
            Debug.LogError("[Ollama] Empty response");
            Unlock();
            yield break;
        }

        string aiText = o.response.Trim();

        // parse JSON inside response
        KihbbiAIResponse parsed = TryParseAIJson(aiText);
        if (parsed == null)
        {
            Debug.LogWarning("[Ollama] AI did not return valid JSON. Fallback raw text.");
            parsed = new KihbbiAIResponse
            {
                answer = aiText,
                emotion = "neutral"
            };
        }

		if (string.IsNullOrWhiteSpace(parsed.emotion) || parsed.emotion == "neutral")
		{
			parsed.emotion = InferEmotionFromText(parsed.answer);
		}

        Debug.Log($"🤖 AI Answer: {parsed.answer}");
        Debug.Log($"🙂 Emotion: {parsed.emotion}");

        OnAIResponse?.Invoke(parsed);

        Unlock();
    }

    KihbbiAIResponse TryParseAIJson(string s)
    {
        int a = s.IndexOf('{');
        int b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return null;

        string json = s.Substring(a, b - a + 1);

        try
        {
            return JsonUtility.FromJson<KihbbiAIResponse>(json);
        }
        catch
        {
            return null;
        }
    }

	string InferEmotionFromText(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return "neutral";

		string t = text.ToLowerInvariant();

		// angry
		if (t.Contains("wtf") || t.Contains("shut up") || t.Contains("stop") || t.Contains("i'm pissed") || t.Contains("annoying"))
			return "angry";

		// sad
		if (t.Contains("sorry") || t.Contains("i'm sorry") || t.Contains("that sucks") || t.Contains("i feel bad") || t.Contains("sad"))
			return "sad";

		// surprised
		if (t.Contains("no way") || t.Contains("what?!") || t.Contains("what?!") || t.Contains("wait") || t.Contains("holy") || text.Contains("?!"))
			return "surprised";

		// happy / excited
		if (t.Contains("yay") || t.Contains("yaaay") || t.Contains("omg") || t.Contains("hehe") || t.Contains("lol") || t.Contains("lmao"))
			return "happy";

		// punctuation based
		int exclamations = 0;
		int questions = 0;
		foreach (char c in text)
		{
			if (c == '!') exclamations++;
			if (c == '?') questions++;
		}

		if (exclamations >= 2) return "happy";
		if (exclamations == 1) return "surprised";
		if (questions >= 2) return "surprised";

		return "neutral";
	}

}
