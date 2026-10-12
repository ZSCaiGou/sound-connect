using NAudio.Wave;

namespace SoundConnect.Services;

/// <summary>
/// 音效混合通道：可以同时叠多个音效，各自从自己的采样数组里读，读完自动退场。
/// 48kHz 立体声浮点，和主混音同格式。
/// </summary>
public sealed class EffectMixerSampleProvider : ISampleProvider
{
    private sealed class Voice
    {
        public required float[] Samples { get; init; }
        public double Gain { get; init; }
        public int Position { get; set; }
    }

    private readonly List<Voice> voices = [];
    private readonly object sync = new();

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(CachedEffect.SampleRate, CachedEffect.Channels);

    public int ActiveVoices
    {
        get
        {
            lock (sync)
            {
                return voices.Count;
            }
        }
    }

    public void Trigger(CachedEffect effect, double gain)
    {
        lock (sync)
        {
            voices.Add(new Voice { Samples = effect.Samples, Gain = gain });
        }
    }

    public void StopAll()
    {
        lock (sync)
        {
            voices.Clear();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        lock (sync)
        {
            for (var index = voices.Count - 1; index >= 0; index--)
            {
                var voice = voices[index];
                var remaining = voice.Samples.Length - voice.Position;
                var take = Math.Min(remaining, count);
                for (var sample = 0; sample < take; sample++)
                {
                    buffer[offset + sample] += (float)(voice.Samples[voice.Position + sample] * voice.Gain);
                }
                voice.Position += take;
                if (voice.Position >= voice.Samples.Length)
                {
                    voices.RemoveAt(index);
                }
            }
        }
        return count;
    }
}
