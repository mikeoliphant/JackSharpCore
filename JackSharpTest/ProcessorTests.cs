using JackSharp;
using NUnit.Framework;

namespace JackSharpTest;

/// <summary>The process cycle: audio and MIDI through JACK, ports changing while running, and nothing allocated.</summary>
[TestFixture, Order(2)]
public sealed class ProcessorTests
{
    [SetUp]
    public void RequireServer() => TestServer.Require();

    [Test]
    public void TheCallbackRunsOncePerCycle()
    {
        using var processor = new Processor("jacksharp-cycles");
        long calls = 0;
        int frames = 0;
        processor.Process = context =>
        {
            calls++;
            frames = context.Frames;
        };
        processor.Activate();
        Thread.Sleep(500);
        processor.Deactivate();
        Assert.That(calls, Is.GreaterThan(0));
        Assert.That(frames, Is.EqualTo(processor.BufferSize));
        Assert.That(processor.Cycles, Is.EqualTo(calls));
        double expected = 0.5 * processor.SampleRate / processor.BufferSize;
        Assert.That(calls, Is.InRange(expected * 0.5, expected * 1.5), "about one call per cycle");
    }

    [Test]
    public void AudioGoesFromOneProcessorToAnother()
    {
        using var source = new Processor("jacksharp-source");
        using var sink = new Processor("jacksharp-sink");
        AudioPort output = source.RegisterAudioPort("out", Direction.Out);
        AudioPort input = sink.RegisterAudioPort("in", Direction.In);
        // Each cycle the source writes a ramp, starting from the cycle's number; the sink checks what arrives is one.
        source.Process = context =>
        {
            Span<float> buffer = output.GetBuffer(context);
            for (int n = 0; n < buffer.Length; n++)
                buffer[n] = context.Cycle + n * 0.001f;
        };
        long received = 0, ramps = 0;
        sink.Process = context =>
        {
            Span<float> buffer = input.GetBuffer(context);
            if (buffer[0] == 0)
                return;
            received++;
            bool ramp = true;
            for (int n = 1; n < buffer.Length; n++)
                ramp &= Math.Abs(buffer[n] - buffer[0] - n * 0.001f) < 1e-3f;
            if (ramp)
                ramps++;
        };
        source.Activate();
        sink.Activate();
        Assert.That(output.ConnectTo(input.FullName), Is.True);
        Assert.That(TestServer.WaitFor(() => Interlocked.Read(ref received) > 20), Is.True, "audio arrives");
        Assert.That(Interlocked.Read(ref ramps), Is.EqualTo(Interlocked.Read(ref received)).Within(1), "intact");
    }

    [Test]
    public void AProcessorCanFeedItself()
    {
        using var processor = new Processor("jacksharp-loop");
        AudioPort output = processor.RegisterAudioPort("out", Direction.Out);
        AudioPort input = processor.RegisterAudioPort("in", Direction.In);
        float seen = 0;
        processor.Process = context =>
        {
            float arrived = input.GetBuffer(context)[0];
            if (arrived != 0)
                seen = arrived;
            output.GetBuffer(context).Fill(0.25f);
        };
        processor.Activate();
        output.ConnectTo(input.FullName);
        Assert.That(TestServer.WaitFor(() => seen == 0.25f), Is.True);
    }

    [Test]
    public void MidiGoesFromOneProcessorToAnother()
    {
        using var source = new Processor("jacksharp-midi-source");
        using var sink = new Processor("jacksharp-midi-sink");
        MidiPort output = source.RegisterMidiPort("out", Direction.Out);
        MidiPort input = sink.RegisterMidiPort("in", Direction.In);
        source.Process = context =>
        {
            MidiWriter writer = output.GetWriter(context);
            Span<byte> noteOn = stackalloc byte[] { 0x90, (byte)(context.Cycle % 128), 100 };
            writer.Write(0, noteOn);
            Span<byte> noteOff = stackalloc byte[] { 0x80, (byte)(context.Cycle % 128), 0 };
            writer.Write((uint)(context.Frames / 2), noteOff);
        };
        long pairs = 0, malformed = 0;
        sink.Process = context =>
        {
            MidiEvents events = input.GetEvents(context);
            if (events.Count == 0)
                return;
            if (events.Count == 2 && events[0].Data.Length == 3 && events[0].Data[0] == 0x90 && events[1].Data[0] == 0x80
                && events[0].Time == 0 && events[1].Time == context.Frames / 2 && events[0].Data[1] == events[1].Data[1])
                pairs++;
            else
                malformed++;
            foreach (MidiEvent e in events)
                _ = e.Data.Length;
        };
        source.Activate();
        sink.Activate();
        Assert.That(output.ConnectTo(input.FullName), Is.True);
        Assert.That(TestServer.WaitFor(() => Interlocked.Read(ref pairs) > 20), Is.True, "MIDI arrives");
        Assert.That(Interlocked.Read(ref malformed), Is.EqualTo(0));
    }

    [Test]
    public void AnUnwrittenMidiOutputIsEmpty()
    {
        using var source = new Processor("jacksharp-midi-quiet");
        using var sink = new Processor("jacksharp-midi-listen");
        MidiPort output = source.RegisterMidiPort("out", Direction.Out);
        MidiPort input = sink.RegisterMidiPort("in", Direction.In);
        bool write = true;
        source.Process = context =>
        {
            if (write)
                output.GetWriter(context).Write(0, stackalloc byte[] { 0x90, 60, 100 });
        };
        long events = 0, cycles = 0;
        sink.Process = context =>
        {
            Interlocked.Increment(ref cycles);
            Interlocked.Add(ref events, input.GetEvents(context).Count);
        };
        source.Activate();
        sink.Activate();
        output.ConnectTo(input.FullName);
        Assert.That(TestServer.WaitFor(() => Interlocked.Read(ref events) > 0), Is.True);
        write = false;
        Thread.Sleep(100);
        long before = Interlocked.Read(ref events);
        Thread.Sleep(300);
        Assert.That(Interlocked.Read(ref events), Is.EqualTo(before), "no events repeat once the source stops writing");
    }

    [Test]
    public void PortsCanComeAndGoWhileRunning()
    {
        using var processor = new Processor("jacksharp-dynamic");
        long cycles = 0, seenPorts = 0;
        processor.Process = context =>
        {
            foreach (AudioPort port in context.AudioOutputs)
                port.GetBuffer(context).Clear();
            seenPorts = context.AudioOutputs.Length;
            cycles++;
        };
        processor.Activate();
        var added = new List<AudioPort>();
        for (int i = 0; i < 8; i++)
        {
            added.Add(processor.RegisterAudioPort($"out_{i}", Direction.Out));
            Thread.Sleep(5);
        }
        Assert.That(TestServer.WaitFor(() => seenPorts == 8), Is.True, "the cycle sees the new ports");
        foreach (AudioPort port in added)
            processor.Unregister(port);
        Assert.That(TestServer.WaitFor(() => seenPorts == 0), Is.True, "and their going");
        Assert.That(added.All(p => !p.IsRegistered), Is.True);
        Assert.That(processor.GetPorts($"^{processor.Name}:"), Is.Empty);
        Assert.That(processor.ProcessExceptions, Is.EqualTo(0));
    }

    [Test]
    public void ACallbacksExceptionIsCountedAndItsOutputsSilenced()
    {
        using var source = new Processor("jacksharp-throws");
        using var sink = new Processor("jacksharp-catches");
        AudioPort output = source.RegisterAudioPort("out", Direction.Out);
        AudioPort input = sink.RegisterAudioPort("in", Direction.In);
        source.Process = context =>
        {
            output.GetBuffer(context).Fill(1);
            throw new InvalidOperationException("on purpose");
        };
        float last = -1;
        sink.Process = context => last = input.GetBuffer(context)[0];
        source.Activate();
        sink.Activate();
        output.ConnectTo(input.FullName);
        Assert.That(TestServer.WaitFor(() => source.ProcessExceptions > 10), Is.True);
        Assert.That(source.LastProcessException?.Message, Is.EqualTo("on purpose"));
        Thread.Sleep(50);
        Assert.That(last, Is.EqualTo(0), "the outputs are silenced, not left as written");
    }

    [Test]
    public void AddedLatencyIsReportedDownstream()
    {
        using var source = new Processor("jacksharp-latency-source");
        using var effect = new Processor("jacksharp-latency-effect");
        AudioPort sourceOut = source.RegisterAudioPort("out", Direction.Out);
        AudioPort effectIn = effect.RegisterAudioPort("in", Direction.In);
        AudioPort effectOut = effect.RegisterAudioPort("out", Direction.Out);
        effect.AddedLatency = 64;
        source.Activate();
        effect.Activate();
        sourceOut.ConnectTo(effectIn.FullName);
        LatencyRange upstream = default;
        Assert.That(TestServer.WaitFor(() =>
        {
            upstream = sourceOut.GetLatency(LatencyMode.Capture);
            return effectOut.GetLatency(LatencyMode.Capture) == new LatencyRange(upstream.Min + 64, upstream.Max + 64);
        }), Is.True, $"the effect's output: its input's capture latency ({upstream}) plus 64, not {effectOut.GetLatency(LatencyMode.Capture)}");
        effect.AddedLatency = 100;
        Assert.That(TestServer.WaitFor(() => effectOut.GetLatency(LatencyMode.Capture) == new LatencyRange(upstream.Min + 100, upstream.Max + 100)),
            Is.True, "and follows a change while active");
        using var late = new Processor("jacksharp-latency-late");
        late.Activate();
        Assert.Throws<InvalidOperationException>(() => late.AddedLatency = 10, "the callback can only be set before activating");
    }

    [Test]
    public void NothingIsAllocatedInACycle()
    {
        using var source = new Processor("jacksharp-alloc-source");
        using var sink = new Processor("jacksharp-alloc-sink");
        AudioPort audioOut = source.RegisterAudioPort("out", Direction.Out);
        MidiPort midiOut = source.RegisterMidiPort("midi", Direction.Out);
        AudioPort audioIn = sink.RegisterAudioPort("in", Direction.In);
        MidiPort midiIn = sink.RegisterMidiPort("midi", Direction.In);
        source.Process = context =>
        {
            audioOut.GetBuffer(context).Fill(0.5f);
            midiOut.GetWriter(context).Write(0, stackalloc byte[] { 0x90, 60, 100 });
        };
        // The bytes allocated on the real-time thread, at the same point of two cycles 200 apart: everything between,
        // the library's own part of each cycle included, must allocate nothing.
        long at100 = -1, at300 = -1;
        float sum = 0;
        sink.Process = context =>
        {
            foreach (float x in audioIn.GetBuffer(context))
                sum += x;
            foreach (MidiEvent e in midiIn.GetEvents(context))
                sum += e.Data[0];
            if (context.Cycle == 100)
                at100 = GC.GetAllocatedBytesForCurrentThread();
            else if (context.Cycle == 300)
                at300 = GC.GetAllocatedBytesForCurrentThread();
        };
        source.Activate();
        sink.Activate();
        audioOut.ConnectTo(audioIn.FullName);
        midiOut.ConnectTo(midiIn.FullName);
        Assert.That(TestServer.WaitFor(() => Interlocked.Read(ref at300) >= 0, 10000), Is.True);
        Assert.That(sum, Is.GreaterThan(0), "data flowed");
        Assert.That(at300 - at100, Is.EqualTo(0), "bytes allocated over 200 cycles");
    }
}
