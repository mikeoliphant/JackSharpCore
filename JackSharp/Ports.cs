namespace JackSharp;

/// <summary>A port of a <see cref="Processor"/>.</summary>
public abstract class Port
{
    private IntPtr handle;

    internal Port(Processor owner, string shortName, Direction direction)
    {
        Owner = owner;
        ShortName = shortName;
        Direction = direction;
    }

    internal Processor Owner { get; }
    internal IntPtr Handle => handle;

    internal void Attach(IntPtr native) => handle = native;

    internal IntPtr Detach()
    {
        IntPtr h = handle;
        handle = IntPtr.Zero;
        return h;
    }

    /// <summary>The name it was registered with, the part after <c>client:</c>.</summary>
    public string ShortName { get; }

    /// <summary>Its full name in the graph, <c>client:port</c>, as connections name it.</summary>
    public string FullName => Native.String(Native.jack_port_name(Live)) ?? $"{Owner.Name}:{ShortName}";

    public Direction Direction { get; }

    public abstract PortType Type { get; }

    /// <summary>Whether it's still registered: false once it's unregistered, or its processor is disposed.</summary>
    public bool IsRegistered => handle != IntPtr.Zero;

    private IntPtr Live => handle != IntPtr.Zero ? handle : throw new ObjectDisposedException($"The JACK port '{ShortName}' isn't registered.");

    /// <summary>Whether it's connected to anything.</summary>
    public bool IsConnected => Native.jack_port_connected(Live) > 0;

    /// <summary>The full names of the ports it's connected to.</summary>
    public IReadOnlyList<string> GetConnections() => Owner.GetConnections(FullName);

    /// <summary>Connects it to another port, by full name, whichever way round its direction needs. Returns true if
    /// they're connected (including if they already were).</summary>
    public bool ConnectTo(string other) =>
        Direction == Direction.Out ? Owner.Connect(FullName, other) : Owner.Connect(other, FullName);

    /// <summary>Disconnects it from another port, by full name.</summary>
    public bool DisconnectFrom(string other) =>
        Direction == Direction.Out ? Owner.Disconnect(FullName, other) : Owner.Disconnect(other, FullName);

    /// <summary>Disconnects it from everything.</summary>
    public void DisconnectAll() => Native.jack_port_disconnect(Owner.HandleForCycle, Live);

    /// <summary>Its latency: for an input, <see cref="LatencyMode.Capture"/> is how long ago what arrives was captured;
    /// for an output, <see cref="LatencyMode.Playback"/> is how long until what leaves is played back.</summary>
    public unsafe LatencyRange GetLatency(LatencyMode mode)
    {
        LatencyRangeNative range;
        Native.jack_port_get_latency_range(Live, (int)mode, &range);
        return new LatencyRange(range.Min, range.Max);
    }

    /// <summary>Sets its latency, as JACK asks of a client that adds latency of its own.</summary>
    public unsafe void SetLatency(LatencyMode mode, LatencyRange latency)
    {
        var range = new LatencyRangeNative { Min = latency.Min, Max = latency.Max };
        Native.jack_port_set_latency_range(Live, (int)mode, &range);
    }

    internal void CheckCycle(in ProcessContext context)
    {
        if (context.Processor != Owner)
            throw new InvalidOperationException("The port belongs to another processor's cycle.");
    }

    public override string ToString() => $"{Owner.Name}:{ShortName}";
}

/// <summary>An audio port: 32-bit float samples, one buffer per cycle.</summary>
public sealed class AudioPort : Port
{
    internal AudioPort(Processor owner, string shortName, Direction direction) : base(owner, shortName, direction) { }

    public override PortType Type => PortType.Audio;

    /// <summary>
    /// This cycle's samples, in JACK's own buffer: read an input's, write an output's. Valid only during the callback.
    /// A port unregistered since the cycle began gives an empty span, as does one JACK has no buffer for this cycle
    /// (PipeWire's JACK can answer with none): check the length before copying a whole cycle in or out.
    /// </summary>
    public unsafe Span<float> GetBuffer(ProcessContext context)
    {
        CheckCycle(context);
        return Buffer(context.Frames);
    }

    // The buffer, or an empty span without a port or a buffer: never a span over a null pointer.
    internal unsafe Span<float> Buffer(int frames)
    {
        IntPtr h = Handle;
        if (h == IntPtr.Zero)
            return Span<float>.Empty;
        void* buffer = Native.jack_port_get_buffer(h, (uint)frames);
        return buffer == null ? Span<float>.Empty : new Span<float>(buffer, frames);
    }
}

/// <summary>A MIDI port: timestamped events, one buffer per cycle.</summary>
public sealed class MidiPort : Port
{
    private long writtenCycle;

    internal MidiPort(Processor owner, string shortName, Direction direction) : base(owner, shortName, direction) { }

    public override PortType Type => PortType.Midi;

    /// <summary>This cycle's events, on an input. Valid only during the callback.</summary>
    public unsafe MidiEvents GetEvents(ProcessContext context)
    {
        CheckCycle(context);
        if (Direction != Direction.In)
            throw new InvalidOperationException("Only an input's events can be read.");
        IntPtr h = Handle;
        if (h == IntPtr.Zero)
            return default;
        void* buffer = Native.jack_port_get_buffer(h, (uint)context.Frames);
        return buffer == null ? default : new MidiEvents(buffer);
    }

    /// <summary>This cycle's writer, on an output: the buffer is cleared the first time it's asked for in a cycle.
    /// Valid only during the callback.</summary>
    public unsafe MidiWriter GetWriter(ProcessContext context)
    {
        CheckCycle(context);
        if (Direction != Direction.Out)
            throw new InvalidOperationException("Only an output can be written.");
        IntPtr h = Handle;
        if (h == IntPtr.Zero)
            return default;
        void* buffer = Native.jack_port_get_buffer(h, (uint)context.Frames);
        if (buffer == null)
            return default;
        if (writtenCycle != context.Cycle)
        {
            Native.jack_midi_clear_buffer(buffer);
            writtenCycle = context.Cycle;
        }
        return new MidiWriter(buffer);
    }

    // An output not written this cycle still has to be cleared, or it would repeat last cycle's events.
    internal unsafe void ClearIfUnwritten(int frames, long cycle)
    {
        IntPtr h = Handle;
        if (writtenCycle == cycle || h == IntPtr.Zero)
            return;
        void* buffer = Native.jack_port_get_buffer(h, (uint)frames);
        if (buffer != null)
            Native.jack_midi_clear_buffer(buffer);
        writtenCycle = cycle;
    }
}

/// <summary>A MIDI event: its time, in frames from the start of the cycle, and its bytes.</summary>
public readonly ref struct MidiEvent
{
    internal MidiEvent(uint time, ReadOnlySpan<byte> data)
    {
        Time = time;
        Data = data;
    }

    public uint Time { get; }
    public ReadOnlySpan<byte> Data { get; }
}

/// <summary>An input's events this cycle, in time order. Allocates nothing.</summary>
public readonly unsafe ref struct MidiEvents
{
    private readonly void* buffer;

    internal MidiEvents(void* buffer)
    {
        this.buffer = buffer;
        Count = buffer == null ? 0 : (int)Native.jack_midi_get_event_count(buffer);
    }

    public int Count { get; }

    /// <summary>Events lost since the last cycle, for want of room.</summary>
    public int Lost => buffer == null ? 0 : (int)Native.jack_midi_get_lost_event_count(buffer);

    public MidiEvent this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            MidiEventNative e;
            Native.jack_midi_event_get(&e, buffer, (uint)index);
            return new MidiEvent(e.Time, new ReadOnlySpan<byte>(e.Buffer, (int)e.Size));
        }
    }

    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly MidiEvents events;
        private int index;

        internal Enumerator(MidiEvents events)
        {
            this.events = events;
            index = -1;
        }

        public bool MoveNext() => ++index < events.Count;

        public MidiEvent Current => events[index];
    }
}

/// <summary>Writes an output's events this cycle, in time order. Allocates nothing.</summary>
public readonly unsafe ref struct MidiWriter
{
    private readonly void* buffer;

    internal MidiWriter(void* buffer) => this.buffer = buffer;

    /// <summary>The largest event that still fits.</summary>
    public int MaxEventSize => buffer == null ? 0 : (int)Native.jack_midi_max_event_size(buffer);

    /// <summary>Writes an event at a time in the cycle, which must be no earlier than the last written. Returns false
    /// if it doesn't fit, or is out of order.</summary>
    public bool Write(uint time, ReadOnlySpan<byte> data)
    {
        if (buffer == null)
            return false;
        fixed (byte* bytes = data)
            return Native.jack_midi_event_write(buffer, time, bytes, (nuint)data.Length) == 0;
    }
}
