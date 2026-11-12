using JackSharp;
using NAudio.Wave;

namespace Jack.NAudio;

/// <summary>
/// Plays an NAudio <see cref="IWaveProvider"/> through a <see cref="Processor"/>'s audio outputs, one channel per output,
/// in the order they were registered.
/// </summary>
/// <remarks>
/// The processor is the caller's: <see cref="Play"/> activates it if it isn't yet, but stopping only stops the sound,
/// and other code (an <see cref="AudioIn"/>, say) can share it. The provider is read on JACK's real-time thread, so it
/// should be quick and allocation-free: buffer anything slow (a file, a network stream) ahead, with a
/// <see cref="BufferedWaveProvider"/>. It must give 32-bit float samples at the server's rate, with a channel per output.
/// </remarks>
public sealed class AudioOut : IWavePlayer
{
    private readonly Processor processor;
    private readonly ProcessCallback callback;
    private IWaveProvider? source;
    private volatile PlaybackState state = PlaybackState.Stopped;
    private float volume = 1;
    private byte[] bytes = Array.Empty<byte>();
    private bool disposed;

    public AudioOut(Processor processor)
    {
        this.processor = processor;
        callback = Process;
        processor.Process += callback;
    }

    /// <summary>What the provider must give: 32-bit float, the server's rate, a channel per audio output.</summary>
    public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(processor.SampleRate, processor.AudioOutputs.Count);

    public PlaybackState PlaybackState => state;

    public float Volume
    {
        get => volume;
        set => volume = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "Volume runs from 0 to 1.");
    }

    /// <summary>Raised when playback stops: by <see cref="Stop"/>, or when the provider runs out (then from a thread-pool
    /// thread).</summary>
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider)
    {
        WaveFormat format = waveProvider.WaveFormat, wanted = OutputWaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
            throw new ArgumentException($"JACK plays 32-bit float samples, not {format}: convert it first (ToSampleProvider().ToWaveProvider()).", nameof(waveProvider));
        if (format.Channels != wanted.Channels)
            throw new ArgumentException($"The processor has {wanted.Channels} audio outputs, and the provider {format.Channels} channels.", nameof(waveProvider));
        if (format.SampleRate != wanted.SampleRate)
            throw new ArgumentException($"The JACK server runs at {wanted.SampleRate} Hz, and the provider at {format.SampleRate}: resample it first.", nameof(waveProvider));
        source = waveProvider;
        state = PlaybackState.Stopped;
        bytes = new byte[processor.BufferSize * format.BlockAlign];
    }

    public void Play()
    {
        if (source == null)
            throw new InvalidOperationException("Init the player before playing.");
        if (!processor.IsActive)
            processor.Activate();
        state = PlaybackState.Playing;
    }

    public void Pause()
    {
        if (state == PlaybackState.Playing)
            state = PlaybackState.Paused;
    }

    public void Stop()
    {
        if (state == PlaybackState.Stopped)
            return;
        state = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    // The real-time thread.
    private void Process(ProcessContext context)
    {
        ReadOnlySpan<AudioPort> outputs = context.AudioOutputs;
        int frames = context.Frames, channels = outputs.Length;
        IWaveProvider? from = source;
        if (state != PlaybackState.Playing || from == null || channels == 0 || from.WaveFormat.Channels != channels)
        {
            foreach (AudioPort port in outputs)
                port.GetBuffer(context).Clear();
            return;
        }
        int wanted = frames * channels * sizeof(float);
        if (bytes.Length < wanted)
            bytes = new byte[wanted];   // Only if the server's buffer size grew.
        int read = from.Read(bytes, 0, wanted);
        if (read < wanted)
            Array.Clear(bytes, read, wanted - read);
        ReadOnlySpan<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, wanted));
        float gain = volume;
        for (int c = 0; c < channels; c++)
        {
            Span<float> to = outputs[c].GetBuffer(context);
            for (int n = 0, i = c; n < frames; n++, i += channels)
                to[n] = samples[i] * gain;
        }
        if (read == 0)
        {
            // The provider has run out: stop, as NAudio's players do, telling listeners off this thread.
            state = PlaybackState.Stopped;
            ThreadPool.QueueUserWorkItem(_ => PlaybackStopped?.Invoke(this, new StoppedEventArgs()));
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        processor.Process -= callback;
        Stop();
    }
}
