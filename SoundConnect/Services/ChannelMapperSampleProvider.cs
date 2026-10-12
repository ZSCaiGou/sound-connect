using NAudio.Wave;

namespace SoundConnect.Services;

/// <summary>把采样统一到目标声道数：单声道复制，多声道取前几路。</summary>
public sealed class ChannelMapperSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int sourceChannels;
    private float[] buffer = [];

    public ChannelMapperSampleProvider(ISampleProvider source, int targetChannels)
    {
        this.source = source;
        sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate,
            targetChannels
        );
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] destination, int offset, int count)
    {
        var targetChannels = WaveFormat.Channels;
        var frames = count / targetChannels;
        var needed = frames * sourceChannels;
        if (buffer.Length < needed)
        {
            buffer = new float[needed];
        }

        var read = source.Read(buffer, 0, needed);
        var readFrames = read / sourceChannels;
        for (var frame = 0; frame < readFrames; frame++)
        {
            for (var channel = 0; channel < targetChannels; channel++)
            {
                var sourceChannel = sourceChannels == 1 ? 0 : Math.Min(channel, sourceChannels - 1);
                destination[offset + frame * targetChannels + channel] =
                    buffer[frame * sourceChannels + sourceChannel];
            }
        }
        return readFrames * targetChannels;
    }
}
