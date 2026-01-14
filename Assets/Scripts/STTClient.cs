using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class STTClient : MonoBehaviour
{
    [Header("STT Server")]
    public string sttUrl = "http://127.0.0.1:8007/stt";

    [Header("Recording")]
    public int recordSeconds = 4;
    public int sampleRate = 16000;
    public KeyCode pushToTalkKey = KeyCode.R;

    private string micDevice;
    private bool isRecording;
    private AudioClip clip;

    [Serializable]
    public class STTResponse
    {
        public string text;
        public string lang;
    }

    void Start()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("No microphone detected.");
            return;
        }

        micDevice = Microphone.devices[0];
        Debug.Log("Mic selected: " + micDevice);
        Debug.Log($"Press [{pushToTalkKey}] to record {recordSeconds}s and transcribe.");
    }

    void Update()
    {
        if (Input.GetKeyDown(pushToTalkKey) && !isRecording)
        {
            StartCoroutine(RecordThenTranscribe());
        }
    }

    IEnumerator RecordThenTranscribe()
    {
        isRecording = true;

        Debug.Log("Recording...");
        clip = Microphone.Start(micDevice, false, recordSeconds, sampleRate);

        // wait until the mic actually starts
        while (Microphone.GetPosition(micDevice) <= 0)
            yield return null;

        yield return new WaitForSeconds(recordSeconds);

        Microphone.End(micDevice);
        Debug.Log("Recording stopped.");

        if (clip == null)
        {
            Debug.LogError("Clip is null.");
            isRecording = false;
            yield break;
        }

        // Convert AudioClip -> WAV bytes
        byte[] wav = WavUtility.FromAudioClip(clip);

        // Send to STT server
        yield return StartCoroutine(SendWav(wav));

        isRecording = false;
    }

    IEnumerator SendWav(byte[] wavBytes)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio", wavBytes, "audio.wav", "audio/wav");

        using UnityWebRequest req = UnityWebRequest.Post(sttUrl, form);
        req.timeout = 120;

        Debug.Log("Sending audio to STT...");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("STT request failed: " + req.error);
            Debug.LogError("Server says: " + req.downloadHandler.text);
            yield break;
        }

        string json = req.downloadHandler.text;
        Debug.Log("Raw STT JSON: " + json);

        STTResponse res = JsonUtility.FromJson<STTResponse>(json);
        Debug.Log($"✅ TEXT: {res.text}");
        Debug.Log($"🌐 LANG: {res.lang}");
    }
}