using JackSharp;
using NAudio.Wave;

namespace Jack.NAudio;

/// <summary>
/// Records a <see cref="Processor"/>'s audio inputs as NAudio wave data: 32-bit float, interleaved, a channel per input
/// in the order they were registered.
/// </summary>
/// <remarks>
/// The processor is the caller's: <see cref="StartRecording"/> activates it if it isn't yet, but stopping only stops the
/// recording. <see cref="DataAvailable"/> is raised on JACK's real-time thread, once per cycle, with the same buffer and
/// event arguments each time (nothing is allocated): copy the data out and return quickly, or queue it for another
/// thread.
/// </remarks>
public sealed class AudioIn : IWaveIn
{
    private readonly Processor processor;
    private readonly ProcessCallback callback;
    private volatile bool recording;
    private byte[] bytes = Array.Empty<byte>();
    private WaveInEventArgs? arguments;
    private bool disposed;

    public AudioIn(Processor processor)
    {
        this.processor = processor;
        callback = Process;
        processor.Process += callback;
    }

    public WaveFormat WaveFormat
    {
        get => WaveFormat.CreateIeeeFloatWaveFormat(processor.SampleRate, processor.AudioInputs.Count);
        set => throw new NotSupportedException("JACK records 32-bit float at the server's rate, a channel per input.");
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (!processor.IsActive)
            processor.Activate();
        recording = true;
    }

    public void StopRecording()
    {
        if (!recording)
            return;
        recording = false;
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    // The real-time thread.
    private void Process(ProcessContext context)
    {
        ReadOnlySpan<AudioPort> inputs = context.AudioInputs;
        int frames = context.Frames, channels = inputs.Length;
        if (!recording || channels == 0 || DataAvailable is not { } handler)
            return;
        int size = frames * channels * sizeof(float);
        if (arguments == null || arguments.BytesRecorded != size)
        {
            // Only when the server's buffer size (or the inputs) changed.
            bytes = new byte[size];
            arguments = new WaveInEventArgs(bytes, size);
        }
        Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, size));
        for (int c = 0; c < channels; c++)
        {
            Span<float> from = inputs[c].GetBuffer(context);
            for (int n = 0, i = c; n < frames; n++, i += channels)
                samples[i] = from[n];
        }
        handler(this, arguments);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        processor.Process -= callback;
        StopRecording();
    }
}
