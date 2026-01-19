using System;
using UnityEngine;

public static class WavDecoder
{
    // Supports: RIFF WAV, PCM 16-bit, mono or stereo
    public static AudioClip ToAudioClip(byte[] wavBytes, string clipName = "wav_clip")
    {
        if (wavBytes == null || wavBytes.Length < 44)
            return null;

        // WAV header parsing
        int channels = BitConverter.ToInt16(wavBytes, 22);
        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);

        if (bitsPerSample != 16)
        {
            Debug.LogError("[WavDecoder] Only 16-bit PCM WAV supported. Found: " + bitsPerSample);
            return null;
        }

        // find "data" chunk (more robust than assuming 44 bytes header)
        int dataChunkOffset = -1;
        int dataChunkSize = -1;

        for (int i = 12; i < wavBytes.Length - 8; i++)
        {
            // "data"
            if (wavBytes[i] == 'd' && wavBytes[i + 1] == 'a' && wavBytes[i + 2] == 't' && wavBytes[i + 3] == 'a')
            {
                dataChunkOffset = i + 8;
                dataChunkSize = BitConverter.ToInt32(wavBytes, i + 4);
                break;
            }
        }

        if (dataChunkOffset < 0 || dataChunkSize <= 0)
        {
            Debug.LogError("[WavDecoder] WAV data chunk not found.");
            return null;
        }

        int samplesCount = dataChunkSize / 2; // 16-bit = 2 bytes
        float[] samples = new float[samplesCount];

        int offset = dataChunkOffset;
        for (int i = 0; i < samplesCount; i++)
        {
            short sample = BitConverter.ToInt16(wavBytes, offset);
            samples[i] = sample / 32768f;
            offset += 2;
        }

        int totalFrames = samplesCount / channels;

        var clip = AudioClip.Create(clipName, totalFrames, channels, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
