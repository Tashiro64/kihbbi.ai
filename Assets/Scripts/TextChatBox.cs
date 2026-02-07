using UnityEngine;

public class TextChatBox : MonoBehaviour
{
    [Header("References")]
    public OllamaClient ollama;
    public AutoVADSTTClient stt; // optional (for canTalkAgain)
    public PiperClient piperClient; // for chat history
    public AIBehaviorManager aiBehaviorManager; // for location commands

    [Header("UI")]
    public bool showUI = true;
    public KeyCode focusKey = KeyCode.T;  // press T to focus typing
    public KeyCode toggleKey = KeyCode.F1;  // press F1 to toggle visibility
    public int maxChars = 200;

    private string input = "";
    private bool hasFocus = false;
    
    // Shared GUI visibility state
    public static bool sharedGUIVisible = false;

    void Update()
    {
        // Handle F1 toggle for GUI visibility
        if (Input.GetKeyDown(toggleKey))
        {
            sharedGUIVisible = !sharedGUIVisible;
        }

        if (Input.GetKeyDown(focusKey))
        {
            hasFocus = true;
        }

        // Enter sends
        if (hasFocus && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
        {
            Send();
        }

        // Escape cancels typing
        if (hasFocus && Input.GetKeyDown(KeyCode.Escape))
        {
            hasFocus = false;
        }
    }

    void Send()
    {
        input = input.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        // Optional lock check
        if (stt != null && !stt.canTalkAgain)
        {
            Debug.Log("⛔ Waiting for AI response...");
            return;
        }

        Debug.Log("💬 USER: " + input);

        // Command detection logic (match STTClient)
        bool handled = false;
        if (stt != null)
        {
            string normalizedText = stt.NormalizeKihbbiVariations(input);
            string normLower = normalizedText.ToLower().Trim();
            string prefixLower = stt.commandPrefix.ToLower();
            bool isCommand = false;
            if (normLower == prefixLower || normLower.StartsWith(prefixLower + " "))
            {
                isCommand = true;
            }
            else if (normLower.StartsWith(prefixLower) && normLower.Length > prefixLower.Length)
            {
                char nextChar = normLower[prefixLower.Length];
                if (!char.IsLetterOrDigit(nextChar))
                    isCommand = true;
            }
            if (isCommand)
            {
                // Add command to chat history
                if (piperClient != null)
                {
                    piperClient.AppendUserMessage(input);
                }
                
                // Check if it's a location/movement command
                if (normLower.Contains("move") || normLower.Contains("go to") || normLower.Contains("go ") || normLower.Contains("let's go") || normLower.Contains("teleport") || normLower.Contains("take me") || normLower.Contains("somewhere else") || normLower.Contains("somewhere"))
                {
                    Debug.Log($"[TextChatBox] Location command detected");
                    
                    if (aiBehaviorManager != null)
                    {
                        // Normalize location name variations
                        string normalizedForLocation = NormalizeLocationNames(normLower);
                        Debug.Log($"[TextChatBox] After location normalization: '{normalizedForLocation}'");
                        
                        // Try to find a specific location in the text
                        string foundLocation = null;
                        
                        if (normalizedForLocation.Contains("home") || normalizedForLocation.Contains("house"))
                            foundLocation = "house_mist";
                        else if (normalizedForLocation.Contains("kugane"))
                            foundLocation = "kugane";
                        else if (normalizedForLocation.Contains("limsa"))
                            foundLocation = "limsa_lominsa";
                        else if (normalizedForLocation.Contains("gridania"))
                            foundLocation = "new_gridania";
                        else if (normalizedForLocation.Contains("uldah"))
                            foundLocation = "uldah";
                        else if (normalizedForLocation.Contains("gold saucer") || normalizedForLocation.Contains("saucer"))
                            foundLocation = "gold_saucer";
                        else if (normalizedForLocation.Contains("eulmore"))
                            foundLocation = "eulmore";
                        else if (normalizedForLocation.Contains("solution nine"))
                            foundLocation = "solution_nine";
                        else if (normalizedForLocation.Contains("tuliyollal"))
                            foundLocation = "tuliyollal";
                        else if (normalizedForLocation.Contains("il mheg"))
                            foundLocation = "il_mheg";
                        else if (normalizedForLocation.Contains("lakeland"))
                            foundLocation = "lakeland";
                        else if (normalizedForLocation.Contains("shroud"))
                            foundLocation = "central_shroud";
                        else if (normalizedForLocation.Contains("la noscea"))
                            foundLocation = "middle_la_noscea";
                        else if (normalizedForLocation.Contains("yaktel"))
                            foundLocation = "yaktel";
                        
                        if (foundLocation != null)
                        {
                            Debug.Log($"[TextChatBox] Found location: {foundLocation}");
                            aiBehaviorManager.ChangeToLocation(foundLocation);
                        }
                        else
                        {
                            Debug.Log($"[TextChatBox] No specific location found, teleporting randomly");
                            aiBehaviorManager.ChangeToRandomLocation();
                        }
                    }
                    handled = true;
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
                        Debug.Log($"[TextChatBox] Webhook command detected (mount/minion/etc), sending to webhook");
                        stt.StartCoroutine(stt.SendToCommandWebhook(normalizedText));
                        handled = true;
                    }
                    else
                    {
                        // Unknown command, treat as normal chat
                        Debug.Log($"[TextChatBox] Unknown command type, treating as normal chat message");
                        // Don't set handled = true, let it fall through
                    }
                }
            }
        }

        if (!handled && ollama != null)
        {
            // Add user message to chat history
            if (piperClient != null)
            {
                piperClient.AppendUserMessage(input);
            }
            
            ollama.Ask(input);
        }

        input = "";
        hasFocus = false;
    }
    
    /// <summary>
    /// Normalize phonetic variations of location names for better recognition
    /// </summary>
    private string NormalizeLocationNames(string text)
    {
        string normalized = text;
        
        // Kugane variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(koogane|kugan|koogan|kugani|coogane|cugane)\b", "Kugane", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Limsa variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(limza|lemsa|limsa|leemsa|limza lominsa|lemsa lominsa)\b", "Limsa", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Gridania variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(gredania|gridanya|greedania|gredanya)\b", "Gridania", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Ul'dah variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(uldah|ooldah|ul da|ool dah|ulda)\b", "Ul'dah", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Eulmore variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(eulmore|yule more|ule more|eelmore)\b", "Eulmore", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Tuliyollal variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(tuliyollal|tuli yollal|tulli yollal|tulia lal|toolie lal)\b", "Tuliyollal", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Il Mheg variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(ill meg|il meg|ill mheg|eel meg|eel mheg)\b", "Il Mheg", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Lakeland variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(lakeland|lake land|lakelend)\b", "Lakeland", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Solution Nine variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(solution 9|solution nine|solution nine|sulution nine)\b", "Solution Nine", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Gold Saucer variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(gold saucer|gold sauce er|golden saucer)\b", "Gold Saucer", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Shroud variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(shroud|shrowd|shrod)\b", "Shroud", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // La Noscea variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(la noscea|la no sea|la noshea|la nos sea)\b", "La Noscea", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Yak T'el variations
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\b(yaktel|yak tel|yak tell|yakk tel)\b", "Yak T'el", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        return normalized;
    }

    void OnGUI()
    {
        if (!showUI || !Application.isPlaying || !sharedGUIVisible) return;

        GUILayout.BeginArea(new Rect(10, 320, 520, 140), GUI.skin.box);
        GUILayout.Label("Text Chat (press T to type, Enter to send, Esc to cancel)");

        GUI.enabled = hasFocus;
        input = GUILayout.TextField(input, maxChars);
        GUI.enabled = true;

        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Focus"))
            hasFocus = true;

        if (GUILayout.Button("Send"))
            Send();

        GUILayout.EndHorizontal();

        GUILayout.EndArea();
    }
}
