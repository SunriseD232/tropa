using System.Buffers.Binary;
using System.Text.Json;

namespace Tropa.Ipc;

/// <summary>Сообщение нарушает протокол: слишком большое, обрезанное или не JSON. Соединение рвётся.</summary>
public sealed class ProtocolViolationException(string message) : Exception(message);

/// <summary>
/// Кадры: 4 байта длины (little-endian) + UTF-8 JSON. Длина проверяется до чтения тела,
/// поэтому клиент не может заставить службу выделить гигабайты памяти.
/// </summary>
public static class Framing
{
    public static async Task WriteAsync<T>(Stream stream, T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, info);
        if (body.Length > IpcProtocol.MaxMessageBytes)
            throw new ProtocolViolationException("Сообщение слишком большое.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>null — собеседник аккуратно закрыл соединение.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
            throw new ProtocolViolationException($"Недопустимая длина сообщения: {length}.");

        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, ct).ConfigureAwait(false))
            throw new ProtocolViolationException("Соединение оборвалось посреди сообщения.");
        try
        {
            return JsonSerializer.Deserialize(body, info) ?? throw new ProtocolViolationException("Пустое сообщение.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolViolationException("Сообщение не разобрано: " + ex.Message);
        }
        catch (NotSupportedException ex)
        {
            throw new ProtocolViolationException("Неизвестный тип сообщения: " + ex.Message);
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return read == 0 ? false : throw new ProtocolViolationException("Соединение оборвалось посреди сообщения.");
            read += n;
        }

        return true;
    }
}
