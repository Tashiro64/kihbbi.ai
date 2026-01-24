using UnityEngine;
using TMPro;
using DG.Tweening;

public class UIManagement : MonoBehaviour
{

	public TextMeshProUGUI statusText;
	public static bool loaded = false;
	
    void Start()
    {
            // Cute bounce animation followed by subtle breathing loop
            statusText.transform.DOPunchScale(Vector3.one * 0.1f, 0.99f, 5, 0.5f)
                .OnComplete(() => {
                    // Subtle breathing effect after punch
                    statusText.transform.DOScale(0.55f, 1.3f).SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine);
                });
            
    }

    void Update()
    {
        if(PiperServerManager.PiperReady && WhisperServerManager.STTReady && !loaded)
        {
            statusText.text = "Everything is ready!";
            
            // Wait 1.5 seconds before hiding UI
            DOVirtual.DelayedCall(1.5f, () => {
                GameObject.Find("/UI/Canvas/back").SetActive(false);
                GameObject.Find("/UI/Canvas/logo").SetActive(false);
                GameObject.Find("/UI/Canvas/text").SetActive(false);
            });
            
            loaded = true;
        }
    }
}
