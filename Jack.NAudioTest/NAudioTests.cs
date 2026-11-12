using Jack.NAudio;
using JackSharp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NUnit.Framework;

namespace JackSharpTest.NAudio;

/// <summary>Playing and recording through JACK with NAudio's interfaces, between the tests' own processors.</summary>
[TestFixture]
public sealed class NAudioTests
{
    [SetUp]
    public void RequireServer() => TestServer.Require();

    [Test]
    public void APlayersSoundArrivesOnEachChannel()
    {
        using var player = new Processor("jacksharp-naudio-player");
        using var listener = new Processor("jacksharp-naudio-listener");
        AudioPort left = player.RegisterAudioPort("out_1", Direction.Out), right = player.RegisterAudioPort("out_2", Direction.Out);
        AudioPort hearLeft = listener.RegisterAudioPort("in_1", Direction.In), hearRight = listener.RegisterAudioPort("in_2", Direction.In);
        float peakLeft = 0, peakRight = 0;
        listener.Process = context =>
        {
            foreach (float x in hearLeft.GetBuffer(context))
                peakLeft = Math.Max(peakLeft, Math.Abs(x));
            foreach (float x in hearRight.GetBuffer(context))
                peakRight = Math.Max(peakRight, Math.Abs(x));
        };
        listener.Activate();

        using var output = new AudioOut(player);
        Assert.That(output.OutputWaveFormat.Channels, Is.EqualTo(2));
        Assert.That(output.OutputWaveFormat.SampleRate, Is.EqualTo(player.SampleRate));
        // A 0.5 sine on the left, silence on the right, at the server's rate.
        var sine = new SignalGenerator(player.SampleRate, 1) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.5 };
        var stereo = new MultiplexingSampleProvider(new ISampleProvider[] { sine, new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(player.SampleRate, 1)).ToSampleProvider() }, 2);
        output.Init(stereo.ToWaveProvider());
        output.Play();
        Assert.That(output.PlaybackState, Is.EqualTo(PlaybackState.Playing));
        left.ConnectTo(hearLeft.FullName);
        right.ConnectTo(hearRight.FullName);
        Assert.That(TestServer.WaitFor(() => peakLeft > 0.45f), Is.True, $"the left channel peaks at 0.5 ({peakLeft})");
        Assert.That(peakRight, Is.EqualTo(0), "the right channel is silent");

        output.Pause();
        Assert.That(output.PlaybackState, Is.EqualTo(PlaybackState.Paused));
        output.Stop();
        Assert.That(output.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(player.IsActive, Is.True, "stopping the player leaves the processor to its owner");
    }

    [Test]
    public void AProviderThatRunsOutStopsPlayback()
    {
        using var player = new Processor("jacksharp-naudio-short");
        player.RegisterAudioPort("out", Direction.Out);
        using var output = new AudioOut(player);
        // A tenth of a second of sound.
        var tone = new SignalGenerator(player.SampleRate, 1) { Gain = 0.1 }.Take(TimeSpan.FromSeconds(0.1));
        output.Init(tone.ToWaveProvider());
        var stopped = new ManualResetEventSlim();
        output.PlaybackStopped += (_, _) => stopped.Set();
        output.Play();
        Assert.That(stopped.Wait(3000), Is.True, "PlaybackStopped at the end");
        Assert.That(output.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public void AWrongFormatIsRefusedWithTheReason()
    {
        using var player = new Processor("jacksharp-naudio-format");
        player.RegisterAudioPort("out", Direction.Out);
        using var output = new AudioOut(player);
        var sixteenBit = new SignalGenerator(player.SampleRate, 1).ToWaveProvider16();
        Assert.That(Assert.Throws<ArgumentException>(() => output.Init(sixteenBit))!.Message, Does.Contain("32-bit float"));
        var stereo = new SignalGenerator(player.SampleRate, 2).ToWaveProvider();
        Assert.That(Assert.Throws<ArgumentException>(() => output.Init(stereo))!.Message, Does.Contain("1 audio outputs"));
        var otherRate = new SignalGenerator(player.SampleRate == 44100 ? 48000 : 44100, 1).ToWaveProvider();
        Assert.That(Assert.Throws<ArgumentException>(() => output.Init(otherRate))!.Message, Does.Contain("resample"));
    }

    [Test]
    public void ARecordingIsInterleavedInInputOrder()
    {
        using var source = new Processor("jacksharp-naudio-source");
        using var recorder = new Processor("jacksharp-naudio-recorder");
        AudioPort first = source.RegisterAudioPort("out_1", Direction.Out), second = source.RegisterAudioPort("out_2", Direction.Out);
        // Constant levels: 0.25 on the first channel, -0.5 on the second.
        source.Process = context =>
        {
            first.GetBuffer(context).Fill(0.25f);
            second.GetBuffer(context).Fill(-0.5f);
        };
        source.Activate();
        AudioPort in1 = recorder.RegisterAudioPort("in_1", Direction.In), in2 = recorder.RegisterAudioPort("in_2", Direction.In);

        using var input = new AudioIn(recorder);
        Assert.That(input.WaveFormat.Channels, Is.EqualTo(2));
        Assert.That(input.WaveFormat.Encoding, Is.EqualTo(WaveFormatEncoding.IeeeFloat));
        long blocks = 0, interleaved = 0;
        input.DataAvailable += (_, e) =>
        {
            float[] samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
            blocks++;
            if (samples[0] == 0.25f && samples[1] == -0.5f && samples[^2] == 0.25f && samples[^1] == -0.5f)
                interleaved++;
        };
        var stopped = false;
        input.RecordingStopped += (_, _) => stopped = true;
        input.StartRecording();
        first.ConnectTo(in1.FullName);
        second.ConnectTo(in2.FullName);
        Assert.That(TestServer.WaitFor(() => Interlocked.Read(ref interleaved) > 10), Is.True, "interleaved data arrives");
        input.StopRecording();
        Assert.That(stopped, Is.True);
        long after = Interlocked.Read(ref blocks);
        Thread.Sleep(100);
        Assert.That(Interlocked.Read(ref blocks), Is.EqualTo(after), "nothing more once stopped");
    }
}
