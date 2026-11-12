using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JackSharp;

/// <summary>
/// A client of a JACK server: what every JACK client can do. <see cref="Processor"/> adds ports and audio processing;
/// <see cref="Controller"/> is a client without ports, for patchbays and monitors.
/// </summary>
/// <remarks>
/// <para>A client opens when it's constructed (or throws a <see cref="JackException"/>, whose
/// <see cref="JackException.NoServer"/> says whether no server is running), and starts taking part in the graph when
/// it's activated. Dispose it to close it: an open client keeps itself alive, since the server can call it at any
/// time.</para>
/// <para>Its events arrive on JACK's notification thread, never the real-time process thread. A handler may call the
/// client's methods, but must not block for long. Disposing the client from a handler is allowed: it closes once the
/// handler returns.</para>
/// </remarks>
public abstract class Client : IDisposable
{
    private readonly object sync = new();
    private IntPtr client;
    private GCHandle self;
    private volatile bool connected;
    private bool active;
    private long activatedAt;   // Stopwatch timestamp of the last activation.
    private int sampleRate, bufferSize;

    // JACK2 1.9.22 on Windows can deadlock in jack_client_close when a client closes within milliseconds of activating
    // (jack2 PR #889, unmerged; the tests hit it, real programs rarely do): a client stays active at least this long.
    private static readonly TimeSpan MinimumActiveTime = TimeSpan.FromMilliseconds(100);

    // Set while this thread is inside one of JACK's callbacks: libjack forbids closing a client from there.
    [ThreadStatic]
    private static int inCallback;

    /// <summary>Opens a client with this name (which JACK may change, see <see cref="Name"/>).</summary>
    protected unsafe Client(string name, ClientOptions? options)
    {
        JackLibrary.Require();
        options ??= new ClientOptions();
        int flags = (options.StartServer ? 0 : JackConstants.NoStartServer) | (options.UseExactName ? JackConstants.UseExactName : 0);
        int status = 0;
        if (options.ServerName != null)
        {
            if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                throw new PlatformNotSupportedException("Choosing a JACK server by name isn't supported on ARM64 macOS: set JACK_DEFAULT_SERVER instead.");
            client = Native.jack_client_open_server(name, flags | JackConstants.ServerName, &status, options.ServerName);
        }
        else
            client = Native.jack_client_open(name, flags, &status);
        if (client == IntPtr.Zero)
            throw new JackException(Describe((JackStatus)status, name), (JackStatus)status);

        connected = true;
        Name = Native.String(Native.jack_get_client_name(client)) ?? name;
        if (options.UseExactName && Name != name)
        {
            // Asked for this name exactly, and given another: refuse, as JACK should have.
            Native.jack_client_close(client);
            client = IntPtr.Zero;
            throw new JackException($"A JACK client named '{name}' already exists.", JackStatus.NameNotUnique | JackStatus.Failure);
        }
        sampleRate = (int)Native.jack_get_sample_rate(client);
        bufferSize = (int)Native.jack_get_buffer_size(client);
        self = GCHandle.Alloc(this);
        IntPtr arg = GCHandle.ToIntPtr(self);
        Native.jack_on_info_shutdown(client, &OnShutdown, arg);
        Native.jack_set_buffer_size_callback(client, &OnBufferSize, arg);
        Native.jack_set_sample_rate_callback(client, &OnSampleRate, arg);
        Native.jack_set_xrun_callback(client, &OnXrun, arg);
        Native.jack_set_graph_order_callback(client, &OnGraphOrder, arg);
        Native.jack_set_client_registration_callback(client, &OnClientRegistration, arg);
        Native.jack_set_port_registration_callback(client, &OnPortRegistration, arg);
        Native.jack_set_port_rename_callback(client, &OnPortRename, arg);
        Native.jack_set_port_connect_callback(client, &OnPortConnect, arg);
    }

    private static string Describe(JackStatus status, string name)
    {
        if (status.HasFlag(JackStatus.ServerFailed))
            return "No JACK server is running" + (status.HasFlag(JackStatus.ServerStarted) ? "." : " (and none was started).");
        if (status.HasFlag(JackStatus.NameNotUnique))
            return $"A JACK client named '{name}' already exists.";
        if (status.HasFlag(JackStatus.VersionError))
            return "The JACK client library doesn't match the server's version.";
        if (status.HasFlag(JackStatus.ShmFailure))
            return "Couldn't reach the JACK server's shared memory.";
        return $"Couldn't open a JACK client ({status}).";
    }

    /// <summary>The client's name in the graph: the name asked for, or the one JACK gave it if that was taken.</summary>
    public string Name { get; }

    /// <summary>The native <c>jack_client_t*</c>, for subclasses and native interop.</summary>
    protected IntPtr Handle
    {
        get
        {
            IntPtr h = client;
            return h != IntPtr.Zero ? h : throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <summary>The <see cref="GCHandle"/> pointer callbacks receive, for subclasses that set their own.</summary>
    protected IntPtr CallbackArgument => GCHandle.ToIntPtr(self);

    /// <summary>Whether the client is still connected to its server: false after the server shut down (or threw the
    /// client out), or once it's disposed.</summary>
    public bool IsConnected => connected;

    /// <summary>Whether the client is active: taking part in the graph, its process callback running.</summary>
    public bool IsActive => active && connected;

    // ---- Lifecycle ----

    /// <summary>Starts taking part in the graph. For a <see cref="Processor"/>, its process callback starts running.</summary>
    public void Activate()
    {
        lock (sync)
        {
            if (active)
                return;
            OnActivating();
            int result = Native.jack_activate(Handle);
            if (result != 0)
                throw new JackException($"Couldn't activate the JACK client '{Name}' (error {result}).");
            active = true;
            activatedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Called just before activating, while the client is still inactive.</summary>
    protected virtual void OnActivating() { }

    /// <summary>Stops taking part in the graph: the process callback stops, and the client's ports lose their
    /// connections.</summary>
    public void Deactivate()
    {
        lock (sync)
        {
            if (!active)
                return;
            active = false;
            if (connected && client != IntPtr.Zero)
            {
                WaitOutActivation();
                Native.jack_deactivate(client);
            }
        }
    }

    /// <summary>Closes the client. Safe after the server has shut down, and from the client's own event handlers.</summary>
    public void Dispose()
    {
        if (inCallback > 0)
        {
            // libjack can't close a client from inside one of its callbacks: close it once this one has returned.
            ThreadPool.QueueUserWorkItem(_ => Dispose());
            return;
        }
        IntPtr closing;
        lock (sync)
        {
            if (client == IntPtr.Zero)
                return;
            if (active && connected)
            {
                WaitOutActivation();
                Native.jack_deactivate(client);
            }
            active = false;
            closing = client;
            client = IntPtr.Zero;
        }
        // After a shutdown, libjack leaves the client for the program to close.
        Native.jack_client_close(closing);
        connected = false;
        OnClosed();
        if (self.IsAllocated)
            self.Free();
        GC.SuppressFinalize(this);
    }

    private void WaitOutActivation()
    {
        TimeSpan since = System.Diagnostics.Stopwatch.GetElapsedTime(activatedAt);
        if (since < MinimumActiveTime)
            Thread.Sleep(MinimumActiveTime - since);
    }

    /// <summary>Called once the client is closed, for subclasses to let go of what referred to it.</summary>
    protected virtual void OnClosed() { }

    // ---- The server ----

    /// <summary>The server's sample rate, in Hz.</summary>
    public int SampleRate => sampleRate;

    /// <summary>The server's buffer size: frames per process cycle.</summary>
    public int BufferSize => bufferSize;

    /// <summary>The server's DSP load, as a percentage of each cycle.</summary>
    public float CpuLoad => Native.jack_cpu_load(Handle);

    /// <summary>Whether the server runs its process cycles in real time (with real-time scheduling).</summary>
    public bool IsRealtime => Native.jack_is_realtime(Handle) != 0;

    /// <summary>The server's frame time now, an estimate between cycles.</summary>
    public uint FrameTime => Native.jack_frame_time(Handle);

    /// <summary>
    /// Sets the server's buffer size. This changes it for every client of the server, and makes every one reconfigure:
    /// it's the user's setting, so prefer to report <see cref="BufferSize"/> and leave this to them.
    /// </summary>
    public void SetBufferSize(int frames)
    {
        int result = Native.jack_set_buffer_size(Handle, (uint)frames);
        if (result != 0)
            throw new JackException($"The JACK server refused a buffer size of {frames} frames (error {result}).");
    }

    // ---- The graph ----

    /// <summary>
    /// The ports in the graph that match. <paramref name="namePattern"/> is a regular expression over full names
    /// (<c>client:port</c>); null matches every port.
    /// </summary>
    public IReadOnlyList<PortInfo> GetPorts(string? namePattern = null, PortType? type = null, Direction? direction = null, bool physicalOnly = false)
    {
        int flags = (direction == Direction.In ? JackConstants.PortIsInput : 0)
            | (direction == Direction.Out ? JackConstants.PortIsOutput : 0)
            | (physicalOnly ? JackConstants.PortIsPhysical : 0);
        string? typePattern = type switch
        {
            PortType.Audio => JackConstants.AudioType,
            PortType.Midi => JackConstants.MidiType,
            _ => null,
        };
        IntPtr h = Handle;
        var ports = new List<PortInfo>();
        foreach (string name in Native.TakeStrings(Native.jack_get_ports(h, namePattern, typePattern, new CULong((uint)flags))))
            if (InfoOf(Native.jack_port_by_name(h, name)) is PortInfo info && (type != PortType.Unknown || info.Type == PortType.Unknown))
                ports.Add(info);
        return ports;
    }

    /// <summary>A port by its full name, or null if there's none.</summary>
    public PortInfo? GetPort(string fullName) => InfoOf(Native.jack_port_by_name(Handle, fullName));

    /// <summary>The full names of the ports connected to this one.</summary>
    public IReadOnlyList<string> GetConnections(string fullName)
    {
        IntPtr h = Handle;
        IntPtr port = Native.jack_port_by_name(h, fullName);
        if (port == IntPtr.Zero)
            throw new JackException($"There's no JACK port '{fullName}'.");
        return Native.TakeStrings(Native.jack_port_get_all_connections(h, port));
    }

    /// <summary>
    /// Connects an output port (<paramref name="source"/>) to an input port. Returns true if they're connected
    /// (including if they already were), false if JACK refused: a port that doesn't exist, the wrong directions, or
    /// different types.
    /// </summary>
    public bool Connect(string source, string destination)
    {
        int result = Native.jack_connect(Handle, source, destination);
        return result == 0 || result == JackConstants.AlreadyConnected;
    }

    /// <summary>Disconnects two ports. Returns false if they weren't connected, or don't exist.</summary>
    public bool Disconnect(string source, string destination) => Native.jack_disconnect(Handle, source, destination) == 0;

    /// <summary>A port's latency, by its full name.</summary>
    public unsafe LatencyRange GetLatency(string fullName, LatencyMode mode)
    {
        IntPtr port = Native.jack_port_by_name(Handle, fullName);
        if (port == IntPtr.Zero)
            throw new JackException($"There's no JACK port '{fullName}'.");
        LatencyRangeNative range;
        Native.jack_port_get_latency_range(port, (int)mode, &range);
        return new LatencyRange(range.Min, range.Max);
    }

    internal unsafe PortInfo? InfoOf(IntPtr port)
    {
        if (port == IntPtr.Zero)
            return null;
        string name = Native.String(Native.jack_port_name(port)) ?? "";
        int flags = Native.jack_port_flags(port);
        PortType type = Native.String(Native.jack_port_type(port)) switch
        {
            JackConstants.AudioType => PortType.Audio,
            JackConstants.MidiType => PortType.Midi,
            _ => PortType.Unknown,
        };
        // Aliases: up to two, into buffers of the longest port name.
        var aliases = new List<string>();
        int size = Native.jack_port_name_size();
        IntPtr first = Marshal.AllocHGlobal(size), second = Marshal.AllocHGlobal(size);
        try
        {
            IntPtr* buffers = stackalloc IntPtr[2] { first, second };
            int count = Native.jack_port_get_aliases(port, buffers);
            for (int i = 0; i < count && i < 2; i++)
                if (Native.String(buffers[i]) is { Length: > 0 } alias)
                    aliases.Add(alias);
        }
        finally
        {
            Marshal.FreeHGlobal(first);
            Marshal.FreeHGlobal(second);
        }
        return new PortInfo(name, type, (flags & JackConstants.PortIsInput) != 0 ? Direction.In : Direction.Out,
            (flags & JackConstants.PortIsPhysical) != 0, (flags & JackConstants.PortIsTerminal) != 0,
            (flags & JackConstants.PortCanMonitor) != 0, aliases);
    }

    private string PortNameById(uint id) =>
        client is var h && h != IntPtr.Zero && Native.jack_port_by_id(h, id) is var port && port != IntPtr.Zero
            ? Native.String(Native.jack_port_name(port)) ?? ""
            : "";

    // ---- Events, on JACK's notification thread ----

    /// <summary>The server's sample rate changed.</summary>
    public event EventHandler<FramesEventArgs>? SampleRateChanged;

    /// <summary>The server's buffer size changed.</summary>
    public event EventHandler<FramesEventArgs>? BufferSizeChanged;

    /// <summary>A cycle missed its deadline.</summary>
    public event EventHandler<XrunEventArgs>? Xrun;

    /// <summary>The server shut down, or threw this client out. The client can't be used after it, but should still
    /// be disposed.</summary>
    public event EventHandler<ShutdownEventArgs>? Shutdown;

    /// <summary>A client joined or left the graph.</summary>
    public event EventHandler<ClientRegistrationEventArgs>? ClientRegistered;

    /// <summary>A port was registered or unregistered, by any client.</summary>
    public event EventHandler<PortRegistrationEventArgs>? PortRegistered;

    /// <summary>A port was renamed.</summary>
    public event EventHandler<PortRenameEventArgs>? PortRenamed;

    /// <summary>Two ports were connected or disconnected, by any client.</summary>
    public event EventHandler<ConnectionEventArgs>? PortsConnected;

    /// <summary>The graph's processing order changed.</summary>
    public event EventHandler? GraphReordered;

    private static Client? From(IntPtr arg) => arg == IntPtr.Zero ? null : GCHandle.FromIntPtr(arg).Target as Client;

    // Runs a handler as a JACK callback: never unwinding into libjack, and marked so Dispose can tell.
    private static void Notify(IntPtr arg, Action<Client> handler)
    {
        if (From(arg) is not Client c)
            return;
        inCallback++;
        try { handler(c); }
        catch (Exception) { /* A handler's exception mustn't unwind into libjack. */ }
        finally { inCallback--; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnShutdown(int status, IntPtr reason, IntPtr arg) => Notify(arg, c =>
    {
        c.connected = false;
        c.Shutdown?.Invoke(c, new ShutdownEventArgs((JackStatus)status, Native.String(reason) ?? ""));
    });

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnBufferSize(uint frames, IntPtr arg)
    {
        Notify(arg, c =>
        {
            c.bufferSize = (int)frames;
            c.BufferSizeChanged?.Invoke(c, new FramesEventArgs((int)frames));
        });
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnSampleRate(uint rate, IntPtr arg)
    {
        Notify(arg, c =>
        {
            c.sampleRate = (int)rate;
            c.SampleRateChanged?.Invoke(c, new FramesEventArgs((int)rate));
        });
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnXrun(IntPtr arg)
    {
        Notify(arg, c =>
        {
            if (c.Xrun is { } handler && c.client is var h && h != IntPtr.Zero)
                handler(c, new XrunEventArgs(Native.jack_get_xrun_delayed_usecs(h)));
        });
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnGraphOrder(IntPtr arg)
    {
        Notify(arg, c => c.GraphReordered?.Invoke(c, EventArgs.Empty));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnClientRegistration(IntPtr name, int register, IntPtr arg) => Notify(arg, c =>
        c.ClientRegistered?.Invoke(c, new ClientRegistrationEventArgs(Native.String(name) ?? "", register != 0)));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPortRegistration(uint port, int register, IntPtr arg) => Notify(arg, c =>
    {
        if (c.PortRegistered is { } handler)
            handler(c, new PortRegistrationEventArgs(c.PortNameById(port), register != 0));
    });

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPortRename(uint port, IntPtr oldName, IntPtr newName, IntPtr arg) => Notify(arg, c =>
        c.PortRenamed?.Invoke(c, new PortRenameEventArgs(Native.String(oldName) ?? "", Native.String(newName) ?? "")));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPortConnect(uint a, uint b, int connect, IntPtr arg) => Notify(arg, c =>
    {
        if (c.PortsConnected is not { } handler || c.client is var h && h == IntPtr.Zero)
            return;
        IntPtr first = Native.jack_port_by_id(h, a), second = Native.jack_port_by_id(h, b);
        string firstName = first != IntPtr.Zero ? Native.String(Native.jack_port_name(first)) ?? "" : "";
        string secondName = second != IntPtr.Zero ? Native.String(Native.jack_port_name(second)) ?? "" : "";
        // JACK names the two ports in no particular order: the source is the output.
        bool firstIsOutput = first != IntPtr.Zero && (Native.jack_port_flags(first) & JackConstants.PortIsOutput) != 0;
        handler(c, firstIsOutput
            ? new ConnectionEventArgs(firstName, secondName, connect != 0)
            : new ConnectionEventArgs(secondName, firstName, connect != 0));
    });

    /// <summary>Marks the current thread as inside a JACK callback, for subclasses' own callbacks.</summary>
    protected static void EnterCallback() => inCallback++;

    protected static void LeaveCallback() => inCallback--;

    /// <summary>The client a callback argument refers to, for subclasses' own callbacks.</summary>
    protected static T? FromCallbackArgument<T>(IntPtr arg) where T : Client => From(arg) as T;
}
