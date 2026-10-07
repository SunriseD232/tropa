using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>
/// Каталог службы в %ProgramData% (docs/02-security.md, §3.4). По умолчанию обычные пользователи
/// могут создавать там папки — значит, могли бы заранее создать «Tropa» со своими правами и
/// подложить файлы. Поэтому служба проверяет владельца, чужой каталог откладывает в сторону
/// и выставляет явные права: только SYSTEM и администраторы, без наследования.
/// </summary>
public static class SecureDirectory
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void Ensure(string path)
    {
        var dir = new DirectoryInfo(path);
        if (dir.Exists)
        {
            var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || (owner != System && owner != Administrators))
            {
                var aside = path.TrimEnd('\\') + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                Directory.Move(path, aside);
                dir = new DirectoryInfo(path);
            }
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(System);
        foreach (var sid in new[] { System, Administrators })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        if (!dir.Exists)
            dir.Create(security);
        else
            dir.SetAccessControl(security);
    }
}
