using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("全屏助手")]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace FullscreenAssistant
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            WindowController.EnableDpiAwareness();
            bool created;
            using (var mutex = new Mutex(true, @"Local\FullscreenAssistant.Desktop.v1", out created))
            {
                if (!created)
                {
                    IntPtr existing = FindWindow(null, "全屏助手");
                    if (existing != IntPtr.Zero) { ShowWindow(existing, 9); Native.SetForegroundWindow(existing); }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    string journal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "窗口恢复记录.xml");
                    using (var controller = new WindowController(journal))
                    {
                        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                        {
                            string errors = controller.RestoreAll();
                            MessageBox.Show("操作遇到问题：" + e.Exception.Message +
                                (errors.Length == 0 ? "\r\n已恢复受控窗口。" : "\r\n" + errors), "全屏助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        };
                        Application.Run(new MainForm(controller));
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "全屏助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
            }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int cmd);
    }
}
