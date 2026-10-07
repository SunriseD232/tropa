using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tropa.Infrastructure.Cores;

/// <summary>
/// Job Object Windows с флагом «убить при закрытии»: если Тропа упадёт или её завершат
/// принудительно, ядра завершатся вместе с ней и не останутся висеть в фоне.
/// </summary>
internal sealed partial class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose() => _handle.Dispose();

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle hJob, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle hJob, SafeProcessHandle hProcess);
}
