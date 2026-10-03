using System;
using System.Collections.Generic;

namespace CabApp
{
    /// <summary>进程分类。</summary>
    public enum ProcKind
    {
        BackgroundApp = 0,  // 后台应用（无界面，默认关闭目标）
        WindowApp = 1,      // 有窗口的应用
        SystemComponent = 2,// Windows 系统组件
        SystemService = 3,  // 会话 0 服务
        Protected = 4       // 系统关键进程 / 自身 / 父进程链，永不关闭
    }

    public class ProcItem
    {
        public int Pid;
        public int ParentPid;
        public string Name = "";
        public string Path = "";
        public string Company = "";
        public string Description = "";
        public long WorkingSet;
        public bool HasWindow;
        public string WindowTitle = "";
        public int SessionId;
        public long StartTimeTicks;   // 进程启动时间，用于关闭前校验 PID 未被系统回收
        public ProcKind Kind = ProcKind.BackgroundApp;
        public string KindText = "";
        public string ProtectReason = "";

        public bool Selectable
        {
            get { return Kind != ProcKind.Protected; }
        }

        public string MemText
        {
            get { return FormatSize(WorkingSet); }
        }

        public string PublisherText
        {
            get
            {
                if (!string.IsNullOrEmpty(Company)) return Company;
                return "未知发布者";
            }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Description)) return Name + "  —  " + Description;
                return Name;
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0.0") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes.ToString() + " B";
        }
    }

    /// <summary>扫描选项。</summary>
    public class ScanOptions
    {
        public bool IncludeWindowApps = false;        // 是否把“有窗口的应用”也纳入
        public bool IncludeSystemComponents = false;  // 是否把 Windows 系统组件也纳入
        public bool IncludeSystemServices = false;    // 是否把会话 0 服务也纳入
        public bool ProtectParentChain = true;        // 保护本程序的父进程链
        public HashSet<string> ExcludeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ScanOptions Clone()
        {
            ScanOptions o = new ScanOptions();
            o.IncludeWindowApps = IncludeWindowApps;
            o.IncludeSystemComponents = IncludeSystemComponents;
            o.IncludeSystemServices = IncludeSystemServices;
            o.ProtectParentChain = ProtectParentChain;
            o.ExcludeNames = new HashSet<string>(ExcludeNames, StringComparer.OrdinalIgnoreCase);
            return o;
        }
    }

    public class ScanResult
    {
        public List<ProcItem> Items = new List<ProcItem>();
        public List<ProcItem> ProtectedItems = new List<ProcItem>();
        public int TotalProcesses;
        public int ProtectedCount;
        public int SkippedCount;   // 因为选项没开而没纳入的
        public int SelfPid;
        public bool IsAdmin;
    }

    public class KillOutcome
    {
        public int Pid;
        public string Name = "";
        public bool Success;
        public string Message = "";
        public string Method = "";
        public long FreedBytes;
    }

    /// <summary>关闭计划：真正会被关闭的完整清单（含子进程），以及被安全闸门挡下的进程。</summary>
    public class KillPlan
    {
        public List<ProcItem> Ordered = new List<ProcItem>();       // 执行顺序：子进程在前、父进程在后
        public List<ProcItem> MainTargets = new List<ProcItem>();   // 用户选中/勾选的
        public List<ProcItem> Descendants = new List<ProcItem>();   // 连带关闭的子进程
        public List<string> Skipped = new List<string>();           // 被保护名单拒绝的
    }
}
