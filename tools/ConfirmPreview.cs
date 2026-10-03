using System;
using System.Windows.Forms;
using CabApp;

// 仅用于截图检查确认对话框的排版，拿假数据构造，不连接任何真实进程。
class ConfirmPreview
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        KillPlan p = new KillPlan();
        string[,] main = new string[,] {
            { "QQ", "25516", "后台应用" },
            { "WeChat", "13788", "后台应用" },
            { "steamwebhelper", "14520", "后台应用" },
            { "NVIDIA Overlay", "20728", "后台应用" },
            { "GameViewerServer", "7760", "系统组件(路径不可读)" },
            { "msedgewebview2", "14952", "后台应用" },
        };
        for (int i = 0; i < main.GetLength(0); i++)
        {
            ProcItem it = new ProcItem();
            it.Name = main[i, 0]; it.Pid = int.Parse(main[i, 1]);
            it.KindText = main[i, 2]; it.WorkingSet = 1024L * 1024 * (120 - i * 15);
            p.MainTargets.Add(it);
            p.Ordered.Add(it);
        }
        string[,] kids = new string[,] {
            { "steamwebhelper", "24524", "子进程" },
            { "crashpad_handler", "8392", "子进程" },
        };
        for (int i = 0; i < kids.GetLength(0); i++)
        {
            ProcItem it = new ProcItem();
            it.Name = kids[i, 0]; it.Pid = int.Parse(kids[i, 1]);
            it.KindText = kids[i, 2]; it.WorkingSet = 1024L * 1024 * 30;
            p.Descendants.Add(it);
            p.Ordered.Add(it);
        }
        p.Skipped.Add("conhost (3820)：Windows 系统关键进程，拒绝关闭");
        p.Skipped.Add("explorer (4521)：Windows 系统关键进程，拒绝关闭");

        Application.Run(new ConfirmForm(p, false, true));
    }
}
