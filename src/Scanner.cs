using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace CabApp
{
    /// <summary>进程扫描 + 安全分类。所有“能不能杀”的判断都集中在这里。</summary>
    public static class Scanner
    {
        /// <summary>
        /// 绝对保护名单：无论用户勾选什么选项，这些进程都不会被关闭。
        /// 它们是 Windows 自身运行所必需的；关掉会蓝屏、掉桌面、掉输入法或直接注销。
        /// </summary>
        private static readonly HashSet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // --- 内核 / 会话基础 ---
            "system", "idle", "registry", "memory compression", "memcompression",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "svchost", "spoolsv", "dwm", "fontdrvhost", "logonui", "userinit",
            "wudfhost", "audiodg", "conhost", "sihost", "taskhostw", "ctfmon",
            "dllhost", "wmiprvse", "msdtc", "rdpclip", "rdpinput", "sppsvc",
            "securityhealthservice", "securityhealthsystray", "wscsvc", "windefend",
            "msmpeng", "nissrv", "mssense", "sgrmbroker", "smartscreen",
            "searchindexer", "searchprotocolhost", "searchfilterhost",
            "trustedinstaller", "tiworker", "usoclient", "mousocoreworker",
            "wuauclt", "wlanext", "warpjitsvc", "msiexec",
            // --- 桌面外壳（关掉=任务栏/开始菜单/输入法消失）---
            "explorer", "shellexperiencehost", "startmenuexperiencehost",
            "textinputhost", "applicationframehost", "runtimebroker",
            "backgroundtaskhost", "systemsettings", "lockapp", "widgets", "widgetservice",
            "searchhost", "searchapp", "useroobebroker", "aggregatorhost",
            "taskmgr", "securityhealthhost",
            // --- 输入法 / 辅助 ---
            "chsime", "tabtip", "osk", "magnify", "narrator",
            // --- 远程 / 会话管理 ---
            "termsrv", "umrdpservice"
        };

        /// <summary>是否是绝对保护的 Windows 关键进程名（关闭引擎展开子进程时也会用它兜底）。</summary>
        public static bool IsProtectedName(string name)
        {
            return !string.IsNullOrEmpty(name) && ProtectedNames.Contains(name);
        }

        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static int CurrentSessionId()
        {
            try { return Process.GetCurrentProcess().SessionId; }
            catch { return -1; }
        }

        private static string WindowsDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }

        /// <summary>可执行文件是否位于 Windows 系统目录 / 应用商店应用目录。
        /// 路径不可读时返回 false，调用方必须自行按“不确定”处理（失败关闭）。</summary>
        public static bool IsSystemPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string win = WindowsDir();
            if (!string.IsNullOrEmpty(win) &&
                path.StartsWith(win + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;

            string[] roots = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string wa = Path.Combine(r, "WindowsApps");
                if (path.StartsWith(wa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>取本进程的父进程链（自身除外）。</summary>
        public static HashSet<int> GetAncestorPids(int selfPid)
        {
            HashSet<int> set = new HashSet<int>();
            int cur = selfPid;
            for (int i = 0; i < 12; i++)
            {
                int parent = Native.GetParentPid(cur);
                if (parent <= 0 || parent == cur) break;
                if (!set.Add(parent)) break;
                cur = parent;
            }
            return set;
        }

        public static ScanResult Scan(ScanOptions opt)
        {
            ScanResult result = new ScanResult();
            result.IsAdmin = IsAdmin();

            Process me = Process.GetCurrentProcess();
            int selfPid = me.Id;
            string selfPath = Native.GetProcessImagePath(selfPid);
            int ownSession = CurrentSessionId();
            result.SelfPid = selfPid;

            HashSet<int> ancestors = GetAncestorPids(selfPid);

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { all = new Process[0]; }

            result.TotalProcesses = all.Length;

            foreach (Process p in all)
            {
                try
                {
                    int pid;
                    try { pid = p.Id; }
                    catch { continue; }
                    if (pid <= 0) continue;

                    ProcItem it = new ProcItem();
                    it.Pid = pid;

                    try { it.Name = p.ProcessName; }
                    catch { it.Name = "?"; }
                    if (string.IsNullOrEmpty(it.Name)) it.Name = "?";

                    // ---------- 1. 硬保护判定 ----------
                    if (pid == selfPid)
                    {
                        it.Kind = ProcKind.Protected;
                        it.ProtectReason = "本程序自身";
                    }
                    else if (ancestors.Contains(pid))
                    {
                        it.Kind = ProcKind.Protected;
                        it.ProtectReason = "本程序的父进程链（防止误杀启动它的程序）";
                    }
                    else if (ProtectedNames.Contains(it.Name))
                    {
                        it.Kind = ProcKind.Protected;
                        it.ProtectReason = "Windows 系统关键进程";
                    }
                    else if (opt.ExcludeNames.Contains(it.Name))
                    {
                        it.Kind = ProcKind.Protected;
                        it.ProtectReason = "在您的排除名单中";
                    }

                    // ---------- 2. 采集信息 ----------
                    try { it.SessionId = p.SessionId; } catch { it.SessionId = -1; }
                    try { it.WorkingSet = p.WorkingSet64; } catch { it.WorkingSet = 0; }
                    try { it.StartTimeTicks = p.StartTime.ToUniversalTime().Ticks; } catch { it.StartTimeTicks = 0; }
                    try
                    {
                        it.HasWindow = p.MainWindowHandle != IntPtr.Zero;
                        if (it.HasWindow) it.WindowTitle = p.MainWindowTitle;
                    }
                    catch { it.HasWindow = false; }

                    it.ParentPid = Native.GetParentPid(pid);

                    string path = Native.GetProcessImagePath(pid);
                    if (string.IsNullOrEmpty(path))
                    {
                        try { path = p.MainModule.FileName; } catch { path = ""; }
                    }
                    it.Path = path;

                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        try
                        {
                            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                            it.Company = (vi.CompanyName ?? "").Trim();
                            it.Description = (vi.FileDescription ?? "").Trim();
                        }
                        catch { }
                    }

                    // 本程序的另一个实例也绝不能被关掉
                    if (it.Kind != ProcKind.Protected && !string.IsNullOrEmpty(selfPath) &&
                        string.Equals(path, selfPath, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Kind = ProcKind.Protected;
                        it.ProtectReason = "本程序的另一个实例";
                    }

                    // 只处理本登录会话里有窗口站的进程。EnumWindows 是会话隔离的，
                    // 别的交互式会话（远程桌面/其他用户）里的窗口程序在这里看起来“没窗口”，
                    // 会被误判成后台应用而被杀掉，所以直接排除。
                    // 会话 0 是服务会话，不在这里拦，留给下面的“系统服务”分类（需显式勾选才处理）。
                    if (it.Kind != ProcKind.Protected && ownSession > 0 &&
                        it.SessionId != ownSession && it.SessionId != 0)
                    {
                        it.Kind = ProcKind.Protected;
                        it.ProtectReason = "属于其他登录会话（远程桌面/其他用户），不属于本次操作范围";
                    }

                    // ---------- 3. 分类 ----------
                    bool pathKnown = !string.IsNullOrEmpty(path);
                    bool uncertain = false;

                    if (it.Kind != ProcKind.Protected)
                    {
                        if (it.SessionId == 0)
                        {
                            it.Kind = ProcKind.SystemService;
                        }
                        else if (!pathKnown)
                        {
                            // 失败关闭：读不到路径就不敢断定它是普通应用，
                            // 归入系统组件，必须显式勾选“包含系统组件”才会被处理。
                            it.Kind = ProcKind.SystemComponent;
                            uncertain = true;
                        }
                        else if (IsSystemPath(path))
                        {
                            it.Kind = ProcKind.SystemComponent;
                        }
                        else if (it.HasWindow)
                        {
                            it.Kind = ProcKind.WindowApp;
                        }
                        else
                        {
                            it.Kind = ProcKind.BackgroundApp;
                        }
                    }

                    switch (it.Kind)
                    {
                        case ProcKind.BackgroundApp: it.KindText = "后台应用"; break;
                        case ProcKind.WindowApp: it.KindText = "有窗口应用"; break;
                        case ProcKind.SystemComponent: it.KindText = uncertain ? "系统组件(路径不可读)" : "系统组件"; break;
                        case ProcKind.SystemService: it.KindText = "系统服务"; break;
                        default: it.KindText = "受保护"; break;
                    }

                    // ---------- 4. 按选项决定是否纳入本次操作范围 ----------
                    if (it.Kind == ProcKind.Protected)
                    {
                        result.ProtectedCount++;
                        result.ProtectedItems.Add(it);
                        continue;
                    }
                    if (it.Kind == ProcKind.WindowApp && !opt.IncludeWindowApps) { result.SkippedCount++; continue; }
                    if (it.Kind == ProcKind.SystemComponent && !opt.IncludeSystemComponents) { result.SkippedCount++; continue; }
                    if (it.Kind == ProcKind.SystemService && !opt.IncludeSystemServices) { result.SkippedCount++; continue; }

                    result.Items.Add(it);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            result.Items.Sort(delegate(ProcItem a, ProcItem b)
            {
                int c = b.WorkingSet.CompareTo(a.WorkingSet);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }
    }
}
