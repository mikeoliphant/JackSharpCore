using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JackSharp;

/// <summary>A process callback: it runs once per cycle on JACK's real-time thread.</summary>
/// <remarks>It must fill every audio output it owns, and must not block, lock, allocate or wait on I/O: the cycle's
/// deadline is <see cref="Client.BufferSize"/> frames away. Exceptions are caught and counted
/// (<see cref="Processor.ProcessExceptions"/>), never passed to JACK.</remarks>
public delegate void ProcessCallback(ProcessContext context);

/// <summary>
/// A JACK client with ports and a real-time process callback: an instrument, an effect, a recorder, a player. Being a
/// <see cref="Client"/>, it can also see and patch the graph, its own ports and everyone else's.
/// </summary>
/// <remarks>
/// Register ports, set <see cref="Process"/>, then <see cref="Client.Activate"/>. Ports can be registered and
/// unregistered while it's active, and <see cref="Process"/> replaced at any time. Nothing in a cycle allocates:
/// ports' buffers are read and written in place, as spans.
/// </remarks>
public sealed class Processor : Client
{
    private readonly object portsLock = new();
    private volatile PortSet ports = PortSet.Empty;
    private volatile ProcessCallback? process;
    private long cycles;
    private long processExceptions;
    private volatile Exception? lastProcessException;
    private volatile int addedLatency = -1;
    private bool latencyCallbackSet;

    public unsafe Processor(string name, ClientOptions? options = null) : base(name, options)
    {
        int result = Native.jack_set_process_callback(Handle, &OnProcess, CallbackArgument);
        if (result != 0)
            throw new JackException($"Couldn't set the process callback of JACK client '{Name}' (error {result}).");
    }

    /// <summary>The process callback. Null leaves every output silent.</summary>
    public ProcessCallback? Process
    {
        get => process;
        set => process = value;
    }

    /// <summary>How many cycles have run since the processor was activated.</summary>
    public long Cycles => Interlocked.Read(ref cycles);

    /// <summary>How many times <see cref="Process"/> threw; <see cref="LastProcessException"/> is the latest.</summary>
    public long ProcessExceptions => Interlocked.Read(ref processExceptions);

    public Exception? LastProcessException => lastProcessException;

    internal IntPtr HandleForCycle => Handle;

    // ---- Latency ----

    /// <summary>
    /// The latency the processor adds, in frames, for JACK to report downstream: its outputs' capture latency is its
    /// inputs' plus this, and its inputs' playback latency is its outputs' plus this, so programs further along can
    /// compensate. -1 (the default) leaves latency to JACK, which passes it through unchanged. Setting it the first time
    /// must be before <see cref="Client.Activate"/>; after that it can change at any time.
    /// </summary>
    public int AddedLatency
    {
        get => addedLatency;
        set
        {
            if (value < -1)
                throw new ArgumentOutOfRangeException(nameof(value), "Latency is -1 (JACK's) or a number of frames.");
            if (value >= 0 && !latencyCallbackSet && IsActive)
                throw new InvalidOperationException("Set AddedLatency before activating: JACK takes a latency callback only then.");
            addedLatency = value;
            if (latencyCallbackSet && IsActive)
                Native.jack_recompute_total_latencies(Handle);
        }
    }

    protected override unsafe void OnActivating()
    {
        if (addedLatency >= 0 && !latencyCallbackSet)
        {
            int result = Native.jack_set_latency_callback(Handle, &OnLatency, CallbackArgument);
            if (result != 0)
                throw new JackException($"Couldn't set the latency callback of JACK client '{Name}' (error {result}).");
            latencyCallbackSet = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnLatency(int mode, IntPtr arg)
    {
        if (FromCallbackArgument<Processor>(arg) is not { } processor)
            return;
        EnterCallback();
        try { processor.SetLatencies((LatencyMode)mode); }
        catch (Exception) { /* Never unwind into libjack. */ }
        finally { LeaveCallback(); }
    }

    // JACK asks for the latencies through this processor: each output's capture latency is the inputs' plus what the
    // processor adds; each input's playback latency is the outputs' plus what it adds.
    private void SetLatencies(LatencyMode mode)
    {
        int added = Math.Max(0, addedLatency);
        PortSet set = ports;
        Port[] from = mode == LatencyMode.Capture ? set.InputsArray : set.OutputsArray;
        Port[] to = mode == LatencyMode.Capture ? set.OutputsArray : set.InputsArray;
        uint min = 0, max = 0;
        bool any = false;
        foreach (Port port in from)
        {
            LatencyRange range = port.GetLatency(mode);
            min = any ? Math.Min(min, range.Min) : range.Min;
            max = any ? Math.Max(max, range.Max) : range.Max;
            any = true;
        }
        var result = new LatencyRange(min + (uint)added, max + (uint)added);
        foreach (Port port in to)
            port.SetLatency(mode, result);
    }

    public IReadOnlyList<AudioPort> AudioInputs => ports.AudioInputs;
    public IReadOnlyList<AudioPort> AudioOutputs => ports.AudioOutputs;
    public IReadOnlyList<MidiPort> MidiInputs => ports.MidiInputs;
    public IReadOnlyList<MidiPort> MidiOutputs => ports.MidiOutputs;

    // ---- Ports ----

    /// <summary>Registers an audio port, named <c>client:name</c> in the graph.</summary>
    public AudioPort RegisterAudioPort(string name, Direction direction) =>
        Register(new AudioPort(this, name, direction), JackConstants.AudioType);

    /// <summary>Registers a MIDI port, named <c>client:name</c> in the graph.</summary>
    public MidiPort RegisterMidiPort(string name, Direction direction) =>
        Register(new MidiPort(this, name, direction), JackConstants.MidiType);

    private T Register<T>(T port, string type) where T : Port
    {
        lock (portsLock)
        {
            int flags = port.Direction == Direction.In ? JackConstants.PortIsInput : JackConstants.PortIsOutput;
            IntPtr handle = Native.jack_port_register(Handle, port.ShortName, type, new CULong((uint)flags), new CULong(0));
            if (handle == IntPtr.Zero)
                throw new JackException($"Couldn't register the JACK port '{Name}:{port.ShortName}' (is the name taken?).");
            port.Attach(handle);
            ports = ports.With(port);
        }
        return port;
    }

    /// <summary>
    /// Unregisters a port: it leaves the graph, losing its connections. While the processor is active, this waits for
    /// any cycle that may still be using the port to finish (at most a couple of cycles), so the process callback can
    /// never see a port that's gone.
    /// </summary>
    public void Unregister(Port port)
    {
        if (port.Owner != this)
            throw new ArgumentException("The port belongs to another processor.", nameof(port));
        lock (portsLock)
        {
            if (port.Handle == IntPtr.Zero)
                return;
            ports = ports.Without(port);
            if (IsActive)
                WaitForCycles(2);
            IntPtr handle = port.Detach();
            Native.jack_port_unregister(Handle, handle);
        }
    }

    // Waits until a number of cycles have started since now, or a moment longer than they should take.
    private void WaitForCycles(int count)
    {
        long target = Cycles + count;
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(50, 4000.0 * count * BufferSize / Math.Max(1, SampleRate)));
        while (Cycles < target && DateTime.UtcNow < deadline && IsActive)
            Thread.Sleep(1);
    }

    protected override void OnClosed()
    {
        lock (portsLock)
        {
            foreach (Port port in ports.All)
                port.Detach();
            ports = PortSet.Empty;
        }
    }

    // ---- The process cycle ----

    // Nothing may unwind out of here into libjack: an exception escaping an UnmanagedCallersOnly method ends the
    // process. Anything the cycle itself throws, outside the callback's own catch, is counted like the callback's.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnProcess(uint frames, IntPtr arg)
    {
        if (FromCallbackArgument<Processor>(arg) is { } processor)
        {
            try
            {
                processor.RunCycle((int)frames);
            }
            catch (Exception ex)
            {
                processor.lastProcessException = ex;
                Interlocked.Increment(ref processor.processExceptions);
            }
        }
        return 0;
    }

    private void RunCycle(int frames)
    {
        PortSet set = ports;
        long cycle = Interlocked.Increment(ref cycles);
        var context = new ProcessContext(this, set, frames, cycle);
        ProcessCallback? callback = process;
        if (callback == null)
            Silence(set, frames);
        else
        {
            try
            {
                callback(context);
            }
            catch (Exception ex)
            {
                lastProcessException = ex;
                Interlocked.Increment(ref processExceptions);
                Silence(set, frames);
            }
        }
        // A MIDI output must be cleared every cycle, written or not.
        foreach (MidiPort port in set.MidiOutputArray)
            port.ClearIfUnwritten(frames, cycle);
    }

    // Every audio output this cycle has a buffer for, cleared. (PipeWire's JACK can give a port no buffer; it's skipped.)
    private static void Silence(PortSet set, int frames)
    {
        foreach (AudioPort port in set.AudioOutputArray)
            port.Buffer(frames).Clear();
    }

    /// <summary>The ports a cycle sees: an immutable snapshot, replaced whenever a port is registered or unregistered.</summary>
    internal sealed class PortSet
    {
        public static readonly PortSet Empty = new(Array.Empty<Port>());

        public PortSet(Port[] all)
        {
            All = all;
            AudioInputArray = all.OfType<AudioPort>().Where(p => p.Direction == Direction.In).ToArray();
            AudioOutputArray = all.OfType<AudioPort>().Where(p => p.Direction == Direction.Out).ToArray();
            MidiInputArray = all.OfType<MidiPort>().Where(p => p.Direction == Direction.In).ToArray();
            MidiOutputArray = all.OfType<MidiPort>().Where(p => p.Direction == Direction.Out).ToArray();
        }

        public Port[] All { get; }
        public Port[] InputsArray => All.Where(p => p.Direction == Direction.In).ToArray();
        public Port[] OutputsArray => All.Where(p => p.Direction == Direction.Out).ToArray();
        public AudioPort[] AudioInputArray { get; }
        public AudioPort[] AudioOutputArray { get; }
        public MidiPort[] MidiInputArray { get; }
        public MidiPort[] MidiOutputArray { get; }
        public IReadOnlyList<AudioPort> AudioInputs => AudioInputArray;
        public IReadOnlyList<AudioPort> AudioOutputs => AudioOutputArray;
        public IReadOnlyList<MidiPort> MidiInputs => MidiInputArray;
        public IReadOnlyList<MidiPort> MidiOutputs => MidiOutputArray;

        public PortSet With(Port port) => new(All.Append(port).ToArray());

        public PortSet Without(Port port) => new(All.Where(p => p != port).ToArray());
    }
}

/// <summary>
/// One process cycle: its frame count, and the processor's ports as they were when it began. Valid only during the
/// callback it's passed to.
/// </summary>
public readonly ref struct ProcessContext
{
    private readonly Processor.PortSet ports;

    internal ProcessContext(Processor processor, Processor.PortSet ports, int frames, long cycle)
    {
        Processor = processor;
        this.ports = ports;
        Frames = frames;
        Cycle = cycle;
    }

    public Processor Processor { get; }

    /// <summary>Frames in this cycle: the length of every audio buffer.</summary>
    public int Frames { get; }

    /// <summary>This cycle's number, counting from 1 when the processor was first activated.</summary>
    public long Cycle { get; }

    /// <summary>The frame time at the start of this cycle.</summary>
    public uint FrameTime => Native.jack_last_frame_time(Processor.HandleForCycle);

    /// <summary>The audio inputs, in the order they were registered.</summary>
    public ReadOnlySpan<AudioPort> AudioInputs => ports.AudioInputArray;

    /// <summary>The audio outputs, in the order they were registered.</summary>
    public ReadOnlySpan<AudioPort> AudioOutputs => ports.AudioOutputArray;

    public ReadOnlySpan<MidiPort> MidiInputs => ports.MidiInputArray;

    public ReadOnlySpan<MidiPort> MidiOutputs => ports.MidiOutputArray;
}
