using System.Diagnostics;
using JackSharp;
using NUnit.Framework;

namespace JackSharpTest;

/// <summary>
/// The JACK server the tests run against: one that's already running, or else one started here on JACK's dummy backend
/// (a timer, with no sound card) and stopped when the tests finish. Without either, the tests are skipped.
/// </summary>
[SetUpFixture]
public sealed class TestServer
{
    private static Process? started;
    private static string? unavailable;

    /// <summary>Where jackd is: JACK2's install folder on Windows, else the search path.</summary>
    public static string? FindJackd()
    {
        if (OperatingSystem.IsWindows())
            foreach (string programs in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string path = Path.Combine(programs, "JACK2", "jackd.exe");
                if (File.Exists(path))
                    return path;
            }
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string name in new[] { "jackd", "jackd.exe" })
                if (folder.Length > 0 && File.Exists(Path.Combine(folder, name)))
                    return Path.Combine(folder, name);
        return null;
    }

    /// <summary>Starts jackd on the dummy backend, under a server name (null for the default), and waits until a client
    /// can connect. Returns null if it couldn't.</summary>
    public static Process? StartDummy(string? serverName, int rate = 48000, int frames = 256)
    {
        string? jackd = FindJackd();
        if (jackd == null)
            return null;
        string name = serverName != null ? $"-n {serverName} " : "";
        var process = Process.Start(new ProcessStartInfo(jackd, $"{name}-d dummy -r {rate} -p {frames} -C 2 -P 2")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process == null)
            return null;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until && !process.HasExited)
        {
            if (CanConnect(serverName))
                return process;
            Thread.Sleep(200);
        }
        Stop(process);
        return null;
    }

    public static void Stop(Process? process)
    {
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    private static bool CanConnect(string? serverName)
    {
        try
        {
            using var probe = new Controller("jacksharp-probe", new ClientOptions { ServerName = serverName });
            return true;
        }
        catch (JackException)
        {
            return false;
        }
    }

    [OneTimeSetUp]
    public void Start()
    {
        if (!JackLibrary.IsAvailable)
        {
            unavailable = "The JACK client library isn't installed.";
            return;
        }
        // Quiet: libjack's messages about a missing server are expected while probing.
        JackLibrary.SetMessageHandlers(null, null);
        if (CanConnect(null))
            return;
        started = StartDummy(null);
        if (started == null)
            unavailable = "No JACK server is running, and jackd couldn't be started on the dummy backend.";
    }

    [OneTimeTearDown]
    public void Finish()
    {
        Stop(started);
        started?.Dispose();
        started = null;
    }

    /// <summary>Whether the server was started here (so its settings are known: 48 kHz, 256 frames).</summary>
    public static bool StartedHere => started != null;

    /// <summary>Skips the test if there's no server to run it against.</summary>
    public static void Require()
    {
        if (unavailable != null)
            Assert.Ignore(unavailable);
    }

    /// <summary>Waits until a condition holds, or a timeout passes; returns whether it held.</summary>
    public static bool WaitFor(Func<bool> condition, int milliseconds = 3000)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            Thread.Sleep(10);
        }
        return condition();
    }
}
