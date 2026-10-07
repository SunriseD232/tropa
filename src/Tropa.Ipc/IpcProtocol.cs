namespace Tropa.Ipc;

/// <summary>Параметры канала между интерфейсом и службой (см. docs/03-architecture.md, §3).</summary>
public static class IpcProtocol
{
    /// <summary>Версия протокола. Меняется при любом несовместимом изменении сообщений.</summary>
    public const int Version = 1;

    /// <summary>Имя именованного канала. Версия в имени, чтобы старый клиент не говорил с новой службой.</summary>
    public const string PipeName = "Tropa.Service.v1";

    /// <summary>Максимальный размер одного сообщения в байтах.</summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;
}
