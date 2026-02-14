using UnityEngine;
using TMPro;

/// <summary>
/// Simple debug: shows full chat history in a TMP text field.
/// Updates every frame. Remove when done debugging.
/// </summary>
public class ChatHistoryDebugUI : MonoBehaviour
{
    public OllamaClient ollamaClient;
    public TextMeshProUGUI historyText;
    
    void Update()
    {
        if (ollamaClient == null || historyText == null)
            return;
        
        var history = ollamaClient.GetChatHistory();
        
        if (history == null || history.Count == 0)
        {
            historyText.text = "No messages yet.";
            return;
        }
        
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        
        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            sb.AppendLine($"[{msg.role.ToUpper()}]");
            sb.AppendLine(msg.content);
            sb.AppendLine();
        }
        
        historyText.text = sb.ToString();
    }
}
