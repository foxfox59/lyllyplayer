using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

/// <summary>
/// Puts ffmpeg / yt-dlp / ffprobe child processes in a job that is torn down when the app exits, so they are
/// clearly tied to this process and are not left orphaned. Task Manager still shows yt-dlp's own children
/// (e.g. Node for EJS) under yt-dlp — that chain is outside our control.
/// </summary>
internal static class ChildToolProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<IntPtr> JobHandle = new(CreateAndConfigureJob, LazyThreadSafetyMode.ExecutionAndPublication);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static IntPtr CreateAndConfigureJob()
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        if (h == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, ref info, size))
        {
            try { _ = CloseHandle(h); } catch { /* ignore */ }
            return IntPtr.Zero;
        }

        return h;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Best-effort: fails silently if the child is already in another job (e.g. some debug hosts).</summary>
    public static void TryAssign(Process? process)
    {
        try
        {
            if (process is null)
                return;
            var job = JobHandle.Value;
            if (job == IntPtr.Zero)
                return;

            _ = process.Handle;
            _ = AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // ignore
        }
    }
}
