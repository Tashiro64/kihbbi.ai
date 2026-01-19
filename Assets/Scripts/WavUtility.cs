using System;
using System.IO;
using UnityEngine;

public static class WavUtility
{
    /// <summary>
    /// Convert AudioClip to WAV byte array (for saving or sending to server)
    /// </summary>
    public static byte[] FromAudioClip(AudioClip clip)
    {
        if (clip == null)
            return null;

        var samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            int sampleRate = clip.frequency;
            int channels = clip.channels;
            int bitsPerSample = 16;

            // RIFF header
            writer.Write(new char[4] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + samples.Length * 2); // file size - 8
            writer.Write(new char[4] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[4] { 'f', 'm', 't', ' ' });
            writer.Write(16); // chunk size
            writer.Write((short)1); // PCM format
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8); // byte rate
            writer.Write((short)(channels * bitsPerSample / 8)); // block align
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(new char[4] { 'd', 'a', 't', 'a' });
            writer.Write(samples.Length * 2); // data size

            // PCM samples
            foreach (float sample in samples)
            {
                short s = (short)(Mathf.Clamp(sample, -1f, 1f) * 32767f);
                writer.Write(s);
            }

            return stream.ToArray();
        }
    }

    /// <summary>
    /// Convert WAV byte array to AudioClip (for playing received audio)
    /// </summary>
    public static AudioClip ToAudioClip(byte[] wavBytes, string clipName = "wav")
    {
        if (wavBytes == null || wavBytes.Length < 44)
            throw new ArgumentException("Invalid WAV data.");

        int channels = BitConverter.ToInt16(wavBytes, 22);
        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);

        if (bitsPerSample != 16)
            throw new NotSupportedException("Only 16-bit PCM WAV is supported.");

        int dataSize = BitConverter.ToInt32(wavBytes, 40);
        int dataStartIndex = 44;
        dataSize = Mathf.Min(dataSize, wavBytes.Length - dataStartIndex);

        int sampleCount = dataSize / 2;
        float[] samples = new float[sampleCount];
        const float scale = 1f / 32768f;

        int offset = dataStartIndex;
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(wavBytes, offset);
            samples[i] = sample * scale;
            offset += 2;
        }

        int totalSamplesPerChannel = sampleCount / channels;

        AudioClip clip = AudioClip.Create(
            clipName,
            totalSamplesPerChannel,
            channels,
            sampleRate,
            false
        );

        clip.SetData(samples, 0);
        return clip;
    }
}
