using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace JackSharp;

/// <summary>
/// Finding and loading the native JACK client library (libjack), and JACK's own error and information messages.
/// </summary>
/// <remarks>
/// The library is found the first time JACK is used. In order, it tries:
/// <list type="number">
/// <item>the path in <see cref="Path"/>, if set before then;</item>
/// <item>the path in the <c>JACK_LIBRARY</c> environment variable;</item>
/// <item>the platform's usual names on the system's search path: <c>libjack64.dll</c> on 64-bit Windows (JACK2 installs
/// it in the Windows folder), <c>libjack.so.0</c> then <c>libjack.so</c> on Linux, <c>libjack.0.dylib</c> on
/// macOS;</item>
/// <item>the usual install folders: JACK2's on Windows; PipeWire's JACK libraries on Linux; Homebrew's and MacPorts' on
/// macOS.</item>
/// </list>
/// A client library should match the server it talks to, so the system's comes before any other a program may bundle.
/// </remarks>
public static class JackLibrary
{
    internal const string Name = "libjack";

    private static readonly object sync = new();
    private static IntPtr handle;
    private static bool tried;
    private static string? path;

    /// <summary>The library to load, in place of searching: set it before JACK is first used.</summary>
    public static string? Path
    {
        get => path;
        set
        {
            lock (sync)
            {
                if (tried)
                    throw new InvalidOperationException("The JACK library is already loaded: set JackLibrary.Path before using JACK.");
                path = value;
            }
        }
    }

    /// <summary>Where the library was loaded from (or the name the system resolved), once it's loaded.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>Whether a JACK client library can be loaded. It doesn't say whether a JACK server is running: opening
    /// a <see cref="Client"/> does.</summary>
    public static bool IsAvailable => Load() != IntPtr.Zero;

    private static int resolving;

    /// <summary>Makes this assembly's calls into libjack resolve through <see cref="Load"/>. Called before the first
    /// call (from <see cref="Native"/>'s static constructor), once.</summary>
    internal static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref resolving, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(JackLibrary).Assembly, (name, _, _) => name == Name ? Load() : IntPtr.Zero);
    }

    internal static IntPtr Load()
    {
        EnsureResolver();
        lock (sync)
        {
            if (tried)
                return handle;
            tried = true;
            foreach (string candidate in Candidates())
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    LoadedFrom = candidate;
                    return handle;
                }
            return handle = IntPtr.Zero;
        }
    }

    /// <summary>Loads the library, or throws a <see cref="DllNotFoundException"/> that lists where it looked.</summary>
    internal static void Require()
    {
        if (Load() == IntPtr.Zero)
            throw new DllNotFoundException("The JACK client library (libjack) wasn't found. Install JACK (or PipeWire's JACK support), " +
                "or set JackLibrary.Path or JACK_LIBRARY. Looked for: " + string.Join(", ", Candidates()));
    }

    private static IEnumerable<string> Candidates()
    {
        if (path != null)
        {
            yield return path;
            yield break;
        }
        if (Environment.GetEnvironmentVariable("JACK_LIBRARY") is { Length: > 0 } fromEnvironment)
            yield return fromEnvironment;

        if (OperatingSystem.IsWindows())
        {
            string file = Environment.Is64BitProcess ? "libjack64.dll" : "libjack.dll";
            yield return file;
            yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), file);
            foreach (string programs in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (programs.Length > 0)
                {
                    yield return System.IO.Path.Combine(programs, "JACK2", file);
                    yield return System.IO.Path.Combine(programs, "Jack", file);
                }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "libjack.0.dylib";
            yield return "libjack.dylib";
            foreach (string folder in new[] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib" })
                yield return System.IO.Path.Combine(folder, "libjack.0.dylib");
        }
        else
        {
            yield return "libjack.so.0";
            yield return "libjack.so";
            // PipeWire's JACK libraries, where the distribution keeps them out of the default search path.
            foreach (string folder in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib" })
                yield return System.IO.Path.Combine(folder, "pipewire-0.3", "jack", "libjack.so.0");
        }
    }

    // ---- JACK's messages ----

    private static Action<string>? errorHandler, infoHandler;

    /// <summary>
    /// Where JACK's error and information messages go, for the whole process. By default libjack prints them to the
    /// console; a handler receives them instead, and null discards them. They can arrive on any thread.
    /// </summary>
    public static unsafe void SetMessageHandlers(Action<string>? error, Action<string>? info)
    {
        Require();
        errorHandler = error;
        infoHandler = info;
        Native.jack_set_error_function(&OnError);
        Native.jack_set_info_function(&OnInfo);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnError(IntPtr message)
    {
        try { errorHandler?.Invoke(Marshal.PtrToStringUTF8(message) ?? ""); }
        catch (Exception) { /* Never unwind into libjack. */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInfo(IntPtr message)
    {
        try { infoHandler?.Invoke(Marshal.PtrToStringUTF8(message) ?? ""); }
        catch (Exception) { /* Never unwind into libjack. */ }
    }
}
