using System.Buffers.Binary;
using System.Text;

namespace Tropa.Core.Diagnostics;

/// <summary>
/// Двоичное значение Internet Settings\Connections\DefaultConnectionSettings (и SavedLegacySettings):
/// по нему WinINet на самом деле решает, есть ли прокси. Раскладка: версия (4 байта), счётчик изменений (4),
/// флаги (4: 1 — напрямую, 2 — прокси, 4 — сценарий, 8 — автоопределение), длина строки прокси (4), строка прокси.
/// </summary>
public static class WinInetConnectionBlob
{
    private const int FlagsOffset = 8;
    private const int ProxyLengthOffset = 12;
    private const int ProxyOffset = 16;

    public const int FlagDirect = 0x1;
    public const int FlagProxy = 0x2;

    /// <summary>Прокси включён и его адрес; null — значение не похоже на ожидаемую раскладку.</summary>
    public static (bool ProxyEnabled, string? Server)? Parse(byte[]? blob)
    {
        if (blob is null || blob.Length < ProxyOffset)
            return null;
        var flags = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(FlagsOffset));
        var length = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(ProxyLengthOffset));
        if (length < 0 || length > blob.Length - ProxyOffset)
            return null;
        var server = length == 0 ? null : Encoding.ASCII.GetString(blob, ProxyOffset, length);
        return ((flags & FlagProxy) != 0, server);
    }

    /// <summary>
    /// Копия со снятым флагом прокси (и включённым «напрямую») и увеличенным счётчиком — так WinINet
    /// замечает изменение. Адрес остаётся. null — раскладка не распознана, менять нечего.
    /// </summary>
    public static byte[]? WithoutProxy(byte[]? blob)
    {
        if (Parse(blob) is not { ProxyEnabled: true })
            return null;
        var copy = (byte[])blob!.Clone();
        var flags = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(FlagsOffset));
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(FlagsOffset), (flags & ~FlagProxy) | FlagDirect);
        var counter = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(4));
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(4), unchecked(counter + 1));
        return copy;
    }
}
