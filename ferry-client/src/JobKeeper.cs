using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ferry
{
    /// <summary>
    /// Windows Job Object —— 让子进程跟着父进程一起死。
    ///
    /// 解决什么问题（实测踩过）：
    ///   客户端起 ssh.exe 和 mihomo.exe 作为子进程。正常停止时 Stop() 会 Kill 它们，
    ///   没问题。但客户端被强杀（任务管理器结束、蓝屏、注销、installer 覆盖安装）时，
    ///   **子进程会变成孤儿继续活着**。
    ///
    ///   实测后果：反复调试点几次之后，机器上堆了 10 个 ssh.exe，各自占着
    ///   10800~10811 的端口。于是每次启动都报「端口被占用，已调整」，
    ///   端口一路往上飘到 10814；再往后会耗尽远端 sshd 的会话数，
    ///   mihomo 也会留下 FerryTun 网卡（下一个 TUN 就建不出来）。
    ///
    /// 为什么用 Job Object 而不是「父进程死前清理」：
    ///   被强杀时父进程**没有机会执行任何代码**，ProcessExit / finally 都不会跑。
    ///   Job Object 是内核层面的约束：句柄一关，Windows 自动杀掉整个 Job 里的进程，
    ///   不依赖任何用户态回调。这是唯一可靠的解法。
    ///
    /// 代价：客户端必须一直持有这个 Job 句柄（存在字段里，不要 Dispose）。
    ///   句柄一关就等于触发清理 —— 那正是我们要的语义，但别在还活着时误关。
    /// </summary>
    static class JobKeeper
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

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
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        // JobObjectExtendedLimitInformation
        const int JobObjectExtendedLimitInformation = 9;

        static IntPtr _job = IntPtr.Zero;
        static readonly object _lock = new object();

        /// <summary>
        /// 初始化（幂等）。失败返回 false —— 不致命，只是失去自动清理能力，
        /// 调用方应记日志但继续启动，不能因此让整个程序起不来。
        /// </summary>
        public static bool Ensure(Process current)
        {
            lock (_lock)
            {
                if (_job != IntPtr.Zero) return true;
                try
                {
                    var job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return false;

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                    int len = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)len))
                    {
                        return false;
                    }

                    // 把自己也放进去。这样"最后一个句柄关闭"的判定点
                    // 就正好是"本进程退出"，语义清晰。
                    if (current != null)
                    {
                        AssignProcessToJobObject(job, current.Handle);
                    }
                    _job = job;
                    return true;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// 把一个刚启动的子进程拉进 Job。
        ///
        /// 为什么必须是"刚启动"：进程一旦开始跑就可能已经派生了孙进程，
        /// 而那些孙进程不会被拉进来。启动后立刻调用，窗口最小。
        /// </summary>
        public static bool Add(Process p)
        {
            lock (_lock)
            {
                if (_job == IntPtr.Zero || p == null) return false;
                try
                {
                    return AssignProcessToJobObject(_job, p.Handle);
                }
                catch (Exception) { return false; }
            }
        }
    }
}
