using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CabApp
{
    /// <summary>底层 Win32 / NT API 封装。全部只用系统自带 DLL，无第三方依赖。</summary>
    internal static class Native
    {
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int WM_CLOSE = 0x0010;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const uint GW_OWNER = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        public static extern uint GetConsoleOutputCP();

        public const int STILL_ACTIVE = 259;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int PROCESS_TERMINATE = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        /// <summary>进程探测结果。</summary>
        public enum Probe
        {
            Gone,          // 已退出 / PID 不存在
            Alive,         // 存在且可达
            AccessDenied   // 存在，但权限不足，无法查询（更可能是高完整性/受保护进程）
        }

        /// <summary>
        /// 精确区分“进程已经没了”和“进程还在但我碰不到”。
        /// .NET 的 Process.HasExited 对高权限进程会抛 AccessDenied 异常，
        /// 如果把它当成“已退出”，就会把没杀掉的东西报告成杀掉了。
        /// </summary>
        public static Probe ProbeProcess(int pid)
        {
            if (pid <= 0) return Probe.Gone;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return err == ERROR_ACCESS_DENIED ? Probe.AccessDenied : Probe.Gone;
            }
            try
            {
                int code;
                if (!GetExitCodeProcess(h, out code)) return Probe.Gone;
                return code == STILL_ACTIVE ? Probe.Alive : Probe.Gone;
            }
            catch { return Probe.Gone; }
            finally { CloseHandle(h); }
        }

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass,
            ref PROCESS_BASIC_INFORMATION pbi, int cb, out int returnLength);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>取父进程 PID；失败返回 0。</summary>
        public static int GetParentPid(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return 0;
                PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                int ret;
                int status = NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out ret);
                if (status != 0) return 0;
                return pbi.InheritedFromUniqueProcessId.ToInt64() > int.MaxValue
                    ? 0
                    : (int)pbi.InheritedFromUniqueProcessId.ToInt64();
            }
            catch { return 0; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>取完整映像路径；失败返回 null。</summary>
        public static string GetProcessImagePath(int pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return null;
                StringBuilder sb = new StringBuilder(2048);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                return null;
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        /// <summary>枚举某进程的全部顶层窗口句柄。</summary>
        public static System.Collections.Generic.List<IntPtr> GetTopLevelWindows(int pid)
        {
            System.Collections.Generic.List<IntPtr> list = new System.Collections.Generic.List<IntPtr>();
            try
            {
                Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint wpid;
                    GetWindowThreadProcessId(hWnd, out wpid);
                    if ((int)wpid == pid) list.Add(hWnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        public static string GetWindowTitleSafe(IntPtr hWnd)
        {
            try
            {
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return "";
                StringBuilder sb = new StringBuilder(len + 2);
                GetWindowText(hWnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}
