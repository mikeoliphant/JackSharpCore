namespace JackSharp;

/// <summary>JACK's status flags from opening a client (<c>jack_status_t</c>).</summary>
[Flags]
public enum JackStatus
{
    None = 0,
    /// <summary>The operation failed.</summary>
    Failure = 0x01,
    /// <summary>The operation had an invalid or unsupported option.</summary>
    InvalidOption = 0x02,
    /// <summary>The client's name was taken, and it was given another (or, with <see cref="ClientOptions.UseExactName"/>, refused).</summary>
    NameNotUnique = 0x04,
    /// <summary>The server was started for this client.</summary>
    ServerStarted = 0x08,
    /// <summary>The client couldn't connect to the server.</summary>
    ServerFailed = 0x10,
    /// <summary>The server's communication failed.</summary>
    ServerError = 0x20,
    NoSuchClient = 0x40,
    LoadFailure = 0x80,
    InitFailure = 0x100,
    /// <summary>The server's shared memory couldn't be accessed.</summary>
    ShmFailure = 0x200,
    /// <summary>The client library and the server are incompatible versions.</summary>
    VersionError = 0x400,
    BackendError = 0x800,
    ClientZombie = 0x1000,
}

/// <summary>A JACK operation failed. <see cref="Status"/> has JACK's status flags, where it gives them.</summary>
public sealed class JackException : Exception
{
    public JackStatus Status { get; }

    public JackException(string message, JackStatus status = JackStatus.None) : base(message) => Status = status;

    /// <summary>Whether it failed because no JACK server is running (and none was to be started).</summary>
    public bool NoServer => Status.HasFlag(JackStatus.ServerFailed);
}

/// <summary>How a client opens.</summary>
public sealed record ClientOptions
{
    /// <summary>Start a JACK server with its default settings if none is running. Off by default: a server's settings
    /// (device, rate, buffer size) are the user's to choose.</summary>
    public bool StartServer { get; init; }

    /// <summary>Refuse to open, rather than be renamed, if another client has this name.</summary>
    public bool UseExactName { get; init; }

    /// <summary>The server to connect to, by name; null for the default (or <c>$JACK_DEFAULT_SERVER</c>). Not supported
    /// on ARM64 Apple platforms, where libjack's variadic argument can't be passed.</summary>
    public string? ServerName { get; init; }
}

public enum Direction
{
    /// <summary>A port that receives: a client's input, or a playback port.</summary>
    In,
    /// <summary>A port that sends: a client's output, or a capture port.</summary>
    Out,
}

public enum PortType
{
    Unknown,
    Audio,
    Midi,
}

/// <summary>Which way latency is measured: from the capture side, or towards the playback side.</summary>
public enum LatencyMode
{
    /// <summary>How long since the data arriving at a port was captured.</summary>
    Capture = 0,
    /// <summary>How long until the data leaving a port is played back.</summary>
    Playback = 1,
}

/// <summary>A latency range, in frames.</summary>
public readonly record struct LatencyRange(uint Min, uint Max);

/// <summary>
/// A port anywhere in the JACK graph, as it was when it was looked up.
/// </summary>
/// <param name="FullName">Its full name, <c>client:port</c>, as connections name it.</param>
/// <param name="Type">Audio, MIDI, or another type JACK has.</param>
/// <param name="Direction">In for a port that receives (a playback port), Out for one that sends (a capture port).</param>
/// <param name="IsPhysical">Whether it's a hardware port: a sound card's channel.</param>
/// <param name="IsTerminal">Whether its data goes nowhere further (or comes from nowhere earlier) in the graph.</param>
/// <param name="CanMonitor">Whether it supports input monitoring.</param>
/// <param name="Aliases">Other names for it: JACK2 gives physical ports their device's channel names here.</param>
public sealed record PortInfo(
    string FullName,
    PortType Type,
    Direction Direction,
    bool IsPhysical,
    bool IsTerminal,
    bool CanMonitor,
    IReadOnlyList<string> Aliases)
{
    /// <summary>The client's part of the name, before the colon.</summary>
    public string ClientName => FullName.Split(':', 2)[0];

    /// <summary>The port's part of the name, after the colon.</summary>
    public string ShortName => FullName.Split(':', 2) is { Length: 2 } parts ? parts[1] : FullName;
}

/// <summary>A change to a frame count: the server's sample rate or buffer size.</summary>
public sealed class FramesEventArgs(int frames) : EventArgs
{
    public int Frames { get; } = frames;
}

/// <summary>An xrun: a cycle that missed its deadline.</summary>
public sealed class XrunEventArgs(float delayedMicroseconds) : EventArgs
{
    /// <summary>How late the cycle was, in microseconds, as JACK measured it.</summary>
    public float DelayedMicroseconds { get; } = delayedMicroseconds;
}

/// <summary>The server shut down, or threw this client out.</summary>
public sealed class ShutdownEventArgs(JackStatus status, string reason) : EventArgs
{
    public JackStatus Status { get; } = status;
    public string Reason { get; } = reason;
}

/// <summary>A client joined or left the graph.</summary>
public sealed class ClientRegistrationEventArgs(string clientName, bool registered) : EventArgs
{
    public string ClientName { get; } = clientName;
    /// <summary>True when it joined, false when it left.</summary>
    public bool Registered { get; } = registered;
}

/// <summary>A port was registered or unregistered.</summary>
public sealed class PortRegistrationEventArgs(string fullName, bool registered) : EventArgs
{
    public string FullName { get; } = fullName;
    /// <summary>True when it appeared, false when it went.</summary>
    public bool Registered { get; } = registered;
}

/// <summary>A port was renamed.</summary>
public sealed class PortRenameEventArgs(string oldName, string newName) : EventArgs
{
    public string OldName { get; } = oldName;
    public string NewName { get; } = newName;
}

/// <summary>Two ports were connected or disconnected.</summary>
public sealed class ConnectionEventArgs(string source, string destination, bool connected) : EventArgs
{
    /// <summary>The output side.</summary>
    public string Source { get; } = source;
    /// <summary>The input side.</summary>
    public string Destination { get; } = destination;
    public bool Connected { get; } = connected;
}
