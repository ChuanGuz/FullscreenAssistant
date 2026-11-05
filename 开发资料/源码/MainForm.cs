using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FullscreenAssistant
{
    internal sealed class MainForm : Form
    {
        private readonly WindowController controller;
        private readonly Color ink = Color.FromArgb(25, 39, 61);
        private readonly Color blue = Color.FromArgb(37, 99, 235);
        private readonly Color muted = Color.FromArgb(97, 113, 135);
        private readonly TextBox search = new TextBox();
        private readonly Button refresh = new Button(), enter = new Button(), restore = new Button(), restoreAll = new Button();
        private readonly DataGridView grid = new DataGridView();
        private readonly ComboBox monitors = new ComboBox(), modes = new ComboBox();
        private readonly NumericUpDown topCrop = new NumericUpDown();
        private readonly Label countLabel = new Label(), selectionLabel = new Label(), status = new Label(), empty = new Label(), shortcut = new Label();
        private readonly Timer refreshTimer = new Timer();
        private readonly Timer layoutTimer = new Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private List<WindowCandidate> windows = new List<WindowCandidate>();
        private Screen[] screens = new Screen[0];
        private bool scanning, applying, hotkeyRegistered;

        public MainForm(WindowController controller)
        {
            SuspendLayout();
            this.controller = controller;
            Text = "全屏助手";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ForeColor = ink;
            BackColor = Color.FromArgb(246, 248, 252);
            ClientSize = new Size(1040, 700);
            MinimumSize = new Size(870, 630);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildUi();
            UpdateMonitors();
            modes.Items.AddRange(new object[] { "智能全屏（优先游戏原生）", "标准窗口全屏", "兼容裁边（残留边框时使用）", "游戏原生全屏（需识别）" });
            modes.SelectedIndex = 0;
            modes.SelectedIndexChanged += delegate { topCrop.Enabled = modes.SelectedIndex == 2; };
            topCrop.Enabled = false;
            search.TextChanged += delegate { RenderWindows(); };
            refresh.Click += delegate { RefreshWindows(); };
            enter.Click += delegate { EnterSelected(); };
            restore.Click += delegate { RestoreSelected(); };
            restoreAll.Click += delegate { RestoreEverything(); };
            grid.SelectionChanged += delegate { UpdateSelection(); };
            refreshTimer.Interval = 5000;
            refreshTimer.Tick += delegate { RefreshWindows(); };
            layoutTimer.Interval = 250;
            layoutTimer.Tick += delegate
            {
                if (applying) return;
                string update = controller.MonitorManagedWindows();
                if (update.Length != 0) { SetStatus(update, false); RenderWindows(); }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开全屏助手", null, delegate { Reveal(); });
            menu.Items.Add("恢复全部窗口", null, delegate { RestoreEverything(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出并恢复窗口", null, delegate { Close(); });
            tray.Icon = Icon;
            tray.Text = "全屏助手 · Ctrl+Alt+F11 恢复全部";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Reveal(); };
            tray.Visible = true;
            SetStatus("先打开一个窗口化的游戏，再从列表选择它。", false);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 32, 52), Margin = Padding.Empty };
            header.Controls.Add(new Label { Text = "全屏助手", Font = new Font(Font.FontFamily, 23, FontStyle.Bold),
                ForeColor = Color.White, Location = new Point(28, 18), AutoSize = true });
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 18, 26, 12),
                ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.Controls.Add(body, 0, 1);

            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            searchRow.Controls.Add(new Label { Text = "查找窗口", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            search.Dock = DockStyle.Fill; search.Margin = new Padding(0, 5, 12, 7); search.AccessibleName = "搜索窗口名称或进程";
            searchRow.Controls.Add(search, 1, 0);
            countLabel.Dock = DockStyle.Fill; countLabel.TextAlign = ContentAlignment.MiddleRight; countLabel.ForeColor = muted;
            countLabel.Padding = new Padding(0, 0, 16, 0); searchRow.Controls.Add(countLabel, 2, 0);
            StyleButton(refresh, "刷新列表", false); refresh.Dock = DockStyle.Fill; refresh.Margin = new Padding(0, 0, 0, 7);
            searchRow.Controls.Add(refresh, 3, 0); body.Controls.Add(searchRow, 0, 0);

            var gridPanel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 0), BackColor = Color.White };
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.MultiSelect = false; grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(235, 239, 246);
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(234, 240, 248),
                ForeColor = muted, Font = Font, Padding = new Padding(9, 0, 0, 0), SelectionBackColor = Color.FromArgb(234, 240, 248) };
            grid.ColumnHeadersHeight = 38; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowTemplate.Height = 42;
            grid.DefaultCellStyle = new DataGridViewCellStyle { Font = Font, ForeColor = ink, BackColor = Color.White,
                SelectionBackColor = Color.FromArgb(221, 234, 255), SelectionForeColor = Color.FromArgb(24, 67, 137), Padding = new Padding(9, 0, 0, 0) };
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "window", HeaderText = "窗口名称", FillWeight = 49, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "process", HeaderText = "程序", FillWeight = 22, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "尺寸", FillWeight = 16, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "状态", FillWeight = 13, SortMode = DataGridViewColumnSortMode.NotSortable });
            empty.Dock = DockStyle.Fill; empty.TextAlign = ContentAlignment.MiddleCenter; empty.ForeColor = muted;
            empty.Text = "正在查找打开的窗口…";
            gridPanel.Controls.Add(grid); gridPanel.Controls.Add(empty); empty.BringToFront();
            body.Controls.Add(gridPanel, 0, 1);

            selectionLabel.Dock = DockStyle.Fill; selectionLabel.TextAlign = ContentAlignment.MiddleLeft; selectionLabel.ForeColor = muted;
            selectionLabel.AutoEllipsis = true; body.Controls.Add(selectionLabel, 0, 2);

            var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
            choices.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); choices.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            choices.Controls.Add(OptionLabel("显示在哪个屏幕"), 0, 0);
            choices.Controls.Add(OptionLabel("全屏方式"), 1, 0);
            choices.Controls.Add(OptionLabel("顶部额外裁切"), 2, 0);
            monitors.DropDownStyle = ComboBoxStyle.DropDownList; monitors.Dock = DockStyle.Fill; monitors.Margin = new Padding(0, 3, 14, 0);
            modes.DropDownStyle = ComboBoxStyle.DropDownList; modes.Dock = DockStyle.Fill; modes.Margin = new Padding(0, 3, 14, 0);
            topCrop.Minimum = 0; topCrop.Maximum = 200; topCrop.Increment = 2; topCrop.Dock = DockStyle.Top; topCrop.Margin = new Padding(0, 3, 0, 0);
            topCrop.AccessibleName = "顶部额外裁切像素，仅兼容模式可用";
            choices.Controls.Add(monitors, 0, 1); choices.Controls.Add(modes, 1, 1); choices.Controls.Add(topCrop, 2, 1);
            body.Controls.Add(choices, 0, 3);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 285));
            StyleButton(enter, "全屏", true); StyleButton(restore, "恢复窗口", false); StyleButton(restoreAll, "恢复全部", false);
            foreach (Button b in new[] { enter, restore, restoreAll }) { b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 9, 12, 9); }
            actions.Controls.Add(enter, 0, 0); actions.Controls.Add(restore, 1, 0); actions.Controls.Add(restoreAll, 2, 0);
            shortcut.Text = "Ctrl + Alt + F11  恢复全部\r\n关闭工具时，也会自动恢复窗口";
            shortcut.ForeColor = muted; shortcut.Dock = DockStyle.Fill; shortcut.TextAlign = ContentAlignment.MiddleRight;
            actions.Controls.Add(shortcut, 4, 0); body.Controls.Add(actions, 0, 5);

            var statusPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(233, 239, 247), Margin = Padding.Empty, Padding = new Padding(26, 0, 26, 0) };
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
            statusPanel.Controls.Add(status); root.Controls.Add(statusPanel, 0, 2);
            enter.Enabled = restore.Enabled = restoreAll.Enabled = false;
        }

        private Label OptionLabel(string text) { return new Label { Text = text, ForeColor = ink, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }; }
        private void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text; button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand;
            button.Font = new Font(Font, FontStyle.Bold);
            button.BackColor = primary ? blue : Color.White;
            button.ForeColor = primary ? Color.White : ink;
            button.FlatAppearance.BorderColor = primary ? blue : Color.FromArgb(207, 218, 232);
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            int[] keys = { 0x7A, 0x79, 0x78 };
            foreach (int key in keys)
            {
                if (!RegisterHotKey(Handle, 101, 0x4003, (uint)key)) continue;
                hotkeyRegistered = true;
                string combination = "Ctrl + Alt + F" + (key - 0x6F);
                shortcut.Text = combination + "  恢复全部\r\n关闭工具时，也会自动恢复窗口";
                tray.Text = "全屏助手 · " + combination + " 恢复全部";
                break;
            }
            if (!hotkeyRegistered)
            {
                shortcut.Text = "当前环境未能启用快捷键\r\n可通过任务栏托盘恢复窗口";
                SetStatus("恢复快捷键未能启用；可用按钮或右下角的托盘图标恢复窗口。", true);
            }
            if (!string.IsNullOrEmpty(controller.LastRecoveryMessage)) SetStatus(controller.LastRecoveryMessage, false);
            RefreshWindows(); refreshTimer.Start(); layoutTimer.Start();
        }

        private void UpdateMonitors()
        {
            int selected = monitors.SelectedIndex;
            screens = Screen.AllScreens;
            monitors.Items.Clear(); monitors.Items.Add("自动 · 使用窗口所在的显示器");
            for (int i = 0; i < screens.Length; i++) monitors.Items.Add("显示器 " + (i + 1) + " · " + screens[i].Bounds.Width + " × " + screens[i].Bounds.Height + (screens[i].Primary ? "（主屏）" : ""));
            monitors.SelectedIndex = selected >= 0 && selected < monitors.Items.Count ? selected : 0;
        }

        private WindowCandidate Selected
        {
            get { return grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as WindowCandidate; }
        }
        private void RefreshWindows()
        {
            if (scanning || applying || IsDisposed) return;
            scanning = true; refresh.Enabled = false;
            Task.Factory.StartNew(() => WindowController.EnumerateWindows()).ContinueWith(task =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((MethodInvoker)delegate
                {
                    scanning = false; refresh.Enabled = true;
                    if (task.IsFaulted) { SetStatus("列表刷新失败，请再点一次刷新。", true); return; }
                    try { controller.PruneClosed(); } catch (Exception ex) { SetStatus(ex.Message, true); }
                    windows = task.Result; RenderWindows();
                }); } catch (InvalidOperationException) { }
            });
        }

        private void RenderWindows()
        {
            IntPtr selected = Selected == null ? IntPtr.Zero : Selected.Handle;
            int scroll = grid.FirstDisplayedScrollingRowIndex;
            string query = search.Text.Trim();
            var filtered = windows.Where(w => query.Length == 0 || (w.Title + " " + w.ProcessName).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderByDescending(w => controller.IsManaged(w.Handle)).ThenBy(w => w.Title).ToList();
            grid.SuspendLayout(); grid.Rows.Clear();
            int selectedIndex = -1;
            foreach (var window in filtered)
            {
                int index = grid.Rows.Add(window.Title, window.ProcessName, window.Minimized ? "已最小化" : window.Width + " × " + window.Height,
                    controller.ManagedStatus(window.Handle));
                grid.Rows[index].Tag = window;
                if (window.Handle == selected) selectedIndex = index;
            }
            grid.ClearSelection();
            if (selectedIndex >= 0) { grid.Rows[selectedIndex].Selected = true; grid.CurrentCell = grid.Rows[selectedIndex].Cells[0]; }
            if (scroll >= 0 && scroll < grid.Rows.Count) try { grid.FirstDisplayedScrollingRowIndex = scroll; } catch { }
            grid.ResumeLayout();
            empty.Visible = filtered.Count == 0;
            empty.Text = query.Length == 0 ? "没有找到可操作的窗口\r\n请先打开窗口化的游戏，再点“刷新列表”。" : "没有匹配的窗口，请换一个名称搜索。";
            countLabel.Text = filtered.Count + " 个窗口";
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            var selected = Selected;
            selectionLabel.Text = selected == null ? "请选择要全屏的窗口" : "已选择：" + selected.Title + "    ·    " +
                (string.IsNullOrEmpty(selected.EngineName) ? selected.ProcessName : "识别到 " + selected.EngineName);
            enter.Enabled = !applying && selected != null;
            restore.Enabled = !applying && selected != null && controller.IsManaged(selected.Handle);
            restoreAll.Enabled = !applying && controller.Count > 0;
        }
        private void EnterSelected()
        {
            var selected = Selected; if (selected == null) return;
            Rectangle bounds = monitors.SelectedIndex > 0 && monitors.SelectedIndex <= screens.Length
                ? screens[monitors.SelectedIndex - 1].Bounds : Screen.FromHandle(selected.Handle).Bounds;
            FullscreenMode mode = modes.SelectedIndex == 0 ? FullscreenMode.Automatic : modes.SelectedIndex == 1 ? FullscreenMode.Borderless :
                modes.SelectedIndex == 2 ? FullscreenMode.CropFrame : FullscreenMode.GameNative;
            RunOperation(delegate { controller.Enter(selected.Handle, bounds, mode, modes.SelectedIndex == 2 ? (int)topCrop.Value : 0); SetStatus(controller.LastResult, false); });
        }
        private void RestoreSelected()
        {
            var selected = Selected; if (selected == null) return;
            RunOperation(delegate { controller.Restore(selected.Handle); SetStatus("已恢复这个窗口原来的位置、尺寸和窗口状态。", false); });
        }
        private void RestoreEverything()
        {
            RunOperation(delegate { string errors = controller.RestoreAll(); if (errors.Length != 0) throw new InvalidOperationException(errors); SetStatus("已恢复全部受控窗口。", false); });
            Reveal();
        }
        private void RunOperation(Action action)
        {
            if (applying) return;
            applying = true; UpdateSelection();
            Cursor = Cursors.WaitCursor;
            try { action(); }
            catch (Exception ex) { SetStatus(ex.Message, true); MessageBox.Show(this, ex.Message, "未能完成操作", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { Cursor = Cursors.Default; applying = false; UpdateSelection(); RefreshWindows(); }
        }
        private void SetStatus(string text, bool error) { status.Text = text.Replace("\r\n", "  "); status.ForeColor = error ? Color.FromArgb(160, 58, 28) : Color.FromArgb(45, 84, 123); }
        private void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); Native.SetForegroundWindow(Handle); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 101) { RestoreEverything(); return; }
            if (m.Msg == 0x007E) UpdateMonitors();
            base.WndProc(ref m);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            refreshTimer.Stop();
            layoutTimer.Stop();
            string errors = controller.RestoreAll();
            if (errors.Length != 0 && e.CloseReason != CloseReason.WindowsShutDown)
            {
                MessageBox.Show(this, "有窗口尚未恢复，请再点“恢复全部”。\r\n" + errors, "恢复未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true; refreshTimer.Start(); layoutTimer.Start();
            }
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (hotkeyRegistered) UnregisterHotKey(Handle, 101);
            tray.Visible = false; tray.Dispose(); refreshTimer.Dispose(); layoutTimer.Dispose(); base.OnFormClosed(e);
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
    }
}
