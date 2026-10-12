using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundConnect.Services;

/// <summary>
/// 一个已经解码好、常驻内存的音效：统一成 48kHz 立体声浮点。
/// 触发时只是从数组里拷数据，所以能做到按下就响。
/// </summary>
public sealed class CachedEffect
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public required string Name { get; init; }
    public required float[] Samples { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)(SampleRate * Channels));

    public static CachedEffect Load(string name, string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }
        if (provider.WaveFormat.Channels != Channels)
        {
            provider = new ChannelMapperSampleProvider(provider, Channels);
        }

        var samples = new List<float>();
        var buffer = new float[SampleRate * Channels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return new CachedEffect { Name = name, Samples = samples.ToArray() };
    }

    /// <summary>内置测试音，用来验证音效通道是不是通的，不依赖任何文件。</summary>
    public static CachedEffect CreateTestTone()
    {
        const int milliseconds = 400;
        var frames = SampleRate * milliseconds / 1000;
        var samples = new float[frames * Channels];
        var fadeFrames = SampleRate / 100;
        for (var frame = 0; frame < frames; frame++)
        {
            var envelope =
                Math.Min(1.0, frame / (double)fadeFrames)
                * Math.Min(1.0, (frames - frame) / (double)fadeFrames);
            var value = (float)(Math.Sin(2 * Math.PI * 1000 * frame / SampleRate) * envelope * 0.5);
            samples[frame * Channels] = value;
            samples[frame * Channels + 1] = value;
        }
        return new CachedEffect { Name = "测试音 1kHz", Samples = samples };
    }
}
