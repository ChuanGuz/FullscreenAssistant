using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FullscreenAssistant;

internal static class UiWorkflowTests
{
    private static readonly List<string> Results = new List<string>();
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static bool passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fixture")
            return (int)typeof(WindowIntegrationTests).GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { args });
        WindowController.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string ready = Path.Combine(Root, "ui-ready-" + Guid.NewGuid().ToString("N") + ".txt");
        Process fixture = null;
        IntPtr handle = IntPtr.Zero;
        string journal = Path.Combine(Root, "ui-workflow.journal.xml");
        try
        {
            var start = new ProcessStartInfo(Application.ExecutablePath, "--fixture normal \"" + ready + "\"") { UseShellExecute = false };
            fixture = Process.Start(start);
            DateTime limit = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(ready) && DateTime.UtcNow < limit) Thread.Sleep(50);
            Assert(File.Exists(ready), "Fixture did not become ready");
            handle = new IntPtr(long.Parse(File.ReadAllText(ready)));
            Snapshot before = Capture(handle);
            var text = new StringBuilder(2048); Native.GetWindowText(handle, text, text.Capacity);
            string fixtureTitle = text.ToString();
            using (var controller = new WindowController(journal))
            using (var form = new MainForm(controller))
            {
                var search = Field<TextBox>(form, "search");
                var grid = Field<DataGridView>(form, "grid");
                var enter = Field<Button>(form, "enter");
                var restore = Field<Button>(form, "restore");
                form.Shown += async delegate
                {
                    try
                    {
                        await WaitUntil(() => FindRow(grid, handle) != null, "Fixture missing from actual GUI list");
                        Results.Add("PASS GUI asynchronously lists the dedicated fixture window");
                        search.Text = fixtureTitle;
                        await WaitUntil(() => grid.Rows.Count == 1 && FindRow(grid, handle) != null, "GUI search did not isolate fixture");
                        Select(grid, handle);
                        Assert(enter.Enabled && !restore.Enabled, "Initial button availability is incorrect");
                        Results.Add("PASS search and row selection enable Fullscreen and keep Restore disabled initially");

                        enter.PerformClick();
                        Assert(controller.IsManaged(handle), "Actual Fullscreen button did not manage fixture");
                        await WaitUntil(() => !Field<bool>(form, "scanning"), "GUI did not finish post-entry refresh");
                        Select(grid, handle);
                        Assert(restore.Enabled, "Restore button was not enabled after full screen");
                        Assert(Convert.ToString(FindRow(grid, handle).Cells["state"].Value) == "全屏中", "GUI row did not show fullscreen state");
                        Results.Add("PASS Fullscreen button applies full screen; refreshed row shows full-screen state and Restore is enabled");

                        restore.PerformClick();
                        await WaitUntil(() => !Field<bool>(form, "scanning"), "GUI did not finish post-restore refresh");
                        Assert(!controller.IsManaged(handle), "Restore button left fixture managed");
                        Compare(before, Capture(handle));
                        Select(grid, handle);
                        Assert(!restore.Enabled, "Restore button remains enabled after restoration");
                        Results.Add("PASS Restore button returns exact original bounds, styles and placement and clears managed state");

                        enter.PerformClick();
                        Assert(controller.IsManaged(handle), "Second Fullscreen action failed");
                        await WaitUntil(() => !Field<bool>(form, "scanning"), "Second fullscreen refresh did not finish");
                        form.Close();
                        Thread.Sleep(200);
                        Assert(controller.Count == 0, "Closing actual tool did not restore managed windows");
                        Compare(before, Capture(handle));
                        Assert(!File.Exists(journal), "Closing tool left recovery record");
                        Results.Add("PASS closing actual GUI restores active fixture automatically and removes recovery record");
                        passed = true;
                    }
                    catch (Exception error)
                    {
                        Results.Add("FAIL " + error);
                        try { controller.RestoreAll(); } catch { }
                        form.Close();
                    }
                };
                Application.Run(form);
            }
        }
        catch (Exception error) { Results.Add("FAIL setup: " + error); }
        finally
        {
            if (fixture != null)
            {
                if (!fixture.HasExited)
                {
                    Extra.PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (!fixture.WaitForExit(3000)) fixture.Kill();
                    fixture.WaitForExit();
                }
                fixture.Dispose();
            }
            if (File.Exists(ready)) File.Delete(ready);
        }
        Results.Add("RESULT: " + (passed ? "PASS" : "FAIL"));
        File.WriteAllLines(Path.Combine(Root, "ui-workflow-results.txt"), Results.ToArray(), Encoding.UTF8);
        foreach (string line in Results) Console.WriteLine(line);
        return passed ? 0 : 1;
    }

    private static async Task WaitUntil(Func<bool> predicate, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception(message);
            await Task.Delay(50);
        }
    }
    private static T Field<T>(object obj, string name)
    {
        return (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    }
    private static DataGridViewRow FindRow(DataGridView grid, IntPtr handle)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            var candidate = row.Tag as WindowCandidate;
            if (candidate != null && candidate.Handle == handle) return row;
        }
        return null;
    }
    private static void Select(DataGridView grid, IntPtr handle)
    {
        DataGridViewRow row = FindRow(grid, handle);
        Assert(row != null, "Fixture row disappeared");
        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0];
    }
    private sealed class Snapshot
    {
        public NativeRect Bounds;
        public WindowPlacement Placement;
        public int Style, ExStyle;
    }
    private static Snapshot Capture(IntPtr handle)
    {
        NativeRect bounds;
        Assert(Native.GetWindowRect(handle, out bounds), "GetWindowRect failed");
        var placement = new WindowPlacement { length = Marshal.SizeOf(typeof(WindowPlacement)) };
        Assert(Native.GetWindowPlacement(handle, ref placement), "GetWindowPlacement failed");
        return new Snapshot { Bounds = bounds, Placement = placement,
            Style = Native.GetWindowLong(handle, -16), ExStyle = Native.GetWindowLong(handle, -20) };
    }
    private static void Compare(Snapshot before, Snapshot after)
    {
        Assert(Rect(before.Bounds) == Rect(after.Bounds), "Original window rectangle was not restored: " + Rect(before.Bounds) + " vs " + Rect(after.Bounds));
        Assert(before.Style == after.Style && before.ExStyle == after.ExStyle, "Window styles were not restored exactly");
        Assert(before.Placement.showCmd == after.Placement.showCmd && Rect(before.Placement.rcNormalPosition) == Rect(after.Placement.rcNormalPosition), "Original window placement was not restored");
    }
    private static string Rect(NativeRect r) { return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom; }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static class Extra
    {
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr handle, uint msg, IntPtr w, IntPtr l);
    }
}
