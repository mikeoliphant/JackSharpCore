using JackSharp;
using NUnit.Framework;

namespace JackSharpTest;

/// <summary>Opening clients, the server's state, and the graph and its events.</summary>
[TestFixture, Order(1)]
public sealed class ClientTests
{
    [SetUp]
    public void RequireServer() => TestServer.Require();

    [Test]
    public void TheLibraryLoads()
    {
        Assert.That(JackLibrary.IsAvailable, Is.True);
        Assert.That(JackLibrary.LoadedFrom, Is.Not.Null);
    }

    [Test]
    public void AClientOpensWithTheServersSettings()
    {
        using var processor = new Processor("jacksharp-open");
        Assert.That(processor.Name, Is.EqualTo("jacksharp-open"));
        Assert.That(processor.IsConnected, Is.True);
        Assert.That(processor.IsActive, Is.False);
        Assert.That(processor.SampleRate, Is.GreaterThan(0));
        Assert.That(processor.BufferSize, Is.GreaterThan(0));
        if (TestServer.StartedHere)
        {
            Assert.That(processor.SampleRate, Is.EqualTo(48000));
            Assert.That(processor.BufferSize, Is.EqualTo(256));
        }
    }

    [Test]
    public void ATakenNameIsChangedOrRefused()
    {
        using var first = new Processor("jacksharp-name");
        using var second = new Processor("jacksharp-name");
        Assert.That(second.Name, Is.Not.EqualTo(first.Name), "JACK renames the second");
        // With UseExactName JACK should refuse the name. JACK2 1.9.22 (on Windows, at least) opens a second client of
        // the same name instead; either way, a client never comes back under another name.
        try
        {
            using var third = new Processor("jacksharp-name", new ClientOptions { UseExactName = true });
            Assert.That(third.Name, Is.EqualTo("jacksharp-name"));
            Assert.Warn("This JACK server accepted a duplicate client name despite JackUseExactName.");
        }
        catch (JackException refused)
        {
            Assert.That(refused.Status.HasFlag(JackStatus.NameNotUnique), Is.True, refused.Message);
        }
    }

    [Test]
    public void NoServerIsAJackExceptionThatSaysSo()
    {
        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            Assert.Ignore("Server names aren't supported on ARM64 macOS.");
        var none = Assert.Throws<JackException>(() => new Controller("jacksharp-none", new ClientOptions { ServerName = "jacksharp-no-such-server" }));
        Assert.That(none!.NoServer, Is.True, none.Message);
    }

    [Test]
    public void PortsAreListedWithTheirKinds()
    {
        using var processor = new Processor("jacksharp-list");
        processor.RegisterAudioPort("in", Direction.In);
        processor.RegisterAudioPort("out", Direction.Out);
        processor.RegisterMidiPort("midi_in", Direction.In);

        IReadOnlyList<PortInfo> mine = processor.GetPorts($"^{processor.Name}:");
        Assert.That(mine.Select(p => p.ShortName), Is.EquivalentTo(new[] { "in", "out", "midi_in" }));
        PortInfo input = mine.Single(p => p.ShortName == "in");
        Assert.That(input.FullName, Is.EqualTo($"{processor.Name}:in"));
        Assert.That(input.ClientName, Is.EqualTo(processor.Name));
        Assert.That(input.Type, Is.EqualTo(PortType.Audio));
        Assert.That(input.Direction, Is.EqualTo(Direction.In));
        Assert.That(input.IsPhysical, Is.False);
        Assert.That(mine.Single(p => p.ShortName == "midi_in").Type, Is.EqualTo(PortType.Midi));

        Assert.That(processor.GetPorts($"^{processor.Name}:", PortType.Audio, Direction.Out).Select(p => p.ShortName), Is.EqualTo(new[] { "out" }));
        Assert.That(processor.GetPorts($"^{processor.Name}:", PortType.Midi).Select(p => p.ShortName), Is.EqualTo(new[] { "midi_in" }));
        Assert.That(processor.GetPort($"{processor.Name}:out")?.Direction, Is.EqualTo(Direction.Out));
        Assert.That(processor.GetPort($"{processor.Name}:nothing"), Is.Null);
    }

    [Test]
    public void ConnectionsAreMadeAndReported()
    {
        using var controller = new Controller("jacksharp-watch");
        var seen = new List<ConnectionEventArgs>();
        controller.PortsConnected += (_, e) => { lock (seen) seen.Add(e); };

        using var processor = new Processor("jacksharp-patch");
        AudioPort output = processor.RegisterAudioPort("out", Direction.Out);
        AudioPort input = processor.RegisterAudioPort("in", Direction.In);
        processor.Activate();

        Assert.That(output.ConnectTo(input.FullName), Is.True);
        Assert.That(processor.Connect(output.FullName, input.FullName), Is.True, "connecting again is no error");
        Assert.That(output.GetConnections(), Is.EqualTo(new[] { input.FullName }));
        Assert.That(controller.GetConnections(input.FullName), Is.EqualTo(new[] { output.FullName }), "another client sees it too");
        Assert.That(input.IsConnected, Is.True);
        Assert.That(TestServer.WaitFor(() => { lock (seen) return seen.Any(e => e.Connected && e.Source == output.FullName && e.Destination == input.FullName); }),
            Is.True, "the controller hears of the connection, source first");

        Assert.That(processor.Disconnect(output.FullName, input.FullName), Is.True);
        Assert.That(output.GetConnections(), Is.Empty);
        Assert.That(TestServer.WaitFor(() => { lock (seen) return seen.Any(e => !e.Connected && e.Source == output.FullName); }), Is.True,
            "and of the disconnection");
        Assert.That(processor.Connect(input.FullName, output.FullName), Is.False, "an input can't be a source");
    }

    [Test]
    public void ClientsAndPortsComingAndGoingAreReported()
    {
        using var controller = new Controller("jacksharp-graph");
        var clients = new List<ClientRegistrationEventArgs>();
        var ports = new List<PortRegistrationEventArgs>();
        controller.ClientRegistered += (_, e) => { lock (clients) clients.Add(e); };
        controller.PortRegistered += (_, e) => { lock (ports) ports.Add(e); };

        string name;
        using (var processor = new Processor("jacksharp-coming"))
        {
            name = processor.Name;
            processor.RegisterAudioPort("out", Direction.Out);
            processor.Activate();
            Assert.That(TestServer.WaitFor(() => { lock (ports) return ports.Any(p => p.Registered && p.FullName == $"{name}:out"); }), Is.True, "port registered");
        }
        Assert.That(TestServer.WaitFor(() => { lock (clients) return clients.Any(c => c.ClientName == name && c.Registered); }), Is.True, "client arrived");
        Assert.That(TestServer.WaitFor(() => { lock (clients) return clients.Any(c => c.ClientName == name && !c.Registered); }), Is.True, "client left");
    }

    [Test]
    public void APortsLatencyCanBeReadAndSet()
    {
        using var processor = new Processor("jacksharp-latency");
        AudioPort output = processor.RegisterAudioPort("out", Direction.Out);
        processor.Activate();
        output.SetLatency(LatencyMode.Playback, new LatencyRange(64, 128));
        Assert.That(output.GetLatency(LatencyMode.Playback), Is.EqualTo(new LatencyRange(64, 128)));
        Assert.That(processor.GetLatency(output.FullName, LatencyMode.Playback), Is.EqualTo(new LatencyRange(64, 128)));
    }
}

/// <summary>A server shutting down under its clients. Last: killing a JACK server can briefly disturb another one
/// running on the same machine, as the others' tests use.</summary>
[TestFixture, Order(3)]
public sealed class ShutdownTests
{
    [SetUp]
    public void RequireServer() => TestServer.Require();

    [Test]
    public void AServerShuttingDownIsReportedAndTheClientCanStillBeDisposed()
    {
        if (OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            Assert.Ignore("Server names aren't supported on ARM64 macOS.");
        // A second server of its own, to shut down.
        var server = TestServer.StartDummy("jacksharp-shutdown");
        if (server == null)
            Assert.Ignore("Couldn't start a second JACK server on the dummy backend.");
        try
        {
            var processor = new Processor("jacksharp-orphan", new ClientOptions { ServerName = "jacksharp-shutdown" });
            processor.RegisterAudioPort("out", Direction.Out);
            var shutdown = new ManualResetEventSlim();
            processor.Shutdown += (_, _) =>
            {
                shutdown.Set();
                processor.Dispose();   // From the handler: it closes once the handler has returned.
            };
            processor.Activate();
            TestServer.Stop(server);
            server = null;
            Assert.That(shutdown.Wait(5000), Is.True, "the Shutdown event");
            Assert.That(TestServer.WaitFor(() => !processor.IsConnected), Is.True);
            Assert.DoesNotThrow(processor.Dispose);
            Assert.DoesNotThrow(processor.Deactivate);
        }
        finally
        {
            TestServer.Stop(server);
        }
    }
}
