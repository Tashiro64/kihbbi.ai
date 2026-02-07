using UnityEngine;
using TMPro;

public class TimeManagement : MonoBehaviour
{
    public int gameHour;
    public int gameMinute;
    public static string currentLight;

    public TextMeshProUGUI timeText;
    public AIBehaviorManager aiBehaviorManager; // Optional: to notify when time of day changes

    private string previousLight;

    void Start()
    {
        if (timeText == null)
        {
            var go = GameObject.Find("UI/Canvas/Time");
            if (go != null) timeText = go.GetComponent<TextMeshProUGUI>();
        }
        
        // Initialize previousLight
        previousLight = currentLight;
    }

    void Update()
    {
        var now = System.DateTime.Now;

        // How far we are inside the current real hour (0→1)
        float secondsIntoHour = now.Minute * 60f + now.Second + now.Millisecond / 1000f;
        float hourProgress = secondsIntoHour / 3600f;

        // Map to full 24h game day
        float gameMinutesInDay = hourProgress * 1440f;

        gameHour = (int)(gameMinutesInDay / 60f);
        gameMinute = (int)(gameMinutesInDay % 60f);

        // Day/Night from GAME time
        currentLight = (gameHour >= 7 && gameHour < 19) ? "day" : "night";

        // Detect time of day change and update background
        if (currentLight != previousLight)
        {
            Debug.Log($"[TimeManagement] Time of day changed from {previousLight} to {currentLight}");
            previousLight = currentLight;
            
            if (aiBehaviorManager != null)
            {
                aiBehaviorManager.RefreshBackground();
            }
            else
            {
                Debug.LogWarning("[TimeManagement] AIBehaviorManager not assigned, cannot refresh background!");
            }
        }

        // 12-hour clock display
        int hour12 = gameHour % 12;
        if (hour12 == 0) hour12 = 12;
        string ampm = gameHour >= 12 ? "PM" : "AM";

        if (timeText != null)
            timeText.text = $"{hour12:00}:{gameMinute:00} {ampm}";
    }
}
