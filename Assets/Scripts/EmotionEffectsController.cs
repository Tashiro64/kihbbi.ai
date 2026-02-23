using UnityEngine;
using DG.Tweening;
using System.Collections.Generic;

/// <summary>
/// Manages visual emotion effects like blush, exclamation marks, sweat drops, etc.
/// Detects emotion keywords from text and displays corresponding visual effects.
/// </summary>
public class EmotionEffectsController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Reference to PiperClient to detect sentence emotions")]
    public PiperClient piperClient;
    
    [Tooltip("Parent object containing all emotion effect GameObjects (e.g., /ariu)")]
    public GameObject effectsParent;
    
    [Header("Effect GameObjects")]
    [Tooltip("Blush effect GameObject (should have a SpriteRenderer component)")]
    public GameObject blushEffect;
    
    [Tooltip("Angry effect GameObject (should have a SpriteRenderer component)")]
    public GameObject angryEffect;
    
    [Tooltip("Exclamation effect GameObject (for future use)")]
    public GameObject exclamationEffect;
    
    [Header("Blush Animation Settings")]
    [Tooltip("Duration for fade-in animation")]
    public float fadeInDuration = 0.7f;
    
    [Tooltip("Duration for fade-out animation")]
    public float fadeOutDuration = 0.7f;
    
    [Tooltip("Maximum opacity for blush effect")]
    [Range(0f, 1f)]
    public float blushMaxOpacity = 0.75f;
    
    [Header("Angry Animation Settings")]
    [Tooltip("Duration for zoom-in animation")]
    public float angryZoomInDuration = 0.5f;
    
    [Tooltip("Duration for zoom-out animation")]
    public float angryZoomOutDuration = 0.5f;
    
    [Tooltip("Starting scale for angry effect (should be 0 or very small)")]
    public float angryStartScale = 0f;
    
    [Tooltip("Target scale for angry effect when fully shown")]
    public float angryTargetScale = 1f;
    
    [Tooltip("Maximum opacity for angry effect")]
    [Range(0f, 1f)]
    public float angryMaxOpacity = 0.75f;
    
    [Header("Debug")]
    public bool showDebugLogs = true;
    
    // Private variables
    private SpriteRenderer blushSpriteRenderer;
    private Tweener blushTween;
    
    private SpriteRenderer angrySpriteRenderer;
    private Tween angryTween;
    private Vector3 angryOriginalScale;
    
    private bool isBlushActive = false;
    private bool isAngryActive = false;
    private bool isTTSPlaying = false;
    
    // Keywords for different effects
    private readonly HashSet<string> blushKeywords = new HashSet<string>
    {
        "blush", "blushes", "blushed", "blushing",
        "shy", "shyly", "embarrassed", "embarrassing",
        "flustered", "flustering", "bashful", "bashfully", 
    };
    
    void Start()
    {
        // Find effects parent if not assigned
        if (effectsParent == null && GameObject.Find("ariu") != null)
        {
            effectsParent = GameObject.Find("ariu");
        }
        
        // Find blush effect if not assigned
        if (blushEffect == null && effectsParent != null)
        {
            Transform blushTransform = effectsParent.transform.Find("Blush");
            if (blushTransform != null)
            {
                blushEffect = blushTransform.gameObject;
            }
        }
        
        // Find angry effect if not assigned
        if (angryEffect == null && effectsParent != null)
        {
            Transform angryTransform = effectsParent.transform.Find("Angry");
            if (angryTransform != null)
            {
                angryEffect = angryTransform.gameObject;
            }
        }
        
        // Setup blush effect components
        if (blushEffect != null)
        {
            SetupBlushEffect();
        }
        else
        {
            Debug.LogWarning("[EmotionEffects] Blush effect GameObject not found!");
        }
        
        // Setup angry effect components
        if (angryEffect != null)
        {
            SetupAngryEffect();
        }
        else
        {
            Debug.LogWarning("[EmotionEffects] Angry effect GameObject not found!");
        }
        
        // Subscribe to PiperClient events if available
        if (piperClient != null && piperClient.ollama != null)
        {
            piperClient.ollama.OnSentenceReady += HandleSentenceEmotion;
        }
    }
    
    void OnDestroy()
    {
        // Unsubscribe from events
        if (piperClient != null && piperClient.ollama != null)
        {
            piperClient.ollama.OnSentenceReady -= HandleSentenceEmotion;
        }
    }
    
    private void SetupBlushEffect()
    {
        // Get SpriteRenderer component
        blushSpriteRenderer = blushEffect.GetComponent<SpriteRenderer>();
        if (blushSpriteRenderer != null)
        {
            Color color = blushSpriteRenderer.color;
            color.a = 0f;
            blushSpriteRenderer.color = color;
            
            if (showDebugLogs)
            {
                Debug.Log("[EmotionEffects] Blush effect setup complete using SpriteRenderer");
            }
        }
        else
        {
            Debug.LogError("[EmotionEffects] Blush effect GameObject must have a SpriteRenderer component!");
        }
    }
    
    private void SetupAngryEffect()
    {
        // Get SpriteRenderer component
        angrySpriteRenderer = angryEffect.GetComponent<SpriteRenderer>();
        if (angrySpriteRenderer != null)
        {
            // Store original scale
            angryOriginalScale = angryEffect.transform.localScale;
            
            // Start invisible and scaled down
            Color color = angrySpriteRenderer.color;
            color.a = 0f;
            angrySpriteRenderer.color = color;
            angryEffect.transform.localScale = Vector3.zero;
            
            if (showDebugLogs)
            {
                Debug.Log("[EmotionEffects] Angry effect setup complete using SpriteRenderer");
            }
        }
        else
        {
            Debug.LogError("[EmotionEffects] Angry effect GameObject must have a SpriteRenderer component!");
        }
    }
    
    /// <summary>
    /// Called when a sentence is ready with its emotion text
    /// </summary>
    private void HandleSentenceEmotion(string sentenceText, string emotion)
    {
        if (string.IsNullOrWhiteSpace(sentenceText))
            return;
        
        // Check for angry emotion
        if (emotion.ToLower() == "angry")
        {
            if (showDebugLogs)
            {
                Debug.Log($"[EmotionEffects] 😠 Angry emotion detected for sentence: '{sentenceText}'");
            }
            ShowAngry();
        }
        
        // Check for blush keywords
        string lowerText = sentenceText.ToLower();
        bool shouldBlush = false;
        
        foreach (string keyword in blushKeywords)
        {
            if (lowerText.Contains(keyword))
            {
                shouldBlush = true;
                if (showDebugLogs)
                {
                    Debug.Log($"[EmotionEffects] 😊 Blush keyword detected: '{keyword}' in text: '{sentenceText}'");
                }
                break;
            }
        }
        
        if (shouldBlush)
        {
            ShowBlush();
        }
    }
    
    /// <summary>
    /// Shows the blush effect with fade-in animation
    /// </summary>
    public void ShowBlush()
    {
        if (blushEffect == null || blushSpriteRenderer == null)
        {
            Debug.LogWarning("[EmotionEffects] Cannot show blush - effect not assigned or missing SpriteRenderer!");
            return;
        }
        
        // Kill existing tween
        if (blushTween != null && blushTween.IsActive())
        {
            blushTween.Kill();
        }
        
        isBlushActive = true;
        isTTSPlaying = true;
        
        if (showDebugLogs)
        {
            Debug.Log("[EmotionEffects] 💖 Showing blush effect");
        }
        
        // Fade in to max opacity
        blushTween = blushSpriteRenderer.DOFade(blushMaxOpacity, fadeInDuration).SetEase(Ease.OutQuad);
    }
    
    /// <summary>
    /// Hides the blush effect with fade-out animation
    /// Call this when TTS is complete
    /// </summary>
    public void HideBlush()
    {
        if (!isBlushActive || blushEffect == null || blushSpriteRenderer == null)
            return;
        
        // Kill existing tween
        if (blushTween != null && blushTween.IsActive())
        {
            blushTween.Kill();
        }
        
        if (showDebugLogs)
        {
            Debug.Log("[EmotionEffects] 💨 Hiding blush effect");
        }
        
        // Fade out
        blushTween = blushSpriteRenderer.DOFade(0f, fadeOutDuration).SetEase(Ease.InQuad).OnComplete(() =>
        {
            isBlushActive = false;
        });
    }
    
    /// <summary>
    /// Shows the angry effect with zoom-in animation
    /// </summary>
    public void ShowAngry()
    {
        if (angryEffect == null || angrySpriteRenderer == null)
        {
            Debug.LogWarning("[EmotionEffects] Cannot show angry - effect not assigned or missing SpriteRenderer!");
            return;
        }
        
        // Kill existing tween
        if (angryTween != null && angryTween.IsActive())
        {
            angryTween.Kill();
        }
        
        isAngryActive = true;
        isTTSPlaying = true;
        
        if (showDebugLogs)
        {
            Debug.Log("[EmotionEffects] 😠 Showing angry effect");
        }
        
        // Reset to start state
        angryEffect.transform.localScale = angryOriginalScale * angryStartScale;
        Color color = angrySpriteRenderer.color;
        color.a = 0f;
        angrySpriteRenderer.color = color;
        
        // Create sequence for simultaneous zoom and fade
        Sequence seq = DOTween.Sequence();
        seq.Append(angryEffect.transform.DOScale(angryOriginalScale * angryTargetScale, angryZoomInDuration).SetEase(Ease.InOutBack));
        seq.Join(angrySpriteRenderer.DOFade(angryMaxOpacity, angryZoomInDuration).SetEase(Ease.InOutBack));
        
        angryTween = seq;
    }
    
    /// <summary>
    /// Hides the angry effect with zoom-out animation
    /// Call this when TTS is complete
    /// </summary>
    public void HideAngry()
    {
        if (!isAngryActive || angryEffect == null || angrySpriteRenderer == null)
            return;
        
        // Kill existing tween
        if (angryTween != null && angryTween.IsActive())
        {
            angryTween.Kill();
        }
        
        if (showDebugLogs)
        {
            Debug.Log("[EmotionEffects] 💨 Hiding angry effect");
        }
        
        // Create sequence for simultaneous zoom out and fade out
        Sequence seq = DOTween.Sequence();
        seq.Append(angryEffect.transform.DOScale(angryOriginalScale * angryStartScale, angryZoomOutDuration).SetEase(Ease.InOutBack));
        seq.Join(angrySpriteRenderer.DOFade(0f, angryZoomOutDuration).SetEase(Ease.InOutBack));
        seq.OnComplete(() =>
        {
            isAngryActive = false;
        });
        
        angryTween = seq;
    }
    
    /// <summary>
    /// Called when TTS playback completes
    /// </summary>
    public void OnTTSComplete()
    {
        isTTSPlaying = false;
        
        // Hide all active effects
        if (isBlushActive)
        {
            HideBlush();
        }
        
        if (isAngryActive)
        {
            HideAngry();
        }
    }
    
    void Update()
    {
        // Monitor TTS state from PiperClient
        if (piperClient != null)
        {
            bool currentlyPlaying = piperClient.IsBusy;
            
            // Detect TTS completion
            if (isTTSPlaying && !currentlyPlaying)
            {
                OnTTSComplete();
            }
            
            isTTSPlaying = currentlyPlaying;
        }
    }
}
