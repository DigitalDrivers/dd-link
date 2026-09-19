using System.Diagnostics;
using System.Text;
using DDLink.Core.Tests;

namespace DDLink.ServerTests;

/// <summary>
/// A real AssettoServer process with the plugin loaded, in a temporary working directory, and a local
/// HTTP receiver standing in for the platform. One short race that loops; no car or track files.
/// </summary>
public sealed class RaceServer : IAsyncDisposable
{
    public const string Secret = "server-test-secret-server-test-secret-01";
    public const string Car = "ks_porsche_911_gt3_cup_2017";
    public const ulong Anna = 76561198000000001;
    public const ulong Ben = 76561198000000002;
    public const ulong Cleo = 76561198000000003;
    public const ulong Stranger = 76561198000000009;

    private readonly Process _process;
    private readonly string _directory;
    private readonly StringBuilder _log = new();
    public int GamePort { get; }
    public TestReceiver Receiver { get; }
    public string Log { get { lock (_log) return _log.ToString(); } }

    private RaceServer(Process process, string directory, int gamePort, TestReceiver receiver)
    {
        _process = process;
        _directory = directory;
        GamePort = gamePort;
        Receiver = receiver;
    }

    public static async Task<RaceServer> StartAsync(int raceLaps)
    {
        var serverDll = Environment.GetEnvironmentVariable("DDLINK_SERVER_DLL") ?? throw new InvalidOperationException("DDLINK_SERVER_DLL is not set; run scripts/check.sh");
        var pluginDir = Environment.GetEnvironmentVariable("DDLINK_PLUGIN_DIR") ?? throw new InvalidOperationException("DDLINK_PLUGIN_DIR is not set; run scripts/check.sh");

        var directory = Directory.CreateTempSubdirectory("dd-link-server-").FullName;
        var preset = Directory.CreateDirectory(Path.Combine(directory, "presets", "test")).FullName;
        CopyDirectory(pluginDir, Path.Combine(directory, "plugins", "DDLinkPlugin"));

        var gamePort = TestReceiver.FreePort();
        var httpPort = TestReceiver.FreePort();
        var receiverPort = TestReceiver.FreePort();
        var receiver = new TestReceiver(receiverPort);

        File.WriteAllText(Path.Combine(preset, "server_cfg.ini"), $"""
            [SERVER]
            NAME=dd-link server test
            TRACK=ks_nurburgring
            CONFIG_TRACK=layout_gp_a
            PASSWORD=
            ADMIN_PASSWORD=server-test-admin
            UDP_PORT={gamePort}
            TCP_PORT={gamePort}
            HTTP_PORT={httpPort}
            MAX_CLIENTS=2
            CLIENT_SEND_INTERVAL_HZ=20
            REGISTER_TO_LOBBY=0
            LOOP_MODE=1
            RACE_OVER_TIME=4
            RESULT_SCREEN_TIME=2

            [RACE]
            NAME=Race
            LAPS={raceLaps}
            WAIT_TIME=3
            IS_OPEN=1

            [WEATHER_0]
            GRAPHICS=3_clear
            BASE_TEMPERATURE_AMBIENT=20
            BASE_TEMPERATURE_ROAD=8
            VARIATION_AMBIENT=2
            VARIATION_ROAD=2
            """);
        // Anna and Ben are one crew in the first car, Cleo drives the second.
        File.WriteAllText(Path.Combine(preset, "entry_list.ini"), $"""
            [CAR_0]
            MODEL={Car}
            SKIN=
            GUID={Anna};{Ben}

            [CAR_1]
            MODEL={Car}
            SKIN=
            GUID={Cleo}
            """);
        File.WriteAllText(Path.Combine(preset, "extra_cfg.yml"), """
            UseSteamAuth: false
            EnablePlugins:
              - DDLinkPlugin
            IgnoreConfigurationErrors:
              MissingCarChecksums: true
              MissingTrackParams: true
              UnsafeAdminWhitelist: true
            """);
        File.WriteAllText(Path.Combine(preset, "plugin_dd_link_cfg.yml"), $"""
            Endpoint: http://127.0.0.1:{receiverPort}/api/link/messages
            LiveEndpoint: http://127.0.0.1:{receiverPort}/api/link/live
            LiveIntervalMilliseconds: 250
            Secret: {Secret}
            EventId: server-test-event
            ServerId: server-test
            SpoolDirectory: dd-link-spool
            """);

        // The server also looks for plugins next to its binary and wants that folder to exist.
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(serverDll)!, "plugins"));

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{serverDll}\" --plugins-from-workdir --preset test")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        var server = new RaceServer(process, directory, gamePort, receiver);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (server._log) server._log.AppendLine(line);
            if (line.Contains("Starting UDP server")) started.TrySetResult();
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var ready = await Task.WhenAny(started.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
        if (ready != started.Task)
        {
            var log = server.Log;
            await server.DisposeAsync();
            throw new InvalidOperationException($"The server did not start:\n{log}");
        }
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
        Receiver.Dispose();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
}
