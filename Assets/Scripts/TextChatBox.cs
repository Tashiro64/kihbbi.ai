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
        string normalizedText = input; // Default to original input
        if (stt != null)
        {
            normalizedText = stt.NormalizeKihbbiVariations(input);
            
            // ALWAYS normalize location names to proper capitalization
            normalizedText = AutoVADSTTClient.NormalizeLocationForDetection(normalizedText);
            
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
                // Add normalized command to chat history
                if (piperClient != null)
                {
                    piperClient.AppendUserMessage(normalizedText);
                }
                
                // Check if it's a location/movement command
                if (normLower.Contains("move") || normLower.Contains("go to") || normLower.Contains("go ") || normLower.Contains("let's go") || normLower.Contains("teleport") || normLower.Contains("take me") || normLower.Contains("somewhere else") || normLower.Contains("somewhere"))
                {
                    Debug.Log($"[TextChatBox] Location command detected");
                    
                    if (aiBehaviorManager != null)
                    {
                        // Location names are already normalized in normalizedText
                        Debug.Log($"[TextChatBox] Checking for location in: '{normalizedText}'");
                        
                        // Check for specific locations (case-insensitive detection)
                        string foundLocation = null;
                        string checkLower = normalizedText.ToLower();
                        
                        if (checkLower.Contains("home") || checkLower.Contains("house"))
                            foundLocation = "house_mist";
                        else if (checkLower.Contains("yak'tel"))
                            foundLocation = "yaktel";
                        else if (checkLower.Contains("limsa"))
                            foundLocation = "limsa_lominsa";
                        else if (checkLower.Contains("gridania"))
                            foundLocation = "new_gridania";
                        else if (checkLower.Contains("ul'dah"))
                            foundLocation = "uldah";
                        else if (checkLower.Contains("gold saucer"))
                            foundLocation = "gold_saucer";
                        else if (checkLower.Contains("solution nine"))
                            foundLocation = "solution_nine";
                        else if (checkLower.Contains("il mheg"))
                            foundLocation = "il_mheg";
                        else if (checkLower.Contains("la noscea"))
                            foundLocation = "middle_la_noscea";
                        else if (checkLower.Contains("kugane"))
                            foundLocation = "kugane";
                        else if (checkLower.Contains("eulmore"))
                            foundLocation = "eulmore";
                        else if (checkLower.Contains("tuliyollal"))
                            foundLocation = "tuliyollal";
                        else if (checkLower.Contains("lakeland"))
                            foundLocation = "lakeland";
                        else if (checkLower.Contains("shroud"))
                            foundLocation = "central_shroud";
                        
                        Debug.Log($"[TextChatBox] Location detection result: foundLocation = {(foundLocation ?? "NULL")}");
                        
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
            // Add normalized user message to chat history
            if (piperClient != null)
            {
                piperClient.AppendUserMessage(normalizedText);
            }
            
            ollama.Ask(normalizedText);
        }

        input = "";
        hasFocus = false;
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
