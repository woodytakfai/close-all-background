using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace CabApp
{
    /// <summary>关闭引擎：先礼后兵 —— 先发 WM_CLOSE 让程序自己退出，超时再强制结束进程树。</summary>
    public static class Killer
    {
        public class Options
        {
            public bool Graceful = true;             // 优先请求正常退出
            public bool KillTree = true;             // 连同子进程一起关闭
            public int GracefulTimeoutMs = 1500;     // 等待正常退出的时间
        }

        // ==================== 进程探测 ====================
        private static bool IsRunning(int pid)
        {
            return Native.ProbeProcess(pid) == Native.Probe.Alive;
        }

        private static bool WaitExit(int pid, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (!IsRunning(pid)) return true;
                Thread.Sleep(80);
                waited += 80;
            }
            return !IsRunning(pid);
        }

        /// <summary>向目标进程的所有顶层窗口投递 WM_CLOSE，请求它体面地退出。</summary>
        public static int RequestClose(int pid)
        {
            int sent = 0;
            List<IntPtr> wins = Native.GetTopLevelWindows(pid);
            foreach (IntPtr h in wins)
            {
                string title = Native.GetWindowTitleSafe(h);
                if (title.Length == 0) continue;   // 只对有标题的窗口发，避免打扰消息窗口
                try
                {
                    if (Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) sent++;
                }
                catch { }
            }
            return sent;
        }

        /// <summary>
        /// 关闭前的身份校验：确认这个 PID 仍然是当初扫描到的那个进程。
        /// 防止用户在确认对话框上停留期间进程退出、PID 被系统回收给别的程序，导致误杀。
        /// </summary>
        private static bool VerifyIdentity(int pid, string expectedName, long expectedStart, out string reason)
        {
            reason = "";
            Native.Probe st = Native.ProbeProcess(pid);
            if (st == Native.Probe.Gone) { reason = "进程已退出"; return false; }
            if (st == Native.Probe.AccessDenied)
            {
                reason = "拒绝访问（该进程权限更高，请以管理员身份运行本程序）";
                return false;
            }

            try
            {
                Process p = Process.GetProcessById(pid);
                if (!string.IsNullOrEmpty(expectedName) &&
                    !string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "PID 已被系统回收并分配给「" + p.ProcessName + "」，出于安全已跳过";
                    return false;
                }
                if (expectedStart > 0)
                {
                    try
                    {
                        long now = p.StartTime.ToUniversalTime().Ticks;
                        if (Math.Abs(now - expectedStart) > TimeSpan.TicksPerSecond * 2)
                        {
                            reason = "PID 对应的进程已被替换（启动时间不符），出于安全已跳过";
                            return false;
                        }
                    }
                    catch { /* 取不到启动时间就不做这层判断 */ }
                }
                return true;
            }
            catch (ArgumentException) { reason = "进程已退出"; return false; }
            catch { reason = "无法确认进程状态，出于安全已跳过"; return false; }
        }

        // ==================== 进程树 ====================
        private static Dictionary<int, List<int>> BuildChildMap()
        {
            Dictionary<int, List<int>> map = new Dictionary<int, List<int>>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return map; }
            foreach (Process p in all)
            {
                try
                {
                    int pid = p.Id;
                    int ppid = Native.GetParentPid(pid);
                    if (ppid <= 0) continue;
                    List<int> lst;
                    if (!map.TryGetValue(ppid, out lst))
                    {
                        lst = new List<int>();
                        map[ppid] = lst;
                    }
                    lst.Add(pid);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return map;
        }

        private static void CollectDescendants(int pid, Dictionary<int, List<int>> map, HashSet<int> into, int depth)
        {
            if (depth > 12) return;
            List<int> kids;
            if (!map.TryGetValue(pid, out kids)) return;
            foreach (int k in kids)
            {
                if (into.Add(k)) CollectDescendants(k, map, into, depth + 1);
            }
        }

        /// <summary>计算进程深度（用于先杀子进程再杀父进程）。</summary>
        private static int Depth(int pid, Dictionary<int, int> parentOf, int guard)
        {
            int d = 0;
            int cur = pid;
            while (d < guard)
            {
                int pp;
                if (!parentOf.TryGetValue(cur, out pp) || pp <= 0) break;
                cur = pp;
                d++;
            }
            return d;
        }

        private static string SafeName(int pid)
        {
            try { return Process.GetProcessById(pid).ProcessName; }
            catch { return "?"; }
        }

        private static long SafeStartTicks(int pid)
        {
            try { return Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }

        private static long SafeWorkingSet(int pid)
        {
            try { return Process.GetProcessById(pid).WorkingSet64; }
            catch { return 0; }
        }

        private static int SafeSession(int pid)
        {
            try { return Process.GetProcessById(pid).SessionId; }
            catch { return -1; }
        }

        // ==================== 计划 ====================
        /// <summary>
        /// 生成关闭计划：把用户选中的目标展开成“连同子进程”的完整清单，并在此阶段就把
        /// 系统关键进程、其他会话的进程、系统目录里的程序、自身/父进程链全部剔除。
        /// 先出计划再执行，界面与命令行才能把“真正会被关掉的东西”如实展示出来。
        /// </summary>
        public static KillPlan BuildPlan(
            List<ProcItem> targets,
            bool killTree,
            HashSet<int> hardProtected,
            Action<string> log)
        {
            KillPlan plan = new KillPlan();
            if (targets == null || targets.Count == 0) return plan;

            int ownSession = Scanner.CurrentSessionId();
            Dictionary<int, List<int>> childMap = BuildChildMap();
            Dictionary<int, ProcItem> wanted = new Dictionary<int, ProcItem>();

            foreach (ProcItem t in targets)
            {
                if (hardProtected != null && hardProtected.Contains(t.Pid))
                {
                    plan.Skipped.Add(t.Name + " (" + t.Pid + ")：本程序自身或父进程链，拒绝关闭");
                    if (log != null) log("  ⛔ " + t.Name + " (" + t.Pid + ") 属于本程序自身或父进程链，已拒绝关闭");
                    continue;
                }
                if (Scanner.IsProtectedName(t.Name))
                {
                    plan.Skipped.Add(t.Name + " (" + t.Pid + ")：Windows 系统关键进程，拒绝关闭");
                    if (log != null) log("  ⛔ " + t.Name + " (" + t.Pid + ") 在系统保护名单中，已拒绝关闭");
                    continue;
                }

                plan.MainTargets.Add(t);
                if (!wanted.ContainsKey(t.Pid)) wanted[t.Pid] = t;

                if (!killTree) continue;

                HashSet<int> kids = new HashSet<int>();
                CollectDescendants(t.Pid, childMap, kids, 0);
                foreach (int k in kids)
                {
                    if (wanted.ContainsKey(k)) continue;

                    string kname = SafeName(k);
                    // 子进程必须过和主目标一模一样的安全闸门，否则会绕过保护名单（例如 conhost）
                    if (hardProtected != null && hardProtected.Contains(k))
                    {
                        plan.Skipped.Add(kname + " (" + k + ")：本程序自身或父进程链，拒绝关闭");
                        continue;
                    }
                    if (Scanner.IsProtectedName(kname))
                    {
                        plan.Skipped.Add(kname + " (" + k + ")：Windows 系统关键进程，拒绝关闭");
                        if (log != null) log("  ⛔ 子进程 " + kname + " (" + k + ") 在系统保护名单中，已跳过");
                        continue;
                    }
                    if (ownSession > 0 && SafeSession(k) != ownSession)
                    {
                        plan.Skipped.Add(kname + " (" + k + ")：属于其他会话，拒绝关闭");
                        if (log != null) log("  ⛔ 子进程 " + kname + " (" + k + ") 属于其他会话，已跳过");
                        continue;
                    }

                    string kpath = Native.GetProcessImagePath(k);
                    if (string.IsNullOrEmpty(kpath))
                    {
                        plan.Skipped.Add(kname + " (" + k + ")：路径不可读，无法确认身份，拒绝关闭");
                        if (log != null) log("  ⛔ 子进程 " + kname + " (" + k + ") 路径不可读，已跳过");
                        continue;
                    }
                    if (Scanner.IsSystemPath(kpath))
                    {
                        plan.Skipped.Add(kname + " (" + k + ")：位于 Windows 系统目录，拒绝关闭");
                        if (log != null) log("  ⛔ 子进程 " + kname + " (" + k + ") 位于系统目录，已跳过");
                        continue;
                    }

                    ProcItem ci = new ProcItem();
                    ci.Pid = k;
                    ci.Name = kname;
                    ci.Path = kpath;
                    ci.ParentPid = Native.GetParentPid(k);
                    ci.StartTimeTicks = SafeStartTicks(k);
                    ci.WorkingSet = SafeWorkingSet(k);
                    ci.SessionId = ownSession;
                    ci.Kind = ProcKind.BackgroundApp;
                    ci.KindText = "子进程";
                    wanted[k] = ci;
                    plan.Descendants.Add(ci);
                }
            }

            // 排序：子进程先杀、父进程后杀，避免父进程先死导致子进程变孤儿
            Dictionary<int, int> parentOf = new Dictionary<int, int>();
            foreach (KeyValuePair<int, ProcItem> kv in wanted)
                parentOf[kv.Key] = kv.Value.ParentPid;

            List<ProcItem> ordered = new List<ProcItem>(wanted.Values);
            ordered.Sort(delegate(ProcItem a, ProcItem b)
            {
                int da = Depth(a.Pid, parentOf, 16);
                int db = Depth(b.Pid, parentOf, 16);
                return db.CompareTo(da);
            });
            plan.Ordered = ordered;
            return plan;
        }

        // ==================== 执行 ====================
        /// <summary>执行关闭计划。计划本身已经过滤过受保护进程，这里再做一次身份校验。</summary>
        public static List<KillOutcome> Kill(KillPlan plan, Options opt, Action<string> log)
        {
            List<KillOutcome> outcomes = new List<KillOutcome>();
            if (plan == null || plan.Ordered.Count == 0) return outcomes;

            foreach (ProcItem it in plan.Ordered)
            {
                int pid = it.Pid;
                KillOutcome o = new KillOutcome();
                o.Pid = pid;
                o.Name = it.Name;
                o.FreedBytes = it.WorkingSet;

                // 身份校验：确保 PID 还是当初那一个进程，且我们碰得到它
                string why;
                if (!VerifyIdentity(pid, it.Name, it.StartTimeTicks, out why))
                {
                    if (why == "进程已退出")
                    {
                        o.Success = true;
                        o.Method = "已退出";
                        o.Message = why;
                    }
                    else
                    {
                        o.Success = false;
                        o.Method = "无法关闭";
                        o.Message = why;
                        if (log != null) log("  ✗ " + it.Name + " (" + pid + ")：" + why);
                    }
                    outcomes.Add(o);
                    continue;
                }

                // 第一步：请求正常退出
                if (opt.Graceful)
                {
                    int sent = RequestClose(pid);
                    if (sent > 0 && log != null) log("  → 已向 " + it.Name + " (" + pid + ") 发送正常关闭请求，等待其保存数据…");
                    if (sent > 0 && WaitExit(pid, opt.GracefulTimeoutMs))
                    {
                        o.Success = true;
                        o.Method = "正常退出";
                        o.Message = "程序自行退出";
                        if (log != null) log("  ✓ " + it.Name + " (" + pid + ") 已正常退出");
                        outcomes.Add(o);
                        continue;
                    }
                }

                // 强制结束前再校验一次（等待期间进程可能已退出、PID 可能被回收）
                if (!VerifyIdentity(pid, it.Name, it.StartTimeTicks, out why))
                {
                    if (why == "进程已退出")
                    {
                        o.Success = true;
                        o.Method = "已退出";
                        o.Message = why;
                    }
                    else
                    {
                        o.Success = false;
                        o.Method = "无法关闭";
                        o.Message = why;
                        if (log != null) log("  ✗ " + it.Name + " (" + pid + ")：" + why);
                    }
                    outcomes.Add(o);
                    continue;
                }

                // 第二步：强制结束
                try
                {
                    Process p = Process.GetProcessById(pid);
                    p.Kill();
                    if (WaitExit(pid, 2500))
                    {
                        o.Success = true;
                        o.Method = "强制结束";
                        o.Message = "已强制结束";
                        if (log != null) log("  ✓ " + it.Name + " (" + pid + ") 已强制结束");
                    }
                    else
                    {
                        o.Success = false;
                        o.Method = "强制结束";
                        o.Message = "结束请求已发出但进程仍在运行";
                        if (log != null) log("  ! " + it.Name + " (" + pid + ") 仍在运行");
                    }
                }
                catch (Win32Exception wex)
                {
                    o.Success = false;
                    o.Method = "强制结束";
                    o.Message = (wex.NativeErrorCode == Native.ERROR_ACCESS_DENIED)
                        ? "拒绝访问（该进程权限更高，请以管理员身份运行本程序）"
                        : "失败：" + wex.Message;
                    if (log != null) log("  ✗ " + it.Name + " (" + pid + ") " + o.Message);
                }
                catch (ArgumentException)
                {
                    o.Success = true;
                    o.Method = "强制结束";
                    o.Message = "结束瞬间已自行退出";
                    if (log != null) log("  ✓ " + it.Name + " (" + pid + ") 已退出");
                }
                catch (Exception ex)
                {
                    o.Success = false;
                    o.Method = "强制结束";
                    o.Message = "失败：" + ex.Message;
                    if (log != null) log("  ✗ " + it.Name + " (" + pid + ") " + o.Message);
                }
                outcomes.Add(o);
            }

            return outcomes;
        }
    }
}
