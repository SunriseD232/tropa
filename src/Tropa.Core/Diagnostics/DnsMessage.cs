using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace Tropa.Core.Diagnostics;

/// <summary>Ответ DNS: код (0 — успех, 3 — домена нет) и адреса из записей A/AAAA.</summary>
public sealed record DnsAnswer(int ResponseCode, IReadOnlyList<IPAddress> Addresses)
{
    public bool IsNxDomain => ResponseCode == 3;
}

/// <summary>
/// Минимальный DNS-клиент для диагностики (RFC 1035): запрос одной записи и разбор ответа.
/// Ответ — недоверенные данные из сети, поэтому разбор проверяет каждую границу.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    public static byte[] BuildQuery(string name, ushort type, ushort id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var ascii = new IdnMapping().GetAscii(name.Trim().TrimEnd('.'));
        var buffer = new List<byte>(32 + ascii.Length);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01; // RD: рекурсивный запрос
        header[5] = 1; // QDCOUNT = 1
        buffer.AddRange(header.ToArray());
        foreach (var label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63)
                throw new ArgumentException("Неверное имя домена.", nameof(name));
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }

        buffer.Add(0);
        buffer.Add((byte)(type >> 8));
        buffer.Add((byte)type);
        buffer.Add(0);
        buffer.Add(1); // класс IN
        return [.. buffer];
    }

    /// <summary>null — пакет повреждён или это ответ не на наш запрос.</summary>
    public static DnsAnswer? Parse(ReadOnlySpan<byte> data, ushort expectedId)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(data) != expectedId || (data[2] & 0x80) == 0)
            return null;
        var rcode = data[3] & 0x0F;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            if (!SkipName(data, ref pos) || pos + 4 > data.Length)
                return null;
            pos += 4;
        }

        var addresses = new List<IPAddress>();
        for (var i = 0; i < an; i++)
        {
            if (!SkipName(data, ref pos) || pos + 10 > data.Length)
                return null;
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
            pos += 10;
            if (pos + length > data.Length)
                return null;
            if ((type == TypeA && length == 4) || (type == TypeAaaa && length == 16))
                addresses.Add(new IPAddress(data.Slice(pos, length)));
            pos += length;
        }

        return new DnsAnswer(rcode, addresses);
    }

    private static bool SkipName(ReadOnlySpan<byte> data, ref int pos)
    {
        for (var guard = 0; guard < 128; guard++)
        {
            if (pos >= data.Length)
                return false;
            var len = data[pos];
            if (len == 0)
            {
                pos++;
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                // Сжатое имя: указатель занимает два байта, дальше не идём.
                pos += 2;
                return pos <= data.Length;
            }

            if ((len & 0xC0) != 0)
                return false;
            pos += 1 + len;
        }

        return false;
    }

    /// <summary>Для DNS поверх TCP сообщение предваряется длиной (RFC 1035, §4.2.2).</summary>
    public static byte[] WithTcpLength(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = new byte[message.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)message.Length);
        message.CopyTo(result, 2);
        return result;
    }
}
