using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class AIBehaviorManager : MonoBehaviour
{
    [Header("References")]
    public PiperClient piperClient;
    public AutoVADSTTClient sttClient;
    public TimeManagement timeManager;
    public OllamaClient ollamaClient;
    
    [Header("Background")]
    [Tooltip("UI Image component for the background (or SpriteRenderer if using sprites)")]
    public SpriteRenderer backgroundImage;
    
    [Tooltip("Path to background sprites in Resources folder (e.g., 'Backgrounds/')")]
    public string backgroundResourcesPath = "Backgrounds/";
    
    [Header("Location Display")]
    [Tooltip("TextMeshPro component to display the current location name")]
    public TextMeshProUGUI locationNameText;
    
    [Header("Locations")]
    [Tooltip("List of location names (without _day or _night suffix). Example: kugane_, limsa_lominsa_")]
    public string[] locations = new string[]
    {
        "list in the inspector",
    };
    
    [Header("Location Change Messages")]
    [Tooltip("Random messages to say when changing location")]
    public string[] locationChangeMessages = new string[]
    {
        "Hey, I'm gonna go for a walk!",
        "Time for a change of scenery!",
        "Let's explore somewhere new!",
        "I feel like going out for a bit.",
        "Want to see somewhere different?",
        "Let's head somewhere else!",
        "I think I'll wander around a bit.",
        "How about we check out another spot?"
    };
    
    [Header("Location Command Responses")]
    [Tooltip("Random responses when user requests a location change")]
    public string[] locationCommandResponses = new string[]
    {
        "Alright, let's do this!",
        "Sounds good to me!",
        "Sure, let's go!",
        "Okay, let's head there!",
        "Perfect, I'll take us there!",
        "Good idea, let's check it out!",
        "Alright, on our way!",
        "Let's go then!"
    };
    
    [Header("Timing")]
    [Tooltip("Minimum seconds between behavior checks")]
    public float minCheckInterval = 30f;
    
    [Tooltip("Maximum seconds between behavior checks")]
    public float maxCheckInterval = 90f;
    
    [Tooltip("Chance (0-1) that the AI will change location when a check occurs")]
    [Range(0f, 1f)]
    public float locationChangeChance = 0.3f;
    
    [Header("Debug")]
    public bool showDebugLogs = true;
    
    // Location descriptions dictionary
    private Dictionary<string, string> locationDescriptions;
    private Dictionary<string, string> locationDisplayNames;
    
    private float nextCheckTime;
    private string currentLocation;
    
    void Start()
    {
        InitializeLocationDescriptions();
        InitializeLocationDisplayNames();
        
        // Always start at mist (home)
        currentLocation = "house_mist";
        UpdateBackground();
        UpdateLocationContext();
        UpdateLocationDisplay();
        
        ScheduleNextCheck();
    }
    
    private void InitializeLocationDescriptions()
    {
        locationDescriptions = new Dictionary<string, string>
        {
			// Central Shroud
			{ "central_shroud_day", "You are in the Central Shroud during the day. This lush forest area is filled with towering trees and vibrant flora. The sounds of birds and rustling leaves create a peaceful atmosphere. The Bentbranch Meadoes is where lives multiple chocobos and the nearby camps of adventurers add to the lively yet serene environment." },
			{ "central_shroud_night", "You are in the Central Shroud at night. The forest takes on a mystical quality under the moonlight, with soft glowing lanterns illuminating the paths. The sounds of nocturnal creatures fill the air, and the scent of pine and earth is more pronounced. The Bentbranch Meadoes is where lives multiple chocobos and the INN's warm lights beckon travelers seeking rest." },

			// Eulmore
			{ "eulmore_day", "You are in Eulmore during the day. This opulent city is built around a massive crystal and features extravagant architecture. The streets are bustling with wealthy citizens, merchants, and adventurers. The Crystal Tower looms in the distance, and the sound of fountains and music fills the air. The atmosphere is one of luxury and excess." },
			{ "eulmore_night", "You are in Eulmore at night. The city takes on an even more decadent vibe after dark, with magical lights illuminating the grand architecture. The Crystal Tower glows brilliantly against the night sky. The streets are alive with music and laughter from the taverns, and the scent of fine food and drink wafts through the air." },

			// Gold Saucer
			{ "gold_saucer_day", "You are in the Gold Saucer during the day. This sprawling amusement park is filled with colorful attractions, games, and rides. The sounds of laughter, music, and cheering create an energetic atmosphere. The bright colors of the structures and the lively crowds make it feel like a never-ending festival." },
			{ "gold_saucer_night", "You are in the Gold Saucer at night. The amusement park is illuminated by countless lights, creating a magical and vibrant atmosphere. The sounds of music and laughter continue into the night, and the night sky adds a sense of wonder to the experience. The rides and attractions look even more dazzling under the stars." },

			// Mist (Kihbbi's Home)
			{ "house_mist_day", "You are in Mist during the day. This residential area is where you live and you are in front of your house. Surrounded by quaint houses and the sounds of daily life. The nearby market is active with locals going about their day. The atmosphere is cozy and familiar, with a strong sense of community." },
			{ "house_mist_night", "You are in Mist at night. This residential area is where you live and you are in front of your house. The neighborhood is quiet and peaceful under the moonlight, with soft glowing street lamps illuminating the paths. The sounds of crickets and distant conversations create a calming ambiance. The stars above add to the tranquil feeling of home." },

			// Il Mheg
			{ "il_mheg_day", "You are in Il Mheg during the day. This whimsical forest realm is home to the fae and is filled with vibrant colors and magical flora. The sounds of laughter and music from the fae can be heard, and the air is thick with enchantment. The scenery is constantly shifting, creating a sense of wonder and unpredictability." },
			{ "il_mheg_night", "You are in Il Mheg at night. The whimsical forest realm takes on an even more magical quality under the moonlight. The colors become more vibrant, and the sounds of the fae and nocturnal creatures create an enchanting atmosphere. The air feels charged with magic, and the shifting scenery adds to the sense of wonder." },
            
            // Kugane
            { "kugane_day", "You are in Kugane during the day. This beautiful Far Eastern port city showcases stunning architecture with its pagodas and bridges. The Shiokaze Hostelry, the markets, and the Ruby Bazaar are active with traders from across the sea. Cherry blossoms drift on the breeze, and you can see the ocean stretching to the horizon. The city blends traditional elegance with commercial prosperity." },
            { "kugane_night", "You are in Kugane at night. The port city is illuminated by countless paper lanterns creating a magical atmosphere. The architecture looks even more striking under the moonlight. The Shiokaze Hostelry's warm lights beckon travelers. The sound of shamisen music drifts from taverns, and the cool sea breeze carries the scent of salt and night-blooming flowers." },

			//Lake Land
			{ "lakeland_day", "You are in Lakeland during the day. This serene area is centered around a large lake with small islands and bridges. The sounds of water lapping and birds singing create a peaceful atmosphere. The architecture is elegant and blends harmoniously with nature. The purple trees are in full bloom. Adventurers can be seen fishing or relaxing by the water's edge." },
			{ "lakeland_night", "You are in Lakeland at night. The tranquil area takes on a dreamy quality under the moonlight, with soft glowing lanterns illuminating the paths and reflecting on the water. The sounds of nocturnal creatures and gentle waves create a calming ambiance. The purple trees are in full bloom. The stars above add to the magical feeling of this lakeside retreat." },

            // Limsa Lominsa
            { "limsa_lominsa_day", "You are in Limsa Lominsa Lower Decks during the day. This bustling port city is the maritime heart of Eorzea. The area features the Drowning Wench tavern, the Arcanists' Guild, and the Adventurers' Guild. The salty sea air and sounds of sailors fill the atmosphere. The sun reflects off the water, and merchants hawk their wares along the docks." },
            { "limsa_lominsa_night", "You are in Limsa Lominsa Lower Decks at night. The port city takes on a different character after dark - lanterns light the walkways, the taverns are lively with song and drink, and the gentle lapping of waves against the docks creates a peaceful ambiance. The Drowning Wench is particularly active with patrons sharing tales of their adventures." },

			// Limsa Lominsa Upper Decks
			{ "limsa_lominsa_upper_day", "You are in Limsa Lominsa Upper Decks during the day. This area of the port city is more upscale, with elegant architecture and a bustling market. The sounds of merchants and shoppers fill the air, along with the salty sea breeze. The Arcanists' Guild stands prominently, and you can see ships coming and going from the harbor. Hundreds of adventurers are regrouped around the central Aetheryte to talk and have a great moment." },
			{ "limsa_lominsa_upper_night", "You are in Limsa Lominsa Upper Decks at night. The area is illuminated by lanterns, creating a warm and inviting atmosphere. The market is quieter but still active with late-night vendors. The Arcanists' Guild glows softly, and the sound of waves and distant music from the Drowning Wench adds to the ambiance. Hundreds of adventurers are regrouped around the central Aetheryte to talk and have a great moment." },
            
			// Middle La Noscea
			{ "middle_la_noscea_day", "You are in Middle La Noscea during the day. This coastal area features a mix of rocky shores and sandy beaches. The sounds of waves crashing and seagulls create a lively atmosphere. The architecture is rustic and blends with the natural surroundings. Adventurers can be seen fishing, relaxing on the beach, or exploring the nearby caves." },
			{ "middle_la_noscea_night", "You are in Middle La Noscea at night. The coastal area takes on a serene quality under the moonlight, with the sound of gentle waves and distant nocturnal creatures creating a calming ambiance. The rustic architecture is silhouetted against the night sky, and the stars reflect on the water's surface. The beach is quiet, with only a few adventurers enjoying the peaceful night." },

			// New Gridania
			{ "new_gridania_day", "You are in New Gridania during the day. This vibrant city is built around the central Aetheryte Plaza, surrounded by shops, taverns, and the bustling market. The sounds of merchants, adventurers, and nature blend together to create a lively atmosphere. The architecture is a mix of wood and stone, with greenery integrated throughout. The city feels alive and welcoming." },
			{ "new_gridania_night", "You are in New Gridania at night. The city takes on a magical quality under the moonlight, with lanterns illuminating the Aetheryte Plaza and surrounding areas. The sounds of nocturnal creatures mix with the quieter activity of late-night merchants and adventurers. The architecture looks even more enchanting at night, and the overall atmosphere is cozy and mystical." },

            // Old Gridania
            { "old_gridania_day", "You are in Old Gridania during the day. This ancient forest city is built among the great trees of the Black Shroud. The Amphitheatre stands prominently here, where important gatherings take place. The Botanists' Guild and Conjurers' Guild are nearby. Sunlight filters through the leaves creating dappled patterns on the wooden walkways. You can hear birds singing and the rustling of leaves." },
            { "old_gridania_night", "You are in Old Gridania at night. The forest city is peaceful under the moonlight, with soft glowing lanterns illuminating the wooden pathways. The Amphitheatre sits quiet and majestic. Nocturnal creatures of the forest can be heard, and the air is cool and fresh with the scent of pine and earth. The city feels mystical and serene." },

			// Solution Nine
			{ "solution_nine_day", "You are in Solution Nine during the day. This mysterious city is a massive, ultra-modern city, built with advanced technology and towering megastructures, it feels more like a cyber-futuristic metropolis than a medieval city, complete with neon lighting, sleek architecture, and artificial systems integrated into everyday life. Visually, it’s clean, imposing, and slightly unsettling—beautiful in its precision, but carrying an underlying sense of control and mystery." },
			{ "solution_nine_night", "You are in Solution Nine at night. This mysterious city transforms into a neon-lit wonderland, with glowing signs and holographic displays illuminating the streets. The hum of technology and distant sounds of nightlife create an energetic yet enigmatic atmosphere. The sleek architecture reflects the vibrant lights, and the city feels alive with possibilities and secrets." },
            
			//Tuliyollal
			{ "tuliyollal_day", "You are in Tuliyollal during the day. This city is built within a massive tree and features intricate wooden architecture. The sounds of nature blend with the quiet activity of the inhabitants. The city has a peaceful and secluded atmosphere, with sunlight filtering through the leaves creating a serene environment." },
			{ "tuliyollal_night", "You are in Tuliyollal at night. The city takes on a mystical quality under the moonlight, with soft glowing lanterns illuminating the wooden structures. The sounds of nocturnal creatures mix with the quiet activity of the inhabitants. The overall atmosphere is tranquil and enchanting, with a strong connection to nature." },

            // Ul'dah
            { "uldah_day", "You are in Ul'dah - Steps of Nald during the day. This wealthy desert city-state gleams under the harsh sun. The grand architecture reflects the city's prosperity from trade and gladiatorial combat. The Thaumaturges' Guild, the Quicksand tavern, and various merchant stalls surround you. The heat is intense, and sand occasionally blows through the streets. The atmosphere is bustling with commerce and ambition." },
            { "uldah_night", "You are in Ul'dah - Steps of Nald at night. The desert city cools down after sunset, and the streets take on a mysterious quality. Torches and magical lights illuminate the golden architecture. The Quicksand tavern is alive with music and chatter. The wealthy elite and common folk alike navigate the streets, and the night air carries the scent of exotic spices." },

			//Yak Tel
			{ "yaktel_day", "You are in Yak-Tel during the day. This forest is built into a massive cavern and features a unique blend of natural rock formations and constructed buildings. The atmosphere is serene and otherworldly, with the sound of dripping water and distant echoes creating a calming ambiance. The architecture is a mix of stone and wood, blending harmoniously with the cavern environment." },
			{ "yaktel_night", "You are in Yak-Tel at night. The forest takes on an even more mystical quality under the moonlight, with soft glowing lanterns illuminating the cavern. The sounds of nocturnal creatures and dripping water create a serene ambiance. The architecture looks even more enchanting at night, and the overall atmosphere is tranquil and magical." },
        };
    }
    
    private void InitializeLocationDisplayNames()
    {
        locationDisplayNames = new Dictionary<string, string>
        {
            // Map location keys to display names
            { "central_shroud", "Central Shroud" },
            { "eulmore", "Eulmore" },
            { "gold_saucer", "Gold Saucer" },
            { "house_mist", "Mist - Residential District" },
            { "mist", "Mist - Residential District" },
            { "il_mheg", "Il Mheg" },
            { "kugane", "Kugane" },
            { "lakeland", "Lakeland" },
            { "limsa_lominsa", "Limsa Lominsa - Lower Decks" },
            { "limsa_lominsa_upper_deck", "Limsa Lominsa - Upper Decks" },
            { "middle_la_noscea", "Middle La Noscea" },
            { "new_gridania", "New Gridania" },
            { "old_gridania", "Old Gridania" },
            { "solution_nine", "Solution Nine" },
            { "tuliyollal", "Tuliyollal" },
            { "uldah", "Ul'dah - Steps of Nald" },
            { "yaktel", "Yak T'el" }
        };
    }
    
    void Update()
    {
        if (Time.time >= nextCheckTime)
        {
            TryPerformRandomAction();
            ScheduleNextCheck();
        }
    }
    
    private void ScheduleNextCheck()
    {
        float interval = Random.Range(minCheckInterval, maxCheckInterval);
        nextCheckTime = Time.time + interval;
        
        if (showDebugLogs)
            Debug.Log($"[AIBehavior] Next check in {interval:F1} seconds");
    }
    
    private void TryPerformRandomAction()
    {
        // Check if we're allowed to perform an action
        if (!CanPerformAction())
        {
            if (showDebugLogs)
                Debug.Log("[AIBehavior] Cannot perform action - user is speaking or TTS is busy");
            return;
        }
        
        // Roll for location change
        if (Random.value <= locationChangeChance)
        {
            ChangeLocation();
        }
        else
        {
            if (showDebugLogs)
                Debug.Log("[AIBehavior] Skipping action this time");
        }
    }
    
    private bool CanPerformAction()
    {
        // Check if user is speaking
        if (sttClient != null && sttClient.IsSpeaking)
        {
            return false;
        }
        
        // Check if TTS is busy
        if (piperClient != null && piperClient.IsBusy)
        {
            return false;
        }
        
        return true;
    }
    
    private void ChangeLocation()
    {
        if (locations.Length == 0)
        {
            Debug.LogWarning("[AIBehavior] No locations defined!");
            return;
        }
        
        // Pick a different location than current
        string newLocation = currentLocation;
        if (locations.Length > 1)
        {
            do
            {
                newLocation = locations[Random.Range(0, locations.Length)];
            } while (newLocation == currentLocation);
        }
        else
        {
            newLocation = locations[0];
        }
        
        currentLocation = newLocation;
        
        if (showDebugLogs)
            Debug.Log($"[AIBehavior] Changing location to: {currentLocation}");
        
        // Update background
        UpdateBackground();
        
        // Update AI's location context
        UpdateLocationContext();
        
        // Update location display text
        UpdateLocationDisplay();
        
        // Say something
        if (piperClient != null && locationChangeMessages.Length > 0)
        {
            string message = locationChangeMessages[Random.Range(0, locationChangeMessages.Length)];
            piperClient.Enqueue(message);
            
            if (showDebugLogs)
                Debug.Log($"[AIBehavior] Saying: {message}");
        }
    }
    
    public void ChangeToLocation(string locationKey)
    {
        if (string.IsNullOrEmpty(locationKey))
        {
            Debug.LogWarning("[AIBehavior] Invalid location key");
            return;
        }
        
        currentLocation = locationKey;
        
        if (showDebugLogs)
            Debug.Log($"[AIBehavior] Changing to location: {currentLocation}");
        
        UpdateBackground();
        UpdateLocationContext();
        UpdateLocationDisplay();
        
        // Say a random response
        if (piperClient != null && locationCommandResponses.Length > 0)
        {
            string response = locationCommandResponses[Random.Range(0, locationCommandResponses.Length)];
            piperClient.Enqueue(response);
        }
    }
    
    public void ChangeToRandomLocation()
    {
        if (locations.Length == 0)
        {
            Debug.LogWarning("[AIBehavior] No locations defined!");
            return;
        }
        
        // Pick any random location (can be same as current)
        string newLocation = locations[Random.Range(0, locations.Length)];
        currentLocation = newLocation;
        
        if (showDebugLogs)
            Debug.Log($"[AIBehavior] Random location change to: {currentLocation}");
        
        UpdateBackground();
        UpdateLocationContext();
        UpdateLocationDisplay();
        
        // Say a random response
        if (piperClient != null && locationCommandResponses.Length > 0)
        {
            string response = locationCommandResponses[Random.Range(0, locationCommandResponses.Length)];
            piperClient.Enqueue(response);
        }
    }
    
    private void UpdateBackground()
    {
        if (backgroundImage == null)
        {
            Debug.LogWarning("[AIBehavior] Background Image not assigned!");
            return;
        }
        
        // Get current time of day - calculate it ourselves to ensure it's always available
        string timeOfDay = GetCurrentTimeOfDay();
        
        // Build full background name: location + "_" + timeOfDay
        string backgroundName = currentLocation + "_" + timeOfDay;
        
        if (showDebugLogs)
            Debug.Log($"[AIBehavior] Loading background: {backgroundResourcesPath}{backgroundName}");
        
        // Load sprite from Resources
        Sprite backgroundSprite = Resources.Load<Sprite>(backgroundResourcesPath + backgroundName);
        
        if (backgroundSprite != null)
        {
            backgroundImage.sprite = backgroundSprite;
            if (showDebugLogs)
                Debug.Log($"[AIBehavior] Background updated to: {backgroundName}");
        }
        else
        {
            Debug.LogWarning($"[AIBehavior] Background sprite not found: {backgroundResourcesPath}{backgroundName}");
        }
    }
    
    /// <summary>
    /// Calculate current time of day (day/night) based on game hour
    /// </summary>
    private string GetCurrentTimeOfDay()
    {
        // First try to use TimeManagement's static variable if available
        if (!string.IsNullOrEmpty(TimeManagement.currentLight))
        {
            return TimeManagement.currentLight;
        }
        
        // Fallback: calculate it ourselves using the same logic as TimeManagement
        var now = System.DateTime.Now;
        float secondsIntoHour = now.Minute * 60f + now.Second + now.Millisecond / 1000f;
        float hourProgress = secondsIntoHour / 3600f;
        float gameMinutesInDay = hourProgress * 1440f;
        int gameHour = (int)(gameMinutesInDay / 60f);
        
        return (gameHour >= 7 && gameHour < 19) ? "day" : "night";
    }
    
    /// <summary>
    /// Manually trigger a location change (for testing or external calls)
    /// </summary>
    [ContextMenu("Force Location Change")]
    public void ForceLocationChange()
    {
        if (CanPerformAction())
        {
            ChangeLocation();
        }
        else
        {
            Debug.Log("[AIBehavior] Cannot force location change - user is speaking or TTS is busy");
        }
    }
    
    /// <summary>
    /// Update background without changing location (useful when time of day changes)
    /// </summary>
    public void RefreshBackground()
    {
        if (showDebugLogs)
            Debug.Log($"[AIBehavior] Refreshing background for time of day change at location: {currentLocation}");
        
        UpdateBackground();
        UpdateLocationContext(); // Also update location context when time changes
        UpdateLocationDisplay(); // Update display with time of day
    }
    
    /// <summary>
    /// Update the AI's knowledge of the current location by sending context to OllamaClient
    /// </summary>
    private void UpdateLocationContext()
    {
        if (ollamaClient == null)
        {
            if (showDebugLogs)
                Debug.LogWarning("[AIBehavior] OllamaClient not assigned, cannot update location context");
            return;
        }
        
        // Get current time of day
        string timeOfDay = GetCurrentTimeOfDay();
        
        // Build full location key
        string locationKey = currentLocation + "_" + timeOfDay;
        
        // Get location description from dictionary
        if (locationDescriptions.TryGetValue(locationKey, out string description))
        {
            ollamaClient.UpdateLocationContext(description);
            
            if (showDebugLogs)
                Debug.Log($"[AIBehavior] Updated AI location context: {locationKey}");
        }
        else
        {
            // Fallback if location not in dictionary
            string fallbackDescription = $"You are currently in {currentLocation.Replace("_", " ")} during {timeOfDay}time.";
            ollamaClient.UpdateLocationContext(fallbackDescription);
            
            if (showDebugLogs)
                Debug.LogWarning($"[AIBehavior] No description found for {locationKey}, using fallback");
        }
    }
    
    /// <summary>
    /// Update the UI text display showing the current location name
    /// </summary>
    private void UpdateLocationDisplay()
    {
        if (locationNameText == null)
        {
            return; // Silently skip if no text component assigned
        }
        
        // Get display name from dictionary
        if (locationDisplayNames.TryGetValue(currentLocation, out string displayName))
        {
            locationNameText.text = displayName;
            
            if (showDebugLogs)
                Debug.Log($"[AIBehavior] Location display updated to: {displayName}");
        }
        else
        {
            // Fallback: format the location key nicely
            string fallbackName = currentLocation.Replace("_", " ");
            // Capitalize first letter of each word
            fallbackName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(fallbackName);
            locationNameText.text = fallbackName;
            
            if (showDebugLogs)
                Debug.LogWarning($"[AIBehavior] No display name found for '{currentLocation}', using fallback: {fallbackName}");
        }
    }
}
