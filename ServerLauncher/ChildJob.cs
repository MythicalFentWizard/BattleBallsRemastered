using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ServerLauncher;

/// Ties the server process to the lifetime of this one.
///
/// The server runs with no console of its own, so if the launcher goes away without
/// stopping it there is nothing left to close it by: an invisible process keeps port
/// 29300 and the game server it started, and the next launch fails to bind with no
/// obvious cause. The Stop button and the window's close handler cover the ordinary
/// path; this covers a crash, a kill from Task Manager, and a power-loss-shaped exit.
///
/// A job object with KILL_ON_JOB_CLOSE does it at the OS level: the handle closes when
/// this process ends, however it ends, and Windows terminates everything in the job.
static class ChildJob
{
    static readonly IntPtr Handle = Create();

    static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            var size = Marshal.SizeOf(info);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(buffer); }

            return job;
        }
        catch { return IntPtr.Zero; }
    }

    /// Best effort. A server that outlives a crashed launcher is a nuisance, not a
    /// reason to refuse to start one.
    public static void Adopt(Process child)
    {
        if (Handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Handle, child.Handle); } catch { }
    }

    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
