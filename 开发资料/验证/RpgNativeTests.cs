using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using FullscreenAssistant;

class RpgNativeTests
{
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint flags);
    static void PrimeForegroundWithOwnedTestWindow()
    {
        Application.EnableVisualStyles();
        NativePoint original; GetCursorPos(out original);
        using(var form = new Form { Text="全屏助手验证 · 仅测试窗口", Size=new Size(380,150), StartPosition=FormStartPosition.CenterScreen, TopMost=true })
        using(var button = new Button { Text="验证游戏原生全屏", Dock=DockStyle.Fill })
        {
            bool clicked=false; button.Click += delegate { clicked=true; }; form.Controls.Add(button);
            form.Show(); Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
            Point center=button.PointToScreen(new Point(button.Width/2,button.Height/2));
            SetCursorPos(center.X,center.Y);
            NativePoint point=new NativePoint { X=center.X,Y=center.Y };
            if(GetAncestor(WindowFromPoint(point),2)!=form.Handle) throw new Exception("Own test button is not under pointer; no mouse input was sent");
            var down=new Native.Input{type=0};down.data.mouse.flags=2;
            var up=down;up.data.mouse.flags=4;
            if(Native.SendInput(2,new[]{down,up},Marshal.SizeOf(typeof(Native.Input)))!=2) throw new Exception("Could not click owned test button");
            DateTime limit=DateTime.UtcNow.AddSeconds(2);
            while(!clicked && DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(20);}
            Assert(clicked,"native input test is initiated from an owned foreground window");
            form.Close();
        }
        SetCursorPos(original.X,original.Y);
    }
    static readonly List<string> log = new List<string>();
    static IntPtr game;
    static string folder;
    static IntPtr FindTarget(string path)
    {
        foreach(var window in WindowController.EnumerateWindows())
        {
            if(window.EngineName==null) continue;
            uint pid; Native.GetWindowThreadProcessId(window.Handle,out pid);
            IntPtr process=Native.OpenProcess(0x1000,false,pid);
            if(process==IntPtr.Zero) continue;
            try
            {
                var text=new System.Text.StringBuilder(4096);int size=text.Capacity;
                if(Native.QueryFullProcessImageName(process,0,text,ref size) &&
                    string.Equals(Path.GetFullPath(text.ToString()),path,StringComparison.OrdinalIgnoreCase)) return window.Handle;
            }
            finally { Native.CloseHandle(process); }
        }
        return IntPtr.Zero;
    }
    static void Assert(bool condition, string description)
    { if (!condition) throw new Exception(description); log.Add("PASS " + description); }
    static NativeRect Bounds()
    { NativeRect rect; if (!Native.GetWindowRect(game, out rect)) throw new Exception("No game rectangle"); return rect; }
    static bool Same(NativeRect a, NativeRect b)
    { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }
    static int Hit(int x, int y)
    {
        IntPtr value; Native.SendMessageTimeout(game, 0x84, IntPtr.Zero, new IntPtr((y << 16) | (x & 65535)), 2, 500, out value);
        return value.ToInt32();
    }
    static void Screenshot()
    {
        if (Native.GetForegroundWindow() != game) return;
        Rectangle screen = Screen.FromHandle(game).Bounds;
        using (var bitmap = new Bitmap(screen.Width, screen.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        { graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size); bitmap.Save(Path.Combine(folder,"rpg-native-fullscreen.png"), ImageFormat.Png); }
    }
    [STAThread] static int Main(string[] args)
    {
        WindowController.EnableDpiAwareness();
        folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
        string path = Path.GetFullPath(args[0]); bool own = true;
        bool initiallyFullscreen = false, didTouchGame = false;
        Process started = null;
        NativeRect initial = new NativeRect(); int initialStyle = 0;
        try
        {
            game = FindTarget(path); own = game == IntPtr.Zero;
            if (game == IntPtr.Zero)
            {
                started = Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) });
                DateTime limit = DateTime.UtcNow.AddSeconds(25);
                while (game == IntPtr.Zero && DateTime.UtcNow < limit)
                {
                    game = FindTarget(path);
                    Thread.Sleep(200);
                }
            }
            Assert(game != IntPtr.Zero, "game window detected");
            Thread.Sleep(10000);
            Assert(WindowController.DetectGameEngine(game) == "RPG Maker MZ / NW.js", "RPG Maker MZ engine identified from runtime and core files");
            Assert(Marshal.SizeOf(typeof(Native.Input)) == 40, "SendInput structure is correct on x64");
            initiallyFullscreen = RpgMakerAdapter.IsFullscreen(game); didTouchGame = true;
            if (initiallyFullscreen) RpgMakerAdapter.SwitchFullscreen(game, false);
            initial = Bounds(); initialStyle = Native.GetWindowLong(game, -16);
            log.Add("ORIGINAL bounds="+initial.Left+","+initial.Top+","+initial.Width+","+initial.Height+" style="+initialStyle.ToString("X8"));
            PrimeForegroundWithOwnedTestWindow();
            string journal = Path.Combine(folder,"native-test-state.xml");
            using (var controller = new WindowController(journal))
            {
                controller.Enter(game, Screen.FromHandle(game).Bounds, FullscreenMode.Automatic, 0);
                Assert(RpgMakerAdapter.IsFullscreen(game), "Automatic uses actual game fullscreen");
                Assert(controller.ManagedStatus(game) == "游戏原生全屏", "UI state identifies native fullscreen");
                NativeRect full = Bounds();
                log.Add("FULLSCREEN bounds=" + full.Left + "," + full.Top + "," + full.Width + "," + full.Height + " style=" + Native.GetWindowLong(game,-16).ToString("X8"));
                int cx = full.Left + full.Width/2, cy = full.Top + full.Height/2;
                Assert(Hit(full.Left,cy) == 1 && Hit(full.Right-1,cy) == 1 && Hit(cx,full.Top) == 1 && Hit(cx,full.Bottom-1) == 1,
                    "all visible screen edges are actual game content, not resize zones");
                Screenshot();
                Native.SetWindowPos(game,IntPtr.Zero,full.Left,full.Top,1200,800,0x0014);
                Thread.Sleep(100); controller.MonitorManagedWindows(); Thread.Sleep(100);
                Assert(RpgMakerAdapter.IsFullscreen(game),"late engine viewport resize is repaired without changing native fullscreen state");
                controller.Enter(game, Screen.FromHandle(game).Bounds, FullscreenMode.Automatic, 0);
                Assert(RpgMakerAdapter.IsFullscreen(game), "repeated Fullscreen does not toggle back to windowed");
                controller.Restore(game); Thread.Sleep(150);
                NativeRect restored=Bounds();
                log.Add("RESTORED bounds="+restored.Left+","+restored.Top+","+restored.Width+","+restored.Height+" style="+Native.GetWindowLong(game,-16).ToString("X8")+" full="+RpgMakerAdapter.IsFullscreen(game));
                Assert(!RpgMakerAdapter.IsFullscreen(game) && Same(initial,Bounds()) && Native.GetWindowLong(game,-16) == initialStyle,
                    "Restore exits engine fullscreen and returns exact original bounds and styles");
                controller.Enter(game, Screen.FromHandle(game).Bounds, FullscreenMode.GameNative, 0);
                Assert(RpgMakerAdapter.IsFullscreen(game), "explicit GameNative enters fullscreen");
                RpgMakerAdapter.SwitchFullscreen(game,false); controller.MonitorManagedWindows();
                Assert(!RpgMakerAdapter.IsFullscreen(game),"user's own F4 exit is respected by monitoring");
                controller.Restore(game);
                Assert(!RpgMakerAdapter.IsFullscreen(game) && Same(initial,Bounds()),"Restore after user exits fullscreen does not toggle fullscreen back on");
                controller.Enter(game, Screen.FromHandle(game).Bounds, FullscreenMode.Automatic, 0);
            }
            Assert(!RpgMakerAdapter.IsFullscreen(game) && Same(initial,Bounds()), "closing controller exits native fullscreen and restores original window");

            using (var first = new WindowController(journal))
            {
                first.Enter(game,Screen.FromHandle(game).Bounds,FullscreenMode.Automatic,0);
                using (var recovery = new WindowController(journal))
                    Assert(!RpgMakerAdapter.IsFullscreen(game) && Same(initial,Bounds()), "journal recovery restores native fullscreen session");
            }
            RpgMakerAdapter.SwitchFullscreen(game,true);
            using (var controller = new WindowController(journal))
            {
                controller.Enter(game,Screen.FromHandle(game).Bounds,FullscreenMode.Automatic,0);
                controller.Restore(game);
                Assert(RpgMakerAdapter.IsFullscreen(game), "a game originally fullscreen remains fullscreen after Restore");
            }
            RpgMakerAdapter.SwitchFullscreen(game,false);
            Assert(Same(initial,Bounds()), "all tests leave original game geometry intact");
            log.Add("RESULT: PASS"); return 0;
        }
        catch (Exception error) { log.Add("FAIL " + error); return 1; }
        finally
        {
            if (game != IntPtr.Zero && Native.IsWindow(game))
            {
                try
                {
                    if (didTouchGame && RpgMakerAdapter.IsFullscreen(game) != initiallyFullscreen)
                        RpgMakerAdapter.SwitchFullscreen(game,initiallyFullscreen);
                }
                catch (Exception ex) { log.Add("Cleanup: " + ex.Message); }
                if (own) PostMessage(game,0x10,IntPtr.Zero,IntPtr.Zero);
            }
            if (started != null) started.Dispose();
            File.WriteAllLines(Path.Combine(folder,"rpg-native-results.txt"),log.ToArray());
            foreach(string line in log) Console.WriteLine(line);
        }
    }
}
