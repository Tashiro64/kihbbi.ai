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
    private float targetMouthOpen = 0f;
    private float currentMouthOpen = 0f;
    private float nextIdleAnimationTime;
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
    }
    
    void Update()
    {
        // Update idle animations
        if (enableIdleAnimations && Time.time >= nextIdleAnimationTime && !isAnimating)
        {
            PerformRandomIdleAnimation();
            ScheduleNextIdleAnimation();
        }
    }
    
    void LateUpdate()
    {
        // Update lip sync in LateUpdate to override any other scripts (like CubismMouthController)
        UpdateLipSync();
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
                Debug.Log($"[VTuberAnimation] ▶️ Started Speaking!");
            }
            else if (!isSpeaking && wasSpeakingLastFrame)
            {
                Debug.Log($"[VTuberAnimation] ⏹️ Stopped Speaking!");
            }
        }
        
        wasSpeakingLastFrame = isSpeaking;
    }
    
    private void ScheduleNextIdleAnimation()
    {
        float interval = Random.Range(minIdleInterval, maxIdleInterval);
        nextIdleAnimationTime = Time.time + interval;
        
        if (showDebugLogs)
            Debug.Log($"[VTuberAnimation] Next idle animation in {interval:F1}s");
    }
    
    private void PerformRandomIdleAnimation()
    {
        // Choose a random animation type
        int animationType = Random.Range(0, 4);
        
        switch (animationType)
        {
            case 0:
                PerformBounceAnimation();
                break;
            case 1:
                PerformRotationAnimation();
                break;
            case 2:
                PerformHorizontalMoveAnimation();
                break;
            case 3:
                PerformCombinedAnimation();
                break;
        }
    }
    
    private void PerformBounceAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing bounce animation");
        
        isAnimating = true;
        
        Sequence seq = DOTween.Sequence();
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y + bounceIntensity, 0.3f).SetEase(Ease.OutQuad));
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y, 0.3f).SetEase(Ease.InQuad));
        seq.OnComplete(() => isAnimating = false);
    }
    
    private void PerformRotationAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing rotation animation");
        
        isAnimating = true;
        
        // Random direction
        float direction = Random.value > 0.5f ? 1f : -1f;
        float targetRotation = rotationIntensity * direction;
        
        Sequence seq = DOTween.Sequence();
        seq.Append(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles + new Vector3(0, 0, targetRotation), 0.4f).SetEase(Ease.OutQuad));
        seq.Append(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles, 0.4f).SetEase(Ease.InQuad));
        seq.OnComplete(() => isAnimating = false);
    }
    
    private void PerformHorizontalMoveAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing horizontal move animation");
        
        isAnimating = true;
        
        // Random direction
        float direction = Random.value > 0.5f ? 1f : -1f;
        float targetX = originalPosition.x + (horizontalMoveIntensity * direction);
        
        Sequence seq = DOTween.Sequence();
        seq.Append(live2dModel.transform.DOLocalMoveX(targetX, 0.5f).SetEase(Ease.InOutQuad));
        seq.Append(live2dModel.transform.DOLocalMoveX(originalPosition.x, 0.5f).SetEase(Ease.InOutQuad));
        seq.OnComplete(() => isAnimating = false);
    }
    
    private void PerformCombinedAnimation()
    {
        if (showDebugLogs)
            Debug.Log("[VTuberAnimation] Performing combined animation");
        
        isAnimating = true;
        
        // Small bounce + slight rotation
        float rotDirection = Random.value > 0.5f ? 1f : -1f;
        float targetRotation = (rotationIntensity * 0.5f) * rotDirection;
        
        Sequence seq = DOTween.Sequence();
        
        // Move up and rotate
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y + bounceIntensity * 0.7f, 0.3f).SetEase(Ease.OutQuad));
        seq.Join(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles + new Vector3(0, 0, targetRotation), 0.3f).SetEase(Ease.OutQuad));
        
        // Return to original
        seq.Append(live2dModel.transform.DOLocalMoveY(originalPosition.y, 0.3f).SetEase(Ease.InQuad));
        seq.Join(live2dModel.transform.DOLocalRotate(originalRotation.eulerAngles, 0.3f).SetEase(Ease.InQuad));
        
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
                PerformRotationAnimation();
                break;
            case "move":
                PerformHorizontalMoveAnimation();
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
    
    void OnDestroy()
    {
        // Kill all tweens when destroyed
        if (live2dModel != null)
        {
            live2dModel.transform.DOKill();
        }
    }
}
