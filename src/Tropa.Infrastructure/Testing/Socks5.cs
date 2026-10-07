using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Tropa.Core.Generation;

namespace Tropa.Infrastructure.Testing;

/// <summary>SOCKS5 отказал: код ответа RFC 1928 (1 — общий сбой, 4 — узел недоступен, 5 — соединение отклонено…).</summary>
public sealed class Socks5Exception(byte reply, string message) : Exception(message)
{
    public byte Reply { get; } = reply;
}

/// <summary>
/// Минимальный клиент SOCKS5 (RFC 1928/1929) для тестов: CONNECT и UDP ASSOCIATE с паролем.
/// Работает только с локальным временным ядром на 127.0.0.1.
/// </summary>
public static class Socks5
{
    public static async Task<TcpClient> OpenAsync(int proxyPort, LocalAuth auth, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, ct).ConfigureAwait(false);
            var s = client.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 2 }, ct).ConfigureAwait(false);
            var choice = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
            if (choice[0] != 5 || choice[1] != 2)
                throw new Socks5Exception(0xFF, "Прокси не принял вход по паролю.");

            var user = Encoding.UTF8.GetBytes(auth.Username);
            var pass = Encoding.UTF8.GetBytes(auth.Password);
            var authMsg = new byte[3 + user.Length + pass.Length];
            authMsg[0] = 1;
            authMsg[1] = (byte)user.Length;
            user.CopyTo(authMsg, 2);
            authMsg[2 + user.Length] = (byte)pass.Length;
            pass.CopyTo(authMsg, 3 + user.Length);
            await s.WriteAsync(authMsg, ct).ConfigureAwait(false);
            var authReply = await ReadExactAsync(s, 2, ct).ConfigureAwait(false);
            if (authReply[1] != 0)
                throw new Socks5Exception(0xFF, "Прокси отклонил пароль.");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>CONNECT к host:port. Возвращает адрес, который прокси сообщил в ответе.</summary>
    public static async Task<IPEndPoint?> ConnectAsync(NetworkStream s, string host, int port, CancellationToken ct)
    {
        await s.WriteAsync(Request(1, host, port), ct).ConfigureAwait(false);
        return await ReadReplyAsync(s, ct).ConfigureAwait(false);
    }

    /// <summary>UDP ASSOCIATE: возвращает адрес, куда отправлять UDP-датаграммы.</summary>
    public static async Task<IPEndPoint> UdpAssociateAsync(NetworkStream s, CancellationToken ct)
    {
        await s.WriteAsync(Request(3, "0.0.0.0", 0), ct).ConfigureAwait(false);
        var relay = await ReadReplyAsync(s, ct).ConfigureAwait(false)
            ?? throw new Socks5Exception(1, "Прокси не сообщил адрес для UDP.");
        // Многие прокси отвечают 0.0.0.0 — это значит «тот же адрес, что и у прокси».
        return relay.Address.Equals(IPAddress.Any) || relay.Address.Equals(IPAddress.IPv6Any)
            ? new IPEndPoint(IPAddress.Loopback, relay.Port)
            : relay;
    }

    /// <summary>Заголовок SOCKS5 для UDP-датаграммы: RSV(2) FRAG ATYP ADDR PORT + данные.</summary>
    public static byte[] WrapUdp(string host, int port, ReadOnlySpan<byte> payload)
    {
        var address = Address(host);
        var packet = new byte[3 + address.Length + 2 + payload.Length];
        address.CopyTo(packet, 3);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3 + address.Length), (ushort)port);
        payload.CopyTo(packet.AsSpan(3 + address.Length + 2));
        return packet;
    }

    /// <summary>Данные из UDP-датаграммы SOCKS5 (без заголовка). Пусто — датаграмма битая или фрагментированная.</summary>
    public static ReadOnlyMemory<byte> UnwrapUdp(byte[] datagram)
    {
        if (datagram.Length < 10 || datagram[2] != 0)
            return ReadOnlyMemory<byte>.Empty;
        var offset = datagram[3] switch
        {
            1 => 4 + 4,
            4 => 4 + 16,
            3 when datagram.Length > 5 => 4 + 1 + datagram[4],
            _ => -1,
        };
        return offset < 0 || offset + 2 > datagram.Length ? ReadOnlyMemory<byte>.Empty : datagram.AsMemory(offset + 2);
    }

    private static byte[] Request(byte command, string host, int port)
    {
        var address = Address(host);
        var req = new byte[3 + address.Length + 2];
        req[0] = 5;
        req[1] = command;
        address.CopyTo(req, 3);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(3 + address.Length), (ushort)port);
        return req;
    }

    /// <summary>ATYP + адрес. Домен передаём как домен: резолвит ядро (или сервер), а не система.</summary>
    private static byte[] Address(string host)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            return [(byte)(bytes.Length == 4 ? 1 : 4), .. bytes];
        }

        var name = Encoding.ASCII.GetBytes(host);
        if (name.Length > 255)
            throw new ArgumentException("Слишком длинное имя узла.", nameof(host));
        return [3, (byte)name.Length, .. name];
    }

    private static async Task<IPEndPoint?> ReadReplyAsync(NetworkStream s, CancellationToken ct)
    {
        var head = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        if (head[0] != 5)
            throw new Socks5Exception(0xFF, "Неверный ответ прокси.");
        if (head[1] != 0)
        {
            throw new Socks5Exception(head[1], head[1] switch
            {
                3 => "Сеть недоступна.",
                4 => "Узел недоступен.",
                5 => "Соединение отклонено.",
                6 => "Время ожидания истекло.",
                _ => "Прокси не смог установить соединение.",
            });
        }

        byte[] addr = head[3] switch
        {
            1 => await ReadExactAsync(s, 4, ct).ConfigureAwait(false),
            4 => await ReadExactAsync(s, 16, ct).ConfigureAwait(false),
            3 => await ReadExactAsync(s, (await ReadExactAsync(s, 1, ct).ConfigureAwait(false))[0], ct).ConfigureAwait(false),
            _ => throw new Socks5Exception(0xFF, "Неизвестный тип адреса в ответе прокси."),
        };
        var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(s, 2, ct).ConfigureAwait(false));
        return head[3] == 3 ? null : new IPEndPoint(new IPAddress(addr), port);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream s, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await s.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}

/// <summary>STUN Binding Request (RFC 5389) — тот же запрос, которым звонки узнают свой внешний адрес.</summary>
public static class Stun
{
    private const uint MagicCookie = 0x2112A442;

    public static (byte[] Packet, byte[] TransactionId) BindingRequest()
    {
        var id = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), MagicCookie);
        id.CopyTo(packet, 8);
        return (packet, id);
    }

    /// <summary>Успешный ответ на наш запрос: тип 0x0101, наш cookie и номер транзакции.</summary>
    public static bool IsSuccessResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transactionId) =>
        data.Length >= 20
        && BinaryPrimitives.ReadUInt16BigEndian(data) == 0x0101
        && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == MagicCookie
        && data.Slice(8, 12).SequenceEqual(transactionId);
}
