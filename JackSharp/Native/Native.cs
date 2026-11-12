using System.Runtime.InteropServices;

namespace JackSharp;

/// <summary>
/// libjack's functions, as generated source rather than runtime-built stubs. Opaque JACK types (<c>jack_client_t*</c>,
/// <c>jack_port_t*</c>, port buffers) are plain pointers; C <c>unsigned long</c> is <see cref="CULong"/>, which is 32
/// bits on Windows and 64 elsewhere. Callbacks are unmanaged function pointers.
/// </summary>
internal static unsafe partial class Native
{
    static Native() => JackLibrary.EnsureResolver();

    // ---- Clients ----

    [LibraryImport(JackLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr jack_client_open(string clientName, int options, int* status);

    // The same function with JackServerName's variadic argument. Passing it as a fourth fixed argument works where
    // variadic arguments go in registers as fixed ones do (x64, and ARM64 except on Apple platforms).
    [LibraryImport(JackLibrary.Name, EntryPoint = "jack_client_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr jack_client_open_server(string clientName, int options, int* status, string serverName);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_client_close(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial IntPtr jack_get_client_name(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_activate(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_deactivate(IntPtr client);

    // ---- Callbacks ----

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_process_callback(IntPtr client, delegate* unmanaged[Cdecl]<uint, IntPtr, int> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_on_info_shutdown(IntPtr client, delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_buffer_size_callback(IntPtr client, delegate* unmanaged[Cdecl]<uint, IntPtr, int> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_sample_rate_callback(IntPtr client, delegate* unmanaged[Cdecl]<uint, IntPtr, int> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_xrun_callback(IntPtr client, delegate* unmanaged[Cdecl]<IntPtr, int> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_graph_order_callback(IntPtr client, delegate* unmanaged[Cdecl]<IntPtr, int> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_client_registration_callback(IntPtr client, delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_port_registration_callback(IntPtr client, delegate* unmanaged[Cdecl]<uint, int, IntPtr, void> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_port_rename_callback(IntPtr client, delegate* unmanaged[Cdecl]<uint, IntPtr, IntPtr, IntPtr, void> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_port_connect_callback(IntPtr client, delegate* unmanaged[Cdecl]<uint, uint, int, IntPtr, void> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_latency_callback(IntPtr client, delegate* unmanaged[Cdecl]<int, IntPtr, void> callback, IntPtr arg);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_recompute_total_latencies(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_set_error_function(delegate* unmanaged[Cdecl]<IntPtr, void> callback);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_set_info_function(delegate* unmanaged[Cdecl]<IntPtr, void> callback);

    // ---- Server state ----

    [LibraryImport(JackLibrary.Name)]
    internal static partial uint jack_get_sample_rate(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial uint jack_get_buffer_size(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_set_buffer_size(IntPtr client, uint frames);

    [LibraryImport(JackLibrary.Name)]
    internal static partial float jack_cpu_load(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_is_realtime(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial uint jack_frame_time(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial uint jack_last_frame_time(IntPtr client);

    [LibraryImport(JackLibrary.Name)]
    internal static partial float jack_get_xrun_delayed_usecs(IntPtr client);

    // ---- Ports ----

    [LibraryImport(JackLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr jack_port_register(IntPtr client, string portName, string portType, CULong flags, CULong bufferSize);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_port_unregister(IntPtr client, IntPtr port);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void* jack_port_get_buffer(IntPtr port, uint frames);

    [LibraryImport(JackLibrary.Name)]
    internal static partial IntPtr jack_port_name(IntPtr port);

    [LibraryImport(JackLibrary.Name)]
    internal static partial IntPtr jack_port_short_name(IntPtr port);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_port_flags(IntPtr port);

    [LibraryImport(JackLibrary.Name)]
    internal static partial IntPtr jack_port_type(IntPtr port);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_port_connected(IntPtr port);

    [LibraryImport(JackLibrary.Name)]
    internal static partial IntPtr jack_port_get_all_connections(IntPtr client, IntPtr port);

    [LibraryImport(JackLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr jack_port_by_name(IntPtr client, string portName);

    [LibraryImport(JackLibrary.Name)]
    internal static partial IntPtr jack_port_by_id(IntPtr client, uint portId);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_port_get_aliases(IntPtr port, IntPtr* aliases);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_port_name_size();

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_port_get_latency_range(IntPtr port, int mode, LatencyRangeNative* range);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_port_set_latency_range(IntPtr port, int mode, LatencyRangeNative* range);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_port_disconnect(IntPtr client, IntPtr port);

    [LibraryImport(JackLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int jack_connect(IntPtr client, string sourcePort, string destinationPort);

    [LibraryImport(JackLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int jack_disconnect(IntPtr client, string sourcePort, string destinationPort);

    [LibraryImport(JackLibrary.Name, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr jack_get_ports(IntPtr client, string? portNamePattern, string? typeNamePattern, CULong flags);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_free(IntPtr pointer);

    // ---- MIDI ----

    [LibraryImport(JackLibrary.Name)]
    internal static partial uint jack_midi_get_event_count(void* portBuffer);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_midi_event_get(MidiEventNative* midiEvent, void* portBuffer, uint eventIndex);

    [LibraryImport(JackLibrary.Name)]
    internal static partial void jack_midi_clear_buffer(void* portBuffer);

    [LibraryImport(JackLibrary.Name)]
    internal static partial int jack_midi_event_write(void* portBuffer, uint time, byte* data, nuint dataSize);

    [LibraryImport(JackLibrary.Name)]
    internal static partial nuint jack_midi_max_event_size(void* portBuffer);

    [LibraryImport(JackLibrary.Name)]
    internal static partial uint jack_midi_get_lost_event_count(void* portBuffer);

    // ---- Helpers ----

    internal static string? String(IntPtr utf8) => utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);

    /// <summary>A NULL-terminated array of strings from libjack, read and then freed with jack_free.</summary>
    internal static List<string> TakeStrings(IntPtr array)
    {
        var strings = new List<string>();
        if (array == IntPtr.Zero)
            return strings;
        try
        {
            for (IntPtr* p = (IntPtr*)array; *p != IntPtr.Zero; p++)
                strings.Add(Marshal.PtrToStringUTF8(*p) ?? "");
        }
        finally
        {
            jack_free(array);
        }
        return strings;
    }
}

internal struct LatencyRangeNative
{
    public uint Min;
    public uint Max;
}

internal unsafe struct MidiEventNative
{
    public uint Time;
    public nuint Size;
    public byte* Buffer;
}

internal static class JackConstants
{
    public const string AudioType = "32 bit float mono audio";
    public const string MidiType = "8 bit raw midi";

    // JackOptions.
    public const int NoStartServer = 0x01;
    public const int UseExactName = 0x02;
    public const int ServerName = 0x04;

    // JackPortFlags.
    public const int PortIsInput = 0x1;
    public const int PortIsOutput = 0x2;
    public const int PortIsPhysical = 0x4;
    public const int PortCanMonitor = 0x8;
    public const int PortIsTerminal = 0x10;

    // jack_connect's result when the ports are already connected.
    public const int AlreadyConnected = 17;   // EEXIST
}
