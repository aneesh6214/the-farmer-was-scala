using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FarmerScala.Runtime;

// One Windows job owns every Scala process in a run. Closing it kills descendants too.
internal sealed class ProcessJob : IDisposable
{
    private IntPtr handle;
    public ProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        var info = new ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
        int length = Marshal.SizeOf<ExtendedLimitInformation>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, 9, ptr, (uint)length))
                throw new System.ComponentModel.Win32Exception();
        }
        catch { Dispose(); throw; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose()
    {
        if (handle == IntPtr.Zero)
            return;
        CloseHandle(handle);
        handle = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
