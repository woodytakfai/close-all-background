using System;
using System.Windows.Forms;

// 测试用的“假后台应用”：一个普通窗口程序，收到 WM_CLOSE 就正常退出。
// 仅用于验证被测程序的“先礼后兵”关闭逻辑，不属于交付物。
class VictimApp
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Form f = new Form();
        f.Text = "VictimApp 测试窗口";
        f.Width = 360;
        f.Height = 220;
        f.StartPosition = FormStartPosition.CenterScreen;
        Application.Run(f);
    }
}
