using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using FullscreenAssistant;

internal static class WindowIntegrationTests
{
    private static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    private static int failures;
    private static readonly List<string> report = new List<string>();

    [STAThread]
    private static int Main(string[] args)
    {
        WindowController.EnableDpiAwareness();
        if (args.Length > 0 && args[0] == "--fixture")
        {
            RunFixture(args[1], args[2]);
            return 0;
        }
        Application.EnableVisualStyles();
        if (args.Length > 0 && args[0] == "--regressions")
        {
            Run("synchronous-custom-client-offset", delegate { TestClientOffset(false); });
            Run("asynchronous-custom-client-offset", delegate { TestClientOffset(true); });
            Run("native-maximize-clears-stale-clipping", TestNativeMaximize);
            report.Add("RESULT: " + (failures == 0 ? "PASS" : "FAIL") + "; failed tests=" + failures);
            File.WriteAllLines(Path.Combine(Root, "custom-frame-regression-results.txt"), report.ToArray(), System.Text.Encoding.UTF8);
            foreach (string line in report) Console.WriteLine(line);
            return failures == 0 ? 0 : 1;
        }
        Test("normal-borderless", "normal", FullscreenMode.Borderless, false, false);
        Test("normal-crop", "normal", FullscreenMode.CropFrame, false, false);
        Test("maximized", "max", FullscreenMode.Borderless, false, false);
        Test("minimized", "min", FullscreenMode.Borderless, false, false);
        Test("repeat-preserves-first-state", "normal", FullscreenMode.Borderless, true, false);
        Test("custom-region", "region", FullscreenMode.CropFrame, false, false);
        Test("original-topmost", "topmost", FullscreenMode.Borderless, false, false);
        Test("dispose-restores", "normal", FullscreenMode.Borderless, false, true);
        Run("destroyed-handle", TestDestroyed);
        Run("journal-recovery", TestRecovery);
        Run("enumeration-two-windows", TestEnumeration);
        Run("refused-resize-rolls-back", TestRefusedResize);
        Run("crop-extra-top-40", delegate { TestTopCrop("normal", false); });
        Run("custom-frame-hits-cropped", delegate { TestTopCrop("customframe", true); });
        report.Add("RESULT: " + (failures == 0 ? "PASS" : "FAIL") + "; failed tests=" + failures);
        File.WriteAllLines(Path.Combine(Root, "integration-results.txt"), report.ToArray(), System.Text.Encoding.UTF8);
        foreach (string line in report) Console.WriteLine(line);
        return failures == 0 ? 0 : 1;
    }

    private static void RunFixture(string mode, string readyPath)
    {
        Application.EnableVisualStyles();
        using (Form form = mode == "customframe" ? new CustomFrameForm() :
            mode == "nccalc-sync" ? new ClientOffsetForm(false) :
            mode == "nccalc-async" ? new ClientOffsetForm(true) : new Form())
        {
            form.Text = "FullscreenAssistant TEST fixture " + Path.GetFileNameWithoutExtension(readyPath);
            form.StartPosition = FormStartPosition.Manual;
            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            form.Bounds = new Rectangle(screen.Left + 90, screen.Top + 100, 640, 420);
            form.BackColor = Color.FromArgb(27, 70, 104);
            form.TopMost = mode == "topmost";
            if (mode == "fixed") form.MaximumSize = new Size(800, 600);
            if (mode == "region") form.Region = new Region(new Rectangle(0, 0, 580, 360));
            form.Shown += delegate
            {
                if (mode == "max") form.WindowState = FormWindowState.Maximized;
                if (mode == "min") form.WindowState = FormWindowState.Minimized;
                var timer = new System.Windows.Forms.Timer { Interval = 350 };
                timer.Tick += delegate { timer.Stop(); timer.Dispose(); File.WriteAllText(readyPath, form.Handle.ToInt64().ToString()); };
                timer.Start();
            };
            Application.Run(form);
        }
    }

    private static void Run(string name, Action action)
    {
        try { action(); report.Add("PASS " + name); }
        catch (Exception error) { failures++; report.Add("FAIL " + name + ": " + error); }
    }

    private static void Test(string name, string fixtureMode, FullscreenMode mode, bool repeat, bool dispose)
    {
        Run(name, delegate
        {
            using (Fixture fixture = Fixture.Start(fixtureMode))
            {
                IntPtr handle = fixture.Handle;
                Snapshot before = Capture(handle);
                string journal = Journal(name);
                using (var controller = new WindowController(journal))
                {
                    Rectangle monitor = Screen.FromHandle(handle).Bounds;
                    EnterMeasured(controller, handle, monitor, mode);
                    Thread.Sleep(200);
                    Assert(controller.IsManaged(handle), "Window not tracked after Enter");
                    Assert(File.Exists(journal), "Recovery journal missing");
                    AssertVisibleFills(handle, monitor);
                    if (repeat)
                    {
                        EnterMeasured(controller, handle, monitor, FullscreenMode.CropFrame);
                        Assert(controller.Count == 1, "Repeat Enter duplicated snapshot");
                    }
                    if (dispose) controller.Dispose(); else controller.Restore(handle);
                    Thread.Sleep(250);
                    Snapshot after = Capture(handle);
                    Compare(before, after, fixtureMode != "min");
                    Assert(controller.Count == 0, "Snapshot remains after restoration");
                    Assert(!File.Exists(journal), "Journal remains after restoration");
                }
            }
        });
    }

    private static void TestDestroyed()
    {
        string journal = Journal("destroyed");
        using (var controller = new WindowController(journal))
        {
            Fixture fixture = Fixture.Start("normal");
            IntPtr handle = fixture.Handle;
            try { controller.Enter(handle, Screen.FromHandle(handle).Bounds, FullscreenMode.Borderless, 0); }
            finally { fixture.Dispose(); }
            controller.PruneClosed();
            Assert(controller.Count == 0, "Destroyed handle remains tracked");
            controller.Restore(handle);
            Assert(!File.Exists(journal), "Destroyed window journal remains");
        }
    }

    private static void TestRecovery()
    {
        string journal = Journal("recovery");
        using (Fixture fixture = Fixture.Start("normal"))
        {
            Snapshot before = Capture(fixture.Handle);
            var first = new WindowController(journal);
            try
            {
                first.Enter(fixture.Handle, Screen.FromHandle(fixture.Handle).Bounds, FullscreenMode.CropFrame, 0);
                using (var recovered = new WindowController(journal))
                {
                    Thread.Sleep(200);
                    Assert(recovered.Count == 0, "Recovered controller still tracks restored state");
                    Assert(!string.IsNullOrEmpty(recovered.LastRecoveryMessage), "Missing recovery notice");
                    Compare(before, Capture(fixture.Handle), true);
                    Assert(!File.Exists(journal), "Recovered journal remains");
                }
            }
            finally { first.Dispose(); }
        }
    }

    private static void TestEnumeration()
    {
        using (Fixture first = Fixture.Start("normal"))
        using (Fixture second = Fixture.Start("normal"))
        {
            List<WindowCandidate> list = WindowController.EnumerateWindows();
            Assert(list.Exists(x => x.Handle == first.Handle), "First fixture missing from enumeration");
            Assert(list.Exists(x => x.Handle == second.Handle), "Second fixture missing from enumeration");
        }
    }

    private static void TestRefusedResize()
    {
        using (Fixture fixture = Fixture.Start("fixed"))
        {
            Snapshot before = Capture(fixture.Handle);
            string journal = Journal("refused");
            using (var controller = new WindowController(journal))
            {
                bool refused = false;
                try { controller.Enter(fixture.Handle, Screen.FromHandle(fixture.Handle).Bounds, FullscreenMode.Borderless, 0); }
                catch (InvalidOperationException) { refused = true; }
                Assert(refused, "Size-restricted fixture was not rejected");
                Thread.Sleep(200);
                Compare(before, Capture(fixture.Handle), true);
                Assert(controller.Count == 0, "Failed Enter did not clear its state after successful rollback");
                Assert(!File.Exists(journal), "Failed Enter left a journal after successful rollback");
            }
        }
    }

    private static void TestClientOffset(bool asynchronous)
    {
        string name = asynchronous ? "nccalc-async" : "nccalc-sync";
        using (Fixture fixture = Fixture.Start(name))
        {
            Snapshot before = Capture(fixture.Handle);
            using (var controller = new WindowController(Journal(name)))
            {
                Rectangle monitor = Screen.FromHandle(fixture.Handle).Bounds;
                EnterMeasured(controller, fixture.Handle, monitor, FullscreenMode.Borderless);
                Thread.Sleep(400);
                if (asynchronous) Monitor(controller);
                Thread.Sleep(150);
                AssertClientMatches(fixture.Handle, monitor);
                AssertVisibleFills(fixture.Handle, monitor);
                controller.Restore(fixture.Handle);
                Thread.Sleep(250);
                Compare(before, Capture(fixture.Handle), true);
            }
        }
    }

    private static void TestNativeMaximize()
    {
        using (Fixture fixture = Fixture.Start("normal"))
        {
            Snapshot before = Capture(fixture.Handle);
            Native.ShowWindow(fixture.Handle, 3);
            Thread.Sleep(250);
            Snapshot nativeMaxReference = Capture(fixture.Handle);
            WindowPlacement originalPlacement = before.Placement;
            Assert(Native.SetWindowPlacement(fixture.Handle, ref originalPlacement), "Could not reset native-max reference fixture");
            Thread.Sleep(200);
            Compare(before, Capture(fixture.Handle), true);
            using (var controller = new WindowController(Journal("native-maximize")))
            {
                Rectangle monitor = Screen.FromHandle(fixture.Handle).Bounds;
                controller.Enter(fixture.Handle, monitor, FullscreenMode.CropFrame, 0);
                Native.ShowWindow(fixture.Handle, 3);
                Thread.Sleep(300);
                Monitor(controller);
                Thread.Sleep(200);
                Snapshot handedOff = Capture(fixture.Handle);
                Assert(Rect(handedOff.Bounds) == Rect(nativeMaxReference.Bounds), "Native maximize handoff differs from original native layout: " + Rect(handedOff.Bounds) + " vs " + Rect(nativeMaxReference.Bounds));
                Assert(handedOff.Style == nativeMaxReference.Style && handedOff.ExStyle == nativeMaxReference.ExStyle, "Native maximize handoff did not restore original frame styles");
                Assert(handedOff.Placement.showCmd == nativeMaxReference.Placement.showCmd, "Native maximize was not preserved");
                Assert(handedOff.Region == nativeMaxReference.Region, "Tool crop remains after handing off to native maximize");
                Assert(controller.Count == 1, "Native maximize lost original saved state");
                controller.Restore(fixture.Handle);
                Thread.Sleep(200);
                Compare(before, Capture(fixture.Handle), true);
            }
        }
    }

    private static void Monitor(WindowController controller)
    {
        var method = typeof(WindowController).GetMethod("MonitorManagedWindows");
        Assert(method != null, "MonitorManagedWindows API is not yet available");
        method.Invoke(controller, new object[0]);
        Thread.Sleep(200);
        method.Invoke(controller, new object[0]);
    }

    private static void AssertClientMatches(IntPtr handle, Rectangle monitor)
    {
        NativeRect client;
        NativePoint origin = new NativePoint();
        Assert(Native.GetClientRect(handle, out client) && Native.ClientToScreen(handle, ref origin), "Client geometry unavailable");
        Assert(origin.X == monitor.Left && origin.Y == monitor.Top && client.Width == monitor.Width && client.Height == monitor.Height,
            "Standard mode's client viewport shifted or leaves a gap: origin=" + origin.X + "," + origin.Y + " size=" + client.Width + "x" + client.Height + " monitor=" + monitor);
    }

    private static void TestTopCrop(string fixtureMode, bool verifyHitZones)
    {
        using (Fixture fixture = Fixture.Start(fixtureMode))
        {
            Snapshot before = Capture(fixture.Handle);
            string journal = Journal("top-crop-" + fixtureMode);
            using (var controller = new WindowController(journal))
            {
                Rectangle monitor = Screen.FromHandle(fixture.Handle).Bounds;
                if (verifyHitZones)
                {
                    controller.Enter(fixture.Handle, monitor, FullscreenMode.Borderless, 0);
                    Assert(Hit(fixture.Handle, monitor.Left + 1, monitor.Top + monitor.Height / 2) == 10, "Fixture did not retain its left resize hit zone");
                    Assert(Hit(fixture.Handle, monitor.Left + monitor.Width / 2, monitor.Top + 1) == 2, "Fixture did not retain its custom caption hit zone");
                }
                EnterMeasured(controller, fixture.Handle, monitor, FullscreenMode.CropFrame, 40);
                Thread.Sleep(150);
                AssertVisibleFills(fixture.Handle, monitor);
                if (verifyHitZones)
                {
                    int cx = monitor.Left + monitor.Width / 2, cy = monitor.Top + monitor.Height / 2;
                    Assert(Hit(fixture.Handle, monitor.Left, cy) == 1, "Left visible edge still resizable");
                    Assert(Hit(fixture.Handle, monitor.Right - 1, cy) == 1, "Right visible edge still resizable");
                    Assert(Hit(fixture.Handle, cx, monitor.Top) == 1, "Top visible edge still caption/resize area");
                    Assert(Hit(fixture.Handle, cx, monitor.Bottom - 1) == 1, "Bottom visible edge still resizable");
                }
                controller.Restore(fixture.Handle);
                Thread.Sleep(200);
                Compare(before, Capture(fixture.Handle), true);
            }
        }
    }

    private static long Hit(IntPtr handle, int x, int y)
    {
        IntPtr result;
        Assert(Extra.SendMessageTimeout(handle, 0x0084, IntPtr.Zero, new IntPtr((y << 16) | (x & 0xFFFF)), 2, 1000, out result) != IntPtr.Zero, "Hit test timed out");
        return result.ToInt64();
    }

    private static void AssertVisibleFills(IntPtr handle, Rectangle monitor)
    {
        NativeRect outer;
        Assert(Native.GetWindowRect(handle, out outer), "GetWindowRect failed");
        IntPtr region = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (Native.GetWindowRgn(handle, region) == 0)
            {
                // Standard full screen deliberately installs no crop region; the client viewport must still fit exactly.
                AssertClientMatches(handle, monitor);
                return;
            }
            NativeRect clip;
            Assert(Extra.GetRgnBox(region, out clip) != 0, "GetRgnBox failed");
            Assert(outer.Left + clip.Left == monitor.Left && outer.Top + clip.Top == monitor.Top &&
                outer.Left + clip.Right == monitor.Right && outer.Top + clip.Bottom == monitor.Bottom,
                "Visible region differs from monitor: outer=" + Rect(outer) + " region=" + Rect(clip) + " monitor=" + monitor);
        }
        finally { Native.DeleteObject(region); }
    }

    private static void EnterMeasured(WindowController controller, IntPtr handle, Rectangle monitor, FullscreenMode mode, int topCrop = 0)
    {
        bool stop = false;
        var samples = new List<string>();
        var thread = new Thread(delegate()
        {
            string previous = null;
            while (!stop)
            {
                NativeRect r, c;
                Native.GetWindowRect(handle, out r); Native.GetClientRect(handle, out c);
                NativePoint p = new NativePoint(); Native.ClientToScreen(handle, ref p);
                string sample = "outer=" + Rect(r) + " client=" + c.Width + "x" + c.Height + " origin=" + p.X + "," + p.Y + " style=" + Native.GetWindowLong(handle,-16).ToString("X8");
                if (sample != previous) { samples.Add(sample); previous = sample; }
                Thread.Sleep(15);
            }
        });
        thread.Start();
        try { controller.Enter(handle, monitor, mode, topCrop); }
        catch (Exception error)
        {
            stop = true; thread.Join();
            throw new Exception(error.Message + "\r\nGeometry samples:\r\n" + string.Join("\r\n", samples.ToArray()), error);
        }
        finally { stop = true; thread.Join(); }
    }

    private sealed class Snapshot
    {
        public NativeRect Bounds;
        public WindowPlacement Placement;
        public int Style, ExStyle;
        public string Region;
    }
    private static Snapshot Capture(IntPtr handle)
    {
        NativeRect bounds;
        Assert(Native.GetWindowRect(handle, out bounds), "GetWindowRect failed");
        var placement = new WindowPlacement { length = Marshal.SizeOf(typeof(WindowPlacement)) };
        Assert(Native.GetWindowPlacement(handle, ref placement), "GetWindowPlacement failed");
        return new Snapshot { Bounds = bounds, Placement = placement, Style = Native.GetWindowLong(handle, -16),
            ExStyle = Native.GetWindowLong(handle, -20), Region = RegionData(handle) };
    }
    private static void Compare(Snapshot before, Snapshot after, bool compareBounds)
    {
        Assert(before.Style == after.Style, "Style differs: " + before.Style.ToString("X8") + " vs " + after.Style.ToString("X8"));
        Assert(before.ExStyle == after.ExStyle, "Exstyle differs: " + before.ExStyle.ToString("X8") + " vs " + after.ExStyle.ToString("X8"));
        Assert(before.Placement.showCmd == after.Placement.showCmd, "showCmd differs: " + before.Placement.showCmd + " vs " + after.Placement.showCmd);
        Assert(Rect(before.Placement.rcNormalPosition) == Rect(after.Placement.rcNormalPosition), "Normal placement differs: " + Rect(before.Placement.rcNormalPosition) + " vs " + Rect(after.Placement.rcNormalPosition));
        if (compareBounds) Assert(Rect(before.Bounds) == Rect(after.Bounds), "Bounds differ: " + Rect(before.Bounds) + " vs " + Rect(after.Bounds));
        Assert(before.Region == after.Region, "Original region differs after restore");
    }
    private static string RegionData(IntPtr handle)
    {
        IntPtr region = Native.CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (Native.GetWindowRgn(handle, region) == 0) return "none";
            uint length = Native.GetRegionData(region, 0, IntPtr.Zero);
            IntPtr data = Marshal.AllocHGlobal((int)length);
            try
            {
                Assert(Native.GetRegionData(region, length, data) != 0, "GetRegionData failed");
                byte[] bytes = new byte[length]; Marshal.Copy(data, bytes, 0, (int)length);
                return Convert.ToBase64String(bytes);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        finally { Native.DeleteObject(region); }
    }
    private static string Rect(NativeRect r) { return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom; }
    private static void Assert(bool success, string message) { if (!success) throw new Exception(message); }
    private static string Journal(string name)
    {
        string path = Path.Combine(Root, name + ".journal.xml");
        if (File.Exists(path)) throw new Exception("Stale journal exists; inspect before rerun: " + path);
        return path;
    }

    private sealed class Fixture : IDisposable
    {
        public IntPtr Handle;
        private Process process;
        private string readyPath;
        public static Fixture Start(string mode)
        {
            string ready = Path.Combine(Root, "ready-" + Guid.NewGuid().ToString("N") + ".txt");
            var start = new ProcessStartInfo(Application.ExecutablePath, "--fixture " + mode + " \"" + ready + "\"");
            start.UseShellExecute = false;
            var fixture = new Fixture { process = Process.Start(start), readyPath = ready };
            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (!File.Exists(ready) && DateTime.UtcNow < deadline)
            {
                if (fixture.process.HasExited) throw new Exception("Fixture exited early: " + fixture.process.ExitCode);
                Thread.Sleep(50);
            }
            if (!File.Exists(ready)) { fixture.Dispose(); throw new Exception("Fixture ready timed out"); }
            fixture.Handle = new IntPtr(long.Parse(File.ReadAllText(ready)));
            return fixture;
        }
        public void Dispose()
        {
            if (process != null)
            {
                if (!process.HasExited)
                {
                    Extra.PostMessage(Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    if (!process.WaitForExit(3000)) process.Kill();
                    process.WaitForExit();
                }
                process.Dispose(); process = null;
            }
            if (readyPath != null && File.Exists(readyPath)) File.Delete(readyPath);
        }
    }
    private sealed class CustomFrameForm : Form
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084)
            {
                int x = (short)(message.LParam.ToInt64() & 0xFFFF);
                int y = (short)((message.LParam.ToInt64() >> 16) & 0xFFFF);
                NativeRect bounds;
                Native.GetWindowRect(Handle, out bounds);
                int localX = x - bounds.Left, localY = y - bounds.Top;
                int hit = 1;
                if (localX < 8) hit = 10;
                else if (localX >= bounds.Width - 8) hit = 11;
                else if (localY < 30) hit = 2;
                else if (localY >= bounds.Height - 8) hit = 15;
                message.Result = new IntPtr(hit);
                return;
            }
            base.WndProc(ref message);
        }
    }
    private sealed class ClientOffsetForm : Form
    {
        private readonly bool asynchronous;
        private int topOffset = 11;
        private bool layoutChangeScheduled;
        private System.Windows.Forms.Timer delayedLayout;
        public ClientOffsetForm(bool asynchronous) { this.asynchronous = asynchronous; }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            bool borderless = IsHandleCreated && (Native.GetWindowLong(Handle, -16) & 0x00C00000) == 0;
            if (message.Msg == 0x0083 && borderless && message.LParam != IntPtr.Zero)
            {
                // The first RECT in either RECT or NCCALCSIZE_PARAMS is the proposed client rectangle.
                int top = Marshal.ReadInt32(message.LParam, 4);
                Marshal.WriteInt32(message.LParam, 4, top + topOffset);
                message.Result = IntPtr.Zero;
            }
            if (message.Msg == 0x007D && asynchronous)
            {
                if (!borderless)
                {
                    topOffset = 11;
                    layoutChangeScheduled = false;
                    if (delayedLayout != null) { delayedLayout.Stop(); delayedLayout.Dispose(); delayedLayout = null; }
                }
                else if (!layoutChangeScheduled)
                {
                    layoutChangeScheduled = true;
                    delayedLayout = new System.Windows.Forms.Timer { Interval = 180 };
                    delayedLayout.Tick += delegate
                    {
                        delayedLayout.Stop(); delayedLayout.Dispose(); delayedLayout = null;
                        topOffset = 0;
                        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
                    };
                    delayedLayout.Start();
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && delayedLayout != null) { delayedLayout.Stop(); delayedLayout.Dispose(); delayedLayout = null; }
            base.Dispose(disposing);
        }
    }
    private static class Extra
    {
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr handle, uint message, IntPtr w, IntPtr l);
        [DllImport("gdi32.dll")] internal static extern int GetRgnBox(IntPtr region, out NativeRect box);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    }
}
