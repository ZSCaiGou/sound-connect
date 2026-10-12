using System.Runtime.InteropServices;

namespace SoundConnect.Audio;

/// <summary>
/// 进程树查询。浏览器会把音频交给子进程（audio service）渲染，而音频会话挂在那个子进程上；
/// 直接抓子进程也能通，但子进程会被浏览器回收，抓到时流就断了。
/// 所以先往上找到"同一个程序自己的树根"再下手。
/// </summary>
public static class ProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static Dictionary<uint, (uint ParentId, string Name)> Snapshot()
    {
        var processes = new Dictionary<uint, (uint, string)>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return processes;
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry))
            {
                return processes;
            }
            do
            {
                processes[entry.ProcessId] = (entry.ParentProcessId, entry.ExeFile);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return processes;
    }

    /// <summary>往上找到同名的最高祖先。QQ 音乐这种单进程会原样返回。</summary>
    public static uint FindTreeRoot(uint processId, string processName)
    {
        var processes = Snapshot();
        var imageName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";
        var current = processId;

        for (var depth = 0; depth < 16; depth++)
        {
            if (!processes.TryGetValue(current, out var info)) break;
            if (!processes.TryGetValue(info.ParentId, out var parent)) break;
            if (!string.Equals(parent.Name, imageName, StringComparison.OrdinalIgnoreCase)) break;
            current = info.ParentId;
        }

        return current;
    }
}
