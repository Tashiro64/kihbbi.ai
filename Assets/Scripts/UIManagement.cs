using UnityEngine;
using UnityEngine.UI;

public class UIManagement : MonoBehaviour
{

	public Text statusText;
	public static bool loaded = false;
	
    void Start()
    {
        
    }

    void Update()
    {
        if(PiperServerManager.PiperReady && WhisperServerManager.STTReady && !loaded)
        {
            statusText.text = "All services are ready!";
			GameObject.Find("/UI/Canvas/back").SetActive(false);
			GameObject.Find("/UI/Canvas/text").SetActive(false);
            loaded = true;
        }
    }
}
