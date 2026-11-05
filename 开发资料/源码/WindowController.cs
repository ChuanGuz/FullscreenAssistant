using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace FullscreenAssistant
{
    public enum FullscreenMode { Borderless, CropFrame, Automatic, GameNative }

    public class WindowCandidate
    {
        public IntPtr Handle;
        public int ProcessId;
        public string Title;
        public string ProcessName;
        public string ClassName;
        public string EngineName;
        public int Width, Height;
        public bool Minimized;
    }

    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X, Y; }
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }
    [Serializable, StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public int length, flags, showCmd;
        public NativePoint ptMinPosition, ptMaxPosition;
        public NativeRect rcNormalPosition;
    }

    public class SavedWindowState
    {
        public long Handle, Marker, ProcessStartUtcTicks;
        public int ProcessId, Style, ExStyle;
        public string Title;
        public WindowPlacement Placement;
        public NativeRect Bounds;
        public byte[] RegionData;
        public int TargetX, TargetY, TargetWidth, TargetHeight, TopCrop;
        public FullscreenMode Mode;
        public bool HasAppliedLayout, NativeLayout;
        public bool EngineManaged, EngineWasFullscreen, EngineFullscreenObserved;
        public string EngineName;
        public NativeRect AppliedBounds;
        public NativePoint AppliedClientOrigin;
        public int AppliedClientWidth, AppliedClientHeight;
        [XmlIgnore] public DateTime LayoutChangeSince;
        [XmlIgnore] public int ReflowAttempts;
    }

    public sealed class WindowController : IDisposable
    {
        private const string MarkerName = "FullscreenAssistant.SavedState.v1";
        private readonly string journalPath;
        private readonly List<SavedWindowState> states = new List<SavedWindowState>();
        private bool monitoring;
        public int Count { get { return states.Count; } }
        public string LastRecoveryMessage { get; private set; }
        public string LastResult { get; private set; }

        public WindowController(string path)
        {
            journalPath = path;
            if (!File.Exists(path)) return;
            try
            {
                using (var stream = File.OpenRead(path))
                    states.AddRange((List<SavedWindowState>)new XmlSerializer(typeof(List<SavedWindowState>)).Deserialize(stream));
                int count = states.Count;
                string errors = RestoreAll();
                LastRecoveryMessage = errors.Length == 0 ? "已恢复上次未正常退出前管理的窗口。" : errors;
                if (count == 0) LastRecoveryMessage = null;
            }
            catch (Exception ex) { LastRecoveryMessage = "上次的恢复记录读取失败：" + ex.Message; }
        }

        public static void EnableDpiAwareness()
        {
            try { if (Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            Native.SetProcessDPIAware();
        }

        public static List<WindowCandidate> EnumerateWindows()
        {
            var result = new List<WindowCandidate>();
            int ownPid = Process.GetCurrentProcess().Id;
            Native.EnumWindows(delegate(IntPtr handle, IntPtr param)
            {
                if (!Native.IsWindowVisible(handle)) return true;
                uint pid; Native.GetWindowThreadProcessId(handle, out pid);
                if (pid == ownPid || pid == 0) return true;
                int length = Native.GetWindowTextLength(handle);
                if (length == 0) return true;
                int ex = Native.GetWindowLong(handle, -20);
                if ((ex & 0x80) != 0 || (ex & 0x08000000) != 0) return true;
                int cloaked;
                try { if (Native.DwmGetWindowAttribute(handle, 14, out cloaked, 4) == 0 && cloaked != 0) return true; }
                catch (DllNotFoundException) { }
                string cls = ClassName(handle);
                if (cls == "Progman" || cls == "WorkerW" || cls.StartsWith("Shell_")) return true;
                NativeRect rect;
                if (!Native.GetWindowRect(handle, out rect)) return true;
                bool iconic = Native.IsIconic(handle);
                if (!iconic && (rect.Width < 100 || rect.Height < 70)) return true;
                var text = new StringBuilder(Math.Min(length + 1, 2048));
                Native.GetWindowText(handle, text, text.Capacity);
                string title = text.ToString().Trim();
                if (title.Length == 0) return true;
                string processName = GetProcessName(pid);
                result.Add(new WindowCandidate { Handle = handle, ProcessId = (int)pid, Title = title,
                    ProcessName = processName, ClassName = cls, Width = rect.Width, Height = rect.Height, Minimized = iconic,
                    EngineName = RpgMakerAdapter.Detect(handle) });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public bool IsManaged(IntPtr handle) { return Find(handle) != null; }
        public string ManagedStatus(IntPtr handle)
        {
            var state = Find(handle);
            if (state != null && state.EngineManaged) return RpgMakerAdapter.IsFullscreen(handle) ? "游戏原生全屏" : "原生模式·窗口";
            return state == null ? "可选择" : state.NativeLayout ? "应用自带布局" : "全屏中";
        }
        public static string DetectGameEngine(IntPtr handle) { return RpgMakerAdapter.Detect(handle); }
        private SavedWindowState Find(IntPtr handle) { return states.Find(s => s.Handle == handle.ToInt64()); }

        private static string ClassName(IntPtr handle)
        {
            var buffer = new StringBuilder(256); Native.GetClassName(handle, buffer, buffer.Capacity); return buffer.ToString();
        }
        private static string GetProcessName(uint pid)
        {
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process != IntPtr.Zero)
            {
                try
                {
                    var path = new StringBuilder(2048); int size = path.Capacity;
                    if (Native.QueryFullProcessImageName(process, 0, path, ref size)) return Path.GetFileNameWithoutExtension(path.ToString());
                }
                finally { Native.CloseHandle(process); }
            }
            return "PID " + pid;
        }

        private static bool Matches(SavedWindowState state)
        {
            IntPtr handle = new IntPtr(state.Handle);
            if (!Native.IsWindow(handle) || Native.GetProp(handle, MarkerName).ToInt64() != state.Marker) return false;
            uint pid; Native.GetWindowThreadProcessId(handle, out pid);
            if (pid != state.ProcessId) return false;
            if (state.ProcessStartUtcTicks != 0)
            {
                try { using (var p = Process.GetProcessById((int)pid)) if (p.StartTime.ToUniversalTime().Ticks != state.ProcessStartUtcTicks) return false; }
                catch { return false; }
            }
            return true;
        }

        private SavedWindowState Capture(IntPtr handle)
        {
            if (!Native.IsWindow(handle)) throw new InvalidOperationException("这个窗口已经关闭，请刷新列表。");
            uint pid; Native.GetWindowThreadProcessId(handle, out pid);
            var placement = new WindowPlacement { length = Marshal.SizeOf(typeof(WindowPlacement)) };
            NativeRect rect = new NativeRect();
            if (!Native.GetWindowPlacement(handle, ref placement) || !Native.GetWindowRect(handle, out rect)) ThrowNative();
            long start = 0;
            try { using (var p = Process.GetProcessById((int)pid)) start = p.StartTime.ToUniversalTime().Ticks; } catch { }
            var title = new StringBuilder(2048); Native.GetWindowText(handle, title, title.Capacity);
            return new SavedWindowState { Handle = handle.ToInt64(), ProcessId = (int)pid, ProcessStartUtcTicks = start,
                Title = title.ToString(), Style = Native.GetWindowLong(handle, -16), ExStyle = Native.GetWindowLong(handle, -20),
                Placement = placement, Bounds = rect, RegionData = CaptureRegion(handle), Marker = (long)(uint)Guid.NewGuid().GetHashCode() + 1 };
        }

        public void Enter(IntPtr handle, Rectangle monitor, FullscreenMode mode, int extraTopCrop)
        {
            if (monitor.Width < 100 || monitor.Height < 100) throw new ArgumentException("显示器尺寸无效。");
            if (extraTopCrop < 0 || extraTopCrop > 200) throw new ArgumentException("顶部裁切应在 0 到 200 像素之间。");
            EnsureResponsive(handle);
            string engine = mode == FullscreenMode.Automatic || mode == FullscreenMode.GameNative ? RpgMakerAdapter.Detect(handle) : null;
            if (mode == FullscreenMode.GameNative && engine == null)
                throw new InvalidOperationException("未识别到支持的 RPG Maker / NW.js 游戏。请改用智能、标准全屏或兼容裁边。");
            var previous = Find(handle);
            if (!string.IsNullOrEmpty(engine))
            {
                if (previous != null && !previous.EngineManaged) Restore(handle);
                EnterNativeGame(handle, monitor, engine);
                return;
            }
            if (previous != null && previous.EngineManaged) Restore(handle);
            if (mode == FullscreenMode.Automatic) mode = FullscreenMode.Borderless;
            var state = Find(handle);
            if (state != null && !Matches(state)) { states.Remove(state); SaveJournal(); state = null; }
            if (state == null)
            {
                if (Native.GetProp(handle, MarkerName) != IntPtr.Zero) throw new InvalidOperationException("这个窗口已有恢复标记，请先使用原工具恢复。");
                state = Capture(handle);
                if (!Native.SetProp(handle, MarkerName, new IntPtr(state.Marker))) ThrowNative();
                states.Add(state);
                try { SaveJournal(); }
                catch { states.Remove(state); Native.RemoveProp(handle, MarkerName); throw; }
            }

            try
            {
                Native.ShowWindow(handle, 9);
                if (Native.SetWindowRgn(handle, IntPtr.Zero, true) == 0) ThrowNative();
                // Minimized/maximized bits are managed by ShowWindow / SetWindowPlacement, not copied into a live resize.
                int style = state.Style & ~0x21000000 & ~0x00CF0000;
                int exStyle = state.ExStyle & ~0x00020301;
                SetStyle(handle, -16, style);
                SetStyle(handle, -20, exStyle);
                Move(handle, monitor.Left, monitor.Top, monitor.Width, monitor.Height, true);
                Thread.Sleep(60);


                // Fit the actual client area rather than assuming that removing a style removes every custom frame.
                NativeRect outer = new NativeRect(), client = new NativeRect();
                NativePoint origin = new NativePoint();
                if (!Native.GetWindowRect(handle, out outer) || !Native.GetClientRect(handle, out client) ||
                    !Native.ClientToScreen(handle, ref origin)) ThrowNative();
                int left = origin.X - outer.Left;
                int top = origin.Y - outer.Top;
                int right = outer.Width - client.Width - left;
                int bottom = outer.Height - client.Height - top;
                if (left < 0 || top < 0 || right < 0 || bottom < 0 || left + right > 400 || top + bottom > 500)
                    throw new InvalidOperationException("这个窗口使用特殊的边框布局，暂时无法可靠调整。");

                // First fit without overscan. A custom title/menu bar is content, not an edge to crop blindly.
                Move(handle, monitor.Left - left, monitor.Top - top,
                    monitor.Width + left + right, monitor.Height + top + bottom, false);
                Thread.Sleep(70);
                int leftCrop = 0, rightCrop = 0, topEdgeCrop = 0, bottomCrop = 0;
                if (mode == FullscreenMode.CropFrame)
                {
                    leftCrop = ResizeInset(handle, monitor.Left, monitor.Top + monitor.Height / 2, 1, 0);
                    rightCrop = ResizeInset(handle, monitor.Right - 1, monitor.Top + monitor.Height / 2, -1, 0);
                    topEdgeCrop = ResizeInset(handle, monitor.Left + monitor.Width / 2, monitor.Top, 0, 1);
                    bottomCrop = ResizeInset(handle, monitor.Left + monitor.Width / 2, monitor.Bottom - 1, 0, -1);
                }
                int trimTop = mode == FullscreenMode.CropFrame ? extraTopCrop : 0;
                Move(handle, monitor.Left - left - leftCrop, monitor.Top - top - topEdgeCrop - trimTop,
                    monitor.Width + left + right + leftCrop + rightCrop,
                    monitor.Height + top + bottom + topEdgeCrop + bottomCrop + trimTop, false, mode == FullscreenMode.CropFrame);
                Thread.Sleep(70);
                // Some applications finish WM_NCCALCSIZE asynchronously after the style change.
                // Converge against the intended viewport, not against an earlier one-time inset measurement.
                for (int pass = 0; pass < 3; pass++)
                {
                    origin = new NativePoint();
                    if (!Native.GetWindowRect(handle, out outer) || !Native.GetClientRect(handle, out client) ||
                        !Native.ClientToScreen(handle, ref origin)) ThrowNative();
                    int deltaX = monitor.Left - leftCrop - origin.X;
                    int deltaY = monitor.Top - topEdgeCrop - trimTop - origin.Y;
                    int deltaWidth = monitor.Width + leftCrop + rightCrop - client.Width;
                    int deltaHeight = monitor.Height + topEdgeCrop + bottomCrop + trimTop - client.Height;
                    if (Math.Abs(deltaX) <= 1 && Math.Abs(deltaY) <= 1 && Math.Abs(deltaWidth) <= 1 && Math.Abs(deltaHeight) <= 1) break;
                    Move(handle, outer.Left + deltaX, outer.Top + deltaY, outer.Width + deltaWidth, outer.Height + deltaHeight,
                        false, mode == FullscreenMode.CropFrame);
                    Thread.Sleep(80);
                }
                if (!Native.GetWindowRect(handle, out outer)) ThrowNative();
                origin = new NativePoint();
                if (!Native.GetClientRect(handle, out client) || !Native.ClientToScreen(handle, ref origin)) ThrowNative();
                if (origin.X > monitor.Left + 2 || origin.Y > monitor.Top + 2 ||
                    origin.X + client.Width < monitor.Right - 2 || origin.Y + client.Height < monitor.Bottom - 2)
                    throw new InvalidOperationException("这个窗口限制了可调整的尺寸，无法铺满显示器。已尝试恢复原状态；可改用游戏内的全屏选项。");

                if (mode == FullscreenMode.CropFrame)
                {
                    IntPtr region = Native.CreateRectRgn(monitor.Left - outer.Left, monitor.Top - outer.Top,
                        monitor.Right - outer.Left, monitor.Bottom - outer.Top);
                    if (region == IntPtr.Zero) ThrowNative();
                    if (Native.SetWindowRgn(handle, region, true) == 0) { Native.DeleteObject(region); ThrowNative(); }
                }
                state.TargetX = monitor.X; state.TargetY = monitor.Y;
                state.TargetWidth = monitor.Width; state.TargetHeight = monitor.Height;
                state.Mode = mode; state.TopCrop = trimTop; state.NativeLayout = false; state.HasAppliedLayout = true;
                if (!monitoring) state.ReflowAttempts = 0;
                state.AppliedBounds = outer; state.AppliedClientOrigin = origin;
                state.AppliedClientWidth = client.Width; state.AppliedClientHeight = client.Height;
                state.LayoutChangeSince = DateTime.MinValue;
                SaveJournal();
                if (!monitoring) Native.SetForegroundWindow(handle);
                LastResult = mode == FullscreenMode.CropFrame
                    ? "已进入兼容全屏，只裁去检测到的缩放边缘；应用标题和菜单会保留。"
                    : "已进入全屏。如果仍有边框，请切换到“兼容裁边”后再点全屏。";
            }
            catch (Exception ex)
            {
                string rollback = "";
                try { Restore(handle); } catch (Exception restoreError) { rollback = "\r\n恢复未完成，请再点恢复窗口：" + restoreError.Message; }
                throw new InvalidOperationException(ex.Message + rollback, ex);
            }
        }

        public string MonitorManagedWindows()
        {
            string message = "";
            foreach (var state in new List<SavedWindowState>(states))
            {
                if (!Matches(state)) continue;
                IntPtr handle = new IntPtr(state.Handle);
                if (Native.IsIconic(handle)) continue;
                if (state.EngineManaged)
                {
                    // A late boot-time resize must not shrink an engine fullscreen viewport.
                    // A real F4/Escape exit restores the normal frame, so it is left under user control.
                    if (state.EngineFullscreenObserved && (state.Style & 0x00C40000) != 0 &&
                        RpgMakerAdapter.HasFullscreenStyle(handle) && !RpgMakerAdapter.IsFullscreen(handle))
                    {
                        try { RpgMakerAdapter.FitNativeViewport(handle, System.Windows.Forms.Screen.FromHandle(handle).Bounds); }
                        catch (Exception ex) { message = "游戏原生全屏尺寸更新未完成：" + ex.Message; }
                    }
                    continue;
                }
                if (!state.HasAppliedLayout || state.NativeLayout) continue;
                try
                {
                    if (Native.IsZoomed(handle))
                    {
                        UseNativeLayout(state, true);
                        message = "已切换到应用自身的最大化布局，并移除工具裁边；仍可恢复到最初状态。";
                        continue;
                    }
                    NativeRect outer = new NativeRect(), client = new NativeRect();
                    NativePoint origin = new NativePoint();
                    if (!Native.GetWindowRect(handle, out outer) || !Native.GetClientRect(handle, out client) ||
                        !Native.ClientToScreen(handle, ref origin)) continue;
                    bool viewportAligned = state.Mode != FullscreenMode.Borderless ||
                        (Math.Abs(origin.X - state.TargetX) <= 1 && Math.Abs(origin.Y - state.TargetY) <= 1 &&
                        Math.Abs(client.Width - state.TargetWidth) <= 1 && Math.Abs(client.Height - state.TargetHeight) <= 1);
                    if (viewportAligned && SameRect(outer, state.AppliedBounds) && origin.X == state.AppliedClientOrigin.X &&
                        origin.Y == state.AppliedClientOrigin.Y && client.Width == state.AppliedClientWidth &&
                        client.Height == state.AppliedClientHeight)
                    { state.LayoutChangeSince = DateTime.MinValue; continue; }
                    if (state.LayoutChangeSince == DateTime.MinValue) { state.LayoutChangeSince = DateTime.UtcNow; continue; }
                    if ((DateTime.UtcNow - state.LayoutChangeSince).TotalMilliseconds < 150) continue;
                    EnsureResponsive(handle);
                    if (state.ReflowAttempts >= 3)
                    {
                        UseNativeLayout(state, false);
                        message = "应用持续改变窗口布局，已停止工具裁边；可使用应用自身布局或恢复窗口。";
                        continue;
                    }
                    state.ReflowAttempts++;
                    monitoring = true;
                    try { Enter(handle, new Rectangle(state.TargetX, state.TargetY, state.TargetWidth, state.TargetHeight), state.Mode, state.TopCrop); }
                    finally { monitoring = false; }
                }
                catch (Exception ex) { message = "窗口布局更新未完成：" + ex.Message; }
            }
            return message;
        }

        private void UseNativeLayout(SavedWindowState state, bool maximize)
        {
            IntPtr handle = new IntPtr(state.Handle);
            EnsureResponsive(handle);
            WindowPlacement current = new WindowPlacement { length = Marshal.SizeOf(typeof(WindowPlacement)) };
            if (!Native.GetWindowPlacement(handle, ref current)) ThrowNative();
            Native.ShowWindow(handle, 9);
            SetStyle(handle, -16, state.Style & ~0x21000000);
            SetStyle(handle, -20, state.ExStyle);
            if (Native.SetWindowRgn(handle, IntPtr.Zero, true) == 0) ThrowNative();
            if (!Native.SetWindowPos(handle, new IntPtr((state.ExStyle & 8) != 0 ? -1 : -2), 0, 0, 0, 0, 0x0033)) ThrowNative();
            if (maximize) Native.ShowWindow(handle, 3);
            else { current.length = Marshal.SizeOf(typeof(WindowPlacement)); if (!Native.SetWindowPlacement(handle, ref current)) ThrowNative(); }
            state.NativeLayout = true;
            SaveJournal();
        }

        private static bool SameRect(NativeRect a, NativeRect b)
        { return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom; }

        private static int ResizeInset(IntPtr handle, int x, int y, int dx, int dy)
        {
            for (int distance = 0; distance < 48; distance++)
            {
                int px = x + dx * distance, py = y + dy * distance;
                IntPtr result;
                if (Native.SendMessageTimeout(handle, 0x84, IntPtr.Zero,
                    new IntPtr(unchecked((py << 16) | (px & 65535))), 2, 40, out result) == IntPtr.Zero) return 0;
                int hit = unchecked((int)result.ToInt64());
                // The resize band may overlap a client-drawn title/menu bar. Preserve that bar unless the user explicitly crops it.
                if (dx == 0 && dy > 0 && hit == 2) return 0;
                if (hit != 4 && hit != 18 && (hit < 10 || hit > 17)) return distance;
            }
            return 48;
        }

        private void EnterNativeGame(IntPtr handle, Rectangle monitor, string engine)
        {
            SavedWindowState state = Find(handle);
            if (state != null && !Matches(state)) { states.Remove(state); SaveJournal(); state = null; }
            if (state == null)
            {
                if (Native.GetProp(handle, MarkerName) != IntPtr.Zero)
                    throw new InvalidOperationException("这个窗口已有恢复标记，请先使用原工具恢复。");
                state = Capture(handle);
                state.EngineManaged = true; state.EngineName = engine;
                state.EngineWasFullscreen = RpgMakerAdapter.IsFullscreen(handle);
                state.EngineFullscreenObserved = state.EngineWasFullscreen;
                if (!Native.SetProp(handle, MarkerName, new IntPtr(state.Marker))) ThrowNative();
                states.Add(state);
                try { SaveJournal(); }
                catch { states.Remove(state); Native.RemoveProp(handle, MarkerName); throw; }
            }
            try
            {
                if (Native.IsIconic(handle)) { Native.ShowWindow(handle, 9); Thread.Sleep(100); }
                if (!RpgMakerAdapter.IsFullscreen(handle))
                {
                    Rectangle currentMonitor = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
                    if (currentMonitor != monitor)
                    {
                        NativeRect bounds;
                        if (!Native.GetWindowRect(handle, out bounds)) ThrowNative();
                        int x = monitor.Left + Math.Max(0, (monitor.Width - bounds.Width) / 2);
                        int y = monitor.Top + Math.Max(0, (monitor.Height - bounds.Height) / 2);
                        if (!Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015)) ThrowNative();
                    }
                    RpgMakerAdapter.SwitchFullscreen(handle, true);
                }
                state.EngineFullscreenObserved = true;
                LastResult = "已识别 " + engine + "，已使用游戏原生全屏；没有裁切游戏内容。";
                SaveJournal();
            }
            catch (Exception error)
            {
                string rollback = "";
                try { Restore(handle); } catch (Exception restoreError) { rollback = "\r\n恢复记录已保留：" + restoreError.Message; }
                throw new InvalidOperationException(error.Message + rollback, error);
            }
        }

        public void Restore(IntPtr handle)
        {
            SavedWindowState state = Find(handle);
            if (state == null) return;
            if (Matches(state))
            {
                EnsureResponsive(handle);
                if (state.EngineManaged)
                {
                    if (Native.IsIconic(handle)) { Native.ShowWindow(handle, 9); Thread.Sleep(100); }
                    bool nativeStyleConfirmed = state.EngineFullscreenObserved && (state.Style & 0x00C40000) != 0 &&
                        RpgMakerAdapter.HasFullscreenStyle(handle);
                    bool currentlyFullscreen = RpgMakerAdapter.IsFullscreen(handle) || nativeStyleConfirmed;
                    if (currentlyFullscreen != state.EngineWasFullscreen)
                        RpgMakerAdapter.SwitchFullscreen(handle, state.EngineWasFullscreen, nativeStyleConfirmed);
                    if (state.EngineWasFullscreen)
                    {
                        Native.RemoveProp(handle, MarkerName); states.Remove(state); SaveJournal(); return;
                    }
                }
                SetStyle(handle, -16, state.Style & ~0x21000000);
                SetStyle(handle, -20, state.ExStyle);
                if (Native.SetWindowPos(handle, new IntPtr((state.ExStyle & 8) != 0 ? -1 : -2), 0, 0, 0, 0, 0x0033) == false) ThrowNative();
                RestoreRegion(handle, state.RegionData);
                WindowPlacement placement = state.Placement;
                placement.length = Marshal.SizeOf(typeof(WindowPlacement));
                if (!Native.SetWindowPlacement(handle, ref placement)) ThrowNative();
                // Some applications react to placement changes by resetting their region. Restore it last as well.
                RestoreRegion(handle, state.RegionData);
                Native.RemoveProp(handle, MarkerName);
            }
            states.Remove(state);
            SaveJournal();
        }

        public string RestoreAll()
        {
            var errors = new List<string>();
            foreach (var state in new List<SavedWindowState>(states))
            {
                try { Restore(new IntPtr(state.Handle)); }
                catch (Exception ex) { errors.Add(state.Title + "：" + ex.Message); }
            }
            return string.Join("\r\n", errors.ToArray());
        }

        public void PruneClosed()
        {
            bool changed = states.RemoveAll(s => !Matches(s)) > 0;
            if (changed) SaveJournal();
        }

        private void SaveJournal()
        {
            if (states.Count == 0) { if (File.Exists(journalPath)) File.Delete(journalPath); return; }
            string temp = journalPath + ".tmp";
            using (var stream = File.Create(temp)) new XmlSerializer(typeof(List<SavedWindowState>)).Serialize(stream, states);
            if (File.Exists(journalPath)) File.Replace(temp, journalPath, null);
            else File.Move(temp, journalPath);
        }

        private static void SetStyle(IntPtr handle, int index, int value)
        {
            Native.SetLastError(0);
            int old = Native.SetWindowLong(handle, index, value);
            if (old == 0 && Marshal.GetLastWin32Error() != 0) ThrowNative();
        }
        private static void EnsureResponsive(IntPtr handle)
        {
            if (!Native.IsWindow(handle)) throw new InvalidOperationException("这个窗口已经关闭，请刷新列表。");
            IntPtr result;
            if (Native.SendMessageTimeout(handle, 0, IntPtr.Zero, IntPtr.Zero, 2, 600, out result) == IntPtr.Zero)
                throw new InvalidOperationException("这个窗口暂时没有响应或拒绝控制，请等待它恢复后再试。恢复记录会保留。");
        }
        private static void Move(IntPtr handle, int x, int y, int w, int h, bool frameChanged, bool skipChanging = false)
        {
            if (!Native.SetWindowPos(handle, IntPtr.Zero, x, y, w, h,
                (uint)(0x0014 | (frameChanged ? 0x0020 : 0) | (skipChanging ? 0x0400 : 0)))) ThrowNative();
        }
        private static void ThrowNative()
        {
            int code = Marshal.GetLastWin32Error();
            if (code == 5) throw new InvalidOperationException("窗口拒绝控制。如果游戏以管理员身份运行，请也以管理员身份运行本工具。");
            throw new Win32Exception(code);
        }
        private static byte[] CaptureRegion(IntPtr handle)
        {
            IntPtr region = Native.CreateRectRgn(0, 0, 0, 0);
            if (region == IntPtr.Zero) ThrowNative();
            try
            {
                if (Native.GetWindowRgn(handle, region) == 0) return null;
                uint length = Native.GetRegionData(region, 0, IntPtr.Zero);
                if (length == 0 || length > 1048576) throw new InvalidOperationException("窗口裁切区域无法保存。");
                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (Native.GetRegionData(region, length, buffer) == 0) ThrowNative();
                    var bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, (int)length); return bytes;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Native.DeleteObject(region); }
        }
        private static void RestoreRegion(IntPtr handle, byte[] data)
        {
            if (data == null) { if (Native.SetWindowRgn(handle, IntPtr.Zero, true) == 0) ThrowNative(); return; }
            IntPtr region = Native.ExtCreateRegion(IntPtr.Zero, (uint)data.Length, data);
            if (region == IntPtr.Zero) ThrowNative();
            if (Native.SetWindowRgn(handle, region, true) == 0) { Native.DeleteObject(region); ThrowNative(); }
        }
        public void Dispose() { RestoreAll(); }
    }

    internal static class RpgMakerAdapter
    {
        internal static string Detect(IntPtr handle)
        {
            try
            {
                uint pid; Native.GetWindowThreadProcessId(handle, out pid);
                IntPtr process = Native.OpenProcess(0x1000, false, pid);
                if (process == IntPtr.Zero) return null;
                string directory;
                try
                {
                    var executable = new StringBuilder(4096); int length = executable.Capacity;
                    if (!Native.QueryFullProcessImageName(process, 0, executable, ref length)) return null;
                    directory = Path.GetDirectoryName(executable.ToString());
                }
                finally { Native.CloseHandle(process); }
                if (!File.Exists(Path.Combine(directory, "nw.dll"))) return null;
                foreach (string root in new[] { directory, Path.Combine(directory, "www") })
                {
                    if (!File.Exists(Path.Combine(root, "index.html"))) continue;
                    foreach (string name in new[] { "rmmz_core.js", "rpg_core.js" })
                    {
                        string core = Path.Combine(root, "js", name);
                        if (!File.Exists(core) || new FileInfo(core).Length > 4194304) continue;
                        string source = File.ReadAllText(core);
                        if (source.Contains("_switchFullScreen") && source.Contains("_requestFullScreen") &&
                            System.Text.RegularExpressions.Regex.IsMatch(source, @"case\s+115\s*:"))
                            return name == "rmmz_core.js" ? "RPG Maker MZ / NW.js" : "RPG Maker MV / NW.js";
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        internal static bool IsFullscreen(IntPtr handle)
        {
            NativeRect bounds;
            if (!Native.GetWindowRect(handle, out bounds) || Native.IsIconic(handle)) return false;
            Rectangle screen = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            return (Native.GetWindowLong(handle, -16) & 0x00C00000) == 0 &&
                Math.Abs(bounds.Left - screen.Left) <= 2 && Math.Abs(bounds.Top - screen.Top) <= 2 &&
                Math.Abs(bounds.Right - screen.Right) <= 2 && Math.Abs(bounds.Bottom - screen.Bottom) <= 2;
        }

        internal static bool HasFullscreenStyle(IntPtr handle)
        { return Native.IsWindow(handle) && (Native.GetWindowLong(handle, -16) & 0x00C40000) == 0; }

        internal static void FitNativeViewport(IntPtr handle, Rectangle monitor)
        {
            if (!HasFullscreenStyle(handle)) return;
            if (!Native.SetWindowPos(handle, IntPtr.Zero, monitor.X, monitor.Y, monitor.Width, monitor.Height, 0x0014))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        internal static void SwitchFullscreen(IntPtr handle, bool fullscreen, bool nativeStyleConfirmed = false)
        {
            Func<bool> currentState = () => nativeStyleConfirmed && !fullscreen ? HasFullscreenStyle(handle) : IsFullscreen(handle);
            if (currentState() == fullscreen) return;
            DateTime keyDeadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (ModifiersPressed())
            {
                if (DateTime.UtcNow >= keyDeadline)
                    throw new InvalidOperationException("请松开 Ctrl、Alt、Shift、Windows 和 F4 键后再试，以便切换游戏原生全屏。");
                Thread.Sleep(20);
            }
            Native.SetForegroundWindow(handle);
            DateTime focusDeadline = DateTime.UtcNow.AddMilliseconds(500);
            while (Native.GetForegroundWindow() != handle && DateTime.UtcNow < focusDeadline) Thread.Sleep(20);
            if (Native.GetForegroundWindow() != handle)
                throw new InvalidOperationException("无法激活游戏窗口。请先切回游戏，再切回助手重试；也可以在游戏中按 F4 切换原生全屏。");
            if (ModifiersPressed()) throw new InvalidOperationException("检测到组合键仍被按下，请松开按键后再试。");
            var down = new Native.Input { type = 1 };
            down.data.keyboard.virtualKey = 0x73;
            var up = down; up.data.keyboard.flags = 2;
            bool styleWasNative = HasFullscreenStyle(handle);
            if (Native.SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(Native.Input))) != 2)
                throw new InvalidOperationException("系统未能向游戏发送全屏快捷键。请确认游戏与助手使用相同的运行权限。");
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(3500);
            DateTime stableSince = DateTime.MinValue;
            NativeRect lastBounds = new NativeRect();
            while (DateTime.UtcNow < deadline)
            {
                if (!Native.IsWindow(handle)) throw new InvalidOperationException("游戏窗口已关闭。");
                if (fullscreen && !styleWasNative && HasFullscreenStyle(handle) && !IsFullscreen(handle))
                    FitNativeViewport(handle, System.Windows.Forms.Screen.FromHandle(handle).Bounds);
                NativeRect bounds;
                if (currentState() == fullscreen && Native.GetWindowRect(handle, out bounds))
                {
                    if (stableSince == DateTime.MinValue || bounds.Left != lastBounds.Left || bounds.Top != lastBounds.Top ||
                        bounds.Right != lastBounds.Right || bounds.Bottom != lastBounds.Bottom)
                    { stableSince = DateTime.UtcNow; lastBounds = bounds; }
                    else if ((DateTime.UtcNow - stableSince).TotalMilliseconds >= 300) return;
                }
                else stableSince = DateTime.MinValue;
                Thread.Sleep(40);
            }
            throw new InvalidOperationException("游戏尚未完成原生全屏切换。请等游戏进入标题画面后再试；若游戏禁用了 F4，可选择标准全屏或兼容裁边。");
        }

        private static bool ModifiersPressed()
        {
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x73 })
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
    }

    internal static class Native
    {
        internal delegate bool EnumWindowProc(IntPtr handle, IntPtr param);
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
        { internal ushort virtualKey, scanCode; internal uint flags, time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
        { internal int dx, dy; internal uint mouseData, flags, time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputData
        { [FieldOffset(0)] internal KeyboardInput keyboard; [FieldOffset(0)] internal MouseInput mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input
        { internal uint type; internal InputData data; }
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, IntPtr param);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] internal static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong(IntPtr handle, int index, int value);
        [DllImport("kernel32.dll")] internal static extern void SetLastError(uint error);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr handle, int command);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool SetProp(IntPtr handle, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetProp(IntPtr handle, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr RemoveProp(IntPtr handle, string name);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int SetWindowRgn(IntPtr handle, IntPtr region, bool redraw);
        [DllImport("user32.dll")] internal static extern int GetWindowRgn(IntPtr handle, IntPtr region);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern uint GetRegionData(IntPtr region, uint length, IntPtr data);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr ExtCreateRegion(IntPtr transform, uint length, byte[] data);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    }
}
