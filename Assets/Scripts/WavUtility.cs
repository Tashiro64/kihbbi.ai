using System;
using UnityEngine;

public static class WavUtility
{
    // Converts an AudioClip to WAV bytes (16-bit PCM)
    public static byte[] FromAudioClip(AudioClip clip)
    {
        if (clip == null) throw new ArgumentNullException(nameof(clip));

        int channels = clip.channels;
        int sampleRate = clip.frequency;
        int samplesCount = clip.samples;

        float[] samples = new float[samplesCount * channels];
        clip.GetData(samples, 0);

        // Convert float samples (-1..1) to 16-bit PCM
        byte[] pcm16 = new byte[samples.Length * 2];
        const float scale = 32767f;

        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Mathf.Clamp(samples[i] * scale, short.MinValue, short.MaxValue);
            byte[] b = BitConverter.GetBytes(s);
            pcm16[i * 2] = b[0];
            pcm16[i * 2 + 1] = b[1];
        }

        // WAV header (44 bytes)
        byte[] header = BuildWavHeader(channels, sampleRate, pcm16.Length);

        // Combine header + data
        byte[] wav = new byte[header.Length + pcm16.Length];
        Buffer.BlockCopy(header, 0, wav, 0, header.Length);
        Buffer.BlockCopy(pcm16, 0, wav, header.Length, pcm16.Length);

        return wav;
    }

    private static byte[] BuildWavHeader(int channels, int sampleRate, int dataLength)
    {
        byte[] header = new byte[44];

        int byteRate = sampleRate * channels * 2;
        int blockAlign = channels * 2;
        int fileSizeMinus8 = 36 + dataLength;

        // RIFF
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BitConverter.GetBytes(fileSizeMinus8).CopyTo(header, 4);
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';

        // fmt
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(header, 16);          // Subchunk1Size
        BitConverter.GetBytes((short)1).CopyTo(header, 20);    // PCM
        BitConverter.GetBytes((short)channels).CopyTo(header, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(header, 24);
        BitConverter.GetBytes(byteRate).CopyTo(header, 28);
        BitConverter.GetBytes((short)blockAlign).CopyTo(header, 32);
        BitConverter.GetBytes((short)16).CopyTo(header, 34);   // BitsPerSample

        // data
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BitConverter.GetBytes(dataLength).CopyTo(header, 40);

        return header;
    }
}