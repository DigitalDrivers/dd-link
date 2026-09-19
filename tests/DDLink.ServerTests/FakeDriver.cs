using System.Net;
using System.Net.Sockets;
using System.Numerics;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets;
using AssettoServer.Shared.Network.Packets.Incoming;

namespace DDLink.ServerTests;

/// <summary>
/// A driver without a game: speaks Assetto Corsa's network protocol as far as the server needs it to
/// treat the connection as a car on track. Handshake and checksum over TCP, then position updates and
/// ping replies over UDP; laps are reported the way the game reports them.
/// </summary>
public sealed class FakeDriver : IAsyncDisposable
{
    public const byte Accepted = (byte)ACServerProtocol.NewCarConnection;
    public const byte NoSlot = (byte)ACServerProtocol.NoSlotsAvailable;

    private readonly TcpClient _tcp;
    private readonly UdpClient _udp;
    private readonly byte _sessionId;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private readonly byte[] _tcpBuffer = new byte[2048];
    private byte _sequence;
    private float _spline;
    private byte _lap;

    private FakeDriver(TcpClient tcp, UdpClient udp, byte sessionId)
    {
        _tcp = tcp;
        _udp = udp;
        _sessionId = sessionId;
    }

    /// <summary>
    /// Asks for a car. Returns the driver once the server treats it as connected and on track, or null
    /// with the server's answer (e.g. <see cref="NoSlot"/>) when it refuses.
    /// </summary>
    public static async Task<(FakeDriver? Driver, byte Answer)> JoinAsync(int port, ulong steamId, string name, string carModel, byte expectedSlot)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var buffer = new byte[2048];

        var writer = new PacketWriter(tcp.GetStream(), buffer);
        writer.WritePacket(new HandshakeRequest
        {
            ClientVersion = 202,
            Guid = steamId,
            Name = name,
            Team = "",
            Nation = "DE",
            RequestedCar = carModel,
            Password = "",
            // What a game with Custom Shaders Patch announces; the server insists on some of these. The last entry is the CSP build.
            Features = "SPECTATING_AWARE,EMOJI,SLOT_INDEX,CLIENT_MESSAGES,CLIENT_UDP_MESSAGES,WEATHERFX_V1,3465",
        });
        await writer.SendAsync();

        var answer = await ReadPacketAsync(tcp.GetStream());
        if (answer.Length == 0 || answer[0] != Accepted)
        {
            tcp.Dispose();
            // A refusal for a reason carries it as text: say it, a bare code is hard to act on.
            if (answer.Length > 2 && answer[0] == (byte)ACServerProtocol.AuthFailed)
                throw new InvalidOperationException($"The server refused {name}: {System.Text.Encoding.UTF32.GetString(answer, 2, answer[1] * 4)}");
            return (null, answer.Length == 0 ? (byte)0 : answer[0]);
        }

        // The test server has no car or track files, so there is nothing to checksum but the car itself.
        writer = new PacketWriter(tcp.GetStream(), buffer);
        writer.WritePacket(new ChecksumPacket { Checksum = new byte[16] });
        await writer.SendAsync();

        var udp = new UdpClient();
        udp.Connect(IPAddress.Loopback, port);
        var driver = new FakeDriver(tcp, udp, expectedSlot);
        await driver.AssociateUdpAsync();
        driver._loops.Add(driver.DriveAsync());
        driver._loops.Add(driver.AnswerPingsAsync());
        driver._loops.Add(driver.DrainTcpAsync());
        return (driver, Accepted);
    }

    // UDP gives no delivery guarantee, so the car announces itself until the server answers, as the game does.
    private async Task AssociateUdpAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await _udp.SendAsync(new byte[] { (byte)ACServerProtocol.CarConnect, _sessionId });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var reply = await _udp.ReceiveAsync(timeout.Token);
                if (reply.Buffer.Length > 0 && reply.Buffer[0] == (byte)ACServerProtocol.CarConnect)
                    return;
            }
            catch (OperationCanceledException)
            {
            }
        }
        throw new InvalidOperationException("The server did not accept the UDP connection of the car");
    }

    // 20 position updates a second: round the lap at 180 km/h, on a circle so the map has something to draw.
    private async Task DriveAsync()
    {
        var buffer = new byte[256];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(_stop.Token))
        {
            _spline = (_spline + 0.002f) % 1f;
            var angle = _spline * MathF.Tau;
            var update = new PositionUpdateIn(_sequence++, (uint)Environment.TickCount, new Vector3(MathF.Cos(angle) * 500, 0, MathF.Sin(angle) * 500),
                Vector3.Zero, new Vector3(50, 0, 0), 100, 100, 100, 100, 127, 127, 7200, 5, 0, 0, 220, _spline);
            var writer = new PacketWriter(buffer);
            var length = writer.WritePacket(update);
            await _udp.SendAsync(buffer.AsMemory(0, length), _stop.Token);
        }
    }

    // The server pings every second and drops a car that does not answer for 15 seconds.
    private async Task AnswerPingsAsync()
    {
        var buffer = new byte[16];
        while (!_stop.IsCancellationRequested)
        {
            var packet = await _udp.ReceiveAsync(_stop.Token);
            if (packet.Buffer.Length < 5 || packet.Buffer[0] != (byte)ACServerProtocol.PingUpdate)
                continue;
            var writer = new PacketWriter(buffer);
            var length = writer.WritePacket(new PingResponse { Time = BitConverter.ToInt32(packet.Buffer, 1), ClientTime = Environment.TickCount });
            await _udp.SendAsync(buffer.AsMemory(0, length), _stop.Token);
        }
    }

    // Car lists, session updates, chat: read and ignored, so the server's send buffer never fills up.
    private async Task DrainTcpAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            if ((await ReadPacketAsync(_tcp.GetStream(), _stop.Token)).Length == 0)
                return;
        }
    }

    /// <summary>Crosses the line: reports a completed lap like the game does.</summary>
    public async Task CompleteLapAsync(uint lapTimeMs)
    {
        var writer = new PacketWriter(_tcp.GetStream(), _tcpBuffer);
        writer.Write((byte)ACServerProtocol.LapCompleted);
        writer.Write((uint)Environment.TickCount);
        writer.Write(lapTimeMs);
        writer.Write<byte>(0); // no sector splits
        writer.Write<byte>(0); // no cuts
        writer.Write(++_lap);
        await writer.SendAsync();
        _spline = 0;
    }

    /// <summary>Leaves the server the clean way, as when the driver clicks "exit" in the pits.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await Task.WhenAll(_loops); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }

        try
        {
            var writer = new PacketWriter(_tcp.GetStream(), _tcpBuffer);
            writer.Write((byte)ACServerProtocol.CleanExitDrive);
            await writer.SendAsync();
        }
        catch (IOException) { }
        _udp.Dispose();
        _tcp.Dispose();
    }

    // TCP packets are prefixed with their length as an unsigned 16-bit number.
    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream, CancellationToken ct = default)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, ct))
            return [];
        var body = new byte[BitConverter.ToUInt16(header)];
        return await ReadExactAsync(stream, body, ct) ? body : [];
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] target, CancellationToken ct)
    {
        var read = 0;
        while (read < target.Length)
        {
            var count = await stream.ReadAsync(target.AsMemory(read), ct);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }
}
