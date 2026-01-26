using UnityEngine;

public class TextChatBox : MonoBehaviour
{
    [Header("References")]
    public OllamaClient ollama;
    public AutoVADSTTClient stt; // optional (for canTalkAgain)

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
                Debug.Log($"[TextChatBox] Command detected (prefix '{stt.commandPrefix}'), sending to webhook");
                stt.StartCoroutine(stt.SendToCommandWebhook(normalizedText));
                handled = true;
            }
        }

        if (!handled && ollama != null)
        {
            ollama.Ask(input);
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
