using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>Условие фильтра WFP. Значения кладутся в неуправляемую память на время вызова.</summary>
public abstract record WfpCondition
{
    private WfpCondition()
    {
    }

    /// <summary>Только петлевой интерфейс (127.0.0.1, ::1).</summary>
    public sealed record Loopback : WfpCondition;

    /// <summary>Программа по полному пути к exe.</summary>
    public sealed record App(string Path) : WfpCondition;

    /// <summary>Локальный интерфейс по LUID (наш TUN).</summary>
    public sealed record LocalInterface(ulong Luid) : WfpCondition;

    public sealed record Protocol(ProtocolType Type) : WfpCondition;

    public sealed record RemotePort(int Port) : WfpCondition;

    /// <summary>Удалённый адрес в подсети (IPv4 или IPv6).</summary>
    public sealed record RemoteSubnet(IPAddress Address, int PrefixLength) : WfpCondition;
}

public enum WfpLayer { ConnectV4, ConnectV6, AcceptV4, AcceptV6 }

/// <summary>
/// Минимальная обёртка над Windows Filtering Platform (fwpuclnt.dll). Сеанс динамический:
/// все фильтры и подуровень исчезают, когда закрывается дескриптор — в том числе если служба
/// упала. Поэтому блокировка не может «застрять» после сбоя Тропы (ADR-021). Требует прав администратора.
/// Структуры описаны для 64-битного процесса (смещения сверены с fwpmtypes.h).
/// </summary>
public sealed partial class WfpSession : IDisposable
{
    /// <summary>Подуровень Тропы: по нему фильтры видно в «netsh wfp show filters».</summary>
    public static readonly Guid SubLayerKey = new("3b0a5f47-6c1d-4a5e-9f43-7a1f0c2d8e51");

    private static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid LayerAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid CondAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid CondFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid CondLocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    private static readonly Guid CondRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid CondRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid CondProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");

    private const uint FwpUint8 = 1;
    private const uint FwpUint16 = 2;
    private const uint FwpUint32 = 3;
    private const uint FwpUint64 = 4;
    private const uint FwpByteBlobType = 12;
    private const uint FwpV4AddrMask = 0x100;
    private const uint FwpV6AddrMask = 0x101;
    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;
    private const uint ConditionFlagIsLoopback = 1;
    private const uint ActionBlock = 0x1001;
    private const uint ActionPermit = 0x1002;
    private const uint SessionFlagDynamic = 1;
    private const uint RpcAuthnWinNt = 10;
    private const uint Infinite = 0xFFFFFFFF;

    private IntPtr _engine;

    public WfpSession(string name)
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Kill switch поддерживается только в 64-битной Windows.");
        using var arena = new Arena();
        var session = new Session
        {
            DisplayName = arena.String(name),
            DisplayDescription = arena.String(name),
            Flags = SessionFlagDynamic,
            TxnWaitTimeoutInMSec = Infinite,
        };
        Check(FwpmEngineOpen0(IntPtr.Zero, RpcAuthnWinNt, IntPtr.Zero, in session, out _engine), "открыть WFP");

        var subLayer = new SubLayer
        {
            Key = SubLayerKey,
            DisplayName = arena.String(name),
            DisplayDescription = arena.String(name),
            Weight = ushort.MaxValue,
        };
        try
        {
            Check(FwpmSubLayerAdd0(_engine, in subLayer, IntPtr.Zero), "добавить подуровень WFP");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Все изменения внутри — одной транзакцией: либо все фильтры, либо ни одного.</summary>
    public void Transaction(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        Check(FwpmTransactionBegin0(_engine, 0), "начать транзакцию WFP");
        try
        {
            body();
            Check(FwpmTransactionCommit0(_engine), "применить транзакцию WFP");
        }
        catch
        {
            _ = FwpmTransactionAbort0(_engine);
            throw;
        }
    }

    public ulong AddFilter(string name, WfpLayer layer, bool permit, byte weight, params WfpCondition[] conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        using var arena = new Arena();
        var native = arena.Conditions(conditions);
        var filter = new Filter
        {
            DisplayName = arena.String(name),
            DisplayDescription = arena.String("Тропа"),
            LayerKey = layer switch
            {
                WfpLayer.ConnectV4 => LayerConnectV4,
                WfpLayer.ConnectV6 => LayerConnectV6,
                WfpLayer.AcceptV4 => LayerAcceptV4,
                _ => LayerAcceptV6,
            },
            SubLayerKey = SubLayerKey,
            WeightType = FwpUint8,
            WeightValue = weight,
            NumConditions = (uint)conditions.Length,
            Conditions = native,
            ActionType = permit ? ActionPermit : ActionBlock,
        };
        Check(FwpmFilterAdd0(_engine, in filter, IntPtr.Zero, out var id), "добавить фильтр WFP");
        return id;
    }

    public void DeleteFilter(ulong id) => Check(FwpmFilterDeleteById0(_engine, id), "удалить фильтр WFP");

    public void Dispose()
    {
        if (_engine == IntPtr.Zero)
            return;
        _ = FwpmEngineClose0(_engine);
        _engine = IntPtr.Zero;
    }

    /// <summary>LUID сетевого интерфейса по его GUID (NetworkInterface.Id).</summary>
    public static ulong InterfaceLuid(string interfaceId)
    {
        var guid = Guid.Parse(interfaceId);
        Check(ConvertInterfaceGuidToLuid(in guid, out var luid), "найти интерфейс");
        return luid;
    }

    private static void Check(uint code, string what)
    {
        if (code != 0)
            throw new Win32Exception(unchecked((int)code), $"Не удалось {what} (код 0x{code:X8}).");
    }

    /// <summary>Неуправляемая память для одного вызова: освобождается целиком в Dispose.</summary>
    private sealed unsafe class Arena : IDisposable
    {
        private readonly List<IntPtr> _hGlobal = [];
        private readonly List<IntPtr> _fwpm = [];

        public IntPtr String(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            _hGlobal.Add(p);
            return p;
        }

        private IntPtr Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(size);
            _hGlobal.Add(p);
            new Span<byte>((void*)p, size).Clear();
            return p;
        }

        public IntPtr Conditions(WfpCondition[] conditions)
        {
            if (conditions.Length == 0)
                return IntPtr.Zero;
            var size = Marshal.SizeOf<Condition>();
            var array = Alloc(size * conditions.Length);
            for (var i = 0; i < conditions.Length; i++)
                Marshal.StructureToPtr(Build(conditions[i]), array + (i * size), false);
            return array;
        }

        private Condition Build(WfpCondition c)
        {
            switch (c)
            {
                case WfpCondition.Loopback:
                    return new Condition { FieldKey = CondFlags, MatchType = MatchFlagsAllSet, ValueType = FwpUint32, Value = ConditionFlagIsLoopback };
                case WfpCondition.App app:
                    Check(FwpmGetAppIdFromFileName0(app.Path, out var blob), "получить идентификатор программы");
                    _fwpm.Add(blob);
                    return new Condition { FieldKey = CondAppId, MatchType = MatchEqual, ValueType = FwpByteBlobType, Value = (ulong)blob };
                case WfpCondition.LocalInterface iface:
                    var luid = Alloc(8);
                    Marshal.WriteInt64(luid, unchecked((long)iface.Luid));
                    return new Condition { FieldKey = CondLocalInterface, MatchType = MatchEqual, ValueType = FwpUint64, Value = (ulong)luid };
                case WfpCondition.Protocol proto:
                    return new Condition { FieldKey = CondProtocol, MatchType = MatchEqual, ValueType = FwpUint8, Value = (byte)proto.Type };
                case WfpCondition.RemotePort port:
                    return new Condition { FieldKey = CondRemotePort, MatchType = MatchEqual, ValueType = FwpUint16, Value = (ushort)port.Port };
                case WfpCondition.RemoteSubnet { Address.AddressFamily: AddressFamily.InterNetwork } v4:
                    // FWP_V4_ADDR_AND_MASK: адрес и маска в порядке байтов хоста.
                    var m4 = Alloc(8);
                    var bytes = v4.Address.GetAddressBytes();
                    var addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                    var mask = v4.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - v4.PrefixLength);
                    Marshal.WriteInt32(m4, unchecked((int)addr));
                    Marshal.WriteInt32(m4 + 4, unchecked((int)mask));
                    return new Condition { FieldKey = CondRemoteAddress, MatchType = MatchEqual, ValueType = FwpV4AddrMask, Value = (ulong)m4 };
                case WfpCondition.RemoteSubnet v6:
                    // FWP_V6_ADDR_AND_MASK: 16 байт адреса и длина префикса.
                    var m6 = Alloc(17);
                    Marshal.Copy(v6.Address.GetAddressBytes(), 0, m6, 16);
                    Marshal.WriteByte(m6 + 16, (byte)v6.PrefixLength);
                    return new Condition { FieldKey = CondRemoteAddress, MatchType = MatchEqual, ValueType = FwpV6AddrMask, Value = (ulong)m6 };
                default:
                    throw new ArgumentOutOfRangeException(nameof(c));
            }
        }

        public void Dispose()
        {
            foreach (var p in _hGlobal)
                Marshal.FreeHGlobal(p);
            foreach (var p in _fwpm)
            {
                var copy = p;
                FwpmFreeMemory0(ref copy);
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct Session
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public IntPtr DisplayName;
        [FieldOffset(24)] public IntPtr DisplayDescription;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(36)] public uint TxnWaitTimeoutInMSec;
        [FieldOffset(40)] public uint ProcessId;
        [FieldOffset(48)] public IntPtr Sid;
        [FieldOffset(56)] public IntPtr Username;
        [FieldOffset(64)] public int KernelMode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct SubLayer
    {
        [FieldOffset(0)] public Guid Key;
        [FieldOffset(16)] public IntPtr DisplayName;
        [FieldOffset(24)] public IntPtr DisplayDescription;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public IntPtr ProviderKey;
        [FieldOffset(48)] public uint ProviderDataSize;
        [FieldOffset(56)] public IntPtr ProviderData;
        [FieldOffset(64)] public ushort Weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Condition
    {
        [FieldOffset(0)] public Guid FieldKey;
        [FieldOffset(16)] public uint MatchType;
        [FieldOffset(24)] public uint ValueType;
        [FieldOffset(32)] public ulong Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct Filter
    {
        [FieldOffset(0)] public Guid FilterKey;
        [FieldOffset(16)] public IntPtr DisplayName;
        [FieldOffset(24)] public IntPtr DisplayDescription;
        [FieldOffset(32)] public uint Flags;
        [FieldOffset(40)] public IntPtr ProviderKey;
        [FieldOffset(48)] public uint ProviderDataSize;
        [FieldOffset(56)] public IntPtr ProviderData;
        [FieldOffset(64)] public Guid LayerKey;
        [FieldOffset(80)] public Guid SubLayerKey;
        [FieldOffset(96)] public uint WeightType;
        [FieldOffset(104)] public ulong WeightValue;
        [FieldOffset(112)] public uint NumConditions;
        [FieldOffset(120)] public IntPtr Conditions;
        [FieldOffset(128)] public uint ActionType;
        [FieldOffset(132)] public Guid ActionFilterType;
        [FieldOffset(152)] public Guid ProviderContextKey;
        [FieldOffset(168)] public IntPtr Reserved;
        [FieldOffset(176)] public ulong FilterId;
        [FieldOffset(184)] public uint EffectiveWeightType;
        [FieldOffset(192)] public ulong EffectiveWeightValue;
    }

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, in Session session, out IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineClose0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerAdd0(IntPtr engineHandle, in SubLayer subLayer, IntPtr sd);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterAdd0(IntPtr engineHandle, in Filter filter, IntPtr sd, out ulong id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionCommit0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionAbort0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [LibraryImport("fwpuclnt.dll")]
    private static partial void FwpmFreeMemory0(ref IntPtr p);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint ConvertInterfaceGuidToLuid(in Guid interfaceGuid, out ulong luid);
}
