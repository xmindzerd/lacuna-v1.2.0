using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OneClickFilm
{
    // Own only the process tree of this production request, including its CLI/MCP workers.
    internal sealed class TaskProcessGroup : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal UIntPtr MinWorkingSet, MaxWorkingSet;
            internal uint ActiveProcesses;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll")]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint code);

        private readonly SafeFileHandle handle;
        private TaskProcessGroup(SafeFileHandle job) { handle = job; }

        internal static TaskProcessGroup Attach(Process child)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (!job.IsInvalid)
            {
                var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
                if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))) &&
                    AssignProcessToJobObject(job, child.Handle)) return new TaskProcessGroup(job);
            }
            job.Dispose();
            return null;
        }

        internal bool Stop() { return TerminateJobObject(handle, 1); }
        public void Dispose() { handle.Dispose(); }
    }
}
