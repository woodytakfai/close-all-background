using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CabApp
{
    public class MainForm : Form
    {
        // ---- 控件 ----
        private Label lblTitle, lblSub, lblAdmin, lblStatus;
        private Button btnOneClick, btnRefresh, btnCheckAll, btnUncheckAll, btnAdmin, btnProtected;
        private CheckBox chkConfirm, chkGraceful, chkTree, chkWindowed, chkSystem, chkServices;
        private ListView lv;
        private TextBox txtLog;
        private ContextMenuStrip menu;
        private ToolTip tips;

        private ScanResult lastScan = new ScanResult();
        private ScanOptions options = new ScanOptions();
        private bool busy;
        private bool rebuilding;

        // ==================== 排除名单持久化 ====================
        private static string ExcludeFile
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CloseAllBackground");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, "exclude.txt");
            }
        }

        private static HashSet<string> LoadExcludes()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(ExcludeFile))
                {
                    foreach (string line in File.ReadAllLines(ExcludeFile))
                    {
                        string s = line.Trim();
                        if (s.Length > 0 && !s.StartsWith("#")) set.Add(s);
                    }
                }
            }
            catch { }
            return set;
        }

        private static void SaveExcludes(HashSet<string> set)
        {
            try
            {
                List<string> lines = new List<string>(set);
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(ExcludeFile, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        // ==================== 构造 ====================
        public MainForm()
        {
            options.ExcludeNames = LoadExcludes();
            BuildUi();
            AppendLog("程序已启动。" + (Scanner.IsAdmin() ? "当前以管理员权限运行。" : "当前为普通权限（部分高权限进程可能无法关闭）。"));
            RefreshList();

            // 关闭窗口时如果还在执行，给出确认，避免把关闭操作拦腰截断且不产生报告
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!busy) return;
                if (MessageBox.Show(this, "正在关闭进程中，现在退出会中断本次操作，且不会生成完整报告。确定退出吗？",
                        "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    e.Cancel = true;
                }
            };
        }

        private Font UiFont { get { return new Font("Microsoft YaHei UI", 9F); } }
        private Font UiFontBold { get { return new Font("Microsoft YaHei UI", 9F, FontStyle.Bold); } }

        /// <summary>右上角权限标签按实际文字宽度右对齐（AutoSize 时 Width 变化不会自动重排）。</summary>
        private void PlaceAdminLabel()
        {
            if (lblAdmin == null || lblAdmin.Parent == null) return;
            int w = TextRenderer.MeasureText(lblAdmin.Text, lblAdmin.Font).Width;
            lblAdmin.Left = Math.Max(20, lblAdmin.Parent.ClientSize.Width - w - 22);
        }

        private void BuildUi()
        {
            Text = "一键关闭后台应用  v1.0";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1040, 700);
            MinimumSize = new Size(900, 600);
            Font = UiFont;
            BackColor = Color.FromArgb(246, 247, 249);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.Padding = new Padding(0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            Controls.Add(root);

            // ---------- 标题 ----------
            Panel pHead = new Panel();
            pHead.Dock = DockStyle.Fill;
            pHead.Padding = new Padding(16, 10, 16, 0);
            lblTitle = new Label();
            lblTitle.Text = "一键关闭后台应用";
            lblTitle.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(28, 32, 40);
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(16, 8);
            lblSub = new Label();
            lblSub.Text = "扫描当前登录会话中的后台程序：先请求它自己正常退出（保存数据），1.5 秒无响应再强制结束；系统关键进程永不触碰。";
            lblSub.ForeColor = Color.FromArgb(110, 116, 128);
            lblSub.AutoSize = true;
            lblSub.Location = new Point(18, 40);
            lblAdmin = new Label();
            lblAdmin.AutoSize = true;
            lblAdmin.Location = new Point(700, 12);
            lblAdmin.ForeColor = Color.FromArgb(200, 120, 0);
            pHead.Controls.Add(lblTitle);
            pHead.Controls.Add(lblSub);
            pHead.Controls.Add(lblAdmin);
            pHead.Resize += delegate { PlaceAdminLabel(); };
            root.Controls.Add(pHead, 0, 0);

            // ---------- 按钮 ----------
            FlowLayoutPanel pBtn = new FlowLayoutPanel();
            pBtn.Dock = DockStyle.Fill;
            pBtn.Padding = new Padding(14, 8, 14, 0);
            pBtn.WrapContents = false;

            btnOneClick = new Button();
            btnOneClick.Text = "⚡ 一键关闭后台应用";
            btnOneClick.Size = new Size(228, 44);
            btnOneClick.FlatStyle = FlatStyle.Flat;
            btnOneClick.FlatAppearance.BorderSize = 0;
            btnOneClick.BackColor = Color.FromArgb(216, 58, 58);
            btnOneClick.ForeColor = Color.White;
            btnOneClick.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            btnOneClick.Margin = new Padding(0, 0, 10, 0);
            btnOneClick.Click += delegate { DoKill(); };

            btnRefresh = MakeBtn("刷新列表", 90);
            btnRefresh.Click += delegate { RefreshList(); };

            btnCheckAll = MakeBtn("全选", 64);
            btnCheckAll.Click += delegate { CheckAll(true); };

            btnUncheckAll = MakeBtn("全不选", 72);
            btnUncheckAll.Click += delegate { CheckAll(false); };

            btnProtected = MakeBtn("受保护进程", 100);
            btnProtected.Click += delegate { ShowProtected(); };

            btnAdmin = MakeBtn("以管理员身份重启", 140);
            btnAdmin.Click += delegate { RestartAsAdmin(); };

            pBtn.Controls.Add(btnOneClick);
            pBtn.Controls.Add(btnRefresh);
            pBtn.Controls.Add(btnCheckAll);
            pBtn.Controls.Add(btnUncheckAll);
            pBtn.Controls.Add(btnProtected);
            pBtn.Controls.Add(btnAdmin);
            root.Controls.Add(pBtn, 0, 1);

            // ---------- 选项 ----------
            FlowLayoutPanel pOpt = new FlowLayoutPanel();
            pOpt.Dock = DockStyle.Fill;
            pOpt.Padding = new Padding(18, 6, 14, 0);
            pOpt.WrapContents = true;
            pOpt.BackColor = Color.White;

            chkConfirm = MakeChk("关闭前确认", true, 110);
            chkGraceful = MakeChk("优先正常退出（推荐）", true, 190);
            chkTree = MakeChk("连带关闭子进程", true, 140);
            chkWindowed = MakeChk("包含有窗口的应用", false, 160);
            chkSystem = MakeChk("包含系统组件（慎用）", false, 180);
            chkServices = MakeChk("包含系统服务（慎用）", false, 180);

            chkWindowed.CheckedChanged += delegate { options.IncludeWindowApps = chkWindowed.Checked; RefreshList(); };
            chkSystem.CheckedChanged += delegate { options.IncludeSystemComponents = chkSystem.Checked; RefreshList(); };
            chkServices.CheckedChanged += delegate { options.IncludeSystemServices = chkServices.Checked; RefreshList(); };

            pOpt.Controls.Add(chkConfirm);
            pOpt.Controls.Add(chkGraceful);
            pOpt.Controls.Add(chkTree);
            pOpt.Controls.Add(chkWindowed);
            pOpt.Controls.Add(chkSystem);
            pOpt.Controls.Add(chkServices);
            root.Controls.Add(pOpt, 0, 2);

            // ---------- 列表 ----------
            lv = new ListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.GridLines = false;
            lv.MultiSelect = true;
            lv.HideSelection = false;
            lv.BackColor = Color.White;
            lv.Columns.Add("", 30);
            lv.Columns.Add("进程名", 210);
            lv.Columns.Add("PID", 62, HorizontalAlignment.Right);
            lv.Columns.Add("内存占用", 88, HorizontalAlignment.Right);
            lv.Columns.Add("类型", 90);
            lv.Columns.Add("发布者", 150);
            lv.Columns.Add("说明 / 窗口标题", 240);
            lv.Columns.Add("路径", 320);
            lv.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                UpdateButtonText();
            };
            lv.MouseDoubleClick += delegate { OpenFileLocation(); };

            menu = new ContextMenuStrip();
            menu.Items.Add("永不关闭此程序", null, delegate { AddExclude(); });
            menu.Items.Add("打开文件所在位置", null, delegate { OpenFileLocation(); });
            menu.Items.Add("复制路径", null, delegate { CopyPath(); });
            lv.ContextMenuStrip = menu;

            tips = new ToolTip();
            tips.SetToolTip(lv, "勾选=会被『一键关闭』处理。右键可把某个程序加入永久排除名单。");

            root.Controls.Add(lv, 0, 3);

            // ---------- 日志 ----------
            txtLog = new TextBox();
            txtLog.Dock = DockStyle.Fill;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(28, 30, 34);
            txtLog.ForeColor = Color.FromArgb(200, 205, 214);
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.Font = new Font("Consolas", 8.5F);
            txtLog.Margin = new Padding(14, 6, 14, 6);
            root.Controls.Add(txtLog, 0, 4);

            // ---------- 状态栏 ----------
            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(16, 0, 0, 0);
            lblStatus.ForeColor = Color.FromArgb(90, 96, 108);
            lblStatus.BackColor = Color.FromArgb(238, 240, 244);
            root.Controls.Add(lblStatus, 0, 5);

            AcceptButton = btnOneClick;
        }

        private Button MakeBtn(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = new Size(width, 44);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 214);
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(40, 44, 52);
            b.Margin = new Padding(0, 0, 8, 0);
            return b;
        }

        private CheckBox MakeChk(string text, bool @checked, int width)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Checked = @checked;
            c.AutoSize = false;
            c.Width = width;
            c.Height = 26;
            c.ForeColor = Color.FromArgb(50, 55, 65);
            return c;
        }

        // ==================== 刷新 ====================
        private void RefreshList()
        {
            if (busy) return;
            // 重建列表期间必须屏蔽 ItemChecked 触发的更新：
            // lv.Items.Add/Checked 会同步回调 ItemChecked，此时集合正处于变更中，
            // 若在回调里再去遍历 lv.Items 就是重入，会拿到不一致的状态。
            rebuilding = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                options.IncludeWindowApps = chkWindowed.Checked;
                options.IncludeSystemComponents = chkSystem.Checked;
                options.IncludeSystemServices = chkServices.Checked;
                options.ProtectParentChain = true;   // 父进程链永远保护，不提供关闭开关
                options.ExcludeNames = LoadExcludes();

                lastScan = Scanner.Scan(options);

                lv.BeginUpdate();
                try
                {
                    lv.Items.Clear();
                    foreach (ProcItem it in lastScan.Items)
                    {
                        ListViewItem li = new ListViewItem("");
                        li.SubItems.Add(it.DisplayName);
                        li.SubItems.Add(it.Pid.ToString());
                        li.SubItems.Add(it.MemText);
                        li.SubItems.Add(it.KindText);
                        li.SubItems.Add(it.PublisherText);
                        li.SubItems.Add(string.IsNullOrEmpty(it.WindowTitle) ? it.Description : it.WindowTitle);
                        li.SubItems.Add(it.Path);
                        li.Tag = it;
                        li.Checked = true;
                        if (it.Kind == ProcKind.SystemService || it.Kind == ProcKind.SystemComponent)
                            li.ForeColor = Color.FromArgb(190, 90, 40);
                        lv.Items.Add(li);
                    }
                }
                finally { lv.EndUpdate(); }

                if (lv.Items.Count > 0) lv.EnsureVisible(0);   // 防止列表默认滚到底部

                lblAdmin.Text = lastScan.IsAdmin ? "● 管理员权限" : "○ 普通权限（建议以管理员身份运行以关闭更多进程）";
                lblAdmin.ForeColor = lastScan.IsAdmin ? Color.FromArgb(30, 140, 70) : Color.FromArgb(200, 120, 0);
                PlaceAdminLabel();
                lblStatus.Text = string.Format(
                    "共 {0} 个进程 │ 可关闭候选 {1} 个 │ 已保护 {2} 个 │ 因选项未纳入 {3} 个 │ 永久排除名单 {4} 项",
                    lastScan.TotalProcesses, lastScan.Items.Count, lastScan.ProtectedCount,
                    lastScan.SkippedCount, options.ExcludeNames.Count);
            }
            catch (Exception ex)
            {
                AppendLog("扫描失败：" + ex.Message);
            }
            finally
            {
                rebuilding = false;
                Cursor = Cursors.Default;
            }
            UpdateButtonText();
        }

        private void UpdateButtonText()
        {
            if (rebuilding) return;                                   // 列表重建中，忽略重入
            if (lv == null || btnOneClick == null) return;
            try
            {
                int n = 0;
                foreach (ListViewItem li in lv.Items)
                {
                    if (li != null && li.Checked) n++;
                }
                btnOneClick.Text = string.Format("⚡ 一键关闭 {0} 个后台应用", n);
                btnOneClick.Enabled = !busy && n > 0;
            }
            catch { }
        }

        private void CheckAll(bool state)
        {
            rebuilding = true;
            lv.BeginUpdate();
            try
            {
                foreach (ListViewItem li in lv.Items) li.Checked = state;
            }
            finally
            {
                lv.EndUpdate();
                rebuilding = false;
            }
            UpdateButtonText();
        }

        // ==================== 关闭动作 ====================
        private void DoKill()
        {
            if (busy) return;

            List<ProcItem> picked = new List<ProcItem>();
            foreach (ListViewItem li in lv.Items)
            {
                if (!li.Checked) continue;
                ProcItem it = li.Tag as ProcItem;
                if (it != null && it.Selectable) picked.Add(it);
            }

            if (picked.Count == 0)
            {
                MessageBox.Show(this, "当前没有勾选任何后台应用。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 先出计划：这一步会把受保护进程、其他会话、系统目录里的子进程全部剔除，
            // 所以确认框里显示的就是真正会被关掉的东西。
            HashSet<int> hardProtected = new HashSet<int>();
            hardProtected.Add(Process.GetCurrentProcess().Id);
            foreach (int a in Scanner.GetAncestorPids(Process.GetCurrentProcess().Id)) hardProtected.Add(a);

            KillPlan plan;
            Cursor = Cursors.WaitCursor;
            try
            {
                plan = Killer.BuildPlan(picked, chkTree.Checked, hardProtected, null);
            }
            finally { Cursor = Cursors.Default; }

            if (plan.Ordered.Count == 0)
            {
                MessageBox.Show(this, "所有目标都被安全闸门拦下了，没有可关闭的进程。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (chkConfirm.Checked)
            {
                using (ConfirmForm cf = new ConfirmForm(plan, chkWindowed.Checked, chkTree.Checked))
                {
                    if (cf.ShowDialog(this) != DialogResult.OK) return;
                }
            }

            Killer.Options ko = new Killer.Options();
            ko.Graceful = chkGraceful.Checked;
            ko.KillTree = chkTree.Checked;
            ko.GracefulTimeoutMs = 1500;

            SetBusy(true);
            AppendLog("");
            AppendLog(string.Format("════ 开始关闭 {0} 个进程{1}（{2}）════",
                plan.Ordered.Count,
                plan.Descendants.Count > 0 ? "，含 " + plan.Descendants.Count + " 个连带子进程" : "",
                ko.Graceful ? "先礼后兵：正常退出优先" : "直接强制结束"));
            foreach (string s in plan.Skipped) AppendLog("  ⛔ 安全跳过：" + s);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                List<KillOutcome> outcomes = null;
                try
                {
                    outcomes = Killer.Kill(plan, ko, AppendLog);
                }
                catch (Exception ex)
                {
                    AppendLog("发生异常：" + ex.Message);
                }
                try
                {
                    BeginInvoke(new Action(delegate()
                    {
                        SetBusy(false);
                        if (outcomes != null) ReportResult(outcomes);
                        RefreshList();
                    }));
                }
                catch { }
            });
        }

        private void ReportResult(List<KillOutcome> outcomes)
        {
            int ok = 0, fail = 0;
            long freed = 0;
            List<string> failed = new List<string>();
            foreach (KillOutcome o in outcomes)
            {
                if (o.Success) { ok++; freed += o.FreedBytes; }
                else { fail++; failed.Add(o.Name + " (" + o.Pid + ")：" + o.Message); }
            }
            AppendLog(string.Format("════ 完成：成功 {0} 个，失败 {1} 个，释放内存约 {2} ════",
                ok, fail, ProcItem.FormatSize(freed)));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("已关闭：{0} 个", ok));
            sb.AppendLine(string.Format("失败：{0} 个", fail));
            sb.AppendLine(string.Format("释放内存：约 {0}", ProcItem.FormatSize(freed)));
            if (failed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("失败明细：");
                for (int i = 0; i < failed.Count && i < 10; i++) sb.AppendLine("  · " + failed[i]);
                if (failed.Count > 10) sb.AppendLine("  … 其余见下方日志");
            }
            MessageBox.Show(this, sb.ToString(), "关闭完成", MessageBoxButtons.OK,
                fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void SetBusy(bool b)
        {
            busy = b;
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
            btnRefresh.Enabled = !b;
            btnCheckAll.Enabled = !b;
            btnUncheckAll.Enabled = !b;
            btnAdmin.Enabled = !b;
            btnProtected.Enabled = !b;
            UpdateButtonText();
            if (b) lblStatus.Text = "正在处理，请稍候…";
        }

        // ==================== 右键菜单 ====================
        private ProcItem SelectedItem()
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as ProcItem;
        }

        private void AddExclude()
        {
            ProcItem it = SelectedItem();
            if (it == null) return;
            HashSet<string> set = LoadExcludes();
            set.Add(it.Name);
            SaveExcludes(set);
            AppendLog("已将「" + it.Name + "」加入永久排除名单（以后不会关闭它）。");
            RefreshList();
        }

        private void OpenFileLocation()
        {
            ProcItem it = SelectedItem();
            if (it == null || string.IsNullOrEmpty(it.Path)) return;
            try { Process.Start("explorer.exe", "/select,\"" + it.Path + "\""); }
            catch (Exception ex) { AppendLog("打开位置失败：" + ex.Message); }
        }

        private void CopyPath()
        {
            ProcItem it = SelectedItem();
            if (it == null) return;
            try { Clipboard.SetText(it.Path); } catch { }
        }

        private void ShowProtected()
        {
            List<ProcItem> list = lastScan.ProtectedItems;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("以下 " + list.Count + " 个进程受保护，本程序永远不会关闭它们：");
            sb.AppendLine();
            sb.AppendLine(string.Format("{0,-26}{1,-8}{2,-42}{3}", "进程名", "PID", "保护原因", "路径"));
            sb.AppendLine(new string('─', 110));
            foreach (ProcItem it in list)
            {
                sb.AppendLine(string.Format("{0,-26}{1,-8}{2,-42}{3}",
                    Trim(it.Name, 24), it.Pid, Trim(it.ProtectReason, 40), it.Path));
            }
            using (TextViewForm f = new TextViewForm("受保护进程列表", sb.ToString()))
                f.ShowDialog(this);
        }

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private void RestartAsAdmin()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                AppendLog("提权失败：" + ex.Message);
            }
        }

        // ==================== 日志 ====================
        private void AppendLog(string line)
        {
            if (txtLog == null) return;
            if (txtLog.InvokeRequired)
            {
                try { txtLog.BeginInvoke(new Action<string>(AppendLog), new object[] { line }); }
                catch { }
                return;
            }
            string stamped = DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine;
            txtLog.AppendText(stamped);
        }
    }

    // ==================== 确认对话框 ====================
    public class ConfirmForm : Form
    {
        public ConfirmForm(KillPlan plan, bool includesWindowed, bool killTree)
        {
            Text = "确认关闭";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 460);
            Font = new Font("Microsoft YaHei UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            long mem = 0;
            foreach (ProcItem it in plan.Ordered) mem += it.WorkingSet;

            Label head = new Label();
            head.Text = string.Format("即将关闭 {0} 个进程，预计释放 {1}：", plan.Ordered.Count, ProcItem.FormatSize(mem));
            head.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            head.SetBounds(16, 14, 568, 24);

            ListBox lb = new ListBox();
            lb.SetBounds(16, 44, 568, 300);
            lb.Font = new Font("Consolas", 9F);
            foreach (ProcItem it in plan.MainTargets)
            {
                lb.Items.Add(string.Format("{0,-28}{1,-8}{2}",
                    Trim(it.Name, 26), it.Pid, it.KindText));
            }
            if (plan.Descendants.Count > 0)
            {
                lb.Items.Add("── 以下 " + plan.Descendants.Count + " 个是连带关闭的子进程 ──");
                foreach (ProcItem it in plan.Descendants)
                {
                    lb.Items.Add(string.Format("{0,-28}{1,-8}{2}",
                        Trim(it.Name, 26), it.Pid, it.KindText));
                }
            }
            if (plan.Skipped.Count > 0)
            {
                lb.Items.Add("── 以下 " + plan.Skipped.Count + " 个触发安全闸门，不会被关闭 ──");
                foreach (string s in plan.Skipped) lb.Items.Add("  " + s);
            }

            Label warn = new Label();
            string w = includesWindowed
                ? "⚠ 已包含「有窗口的应用」：正在编辑的文档等未保存内容可能丢失！"
                : "⚠ 后台程序不会提示保存，未保存的数据可能丢失。";
            warn.Text = w;
            warn.ForeColor = Color.FromArgb(200, 50, 50);
            warn.SetBounds(16, 352, 568, 22);

            Label note = new Label();
            note.Text = killTree
                ? "（已开启「连带关闭子进程」：列表里带“子进程”标记的也会被关闭）"
                : "（未开启「连带关闭子进程」：只关闭上面列出的主目标）";
            note.ForeColor = Color.FromArgb(110, 116, 128);
            note.SetBounds(16, 374, 568, 20);

            Button ok = new Button();
            ok.Text = "确认关闭";
            ok.SetBounds(372, 406, 104, 34);
            ok.DialogResult = DialogResult.OK;
            ok.BackColor = Color.FromArgb(216, 58, 58);
            ok.ForeColor = Color.White;
            ok.FlatStyle = FlatStyle.Flat;

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(488, 406, 96, 34);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(head);
            Controls.Add(lb);
            Controls.Add(warn);
            Controls.Add(note);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }

    // ==================== 纯文本查看窗口 ====================
    public class TextViewForm : Form
    {
        public TextViewForm(string title, string content)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(820, 520);
            Font = new Font("Microsoft YaHei UI", 9F);
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Both;
            tb.WordWrap = false;
            tb.Dock = DockStyle.Fill;
            tb.Font = new Font("Consolas", 8.75F);
            tb.Text = content;
            Controls.Add(tb);
        }
    }
}
