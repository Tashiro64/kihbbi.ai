using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class AutoVADSTTClient : MonoBehaviour
{
    [Header("TTS")]
    public PiperClient piperClient;
    [Header("AI")]
    public OllamaClient ollama;
    public AIBehaviorManager aiBehaviorManager;

    [Header("STT Server")]
    public string sttUrl = "http://127.0.0.1:8007/stt";

    [Header("Command Intercept")]
    public string commandPrefix = "hey kihbbi";
    public string commandWebhookUrl = "https://tashiroworld.com/api/kihbbi.ai/ffxivcollect.php";
    [Tooltip("Phonetic variations of 'kihbbi' that Whisper might transcribe")]
    public string[] kihbbiVariations = new string[] 
    { 
        "kee bee", "kibi", "kibby", "keebi", "keebee", "key bee", 
        "chi bee", "chibi", "chibby", "chi be", "khibi", "kheebi",
		"khibby", "kheebee"
    };

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
    public KeyCode toggleKey = KeyCode.F1;  // press F1 to toggle visibility

    private string micDevice;
    // Use shared GUI visibility state from TextChatBox
    private AudioClip micClip;

    private const int MIC_BUFFER_SECONDS = 60; // mic recording ring buffer length
    private int lastMicPos = 0;

    private bool isSpeaking = false;
    private float silenceTimer = 0f;
    private float speakingTimer = 0f;

    private float lastRms = 0f;

    /// <summary>
    /// Returns true if the user is currently speaking into the microphone
    /// </summary>
    public bool IsSpeaking => isSpeaking;

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

        // Check if Left Ctrl is being held - if so, pause microphone processing
        if (Input.GetKey(KeyCode.LeftControl))
        {
            // If we were speaking, stop the current utterance
            if (isSpeaking)
            {
                if (showDebugLogs)
                    Debug.Log("[AutoVAD] 🚫 Left Ctrl held - stopping current speech");
                
                isSpeaking = false;
                silenceTimer = 0f;
                speakingTimer = 0f;
                utterance.Clear();
            }
            
            // Don't process any speech detection while Ctrl is held
            return;
        }

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

        // Handle F1 toggle for GUI visibility
        if (Input.GetKeyDown(toggleKey))
        {
            TextChatBox.sharedGUIVisible = !TextChatBox.sharedGUIVisible;
            if (showDebugLogs)
                Debug.Log($"[STTClient] F1 pressed, GUI visibility: {TextChatBox.sharedGUIVisible}");
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
            if (showDebugLogs) Debug.Log("[AutoVAD] Entered post-STT command/casual check block");
            canTalkAgain = false; // 🔒 lock
            allowSTTRequests = false; // 🔒 HARD LOCK until Ollama finishes (Ollama must re-enable!)

            // Normalize phonetic variations of "kihbbi" before checking
            string normalizedText = NormalizeKihbbiVariations(res.text);
			
			if (showDebugLogs)
                Debug.Log($"[AutoVAD] Before command prefix fixes: '{normalizedText}'");
			
			// Fix common STT errors for "hey kihbbi" command prefix - use word boundaries for better matching
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA key B\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA kihbbi\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bEi que bi\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA. Ki B\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA\. Ki B\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA. kihbbi\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA\. kihbbi\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bAy kihbbi\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA KB\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bAQB\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bAKB\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA qui B\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA\.kihbbi\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA. Qui B\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			normalizedText = System.Text.RegularExpressions.Regex.Replace(normalizedText, @"\bA\. Qui B\b", "hey kihbbi", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			
			normalizedText = normalizedText.Replace(",", "");
			
			// Normalize location names for better display in chat log
			normalizedText = NormalizeLocationNames(normalizedText);
			
			if (showDebugLogs)
                Debug.Log($"[AutoVAD] After all normalizations: '{normalizedText}'");

            // Check if text starts with command prefix (allowing for extra text after the prefix)
            string normLower = normalizedText.ToLower().Trim();
            string prefixLower = commandPrefix.ToLower();
            bool isCommand = false;
            if (showDebugLogs)
            {
                Debug.Log($"[AutoVAD] Command check: normLower='{normLower}', prefixLower='{prefixLower}'");
            }
            if (normLower == prefixLower || normLower.StartsWith(prefixLower + " "))
            {
                isCommand = true;
                if (showDebugLogs) Debug.Log($"[AutoVAD] Command detected by exact or space match.");
            }
            // Also allow for punctuation after prefix (e.g., "hey kihbbi, ...")
            else if (normLower.StartsWith(prefixLower) && normLower.Length > prefixLower.Length)
            {
                char nextChar = normLower[prefixLower.Length];
                if (!char.IsLetterOrDigit(nextChar))
                {
                    isCommand = true;
                    if (showDebugLogs) Debug.Log($"[AutoVAD] Command detected by punctuation match: nextChar='{nextChar}'");
                }
            }

            // Add user message to chat history (for both commands and regular messages)
            // Use normalized text so variations like "kibi" are shown as "kihbbi"
            if (piperClient != null)
            {
                piperClient.AppendUserMessage(normalizedText);
            }

            if (isCommand)
            {
                if (showDebugLogs) Debug.Log($"[AutoVAD] Command text for location check: '{normLower}'");
                
                // Check if it's a location/movement command
                if (normLower.Contains("move") || normLower.Contains("going") || normLower.Contains("go to") || normLower.Contains("go ") || normLower.Contains("let's go") || normLower.Contains("teleport") || normLower.Contains("take me") || normLower.Contains("somewhere else") || normLower.Contains("somewhere"))
                {
                    if (showDebugLogs) Debug.Log($"[AutoVAD] Location command detected");
                    
                    if (aiBehaviorManager != null)
                    {
                        // Use the already lowercased normLower for location detection
                        // (normalizedText already has location names properly capitalized)
                        string locationLower = normLower;
                        
                        // Try to find a specific location in the text
                        string foundLocation = null;
                        
                        if (locationLower.Contains("home") || locationLower.Contains("house"))
                            foundLocation = "house_mist";
                        else if (locationLower.Contains("kugane"))
                            foundLocation = "kugane";
                        else if (locationLower.Contains("limsa"))
                            foundLocation = "limsa_lominsa";
                        else if (locationLower.Contains("gridania"))
                            foundLocation = "new_gridania";
                        else if (locationLower.Contains("uldah"))
                            foundLocation = "uldah";
                        else if (locationLower.Contains("gold saucer") || locationLower.Contains("saucer"))
                            foundLocation = "gold_saucer";
                        else if (locationLower.Contains("eulmore"))
                            foundLocation = "eulmore";
                        else if (locationLower.Contains("solution nine"))
                            foundLocation = "solution_nine";
                        else if (locationLower.Contains("tuliyollal"))
                            foundLocation = "tuliyollal";
                        else if (locationLower.Contains("il mheg"))
                            foundLocation = "il_mheg";
                        else if (locationLower.Contains("lakeland"))
                            foundLocation = "lakeland";
                        else if (locationLower.Contains("shroud"))
                            foundLocation = "central_shroud";
                        else if (locationLower.Contains("la noscea"))
                            foundLocation = "middle_la_noscea";
                        else if (locationLower.Contains("yaktel"))
                            foundLocation = "yaktel";
                        
                        if (foundLocation != null)
                        {
                            if (showDebugLogs) Debug.Log($"[AutoVAD] Found location: {foundLocation}");
                            
                            // Add user's command to conversation history
                            ollama.AddUserMessageToHistory(normalizedText);
                            
                            if (showDebugLogs) Debug.Log($"[AutoVAD] Calling ChangeToLocation({foundLocation})");
                            aiBehaviorManager.ChangeToLocation(foundLocation);
                        }
                        else
                        {
                            if (showDebugLogs) Debug.Log($"[AutoVAD] No specific location found, teleporting randomly");
                            
                            // Add user's command to conversation history
                            ollama.AddUserMessageToHistory(normalizedText);
                            
                            if (showDebugLogs) Debug.Log($"[AutoVAD] Calling ChangeToRandomLocation()");
                            aiBehaviorManager.ChangeToRandomLocation();
                        }
                        
                        // Re-enable STT after location command
                        canTalkAgain = true;
                        allowSTTRequests = true;
                    }
                }
                else
                {
                    // Check if it's a webhook command (mount/minion/etc)
                    string lowerText = normalizedText.ToLower();
                    bool isWebhookCommand = lowerText.Contains("mount") || lowerText.Contains("mounted") || lowerText.Contains("mounts") || lowerText.Contains("mouth") ||
                                           lowerText.Contains("minion") || lowerText.Contains("minions") || lowerText.Contains("menion") || lowerText.Contains("me nion") ||
                                           lowerText.Contains("hairstyle") || lowerText.Contains("hair style") || lowerText.Contains("haircut") || lowerText.Contains("hair cut") ||
                                           lowerText.Contains("emote") || lowerText.Contains("emotes") || lowerText.Contains("he moats") || lowerText.Contains("he moat") || lowerText.Contains("hemoat") || lowerText.Contains("emoat") || lowerText.Contains("e moats") ||
                                           lowerText.Contains("barding") || lowerText.Contains("bardings") || lowerText.Contains("bard ding") || lowerText.Contains("bar dings") || lowerText.Contains("bar ding") || lowerText.Contains("bard dings") || lowerText.Contains("bardin") || lowerText.Contains("bar din");
                    
                    if (isWebhookCommand)
                    {
                        // Send to webhook for mount/minion/etc
                        if (showDebugLogs) Debug.Log($"[AutoVAD] Webhook command detected (mount/minion/etc), sending to webhook");
                        
                        // Add user's command to conversation history
                        ollama.AddUserMessageToHistory(normalizedText);
                        
                        StartCoroutine(SendToCommandWebhook(normalizedText));
                    }
                    else
                    {
                        // Unknown command, treat as normal chat
                        if (showDebugLogs) Debug.Log($"[AutoVAD] Unknown command type, treating as normal chat message");
                        canTalkAgain = true;
                        allowSTTRequests = true;
                        ollama.Ask(normalizedText);
                    }
                }
            }
            else
            {
                // No command, send to normal chat (use normalized text)
                if (showDebugLogs) Debug.Log($"[AutoVAD] Not a command, sending to chat. normLower='{normLower}'");
                ollama.Ask(normalizedText);
            }
        }
    }

    /// <summary>
    /// Replace phonetic variations of "kihbbi" with "kihbbi" for command detection
    /// </summary>
    public string NormalizeKihbbiVariations(string text)
    {
        string normalized = text;
        
        foreach (string variation in kihbbiVariations)
        {
            // Case-insensitive replace
            normalized = System.Text.RegularExpressions.Regex.Replace(
                normalized, 
                variation, 
                "kihbbi", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }
        
        return normalized;
    }
    
    /// <summary>
    /// Normalize phonetic variations of location names for better recognition
    /// </summary>
    public string NormalizeLocationNames(string text)
    {
        string normalized = text;
        
        // Kugane variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(kugeni|koogane|kugan|koogan|kugani|coogane|cugane)\b", "Kugane", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Limsa variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(limza|lemsa|limsa|leemsa|limza lominsa|lemsa lominsa)\b", "Limsa", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Lominsa variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(lominsa|lominza|lomin za)\b", "Lominsa", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Gridania variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(gredania|gridanya|greedania|gredanya)\b", "Gridania", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Ul'dah variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(uldah|ooldah|ul da|ool dah|ulda)\b", "Ul'dah", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Eulmore variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(old moor|eulmore|yule more|ule more|eelmore)\b", "Eulmore", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Tuliyollal variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(tuliyollal|tuli yollal|tulli yollal|tulia lal|toolie lal)\b", "Tuliyollal", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Il Mheg variations (already handled but adding more)
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(ill meg|il meg|ill mheg|eel meg|eel mheg)\b", "Il Mheg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Lakeland variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(lakeland|lake land|lakelend)\b", "Lakeland", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Solution Nine variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(solution 9|solution nine|solution nine|sulution nine)\b", "Solution Nine", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Gold Saucer variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(gold saucer|gold sauce er|golden saucer)\b", "Gold Saucer", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Shroud variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(shrood|shroud|shrowd|shrod)\b", "Shroud", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // La Noscea variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(la noscea|la no sea|la noshea|la nos sea)\b", "La Noscea", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Yak T'el variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(yaktel|yak tel|yak tell|yakk tel)\b", "Yaktel", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        return normalized;
    }

    public IEnumerator SendToCommandWebhook(string text)
    {
        Debug.Log($"[Command] Sending to webhook: {text}");

        // Manually enqueue a TTS response using PiperClient
        string chosenTTS = "";
        if (piperClient != null)
        {
            string[] ttsResponses = new string[]
            {
                "Let me check that for you.",
                "Looking that up now.",
                "Checking that right now.",
                "One moment, pulling that information up.",
                "Let me take a quick look at that.",
				"I'll find that information for you.",
				"Just a second, retrieving that data.",
				"Hold on, getting that for you.",
				"I'll look into that right away.",
				"Give me a moment to find that out."
            };
            chosenTTS = ttsResponses[UnityEngine.Random.Range(0, ttsResponses.Length)];
            piperClient.Enqueue(chosenTTS);
            
            // Add initial response to AI conversation history
            if (ollama != null)
            {
                ollama.AddAssistantMessageToHistory(chosenTTS);
            }
        }
        else
        {
            Debug.LogWarning("[Command] PiperClient not assigned, cannot queue TTS.");
        }

        // Send text as JSON to your server
        string jsonData = JsonUtility.ToJson(new { text = text });

		//if text contains "mount" add ?action=mount to the URL
		string urlToUse = commandWebhookUrl;
		if (text.ToLower().Contains("mount") || text.ToLower().Contains("mounted") || text.ToLower().Contains("mounts") || text.ToLower().Contains("mouth")){
			urlToUse += "?action=mounts";
		} else if (text.ToLower().Contains("minion") || text.ToLower().Contains("minions") || text.ToLower().Contains("menion") || text.ToLower().Contains("me nion")){
			urlToUse += "?action=minions";
		} else if (text.ToLower().Contains("hairstyles") || text.ToLower().Contains("hairstyle") || text.ToLower().Contains("hair styles") || text.ToLower().Contains("hair style") || text.ToLower().Contains("haircut") || text.ToLower().Contains("hair cuts") || text.ToLower().Contains("hair cut")){
			urlToUse += "?action=hairstyles";
		} else if (text.ToLower().Contains("emote") || text.ToLower().Contains("emotes") || text.ToLower().Contains("he moats") || text.ToLower().Contains("he moat") || text.ToLower().Contains("hemoat") || text.ToLower().Contains("emoat") || text.ToLower().Contains("e moats")){
			urlToUse += "?action=emotes";
		} else if (text.ToLower().Contains("bardings") || text.ToLower().Contains("barding") || text.ToLower().Contains("bard ding") || text.ToLower().Contains("bar dings") || text.ToLower().Contains("bar ding") || text.ToLower().Contains("bard dings") || text.ToLower().Contains("bardin") || text.ToLower().Contains("bar din")){
			urlToUse += "?action=bardings";
		}

		urlToUse += "&text=" + UnityWebRequest.EscapeURL(text);

		Debug.Log(urlToUse);

        using UnityWebRequest req = new UnityWebRequest(urlToUse, "POST");
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(jsonData));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = 10;

        yield return req.SendWebRequest();

        // Enqueue the exact HTML/text response from the webhook as a Piper TTS
        string htmlResponse = req.downloadHandler != null ? req.downloadHandler.text : "";
        if (piperClient != null && !string.IsNullOrEmpty(htmlResponse))
        {
            piperClient.Enqueue(htmlResponse);
            
            // Add webhook response to AI conversation history
            if (ollama != null)
            {
                ollama.AddAssistantMessageToHistory(htmlResponse);
            }
        }
        else if (piperClient == null)
        {
            Debug.LogWarning("[Command] PiperClient not assigned, cannot queue HTML TTS.");
        }

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"[Command] Webhook response: {req.downloadHandler.text}");
        }
        else
        {
            Debug.LogWarning($"[Command] Webhook failed: {req.error}");
        }

        // Re-enable STT after command
        canTalkAgain = true;
        allowSTTRequests = true;
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
        if (!showOnScreenStatus || !Application.isPlaying || !TextChatBox.sharedGUIVisible) return;

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
