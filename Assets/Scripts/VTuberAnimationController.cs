using UnityEngine;
using DG.Tweening;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;

public class VTuberAnimationController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Live2D model GameObject")]
    public GameObject live2dModel;
    
    [Tooltip("Reference to PiperClient to detect when TTS is playing")]
    public PiperClient piperClient;
    
    [Tooltip("Reference to OllamaClient to get emotion data")]
    public OllamaClient ollamaClient;
    
    [Header("Live2D Parameters")]
    [Tooltip("Name of the mouth open parameter in your Live2D model. Can be the display name (e.g., '哔 张开和闭合') or ID (e.g., 'ParamMouthOpenY'). Leave as is to auto-detect.")]
    public string mouthOpenParamName = "ParamMouthOpenY";
    
    [Tooltip("How much to open the mouth (0-1)")]
    [Range(0f, 1f)]
    public float mouthOpenAmount = 0.8f;
    
    [Tooltip("Speed of mouth animation (talking frequency in Hz)")]
    [Range(1f, 10f)]
    public float mouthAnimationSpeed = 5f;
    
    [Header("Idle Animation Settings")]
    [Tooltip("Enable random idle animations")]
    public bool enableIdleAnimations = true;
    
    [Tooltip("Minimum seconds between idle animations")]
    public float minIdleInterval = 2f;
    
    [Tooltip("Maximum seconds between idle animations")]
    public float maxIdleInterval = 5f;
    
    [Tooltip("Bounce animation intensity")]
    public float bounceIntensity = 0.05f;
    
    [Tooltip("Rotation animation intensity (degrees)")]
    public float rotationIntensity = 3f;
    
    [Tooltip("Horizontal movement intensity")]
    public float horizontalMoveIntensity = 0.03f;
    
    [Header("Debug")]
    public bool showDebugLogs = true;
    
    // Private variables
    private CubismParameter mouthParameter;
    private CubismParameter mouthFormParameter;
    private CubismParameter eyeLeftParameter;
    private CubismParameter eyeRightParameter;
    private CubismParameter eyeBallXParameter;
    private CubismParameter eyeBallYParameter;
    private CubismParameter browLeftParameter;
    private CubismParameter browRightParameter;
    private CubismParameter bodyAngleXParameter;
    private CubismParameter rightArmParameter;
    private CubismParameter leftArmParameter;
    private CubismModel cubismModel;
    private float targetMouthOpen = 0f;
    private float currentMouthOpen = 0f;
    private float nextIdleAnimationTime;
    private float nextBlinkTime;
    private float nextEyeMoveTime;
    private float targetBodyAngleX = 0f;
    private float currentBodyAngleX = 0f;
    private float startBodyAngleX = 0f;
    private float bodyAngleXDuration = 1f;
    private float bodyAngleXElapsed = 0f;
    private float nextBodyAngleXChangeTime = 0f;
    
    // Arm animation state variables
    private float targetRightArm = 0f;
    private float currentRightArm = 0f;
    private float startRightArm = 0f;
    private float rightArmDuration = 1f;
    private float rightArmElapsed = 0f;
    private float nextRightArmChangeTime = 0f;
    
    private float targetLeftArm = 0f;
    private float currentLeftArm = 0f;
    private float startLeftArm = 0f;
    private float leftArmDuration = 1f;
    private float leftArmElapsed = 0f;
    private float nextLeftArmChangeTime = 0f;
    
    private bool isBlinking = false;
    private bool isMovingEyes = false;
    private bool isEmotionActive = false;
    private bool isAnimatingEyeEmotion = false;
    private bool isAngryEmotionActive = false;
    private bool isHappyEmotionActive = false;
    private bool isAnimatingMouthFormEmotion = false;
    private Coroutine currentBlinkCoroutine = null;
    private Coroutine currentEyeMoveCoroutine = null;
    private Tweener eyeLeftTween = null;
    private Tweener eyeRightTween = null;
    private Tweener browLeftTween = null;
    private Tweener browRightTween = null;
    private Tweener mouthFormTween = null;
    
    // Static emotion - set once per AI response
    public static string currentEmotion = "neutral";
    
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private bool isAnimating = false;
    private bool wasSpeakingLastFrame = false;
    private float nextMouthChangeTime = 0f;
    private float mouthChangeInterval = 0.08f; // Change target every 80ms for natural talking
    
    void Start()
    {
        if (live2dModel == null)
        {
            Debug.LogError("[VTuberAnimation] Live2D model not assigned!");
            enabled = false;
            return;
        }
        
        if (piperClient == null)
        {
            Debug.LogWarning("[VTuberAnimation] PiperClient not assigned, lip sync won't work!");
        }
        
        // Find the CubismModel - check assigned GameObject and its children
        var model = live2dModel.GetComponent<CubismModel>();
        if (model == null)
        {
            Debug.LogWarning("[VTuberAnimation] CubismModel not found on assigned GameObject, searching children...");
            model = live2dModel.GetComponentInChildren<CubismModel>();
        }
        
        if (model != null)
        {
            cubismModel = model;
            Debug.Log($"[VTuberAnimation] Found CubismModel on: {model.gameObject.name}");
            var parameters = model.Parameters;
            
            // First, try to find by exact ID match
            foreach (var param in parameters)
            {
                if (param.Id == mouthOpenParamName)
                {
                    mouthParameter = param;
                    Debug.Log($"[VTuberAnimation] Found mouth parameter by ID: {mouthOpenParamName}");
                    break;
                }
            }
            
            // If not found, try to find by display name
            if (mouthParameter == null)
            {
                foreach (var param in parameters)
                {
                    if (param.name == mouthOpenParamName)
                    {
                        mouthParameter = param;
                        Debug.Log($"[VTuberAnimation] Found mouth parameter by name: {mouthOpenParamName}");
                        break;
                    }
                }
            }
            
            // If still not found, search for common mouth parameter patterns
            if (mouthParameter == null)
            {
                foreach (var param in parameters)
                {
                    string paramName = param.name.ToLower();
                    string paramId = param.Id.ToLower();
                    
                    if (paramName.Contains("mouth") || paramId.Contains("mouth") || 
                        paramName.Contains("哔") || paramId.Contains("mouthopen"))
                    {
                        mouthParameter = param;
                        Debug.Log($"[VTuberAnimation] Auto-detected mouth parameter: {param.name} (ID: {param.Id})");
                        break;
                    }
                }
            }
            
            if (mouthParameter == null)
            {
                Debug.LogWarning($"[VTuberAnimation] Mouth parameter '{mouthOpenParamName}' not found! Available parameters:");
                foreach (var param in parameters)
                {
                    Debug.Log($"  - Name: '{param.name}' | ID: '{param.Id}' | Min: {param.MinimumValue} | Max: {param.MaximumValue} | Current: {param.Value}");
                }
            }
            else
            {
                Debug.Log($"[VTuberAnimation] ✅ Mouth parameter ready: Name='{mouthParameter.name}', ID='{mouthParameter.Id}', Range={mouthParameter.MinimumValue} to {mouthParameter.MaximumValue}");
            }
            
            // Find eye parameters for blinking
            foreach (var param in parameters)
            {
                if (param.Id == "ParamEyeLOpen")
                {
                    eyeLeftParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found left eye parameter: {param.Id}");
                }
                else if (param.Id == "ParamEyeROpen")
                {
                    eyeRightParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found right eye parameter: {param.Id}");
                }
                else if (param.Id == "ParamEyeBallX")
                {
                    eyeBallXParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found eye ball X parameter: {param.Id}");
                }
                else if (param.Id == "ParamEyeBallY")
                {
                    eyeBallYParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found eye ball Y parameter: {param.Id}");
                }
                else if (param.Id == "ParamBrowLY")
                {
                    browLeftParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found left brow parameter: {param.Id}");
                }
                else if (param.Id == "ParamBrowRY")
                {
                    browRightParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found right brow parameter: {param.Id}");
                }
                else if (param.Id == "ParamMouthForm")
                {
                    mouthFormParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found mouth form parameter: {param.Id}");
                }
                else if (param.Id == "ParamBodyAngleX")
                {
                    bodyAngleXParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found body angle X parameter: {param.Id}");
                }
                else if (param.Id == "Param")
                {
                    rightArmParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found right arm parameter: {param.Id}");
                }
                else if (param.Id == "Param5")
                {
                    leftArmParameter = param;
                    Debug.Log($"[VTuberAnimation] ✅ Found left arm parameter: {param.Id}");
                }
            }
            
            if (eyeLeftParameter == null || eyeRightParameter == null)
            {
                Debug.LogWarning($"[VTuberAnimation] Eye parameters not found - blinking disabled");
            }
            
            if (eyeBallXParameter == null || eyeBallYParameter == null)
            {
                Debug.LogWarning($"[VTuberAnimation] Eye ball parameters not found - eye movement disabled");
            }
            
            if (browLeftParameter == null || browRightParameter == null)
            {
                Debug.LogWarning($"[VTuberAnimation] Brow parameters not found - brow animations disabled");
            }
            
            if (mouthFormParameter == null)
            {
                Debug.LogWarning($"[VTuberAnimation] Mouth form parameter not found - angry emotion mouth form disabled");
            }
            else
            {
                // Initialize mouth form to 0 (neutral)
                mouthFormParameter.Value = 0f;
                Debug.Log($"[VTuberAnimation] ✅ Initialized ParamMouthForm to 0 (neutral)");
            }
            
            if (bodyAngleXParameter == null)
            {
                Debug.LogWarning($"[VTuberAnimation] Body angle X parameter not found - body rotation idle animation disabled");
            }
            else
            {
                // Initialize body angle to 0
                bodyAngleXParameter.Value = 0f;
                currentBodyAngleX = 0f;
                targetBodyAngleX = 0f;
                nextBodyAngleXChangeTime = Time.time + 0.5f; // Start first change in 0.5s
                Debug.Log($"[VTuberAnimation] ✅ Initialized ParamBodyAngleX to 0");
            }
            
            // Initialize arm parameters
            if (rightArmParameter != null)
            {
                rightArmParameter.Value = 0f;
                currentRightArm = 0f;
                targetRightArm = 0f;
                nextRightArmChangeTime = Time.time + Random.Range(0.5f, 2f);
                Debug.Log($"[VTuberAnimation] ✅ Initialized Param (right arm) to 0");
            }
            if (leftArmParameter != null)
            {
                leftArmParameter.Value = 0f;
                currentLeftArm = 0f;
                targetLeftArm = 0f;
                nextLeftArmChangeTime = Time.time + Random.Range(0.5f, 2f);
                Debug.Log($"[VTuberAnimation] ✅ Initialized Param5 (left arm) to 0");
            }
        }
        else
        {
            Debug.LogError("[VTuberAnimation] CubismModel component not found on live2dModel or its children! Make sure you assigned the correct GameObject.");
        }
        
        // Store original transform - use the assigned GameObject for animations
        originalPosition = live2dModel.transform.localPosition;
        originalRotation = live2dModel.transform.localRotation;
        
        Debug.Log($"[VTuberAnimation] Animating GameObject: {live2dModel.name}");
        
        // Schedule first idle animation
        ScheduleNextIdleAnimation();
        
        // Schedule first eye blink
        ScheduleNextBlink();
        
        // Schedule first eye movement
        ScheduleNextEyeMove();
    }
    
    void OnDestroy()
    {
        // Kill all tweens when destroyed
        if (live2dModel != null)
        {
            live2dModel.transform.DOKill();
        }
        
        // Kill eye emotion tweens
        eyeLeftTween?.Kill();
        eyeRightTween?.Kill();
        browLeftTween?.Kill();
        browRightTween?.Kill();
        mouthFormTween?.Kill();
    }
    
    void Update()
    {
        // Update idle animations
        if (enableIdleAnimations && Time.time >= nextIdleAnimationTime && !isAnimating)
        {
            PerformRandomIdleAnimation();
            ScheduleNextIdleAnimation();
        }
        
        // Update eye blinks (skip if emotion is active)
        if (eyeLeftParameter != null && eyeRightParameter != null && !isBlinking && !isEmotionActive && Time.time >= nextBlinkTime)
        {
            currentBlinkCoroutine = StartCoroutine(PerformBlink());
        }
        
        // Update eye movement
        if (eyeBallXParameter != null && eyeBallYParameter != null && !isMovingEyes && Time.time >= nextEyeMoveTime)
        {
            currentEyeMoveCoroutine = StartCoroutine(PerformEyeMove());
        }
    }
    
    void LateUpdate()
    {
        // Update lip sync in LateUpdate to override any other scripts (like CubismMouthController)
        UpdateLipSync();
        
        // Update body angle X animation
        UpdateBodyAngleX();
        
        // Update arm animations
        UpdateRightArm();
        UpdateLeftArm();
    }
    
    private void UpdateLipSync()
    {
        if (mouthParameter == null)
        {
            if (showDebugLogs && Time.frameCount % 300 == 0) // Log every 5 seconds
            {
                Debug.LogWarning("[VTuberAnimation] Mouth parameter is null - cannot animate!");
            }
            return;
        }
        
        // Check if TTS is playing
        bool isSpeaking = false;
        if (piperClient != null)
        {
            isSpeaking = piperClient.IsBusy;
        }
        else
        {
            if (showDebugLogs && Time.frameCount % 300 == 0) // Log every 5 seconds
            {
                Debug.LogWarning("[VTuberAnimation] PiperClient is null - assign it in Inspector!");
            }
        }
        
        // Set target mouth position
        if (isSpeaking)
        {
            // Pick a new random target value at intervals for natural varied talking
            if (Time.time >= nextMouthChangeTime)
            {
                // Random mouth opening between 0.0 and mouthOpenAmount
                targetMouthOpen = Random.Range(0f, mouthOpenAmount);
                
                // Vary the interval slightly for more natural rhythm
                mouthChangeInterval = Random.Range(0.06f, 0.12f); // 60-120ms between changes
                nextMouthChangeTime = Time.time + mouthChangeInterval;
            }
        }
        else
        {
            targetMouthOpen = 0f;
            nextMouthChangeTime = 0f; // Reset for next speaking session
        }
        
        // Smoothly interpolate to target - slower when closing for natural finish
        float lerpSpeed = (targetMouthOpen == 0f && currentMouthOpen > 0.05f) ? 8f : 25f;
        currentMouthOpen = Mathf.Lerp(currentMouthOpen, targetMouthOpen, Time.deltaTime * lerpSpeed);
        
        // Apply to Live2D parameter
        mouthParameter.Value = currentMouthOpen;
        
        // Debug logging - detect state changes
        if (showDebugLogs)
        {
            // Log when speaking state changes
            if (isSpeaking && !wasSpeakingLastFrame)
            {
                Debug.Log($"[VTuberAnimation] ▶️ Started Speaking! Current emotion: {currentEmotion}");
            }
            else if (!isSpeaking && wasSpeakingLastFrame)
            {
                Debug.Log($"[VTuberAnimation] ⏹️ Stopped Speaking!");
            }
        }
        
        // Handle emotion-based animations
        if (eyeLeftParameter != null && eyeRightParameter != null)
        {
            // Debug emotion state every frame when speaking or emotion active
            if (showDebugLogs && (isSpeaking || isEmotionActive))
            {
                if (Time.frameCount % 60 == 0) // Every second at 60fps
                {
                    Debug.Log($"[VTuberAnimation] 🎭 Emotion State: isSpeaking={isSpeaking}, currentEmotion={currentEmotion}, isEmotionActive={isEmotionActive}, eyeLeft={eyeLeftParameter.Value}, eyeRight={eyeRightParameter.Value}");
                }
            }
            
            // Apply emotion while speaking
            if (isSpeaking)
            {
                if (currentEmotion == "surprised" && !isEmotionActive)
                {
                    // Cancel any ongoing blink that might interfere
                    if (currentBlinkCoroutine != null)
                    {
                        StopCoroutine(currentBlinkCoroutine);
                        currentBlinkCoroutine = null;
                        isBlinking = false;
                        if (showDebugLogs)
                        {
                            Debug.Log("[VTuberAnimation] 🛑 Stopped blink coroutine for emotion");
                        }
                    }
                    
                    isEmotionActive = true;
                    isAnimatingEyeEmotion = true;
                    
                    // Kill any existing eye and brow tweens
                    eyeLeftTween?.Kill();
                    eyeRightTween?.Kill();
                    browLeftTween?.Kill();
                    browRightTween?.Kill();
                    
                    // Animate eyes from current value to 1.5 over 0.3s
                    float currentLeft = eyeLeftParameter.Value;
                    float currentRight = eyeRightParameter.Value;
                    
                    eyeLeftTween = DOTween.To(() => eyeLeftParameter.Value, x => eyeLeftParameter.Value = x, 1.5f, 0.3f)
                        .SetEase(Ease.OutQuad)
                        .OnComplete(() => isAnimatingEyeEmotion = false);
                    
                    eyeRightTween = DOTween.To(() => eyeRightParameter.Value, x => eyeRightParameter.Value = x, 1.5f, 0.3f)
                        .SetEase(Ease.OutQuad);
                    
                    // Animate eyebrows from current value to 1.0 (raised) over 0.3s
                    if (browLeftParameter != null && browRightParameter != null)
                    {
                        browLeftTween = DOTween.To(() => browLeftParameter.Value, x => browLeftParameter.Value = x, 1f, 0.3f)
                            .SetEase(Ease.OutQuad);
                        
                        browRightTween = DOTween.To(() => browRightParameter.Value, x => browRightParameter.Value = x, 1f, 0.3f)
                            .SetEase(Ease.OutQuad);
                    }
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[VTuberAnimation] 😲 Surprised - animating eyes from {currentLeft:F2} to 1.5 and brows to 1.0 over 0.3s");
                    }
                }
                else if (currentEmotion == "surprised" && isEmotionActive && !isAnimatingEyeEmotion)
                {
                    // Keep eyes at 1.5 and brows at 1.0 after animation completes (don't force during animation)
                    eyeLeftParameter.Value = 1.5f;
                    eyeRightParameter.Value = 1.5f;
                    
                    if (browLeftParameter != null && browRightParameter != null)
                    {
                        browLeftParameter.Value = 1f;
                        browRightParameter.Value = 1f;
                    }
                }
            }
            // Animate back to normal when TTS stops
            else if (!isSpeaking && isEmotionActive)
            {
                AnimateEyesBackToNormal();
            }
        }
        
        // Handle angry and happy emotions (mouth form)
        if (mouthFormParameter != null)
        {
            if (isSpeaking)
            {
                // Angry emotion: mouth form to -1
                if (currentEmotion == "angry" && !isAngryEmotionActive)
                {
                    isAngryEmotionActive = true;
                    isHappyEmotionActive = false;
                    isAnimatingMouthFormEmotion = true;
                    
                    // Kill any existing mouth form tween
                    mouthFormTween?.Kill();
                    
                    // Animate mouth form from current value to -1 over 0.3s
                    float currentMouthForm = mouthFormParameter.Value;
                    
                    mouthFormTween = DOTween.To(() => mouthFormParameter.Value, x => mouthFormParameter.Value = x, -1f, 0.3f)
                        .SetEase(Ease.OutQuad)
                        .OnComplete(() => isAnimatingMouthFormEmotion = false);
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[VTuberAnimation] 😠 Angry - animating mouth form from {currentMouthForm:F2} to -1 over 0.3s");
                    }
                }
                else if (currentEmotion == "angry" && isAngryEmotionActive && !isAnimatingMouthFormEmotion)
                {
                    // Keep mouth form at -1 after animation completes (don't force during animation)
                    mouthFormParameter.Value = -1f;
                }
                // Happy emotion: mouth form to +1
                else if (currentEmotion == "happy" && !isHappyEmotionActive)
                {
                    isHappyEmotionActive = true;
                    isAngryEmotionActive = false;
                    isAnimatingMouthFormEmotion = true;
                    
                    // Kill any existing mouth form tween
                    mouthFormTween?.Kill();
                    
                    // Animate mouth form from current value to +1 over 0.3s
                    float currentMouthForm = mouthFormParameter.Value;
                    
                    mouthFormTween = DOTween.To(() => mouthFormParameter.Value, x => mouthFormParameter.Value = x, 1f, 0.3f)
                        .SetEase(Ease.OutQuad)
                        .OnComplete(() => isAnimatingMouthFormEmotion = false);
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[VTuberAnimation] 😊 Happy - animating mouth form from {currentMouthForm:F2} to +1 over 0.3s");
                    }
                }
                else if (currentEmotion == "happy" && isHappyEmotionActive && !isAnimatingMouthFormEmotion)
                {
                    // Keep mouth form at +1 after animation completes (don't force during animation)
                    mouthFormParameter.Value = 1f;
                }
                // Other emotions (neutral, sad, surprised): mouth form to 0
                else if (currentEmotion != "angry" && currentEmotion != "happy" && (isAngryEmotionActive || isHappyEmotionActive))
                {
                    // Reset to neutral mouth form
                    if (showDebugLogs)
                    {
                        Debug.Log($"[VTuberAnimation] 😐 {currentEmotion} - resetting mouth form to 0");
                    }
                    AnimateMouthFormBackToNormal();
                }
            }
            // Animate back to normal when TTS stops
            else if (!isSpeaking && (isAngryEmotionActive || isHappyEmotionActive))
            {
                AnimateMouthFormBackToNormal();
            }
            // Ensure mouth form stays at 0 when not speaking and not in angry/happy emotion
            else if (!isSpeaking && !isAngryEmotionActive && !isHappyEmotionActive)
            {
                mouthFormParameter.Value = 0f;
            }
        }
        
        wasSpeakingLastFrame = isSpeaking;
        
        // Force unwanted parameters to 0
        if (cubismModel != null)
        {
            ForceParameterToZero("waitao");
            ForceParameterToZero("jkbao");
            ForceParameterToZero("maweir");
            ForceParameterToZero("maweil");
            ForceParameterToZero("shoubing");
            ForceParameterToZero("quinzi");
            ForceParameterToZero("qqy");
            ForceParameterToZero("aixin");
            ForceParameterToZero("heilian");
            ForceParameterToZero("mz");
            ForceParameterToZero("ParamBreath");
        }
    }
    
    private void ForceParameterToZero(string paramId)
    {
        foreach (var param in cubismModel.Parameters)
        {
            if (param.Id.ToLower() == paramId.ToLower())
            {
                param.Value = 0f;
                return;
            }
        }
    }
    
    private void AnimateEyesBackToNormal()
    {
        if (showDebugLogs)
        {
            Debug.Log($"[VTuberAnimation] Animating eyes and brows back to normal from {eyeLeftParameter.Value:F2}");
        }
        
        isEmotionActive = false;
        
        // Kill any existing eye and brow tweens
        eyeLeftTween?.Kill();
        eyeRightTween?.Kill();
        browLeftTween?.Kill();
        browRightTween?.Kill();
        
        // Animate eyes from current value back to 1.0 over 0.3s
        eyeLeftTween = DOTween.To(() => eyeLeftParameter.Value, x => eyeLeftParameter.Value = x, 1f, 0.3f)
            .SetEase(Ease.InOutQuad);
        
        eyeRightTween = DOTween.To(() => eyeRightParameter.Value, x => eyeRightParameter.Value = x, 1f, 0.3f)
            .SetEase(Ease.InOutQuad);
        
        // Animate eyebrows from current value back to 0.0 over 0.3s
        if (browLeftParameter != null && browRightParameter != null)
        {
            browLeftTween = DOTween.To(() => browLeftParameter.Value, x => browLeftParameter.Value = x, 0f, 0.3f)
                .SetEase(Ease.InOutQuad);
            
            browRightTween = DOTween.To(() => browRightParameter.Value, x => browRightParameter.Value = x, 0f, 0.3f)
                .SetEase(Ease.InOutQuad);
        }
    }
    
    private void AnimateMouthFormBackToNormal()
    {
        if (showDebugLogs)
        {
            Debug.Log($"[VTuberAnimation] Animating mouth form back to normal from {mouthFormParameter.Value:F2}");
        }
        
        isAngryEmotionActive = false;
        isHappyEmotionActive = false;
        
        // Kill any existing mouth form tween
        mouthFormTween?.Kill();
        
        // Animate mouth form from current value back to 0 over 0.3s
        mouthFormTween = DOTween.To(() => mouthFormParameter.Value, x => mouthFormParameter.Value = x, 0f, 0.3f)
            .SetEase(Ease.InOutQuad);
    }
    
    private void ScheduleNextIdleAnimation()
    {
        float interval = Random.Range(minIdleInterval, maxIdleInterval);
        nextIdleAnimationTime = Time.time + interval;
        
        if (showDebugLogs)
            Debug.Log($"[VTuberAnimation] Next idle animation in {interval:F1}s");
    }
    
    private void ScheduleNextBlink()
    {
        float interval = Random.Range(1f, 6f);
        nextBlinkTime = Time.time + interval;
        
        if (showDebugLogs)
            Debug.Log($"[VTuberAnimation] Next blink in {interval:F1}s");
    }
    
    private System.Collections.IEnumerator PerformBlink()
    {
        isBlinking = true;
        
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Blinking");
        
        float duration = 0.12f; // Half of 0.24s (close + open)
        float elapsed = 0f;
        
        // Close eyes (1 → 0)
        while (elapsed < duration)
        {
            // Double check emotion isn't active (in case it became active during blink)
            if (isEmotionActive)
            {
                if (showDebugLogs)
                    Debug.Log("[VTuberAnimation] Blink interrupted by emotion");
                isBlinking = false;
                yield break;
            }
            
            float t = elapsed / duration;
            float value = Mathf.Lerp(1f, 0f, t);
            eyeLeftParameter.Value = value;
            eyeRightParameter.Value = value;
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        eyeLeftParameter.Value = 0f;
        eyeRightParameter.Value = 0f;
        
        elapsed = 0f;
        
        // Open eyes (0 → 1)
        while (elapsed < duration)
        {
            // Double check emotion isn't active
            if (isEmotionActive)
            {
                if (showDebugLogs)
                    Debug.Log("[VTuberAnimation] Blink interrupted by emotion during open");
                isBlinking = false;
                yield break;
            }
            
            float t = elapsed / duration;
            float value = Mathf.Lerp(0f, 1f, t);
            eyeLeftParameter.Value = value;
            eyeRightParameter.Value = value;
            elapsed += Time.deltaTime;
            yield return null;
        }
        
        eyeLeftParameter.Value = 1f;
        eyeRightParameter.Value = 1f;
        
        isBlinking = false;
        ScheduleNextBlink();
    }
    
    private void ScheduleNextEyeMove()
    {
        float interval = Random.Range(1f, 2.4f); // 20% shorter delay
        nextEyeMoveTime = Time.time + interval;
        
        if (showDebugLogs)
            Debug.Log($"[VTuberAnimation] Next eye movement in {interval:F1}s");
    }
    
    private System.Collections.IEnumerator PerformEyeMove()
    {
        isMovingEyes = true;
        
        // Random target positions - X and Y are independent
        float targetX = Random.Range(-1f, 1f);
        float targetY = Random.Range(-1f, 1f);
        
        // Independent durations for X and Y movement
        float durationX = Random.Range(0.05f, 0.19f);
        float durationY = Random.Range(0.09f, 0.28f);
        
        if (showDebugLogs)
            Debug.Log($"[VTuberAnimation] Moving eyes to ({targetX:F2}, {targetY:F2}) - X in {durationX:F2}s, Y in {durationY:F2}s");
        
        float startX = eyeBallXParameter.Value;
        float startY = eyeBallYParameter.Value;
        float elapsedX = 0f;
        float elapsedY = 0f;
        bool xComplete = false;
        bool yComplete = false;
        
        while (!xComplete || !yComplete)
        {
            // Animate X independently
            if (!xComplete)
            {
                elapsedX += Time.deltaTime;
                if (elapsedX >= durationX)
                {
                    eyeBallXParameter.Value = targetX;
                    xComplete = true;
                }
                else
                {
                    float tX = elapsedX / durationX;
                    eyeBallXParameter.Value = Mathf.Lerp(startX, targetX, tX);
                }
            }
            
            // Animate Y independently
            if (!yComplete)
            {
                elapsedY += Time.deltaTime;
                if (elapsedY >= durationY)
                {
                    eyeBallYParameter.Value = targetY;
                    yComplete = true;
                }
                else
                {
                    float tY = elapsedY / durationY;
                    eyeBallYParameter.Value = Mathf.Lerp(startY, targetY, tY);
                }
            }
            
            yield return null;
        }
        
        isMovingEyes = false;
        ScheduleNextEyeMove();
    }
    
    private void UpdateBodyAngleX()
    {
        if (bodyAngleXParameter == null)
            return;
        
        // Check if it's time to pick a new target
        if (Time.time >= nextBodyAngleXChangeTime)
        {
            // Save starting angle
            startBodyAngleX = currentBodyAngleX;
            
            // Pick new random target between -8 and 8
            targetBodyAngleX = Random.Range(-8f, 8f);
            
            // Pick new random duration between 0.5 and 2 seconds
            bodyAngleXDuration = Random.Range(0.5f, 2f);
            
            // Reset elapsed time
            bodyAngleXElapsed = 0f;
            
            // Schedule next change with a small pause (0.1-0.5s after this animation completes)
            nextBodyAngleXChangeTime = Time.time + bodyAngleXDuration + Random.Range(0.1f, 0.5f);
            
            if (showDebugLogs)
                Debug.Log($"[VTuberAnimation] ParamBodyAngleX: {startBodyAngleX:F2}° → {targetBodyAngleX:F2}° over {bodyAngleXDuration:F2}s");
        }
        
        // Animate towards target
        if (bodyAngleXElapsed < bodyAngleXDuration)
        {
            bodyAngleXElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(bodyAngleXElapsed / bodyAngleXDuration);
            
            // Apply easing (InOutSine)
            float easedT = t < 0.5f 
                ? (1f - Mathf.Cos(t * Mathf.PI)) / 2f 
                : (Mathf.Sin((t - 0.5f) * Mathf.PI) + 1f) / 2f;
            
            // Lerp from START to TARGET, not from current to target
            currentBodyAngleX = Mathf.Lerp(startBodyAngleX, targetBodyAngleX, easedT);
        }
        else
        {
            // Ensure we reach the target exactly
            currentBodyAngleX = targetBodyAngleX;
        }
        
        // Apply to parameter every frame in LateUpdate
        bodyAngleXParameter.Value = currentBodyAngleX;
    }
    
    private void UpdateRightArm()
    {
        if (rightArmParameter == null)
            return;
        
        // Check if it's time to pick a new target
        if (Time.time >= nextRightArmChangeTime)
        {
            // Save starting position
            startRightArm = currentRightArm;
            
            // Pick new random target between 0 and 14
            targetRightArm = Random.Range(0f, 14f);
            
            // Pick new random duration between 0.5 and 1 seconds
            rightArmDuration = Random.Range(0.5f, 1f);
            
            // Reset elapsed time
            rightArmElapsed = 0f;
            
            // Schedule next change with a pause (0.3-1s after this animation completes)
            nextRightArmChangeTime = Time.time + rightArmDuration + Random.Range(0.3f, 1f);
            
            if (showDebugLogs)
                Debug.Log($"[VTuberAnimation] Right arm (Param): {startRightArm:F2} → {targetRightArm:F2} over {rightArmDuration:F2}s");
        }
        
        // Animate towards target
        if (rightArmElapsed < rightArmDuration)
        {
            rightArmElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(rightArmElapsed / rightArmDuration);
            
            // Apply easing (InOutSine)
            float easedT = t < 0.5f 
                ? (1f - Mathf.Cos(t * Mathf.PI)) / 2f 
                : (Mathf.Sin((t - 0.5f) * Mathf.PI) + 1f) / 2f;
            
            currentRightArm = Mathf.Lerp(startRightArm, targetRightArm, easedT);
        }
        else
        {
            // Ensure we reach the target exactly
            currentRightArm = targetRightArm;
        }
        
        // Apply to parameter every frame in LateUpdate
        rightArmParameter.Value = currentRightArm;
    }
    
    private void UpdateLeftArm()
    {
        if (leftArmParameter == null)
            return;
        
        // Check if it's time to pick a new target
        if (Time.time >= nextLeftArmChangeTime)
        {
            // Save starting position
            startLeftArm = currentLeftArm;
            
            // Pick new random target between 0 and -14
            targetLeftArm = Random.Range(0f, -14f);
            
            // Pick new random duration between 0.5 and 2 seconds
            leftArmDuration = Random.Range(0.5f, 1f);
            
            // Reset elapsed time
            leftArmElapsed = 0f;
            
            // Schedule next change with a pause (0.3-1s after this animation completes)
            nextLeftArmChangeTime = Time.time + leftArmDuration + Random.Range(0.3f, 1f);
            
            if (showDebugLogs)
                Debug.Log($"[VTuberAnimation] Left arm (Param5): {startLeftArm:F2} → {targetLeftArm:F2} over {leftArmDuration:F2}s");
        }
        
        // Animate towards target
        if (leftArmElapsed < leftArmDuration)
        {
            leftArmElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(leftArmElapsed / leftArmDuration);
            
            // Apply easing (InOutSine)
            float easedT = t < 0.5f 
                ? (1f - Mathf.Cos(t * Mathf.PI)) / 2f 
                : (Mathf.Sin((t - 0.5f) * Mathf.PI) + 1f) / 2f;
            
            currentLeftArm = Mathf.Lerp(startLeftArm, targetLeftArm, easedT);
        }
        else
        {
            // Ensure we reach the target exactly
            currentLeftArm = targetLeftArm;
        }
        
        // Apply to parameter every frame in LateUpdate
        leftArmParameter.Value = currentLeftArm;
    }
    
    private void PerformRandomIdleAnimation()
    {
        // Choose a random animation type
        int animationType = Random.Range(0, 3);
        
        switch (animationType)
        {
            case 0:
                PerformBounceAnimation();
                break;
            case 1:
                PerformRotationAnimation();
                break;
            case 2:
                PerformCombinedAnimation();
                break;
        }
    }
    
    private void PerformBounceAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing bounce animation");
        
        isAnimating = true;
        
        // Randomize bounce intensity (1x to 3x the base value)
        float randomBounce = Random.Range(bounceIntensity, bounceIntensity * 3f);
        
        Sequence seq = DOTween.Sequence();
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y + randomBounce, 0.4f).SetEase(Ease.InOutSine));
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y, 0.4f).SetEase(Ease.InOutSine));
        seq.OnComplete(() => isAnimating = false);
    }
    
    private void PerformRotationAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing sway animation");
        
        isAnimating = true;
        
        // Randomize rotation intensity (0.33x to 1.67x the base value, e.g., 1-5 degrees when base is 3)
        float randomRotation = Random.Range(rotationIntensity / 3f, rotationIntensity * 3f / 3f);
        
        // Random starting direction
        float direction = Random.value > 0.5f ? 1f : -1f;
        float targetRotation = randomRotation * direction;
        
        Sequence seq = DOTween.Sequence();
        // Sway to one side
        seq.Append(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles + new Vector3(0, 0, targetRotation), 0.8f).SetEase(Ease.InOutSine));
        // Sway smoothly to the other side
        seq.Append(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles + new Vector3(0, 0, -targetRotation), 0.99f).SetEase(Ease.InOutSine));
        // Return to center
        seq.Append(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles, 0.88f).SetEase(Ease.InOutSine));
        seq.OnComplete(() => isAnimating = false);
    }
    

    
    private void PerformCombinedAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing combined animation");
        
        isAnimating = true;
        
        // Randomize intensities for variety
        float randomBounce = Random.Range(bounceIntensity, bounceIntensity * 3f);
        float randomRotation = Random.Range(rotationIntensity / 3f, rotationIntensity * 3f / 3f);
        
        // Small bounce + slight sway
        float rotDirection = Random.value > 0.5f ? 1f : -1f;
        float targetRotation = (randomRotation * 0.5f) * rotDirection;
        
        Sequence seq = DOTween.Sequence();
        
        // Move up and rotate smoothly
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y + randomBounce * 0.7f, 0.4f).SetEase(Ease.InOutSine));
        seq.Join(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles + new Vector3(0, 0, targetRotation), 0.4f).SetEase(Ease.InOutSine));
        
        // Return to original smoothly
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y, 0.4f).SetEase(Ease.InOutSine));
        seq.Join(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles, 0.4f).SetEase(Ease.InOutSine));
        
        seq.OnComplete(() => isAnimating = false);
    }
    
    /// <summary>
    /// Trigger a specific animation on demand
    /// </summary>
    public void TriggerAnimation(string animationType)
    {
        if (isAnimating) return;
        
        switch (animationType.ToLower())
        {
            case "bounce":
                PerformBounceAnimation();
                break;
            case "rotate":
            case "sway":
                PerformRotationAnimation();
                break;
            case "combined":
                PerformCombinedAnimation();
                break;
            default:
                Debug.LogWarning($"[VTuberAnimation] Unknown animation type: {animationType}");
                break;
        }
    }
    
    /// <summary>
    /// Test mouth animation manually
    /// </summary>
    [ContextMenu("Test Mouth Open")]
    public void TestMouthOpen()
    {
        if (mouthParameter == null)
        {
            Debug.LogError("[VTuberAnimation] Mouth parameter not initialized!");
            return;
        }
        
        StartCoroutine(TestMouthCoroutine());
    }
    
    private System.Collections.IEnumerator TestMouthCoroutine()
    {
        Debug.Log($"[VTuberAnimation] Testing mouth - Opening to {mouthOpenAmount}");
        
        // Open mouth
        float elapsed = 0f;
        float duration = 0.5f;
        while (elapsed < duration)
        {
            mouthParameter.Value = Mathf.Lerp(0f, mouthOpenAmount, elapsed / duration);
            elapsed += Time.deltaTime;
            yield return null;
        }
        mouthParameter.Value = mouthOpenAmount;
        
        Debug.Log($"[VTuberAnimation] Mouth fully open at {mouthParameter.Value}");
        yield return new UnityEngine.WaitForSeconds(1f);
        
        // Close mouth
        elapsed = 0f;
        while (elapsed < duration)
        {
            mouthParameter.Value = Mathf.Lerp(mouthOpenAmount, 0f, elapsed / duration);
            elapsed += Time.deltaTime;
            yield return null;
        }
        mouthParameter.Value = 0f;
        
        Debug.Log($"[VTuberAnimation] Mouth closed at {mouthParameter.Value}");
    }
    
    /// <summary>
    /// Test idle animation manually
    /// </summary>
    [ContextMenu("Test Idle Animation")]
    public void TestIdleAnimation()
    {
        if (!isAnimating)
        {
            PerformCombinedAnimation();
        }
    }
    
    /// <summary>
    /// Reset to original position and rotation
    /// </summary>
    [ContextMenu("Reset Transform")]
    public void ResetTransform()
    {
        if (live2dModel != null)
        {
            live2dModel.transform.DOKill();
            live2dModel.transform.localPosition = originalPosition;
            live2dModel.transform.localRotation = originalRotation;
            isAnimating = false;
            
            if (showDebugLogs)
                Debug.Log("[VTuberAnimation] Transform reset to original");
        }
    }
}
