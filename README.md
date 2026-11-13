# JackSharpCore

.NET bindings for the [JACK Audio Connection Kit](https://jackaudio.org/): clients, ports, real-time-safe audio and
MIDI processing, and patching, on Windows, Linux (JACK2, or PipeWire's JACK support) and macOS.

The original repository is [residuum/JackSharp](https://github.com/residuum/JackSharp), by Thomas Mayer. This fork is
based on [h3ll5ur7er/JackSharp](https://github.com/h3ll5ur7er/JackSharp), which moved it to .NET Core.

NuGet package: [JackSharpCore](https://www.nuget.org/packages/JackSharpCore).

## Why .NET Core and not .NET Standard?

.NET Standard doesn't provide `NativeLibrary`, which finds the native JACK library at run time.

## Using it

```csharp
using JackSharp;

// A client with ports and a process callback. Opening throws a JackException if no JACK server is running.
using var processor = new Processor("my-effect");
AudioPort input = processor.RegisterAudioPort("in", Direction.In);
AudioPort output = processor.RegisterAudioPort("out", Direction.Out);

// Runs once per cycle on JACK's real-time thread: buffers are spans on JACK's own memory, and nothing allocates.
processor.Process = context =>
{
    Span<float> from = input.GetBuffer(context), to = output.GetBuffer(context);
    for (int n = 0; n < context.Frames; n++)
        to[n] = from[n] * 0.5f;
};
processor.Activate();

// Every client can see and patch the graph, its own ports and other clients'.
foreach (PortInfo capture in processor.GetPorts(type: PortType.Audio, direction: Direction.Out, physicalOnly: true))
    Console.WriteLine($"{capture.FullName} {string.Join(", ", capture.Aliases)}");
input.ConnectTo("system:capture_1");
output.ConnectTo("system:playback_1");
```

- **`Client`** is what every JACK client can do: the server's state (`SampleRate`, `BufferSize`, `CpuLoad`), the graph
  (`GetPorts`, `GetConnections`, `Connect`, `Disconnect`, `GetLatency`), and events for the server (rate and buffer
  size changes, xruns, shutdown) and the graph (clients and ports coming and going, renames, connections). Events
  arrive on JACK's notification thread.
- **`Processor`** adds ports, registered and unregistered at any time, and the process callback. MIDI is read with
  `MidiPort.GetEvents` and written with `MidiPort.GetWriter`, in place. `AddedLatency` reports the latency it adds,
  through JACK's latency callback, so programs downstream can compensate.
- **`Controller`** is a client without ports, for patchbays and monitors.
- **`JackLibrary`** finds libjack: an explicit `JackLibrary.Path` or `JACK_LIBRARY`, else the system's (`libjack64.dll`,
  `libjack.so.0`, `libjack.0.dylib`), then the usual install folders (JACK2's, PipeWire's, Homebrew's and MacPorts').
  `JackLibrary.SetMessageHandlers` routes JACK's error and information messages, which libjack otherwise prints.

**Jack.NAudio** adapts a `Processor` to [NAudio](https://github.com/naudio/NAudio)'s `IWavePlayer` and `IWaveIn`.

## Tests

`dotnet test JackSharpTest` runs against a JACK server that's already running, or else starts `jackd` on JACK's
dummy backend (a timer, with no sound card) and stops it afterwards. The tests only connect their own ports, never
hardware ones. Without libjack or `jackd` they're skipped.

## Known issues

- **JACK2 1.9.22 on Windows can stall in `jack_client_close`** when a client closes within milliseconds of activating
  (see the unmerged [jack2 PR #889](https://github.com/jackaudio/jack2/pull/889)). A client therefore stays active at
  least 100 ms before it deactivates or closes. Repeatedly starting a server, then opening and closing a client while a
  second client of the same process watches the graph, can still stall a close; the tests don't do that, and a program
  with one long-lived client won't meet it.
- **JACK2 1.9.22 on Windows ignores `UseExactName`** and opens a second client with the same name. A client whose name
  JACK changes despite it is closed with a `JackException`.

## Changes in this fork

**Before 1.0** (h3ll5ur7er, then this fork):

- Moved to .NET 8, and added the AudioTranscription sample.
- Finds the native library with `NativeLibrary` at run time, rather than by how it was built; resolution moved to the
  base client, so a `Controller` created first no longer crashes.
- Fixed reading MIDI input.
- `Controller.Connect` by port name, and a port type `Unknown` for types other than audio and MIDI.
- Published as the JackSharpCore NuGet package.

**1.1.1:**

- **A port without a buffer no longer crashes the process.** PipeWire's JACK can answer `jack_port_get_buffer` with
  no buffer; that became a span over a null pointer, and clearing it in the cycle (after a callback that had thrown)
  threw out of the `[UnmanagedCallersOnly]` callback, which ends the process. `AudioPort.GetBuffer` now gives an empty
  span then (check its length before copying a cycle in or out), MIDI ports give no events and no writer, and nothing
  a cycle throws escapes to JACK: it's counted in `ProcessExceptions` like the callback's own.

**1.1:**

- `Processor.AddedLatency` reports a processor's own latency through JACK's latency callback: its outputs' capture
  latency is its inputs' plus what it adds, and its inputs' playback latency its outputs' plus what it adds.

**1.0** (breaking):

- **One client, layered as JACK's is.** Patching and graph events moved into `Client`, so a `Processor` can patch
  without a second client, and `Controller` is a client without ports.
- **A process callback that allocates nothing.** Buffers are spans on JACK's memory, and MIDI is read and written in
  place; before, every cycle allocated arrays, copies of each buffer, and objects per port.
- **Ports register and unregister while running.** Unregistering waits for any cycle still using the port.
- **Errors say why.** Opening throws `JackException` with JACK's status flags (`NoServer`, name taken, version
  mismatch) instead of returning `false`; a client never starts a server unless asked.
- **Lifecycle fixes.** Disposing after the server shuts down, or from a client's own event handler, is safe, and no
  finalizer calls into libjack.
- **More of JACK:** port aliases, latency ranges, CPU load, buffer size changes, and xrun delays.
- **Native loading** tries each platform's usual names and folders, PipeWire's included, with an explicit override.
- **Modern interop:** generated `LibraryImport` calls and `[UnmanagedCallersOnly]` callbacks, compatible with native
  AOT.
- Removed the CSCore binding, whose library is no longer maintained, and the unused transport wrappers.
- Tests rewritten for the new API, starting a dummy JACK server when none is running.

## License

MIT, as the original: see [LICENSE](LICENSE).
