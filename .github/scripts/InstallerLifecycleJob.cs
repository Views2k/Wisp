using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The supervisor owns the only job handle. Windows stops the worker and its
// descendants when that handle closes, including if the supervisor itself exits.
public sealed class InstallerLifecycleJob : IDisposable
{
    private readonly SafeFileHandle job;

    public InstallerLifecycleJob()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception();

        const uint killOnJobClose = 0x2000;
        const int extendedLimitInformation = 9;
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = killOnJobClose } };
        if (!SetInformationJobObject(job, extendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(job, process.SafeHandle))
            throw new Win32Exception();
    }

    public void Dispose() => job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
