using UnityEngine;

public class TextChatBox : MonoBehaviour
{
    [Header("References")]
    public OllamaClient ollama;
    public AutoVADSTTClient stt; // optional (for canTalkAgain)

    [Header("UI")]
    public bool showUI = true;
    public KeyCode focusKey = KeyCode.T;  // press T to focus typing
    public int maxChars = 200;

    private string input = "";
    private bool hasFocus = false;

    void Update()
    {
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

        if (ollama != null)
            ollama.Ask(input);

        input = "";
        hasFocus = false;
    }

    void OnGUI()
    {
        if (!showUI || !Application.isPlaying) return;

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
