using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CabApp
{
    public static class Program
    {
        private const string Version = "1.0";

        private class Cli
        {
            public string Mode = "gui";       // gui | list | dryrun | kill
            public bool Yes;
            public bool Force;
            public bool Json;
            public bool NoTree;
            public string OutFile = "";
            public List<int> Pids = new List<int>();
            public ScanOptions Opts = new ScanOptions();
        }

        [STAThread]
        public static int Main(string[] args)
        {
            // 先接管控制台，否则 /target:winexe 在无控制台、无重定向时会把所有输出丢掉
            TextWriter w = SetupConsole();

            Cli c;
            try { c = Parse(args); }
            catch (HelpRequested) { w.WriteLine(HelpText()); return 0; }
            catch (Exception ex) { w.WriteLine("参数错误：" + ex.Message); w.WriteLine(); w.WriteLine(HelpText()); return 1; }

            if (c.Mode == "gui")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    ReportCrash(e.Exception, "界面线程");
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    ReportCrash(e.ExceptionObject as Exception, "后台线程");
                };
                Application.Run(new MainForm());
                return 0;
            }

            if (c.Mode == "list" || c.Mode == "dryrun")
            {
                ScanResult scan = Scanner.Scan(c.Opts);
                string report = c.Json ? BuildJson(scan, c) : BuildTextReport(scan, c);
                Emit(w, c, report);
                return 0;
            }

            // ---------- kill 模式 ----------
            if (!c.Yes)
            {
                // --all 没带 --yes：只预演，不执行
                ScanResult scan = Scanner.Scan(c.Opts);
                KillPlan preview = Killer.BuildPlan(scan.Items, !c.NoTree, ProtectedPids(), null);
                Emit(w, c, BuildPlanText(preview, true));
                return 0;
            }

            List<ProcItem> targets;
            StringBuilder pidNotes = new StringBuilder();
            if (c.Pids.Count > 0)
            {
                targets = ResolvePids(c.Pids, w, pidNotes);
            }
            else
            {
                ScanResult scan = Scanner.Scan(c.Opts);
                targets = new List<ProcItem>(scan.Items);
            }

            if (targets.Count == 0)
            {
                w.WriteLine("没有需要关闭的后台应用。");
                Emit(w, c, pidNotes.ToString());
                return 0;
            }

            HashSet<int> hard = ProtectedPids();

            // 日志同时进控制台和报告文件；BuildPlan 也要用它，跳过原因才会出现在 --out 报告里
            StringBuilder logBuf = new StringBuilder();
            Action<string> logger = delegate(string line)
            {
                lock (logBuf) logBuf.AppendLine(line);
                w.WriteLine(line);
            };

            KillPlan plan = Killer.BuildPlan(targets, !c.NoTree, hard, logger);

            if (plan.Ordered.Count == 0)
            {
                w.WriteLine("所有目标都被安全闸门拦下了，没有可关闭的进程。");
                foreach (string s in plan.Skipped) logger("  安全跳过：" + s);
                lock (logBuf)
                {
                    if (!string.IsNullOrEmpty(c.OutFile)) WriteFile(c.OutFile, logBuf.ToString());
                }
                return 3;
            }

            Killer.Options ko = new Killer.Options();
            ko.Graceful = !c.Force;
            ko.KillTree = !c.NoTree;

            w.WriteLine("开始关闭 " + plan.Ordered.Count + " 个进程" +
                        (plan.Descendants.Count > 0 ? "（含 " + plan.Descendants.Count + " 个连带子进程）" : "") + "…");
            List<KillOutcome> outcomes = Killer.Kill(plan, ko, logger);

            int ok = 0, fail = 0;
            long freed = 0;
            foreach (KillOutcome o in outcomes)
            {
                if (o.Success) { ok++; freed += o.FreedBytes; } else fail++;
            }
            string summary = string.Format("完成：成功 {0} 个，失败 {1} 个，释放内存约 {2}",
                ok, fail, ProcItem.FormatSize(freed));
            w.WriteLine(summary);
            lock (logBuf) logBuf.AppendLine(summary);

            foreach (KillOutcome o in outcomes)
                if (!o.Success) w.WriteLine("  失败：" + o.Name + " (" + o.Pid + ") " + o.Message);
            foreach (string s in plan.Skipped) logger("  安全跳过：" + s);

            lock (logBuf)
            {
                if (!string.IsNullOrEmpty(c.OutFile))
                    WriteFile(c.OutFile, logBuf.ToString());
            }
            return fail > 0 ? 2 : 0;
        }

        /// <summary>本程序自身 + 父进程链，永远不允许被关闭。</summary>
        private static HashSet<int> ProtectedPids()
        {
            HashSet<int> hard = new HashSet<int>();
            int me = Process.GetCurrentProcess().Id;
            hard.Add(me);
            foreach (int a in Scanner.GetAncestorPids(me)) hard.Add(a);
            return hard;
        }

        // ==================== 崩溃日志 ====================
        private static bool crashReporting;

        private static void ReportCrash(Exception ex, string source)
        {
            if (crashReporting) return;
            crashReporting = true;

            string detail = ex == null ? "(未知异常)" : ex.ToString();
            string file = Path.Combine(Path.GetTempPath(),
                "CloseAllBackground-crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            try
            {
                File.WriteAllText(file, "来源：" + source + Environment.NewLine +
                    "时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    Environment.NewLine + detail, new UTF8Encoding(true));
            }
            catch { }

            try
            {
                MessageBox.Show(
                    "程序遇到一个未处理的错误，已记录到：" + Environment.NewLine + file +
                    Environment.NewLine + Environment.NewLine + detail,
                    "一键关闭后台应用 - 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        // ==================== 参数解析 ====================
        private static Cli Parse(string[] args)
        {
            Cli c = new Cli();
            if (args == null || args.Length == 0) return c;

            foreach (string raw in args)
            {
                string a = (raw ?? "").Trim();
                if (a.Length == 0) continue;
                string lower = a.ToLowerInvariant();

                if (lower == "--list") { c.Mode = "list"; continue; }
                if (lower == "--dry-run" || lower == "--dryrun") { c.Mode = "dryrun"; continue; }
                if (lower == "--all") { c.Mode = "kill"; continue; }
                if (lower == "--yes" || lower == "-y") { c.Yes = true; continue; }
                if (lower == "--force" || lower == "-f") { c.Force = true; continue; }
                if (lower == "--json") { c.Json = true; continue; }
                if (lower == "--gui") { c.Mode = "gui"; continue; }
                if (lower == "--no-tree") { c.NoTree = true; continue; }
                if (lower == "--include-windows") { c.Opts.IncludeWindowApps = true; continue; }
                if (lower == "--include-system") { c.Opts.IncludeSystemComponents = true; continue; }
                if (lower == "--include-services") { c.Opts.IncludeSystemServices = true; continue; }
                if (lower == "--help" || lower == "-h" || lower == "/?") throw new HelpRequested();

                if (lower.StartsWith("--pid="))
                {
                    string[] parts = a.Substring(6).Split(',');
                    List<string> bad = new List<string>();
                    foreach (string s in parts)
                    {
                        string t = s.Trim();
                        if (t.Length == 0) continue;
                        int pid;
                        if (int.TryParse(t, out pid) && pid > 0) c.Pids.Add(pid);
                        else bad.Add(t);
                    }
                    if (bad.Count > 0)
                        throw new Exception("--pid 里有无法识别的进程号：" + string.Join(", ", bad.ToArray()) +
                                            "（正确写法：--pid=1234,5678）");
                    if (c.Pids.Count == 0)
                        throw new Exception("--pid= 后面没有有效的进程号");
                    c.Mode = "kill";
                    continue;
                }
                if (lower == "--pid")
                    throw new Exception("--pid 后面必须紧跟 = 和进程号，例如：--pid=1234,5678");

                if (lower.StartsWith("--out="))
                {
                    c.OutFile = a.Substring(6);
                    if (c.OutFile.Length == 0) throw new Exception("--out= 后面没有文件路径");
                    continue;
                }
                if (lower == "--out")
                    throw new Exception("--out 后面必须紧跟 = 和文件路径，例如：--out=报告.txt");

                if (lower.StartsWith("--pid") || lower.StartsWith("--out"))
                    throw new Exception("无法识别的参数：" + raw + "（正确写法：--pid=1234,5678 / --out=报告.txt）");

                throw new Exception("未知参数：" + raw);
            }

            if (c.Pids.Count > 0) { c.Mode = "kill"; c.Yes = true; }
            return c;
        }

        private class HelpRequested : Exception { }

        private static string HelpText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("一键关闭后台应用 v" + Version);
            sb.AppendLine();
            sb.AppendLine("用法：");
            sb.AppendLine("  一键关闭后台应用.exe                     打开图形界面（双击即可）");
            sb.AppendLine("  一键关闭后台应用.exe --list               列出所有可关闭的后台应用");
            sb.AppendLine("  一键关闭后台应用.exe --dry-run            显示将要关闭的内容，但不执行");
            sb.AppendLine("  一键关闭后台应用.exe --all --yes          静默关闭全部后台应用");
            sb.AppendLine("  一键关闭后台应用.exe --pid=1234,5678      关闭指定 PID");
            sb.AppendLine();
            sb.AppendLine("可选参数：");
            sb.AppendLine("  --include-windows      连有窗口的应用一起关闭");
            sb.AppendLine("  --include-system       连 Windows 系统组件一起关闭（慎用）");
            sb.AppendLine("  --include-services     连会话 0 服务一起关闭（需管理员，慎用）");
            sb.AppendLine("  --no-tree              不连带关闭目标程序的子进程");
            sb.AppendLine("  --force                跳过“正常退出”，直接强制结束");
            sb.AppendLine("  --json                 以 JSON 输出列表");
            sb.AppendLine("  --out=文件路径          把报告写入文件（UTF-8）");
            sb.AppendLine();
            sb.AppendLine("说明：本程序自身、父进程链、Windows 系统关键进程永远不会被关闭；");
            sb.AppendLine("      --all 必须同时给出 --yes 才会真正执行，否则只输出预演计划。");
            return sb.ToString();
        }

        // ==================== 报告 ====================
        private static string BuildPlanText(KillPlan plan, bool preview)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(preview
                ? "【预演模式】以下内容会被关闭（加 --yes 才会真的执行）："
                : "以下内容会被关闭：");
            sb.AppendLine();
            sb.AppendLine("主目标 " + plan.MainTargets.Count + " 个：");
            foreach (ProcItem it in plan.MainTargets)
                sb.AppendLine(string.Format("  {0,-30}{1,-8}{2,-11}{3}", Cut(it.Name, 28), it.Pid, it.MemText, it.KindText));

            if (plan.Descendants.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("连带关闭的子进程 " + plan.Descendants.Count + " 个：");
                foreach (ProcItem it in plan.Descendants)
                    sb.AppendLine(string.Format("  {0,-30}{1,-8}{2,-11}{3}", Cut(it.Name, 28), it.Pid, it.MemText, it.KindText));
            }

            if (plan.Skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("安全闸门已拦下 " + plan.Skipped.Count + " 个：");
                foreach (string s in plan.Skipped) sb.AppendLine("  " + s);
            }
            return sb.ToString();
        }

        private static string BuildTextReport(ScanResult scan, Cli c)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("一键关闭后台应用 v" + Version + "  扫描报告");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("权限：" + (scan.IsAdmin ? "管理员" : "普通用户"));
            sb.AppendLine(string.Format("进程总数 {0} │ 可关闭候选 {1} │ 受保护 {2} │ 因选项未纳入 {3}",
                scan.TotalProcesses, scan.Items.Count, scan.ProtectedCount, scan.SkippedCount));
            sb.AppendLine();
            sb.AppendLine(string.Format("{0,-4}{1,-30}{2,-8}{3,-11}{4,-14}{5,-22}{6}",
                "#", "进程名", "PID", "内存", "类型", "发布者", "路径"));
            sb.AppendLine(new string('-', 150));
            int i = 0;
            foreach (ProcItem it in scan.Items)
            {
                i++;
                sb.AppendLine(string.Format("{0,-4}{1,-30}{2,-8}{3,-11}{4,-14}{5,-22}{6}",
                    i, Cut(it.Name, 28), it.Pid, it.MemText, it.KindText, Cut(it.PublisherText, 20), it.Path));
            }
            if (scan.Items.Count == 0) sb.AppendLine("（没有符合条件的后台应用）");
            return sb.ToString();
        }

        private static string BuildJson(ScanResult scan, Cli c)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": \"" + Version + "\",");
            sb.AppendLine("  \"time\": \"" + DateTime.Now.ToString("s") + "\",");
            sb.AppendLine("  \"isAdmin\": " + (scan.IsAdmin ? "true" : "false") + ",");
            sb.AppendLine("  \"totalProcesses\": " + scan.TotalProcesses + ",");
            sb.AppendLine("  \"protectedCount\": " + scan.ProtectedCount + ",");
            sb.AppendLine("  \"selfPid\": " + scan.SelfPid + ",");
            sb.AppendLine("  \"candidates\": [");
            for (int i = 0; i < scan.Items.Count; i++)
            {
                ProcItem it = scan.Items[i];
                sb.Append("    {");
                sb.Append("\"pid\": " + it.Pid + ", ");
                sb.Append("\"name\": " + J(it.Name) + ", ");
                sb.Append("\"kind\": " + J(it.KindText) + ", ");
                sb.Append("\"memBytes\": " + it.WorkingSet + ", ");
                sb.Append("\"session\": " + it.SessionId + ", ");
                sb.Append("\"publisher\": " + J(it.Company) + ", ");
                sb.Append("\"path\": " + J(it.Path));
                sb.Append("}");
                sb.AppendLine(i == scan.Items.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"protected\": [");
            for (int i = 0; i < scan.ProtectedItems.Count; i++)
            {
                ProcItem it = scan.ProtectedItems[i];
                sb.Append("    {\"pid\": " + it.Pid + ", \"name\": " + J(it.Name) +
                          ", \"reason\": " + J(it.ProtectReason) + "}");
                sb.AppendLine(i == scan.ProtectedItems.Count - 1 ? "" : ",");
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string J(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u" + ((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private static void Emit(TextWriter w, Cli c, string text)
        {
            if (!string.IsNullOrEmpty(text)) w.Write(text);
            w.Flush();
            if (!string.IsNullOrEmpty(c.OutFile)) WriteFile(c.OutFile, text);
        }

        private static void WriteFile(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(true));
            }
            catch (Exception ex) { Console.Error.WriteLine("写文件失败：" + ex.Message); }
        }

        /// <summary>
        /// 解析 --pid 指定的进程。这里同样要过安全闸门：保护名单、自身/父进程链、其他会话一律拒绝。
        /// </summary>
        private static List<ProcItem> ResolvePids(List<int> pids, TextWriter w, StringBuilder notes)
        {
            List<ProcItem> list = new List<ProcItem>();
            HashSet<int> hard = ProtectedPids();
            int ownSession = Scanner.CurrentSessionId();

            Action<string> note = delegate(string line)
            {
                notes.AppendLine(line);
                w.WriteLine(line);
            };

            foreach (int pid in pids)
            {
                Native.Probe st = Native.ProbeProcess(pid);
                if (st == Native.Probe.Gone)
                {
                    note("[跳过] PID " + pid + " 不存在或已退出");
                    continue;
                }

                string name = "";
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch { }

                if (hard.Contains(pid))
                {
                    note("[拒绝] " + name + " (" + pid + ") 是本程序自身或父进程链，不能关闭");
                    continue;
                }
                if (Scanner.IsProtectedName(name))
                {
                    note("[拒绝] " + name + " (" + pid + ") 在 Windows 系统保护名单中，不能关闭");
                    continue;
                }
                if (st == Native.Probe.AccessDenied)
                {
                    note("[拒绝] PID " + pid + " 权限不足，无法确认身份（请以管理员身份运行）");
                    continue;
                }

                try
                {
                    Process p = Process.GetProcessById(pid);
                    ProcItem it = new ProcItem();
                    it.Pid = pid;
                    it.Name = p.ProcessName;
                    try { it.WorkingSet = p.WorkingSet64; } catch { }
                    try { it.StartTimeTicks = p.StartTime.ToUniversalTime().Ticks; } catch { }
                    try { it.HasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                    try { it.SessionId = p.SessionId; } catch { it.SessionId = -1; }

                    if (ownSession > 0 && it.SessionId != ownSession)
                    {
                        note("[拒绝] " + name + " (" + pid + ") 属于其他登录会话，不属于本次操作范围");
                        continue;
                    }

                    it.Path = Native.GetProcessImagePath(pid);
                    if (!string.IsNullOrEmpty(it.Path))
                    {
                        try
                        {
                            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(it.Path);
                            it.Company = (vi.CompanyName ?? "").Trim();
                        }
                        catch { }
                    }
                    it.KindText = "指定进程";
                    list.Add(it);
                }
                catch { note("[跳过] PID " + pid + " 无法访问"); }
            }
            return list;
        }

        /// <summary>
        /// 接管输出。/target:winexe 默认没有控制台，直接 Console.Write 会全部丢掉；
        /// 这里挂到父进程的控制台，并按真实控制台代码页输出（936 走 GBK、65001 走 UTF-8），
        /// 这样在 cmd、Windows Terminal、PowerShell 里中文都不会乱码。
        ///
        /// 关键顺序：AttachConsole 会把进程的标准句柄替换成控制台句柄，
        /// 所以“被重定向时”必须**先**把原来的输出流抓在手里，再 AttachConsole，
        /// 否则重定向到文件/管道的输出会被顶掉、变成空文件。
        /// </summary>
        private static TextWriter SetupConsole()
        {
            bool redirected = false;
            try { redirected = Console.IsOutputRedirected; } catch { }

            Stream captured = null;
            if (redirected)
            {
                try { captured = Console.OpenStandardOutput(); } catch { }
            }

            try { Native.AttachConsole(-1); } catch { }

            Encoding enc = null;
            try
            {
                uint cp = Native.GetConsoleOutputCP();
                if (cp > 0) enc = Encoding.GetEncoding((int)cp);
            }
            catch { }
            if (enc == null)
            {
                try { enc = Console.OutputEncoding; } catch { enc = Encoding.UTF8; }
            }

            Stream target = captured;
            if (target == null && !redirected)
            {
                // 没有控制台（双击运行）时这里会失败，属于预期情况，输出丢弃即可
                try { target = Console.OpenStandardOutput(); } catch { }
            }

            if (target != null)
            {
                try
                {
                    StreamWriter sw = new StreamWriter(target, enc);
                    sw.AutoFlush = true;
                    Console.SetOut(sw);
                }
                catch { }
            }

            try { return Console.Out; }
            catch { return TextWriter.Null; }
        }
    }
}
