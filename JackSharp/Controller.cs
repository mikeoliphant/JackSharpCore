namespace JackSharp;

/// <summary>
/// A JACK client without ports, for programs that watch and patch the graph rather than process audio: patchbays,
/// monitors, session managers. It's active as soon as it's constructed, since a client receives the graph's events
/// only while active.
/// </summary>
public sealed class Controller : Client
{
    public Controller(string name, ClientOptions? options = null) : base(name, options)
    {
        Activate();
    }
}
