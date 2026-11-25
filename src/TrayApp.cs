using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Windows.Forms;

namespace WindowsBaselineTestSimulator
{
    public static class TrayApp
    {
        public static int Run(bool showPanelFirst)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var context = new TrayContext())
            {
                if (showPanelFirst) context.ShowPanel();
                Application.Run(context);
            }
            return 0;
        }
    }

    /// <summary>托盘图标用的圆形状态灯。</summary>
    /// <summary>一次性气泡提示（用于 CLI/快捷方式触发的快速开关，无需托盘常驻）。</summary>
    public static class Toast
    {
        public static void Show(string text, bool good)
        {
            try
            {
                var icon = new NotifyIcon();
                icon.Icon = Icons.Dot(good ? Colors.Good : Color.Gray);
                icon.Text = "WindowsBaselineTestSimulator";
                icon.BalloonTipTitle = "WindowsBaselineTestSimulator";
                icon.BalloonTipText = text;
                icon.Visible = true;
                icon.ShowBalloonTip(2600);

                var timer = new Timer();
                timer.Interval = 3400;
                timer.Tick += delegate
                {
                    timer.Stop();
                    timer.Dispose();
                    icon.Visible = false;
                    icon.Dispose();
                    Application.Exit();
                };
                timer.Start();
                Application.Run();
            }
            catch (Exception ex) { Store.Log("Toast failed: " + ex.Message); }
        }
    }

    public static class Icons
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        public static Icon Dot(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                        g.FillEllipse(shadow, 2.2f, 2.6f, 13f, 13f);
                    using (var brush = new SolidBrush(color))
                        g.FillEllipse(brush, 1.6f, 1.6f, 12.8f, 12.8f);
                    using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1.4f))
                        g.DrawEllipse(pen, 2.2f, 2.2f, 11.6f, 11.6f);
                }

                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle))
                        return (Icon)temp.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }
    }

    public class TrayContext : ApplicationContext
    {
        readonly NotifyIcon _icon;
        readonly PanelForm _panel;
        readonly Timer _timer;
        readonly ToolStripMenuItem _summaryItem;

        public PanelForm Panel { get { return _panel; } }

        public TrayContext()
        {
            _summaryItem = new ToolStripMenuItem("状态：读取中…");
            _summaryItem.Enabled = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("检测服务：开 / 关  (Ctrl+Alt+D)", null, delegate { ToggleDetection(); }));
            menu.Items.Add(_summaryItem);
            menu.Items.Add(new ToolStripMenuItem("打开面板  (Ctrl+Alt+B)", null, delegate { ShowPanel(); }));
            menu.Items.Add(new ToolStripMenuItem("启动 / 停止 Windows 服务（需管理员）", null, delegate { ToggleServiceElevated(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("应用并模拟重启", null, delegate { ApplyBoot(); }));
            menu.Items.Add(new ToolStripMenuItem("全部模拟为不合格", null, delegate { SimulateAllFail(); }));
            menu.Items.Add(new ToolStripMenuItem("恢复真实检测", null, delegate { ClearSimulation(); }));
            menu.Items.Add(new ToolStripMenuItem("复制判定 JSON", null, delegate { CopyJson(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("打开系统设置页", null, delegate { OpenSettings(); }));
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitApp(); }));

            _icon = new NotifyIcon();
            _icon.Icon = Icons.Dot(Color.Gray);
            _icon.Text = "基线模拟器：读取中";
            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate { ShowPanel(); };

            _panel = new PanelForm(this);

            _timer = new Timer();
            _timer.Interval = 8000;
            _timer.Tick += delegate { RefreshIcon(); };

            // 首次评估放到消息循环里异步做：TPM/DeviceGuard 的 WMI 在非提权下可能耗时数秒，
            // 同步做会让托盘图标在这段时间内不响应。
            var first = new Timer();
            first.Interval = 200;
            first.Tick += delegate
            {
                first.Stop();
                first.Dispose();
                RefreshIcon();
                _timer.Start();
            };
            first.Start();
        }

        public void ShowPanel()
        {
            _panel.Reload();
            _panel.Show();
            _panel.WindowState = FormWindowState.Normal;
            _panel.Activate();
            _panel.BringToFront();
        }

        public void RefreshIcon()
        {
            try
            {
                var list = Baseline.CurrentView();
                Verdict boot = Baseline.VerdictOf(list, true);
                Verdict next = Baseline.VerdictOf(list, false);

                bool enabled = Store.IsDetectionEnabled();
                _icon.Icon = Icons.Dot(enabled ? Colors.For(boot) : Color.Gray);
                _icon.Text = Clip(enabled
                    ? ("基线：" + Baseline.VerdictName(boot, true) + " / 下次：" + Baseline.VerdictName(next, true))
                    : "基线：检测已关闭", 62);
                _summaryItem.Text = enabled
                    ? ("本次启动：" + Baseline.VerdictName(boot, true) + "    下次启动：" + Baseline.VerdictName(next, true))
                    : "检测服务：已关闭（Ctrl+Alt+D 开启）";
            }
            catch (Exception ex) { Store.Log("RefreshIcon: " + ex.Message); }
        }

        public void ToggleDetection()
        {
            bool target = !Store.IsDetectionEnabled();
            Ipc.SendOrLocal(target ? "enable" : "disable");
            bool now = Store.IsDetectionEnabled();

            RefreshIcon();
            if (_panel.Visible) _panel.Reload();
            _icon.ShowBalloonTip(2200, "WindowsBaselineTestSimulator",
                now ? "检测已开启：会重新评估 8 项" : "检测已关闭：不做任何评估（软开关）",
                now ? ToolTipIcon.Info : ToolTipIcon.Warning);
            Store.Log("detection toggled -> " + (now ? "on" : "off"));
        }

        void ToggleServiceElevated()
        {
            string cli = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wbts.exe");
            string action = "start";

            try
            {
                var controller = new ServiceController(ServiceNames.Name);
                controller.Refresh();
                if (controller.Status == ServiceControllerStatus.Running) action = "stop";
            }
            catch { action = "start"; }

            try
            {
                var info = new ProcessStartInfo(cli, "svc " + action);
                info.Verb = "runas";
                info.UseShellExecute = true;
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
                _icon.ShowBalloonTip(2200, "Windows 服务",
                    "已请求" + (action == "start" ? "启动" : "停止") + "服务（请在 UAC 提示中确认）", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Store.Log("ToggleServiceElevated failed: " + ex.Message);
                _icon.ShowBalloonTip(2500, "无法执行", "提权被取消或失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        void ApplyBoot()
        {
            Ipc.SendOrLocal("apply");
            RefreshIcon();
            if (_panel.Visible) _panel.Reload();
        }

        void SimulateAllFail()
        {
            foreach (CriterionDef def in Baseline.Criteria) Ipc.SendOrLocal("set " + def.Id + " off");
            RefreshIcon();
            if (_panel.Visible) _panel.Reload();
        }

        void ClearSimulation()
        {
            Ipc.SendOrLocal("reset");
            RefreshIcon();
            if (_panel.Visible) _panel.Reload();
        }

        void CopyJson()
        {
            try
            {
                Clipboard.SetText(Baseline.Json(Ipc.IsServiceAvailable() ? "service" : "local"));
                _icon.ShowBalloonTip(2000, "已复制", "判定 JSON 已放入剪贴板", ToolTipIcon.Info);
            }
            catch (Exception ex) { Store.Log("CopyJson: " + ex.Message); }
        }

        void OpenSettings()
        {
            try { Process.Start("ms-settings:baseline-protection"); }
            catch
            {
                try { Process.Start("ms-settings:windowsdefender"); }
                catch (Exception ex) { Store.Log("OpenSettings: " + ex.Message); }
            }
        }

        void ExitApp()
        {
            _timer.Stop();
            _icon.Visible = false;
            _panel.AllowClose();
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) _timer.Dispose();
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
                if (_panel != null) _panel.Dispose();
            }
            base.Dispose(disposing);
        }

        static string Clip(string text, int max)
        {
            if (text == null) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }

    public static class Colors
    {
        public static readonly Color Good = Color.FromArgb(16, 124, 16);
        public static readonly Color Bad = Color.FromArgb(196, 43, 28);
        public static readonly Color Warn = Color.FromArgb(157, 93, 0);

        public static Color For(Verdict verdict)
        {
            switch (verdict)
            {
                case Verdict.In: return Good;
                case Verdict.NotIn: return Bad;
                default: return Warn;
            }
        }

        public static Color For(CState state)
        {
            switch (state)
            {
                case CState.On: return Good;
                case CState.Off: return Bad;
                case CState.NotSupported: return Bad;
                default: return Warn;
            }
        }
    }

    public class PanelForm : Form
    {
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0xB15;      // Ctrl+Alt+B：面板
        const int HotkeyIdToggle = 0xB16; // Ctrl+Alt+D：检测开关
        const int MOD_ALT = 0x0001;
        const int MOD_CONTROL = 0x0002;
        const int MOD_NOREPEAT = 0x4000;

        const int ColName = 0;
        const int ColBoot = 1;
        const int ColNext = 2;
        const int ColReason = 3;
        const int ColSim = 4;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        readonly TrayContext _owner;
        readonly Label _verdict = new Label();
        readonly Label _sub = new Label();
        readonly Label _note = new Label();
        readonly DataGridView _grid = new DataGridView();
        bool _loading;
        bool _allowClose;

        public PanelForm(TrayContext owner)
        {
            _owner = owner;
            Text = "WindowsBaselineTestSimulator（基线保护模拟器）";

            // 高 DPI 环境下必须自己按“物理像素”定尺寸：AutoScaleMode 会把写死的逻辑尺寸再乘一次缩放，
            // 在 150% 下直接顶出屏幕。这里关闭自动缩放，改由 ClampToScreen 按工作区计算。
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(780, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ShowInTaskbar = true;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Padding = new Padding(0);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));

            var header = new Panel();
            header.Dock = DockStyle.Fill;
            header.Padding = new Padding(0);

            _verdict.AutoSize = false;
            _verdict.Dock = DockStyle.Top;
            _verdict.Height = 48;
            _verdict.TextAlign = ContentAlignment.MiddleLeft;
            _verdict.Padding = new Padding(14, 0, 12, 0);
            _verdict.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
            _verdict.Text = "基线保护：读取中…";

            _sub.AutoSize = false;
            _sub.Dock = DockStyle.Fill;
            _sub.TextAlign = ContentAlignment.MiddleLeft;
            _sub.Padding = new Padding(16, 0, 12, 0);
            _sub.ForeColor = Color.DimGray;

            header.Controls.Add(_sub);
            header.Controls.Add(_verdict);

            BuildGrid();

            _note.AutoSize = false;
            _note.Dock = DockStyle.Fill;
            _note.Padding = new Padding(16, 6, 12, 6);
            _note.ForeColor = Color.DimGray;
            _note.Text = "说明：勾选“模拟关闭”只会写入模拟注入文件，不改动任何真实安全设置；" +
                         "只有“本次启动”列进判定，而它仅在接受 apply（模拟重启）后变化——与微软基线保护“必须重启才生效”的语义一致。第 7 项为代理检测：看已加载驱动的签名集合，详见 README。";

            var buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(12, 8, 12, 8);
            buttons.Controls.Add(MakeButton("检测 开/关", delegate { _owner.ToggleDetection(); }));
            buttons.Controls.Add(MakeButton("刷新", delegate { Reload(); }));
            buttons.Controls.Add(MakeButton("应用并模拟重启", delegate { ApplyBoot(); }));
            buttons.Controls.Add(MakeButton("全部模拟为不合格", delegate { SimulateAllFail(); }));
            buttons.Controls.Add(MakeButton("恢复真实检测", delegate { ClearSimulation(); }));
            buttons.Controls.Add(MakeButton("复制判定 JSON", delegate { CopyJson(); }));
            buttons.Controls.Add(MakeButton("打开系统设置页", delegate { OpenSettings(); }));

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(_grid, 0, 1);
            root.Controls.Add(_note, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);

            // 不在构造里做评估：首次检测可能耗时数秒（需提权的 WMI），
            // 改为 OnShown 时加载，避免窗口/托盘长时间无响应。
            _verdict.Text = "基线保护：读取中…";
            _sub.Text = "正在做首次启动测量…";

            IntPtr handle = Handle; // 提前创建窗口句柄，供全局热键注册
            if (handle == IntPtr.Zero) Store.Log("PanelForm: handle creation failed");
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ClampToScreen();
            Reload();
        }

        /// <summary>把窗口限制在当前显示器工作区内（DPI 感知下单位就是物理像素）。</summary>
        void ClampToScreen()
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(1120, Math.Max(760, (int)(work.Width * 0.85)));
            int height = Math.Min(700, Math.Max(460, (int)(work.Height * 0.85)));

            // 调试/适配用：WBS_SIZE=1280x800 可强制指定窗口尺寸
            string overrideSize = Environment.GetEnvironmentVariable("WBS_SIZE");
            if (!string.IsNullOrEmpty(overrideSize))
            {
                string[] parts = overrideSize.ToLowerInvariant().Split('x');
                int w, h;
                if (parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h) && w > 400 && h > 300)
                {
                    width = w; height = h;
                }
            }

            Size = new Size(width, height);

            int left = work.Left + Math.Max(0, (work.Width - width) / 2);
            int top = work.Top + Math.Max(0, (work.Height - height) / 2);
            Location = new Point(left, top);
        }

        void BuildGrid()
        {
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.BorderStyle = BorderStyle.None;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.GridColor = Color.Gainsboro;
            _grid.RowTemplate.Height = 34;
            _grid.ColumnHeadersHeight = 38;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.AllowUserToOrderColumns = false;

            var name = new DataGridViewTextBoxColumn();
            name.HeaderText = "检测项";
            name.ReadOnly = true;
            name.FillWeight = 24f;
            name.MinimumWidth = 180;

            var boot = new DataGridViewTextBoxColumn();
            boot.HeaderText = "本次启动（进判定）";
            boot.ReadOnly = true;
            boot.FillWeight = 16f;
            boot.MinimumWidth = 140;

            var next = new DataGridViewTextBoxColumn();
            next.HeaderText = "下次启动（预期）";
            next.ReadOnly = true;
            next.FillWeight = 12f;
            next.MinimumWidth = 110;

            var reason = new DataGridViewTextBoxColumn();
            reason.HeaderText = "原因 / 取证";
            reason.ReadOnly = true;
            reason.FillWeight = 40f;
            reason.MinimumWidth = 260;

            var sim = new DataGridViewCheckBoxColumn();
            sim.HeaderText = "模拟关闭";
            sim.ReadOnly = false;
            sim.FillWeight = 8f;
            sim.MinimumWidth = 84;
            sim.SortMode = DataGridViewColumnSortMode.NotSortable;

            _grid.Columns.AddRange(new DataGridViewColumn[] { name, boot, next, reason, sim });
            _grid.CellValueChanged += OnCellValueChanged;
            _grid.CurrentCellDirtyStateChanged += OnDirtyStateChanged;
        }

        Button MakeButton(string text, EventHandler onClick)
        {
            var button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Height = 28;
            button.Margin = new Padding(0, 0, 8, 0);
            button.Click += onClick;
            return button;
        }

        void OnDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        void OnCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex != ColSim) return;

            DataGridViewRow row = _grid.Rows[e.RowIndex];
            var result = row.Tag as CriterionResult;
            if (result == null) return;

            object value = row.Cells[ColSim].Value;
            bool wantOff = value != null && Convert.ToBoolean(value);

            Ipc.SendOrLocal("set " + result.Def.Id + " " + (wantOff ? "off" : "reset"));

            // 不能在 CellValueChanged 里同步重建行，推迟到消息循环之后
            if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { Reload(); _owner.RefreshIcon(); }));
        }

        public void Reload()
        {
            _loading = true;
            try
            {
                Baseline.BootInfo boot = Baseline.LoadBoot();
                var list = Baseline.CurrentView();
                Verdict state = Baseline.VerdictOf(list, true);
                Verdict next = Baseline.VerdictOf(list, false);

                bool enabled = Store.IsDetectionEnabled();
                _verdict.Text = enabled ? ("基线保护：" + Baseline.VerdictName(state, true)) : "基线保护：检测已关闭";
                _verdict.ForeColor = enabled ? Colors.For(state) : Color.Gray;
                _sub.Text = "本次启动 [" + Baseline.VerdictName(state) + "]     下次启动 [" + Baseline.VerdictName(next) + "]     检测 " + (Store.IsDetectionEnabled() ? "开" : "关") + "     基础版本 v" +
                             Baseline.Version + "     bootId " + (boot.BootId.Length > 0 ? boot.BootId : "(尚未建立快照)") +
                             "     管道 " + (Ipc.IsServiceAvailable() ? "在线" : "离线（本地模式）");

                _grid.Rows.Clear();
                foreach (CriterionResult r in list)
                {
                    int index = _grid.Rows.Add(
                        r.Def.Name + "  (" + r.Def.Id + ")",
                        Baseline.StateName(r.BootState, true) + "  " + r.BootCode,
                        Baseline.StateName(r.CurrentState, true),
                        r.CurrentReason,
                        r.Simulated);

                    DataGridViewRow row = _grid.Rows[index];
                    row.Tag = r;
                    row.Cells[ColBoot].Style.ForeColor = Colors.For(r.BootState);
                    row.Cells[ColNext].Style.ForeColor = Colors.For(r.CurrentState);
                    if (r.Simulated) row.Cells[ColSim].Style.BackColor = Color.FromArgb(255, 244, 206);
                }
            }
            catch (Exception ex) { Store.Log("Reload: " + ex.Message); }
            finally { _loading = false; }
        }

        void ApplyBoot()
        {
            Ipc.SendOrLocal("apply");
            Reload();
            _owner.RefreshIcon();
        }

        void SimulateAllFail()
        {
            foreach (CriterionDef def in Baseline.Criteria) Ipc.SendOrLocal("set " + def.Id + " off");
            Reload();
            _owner.RefreshIcon();
        }

        void ClearSimulation()
        {
            Ipc.SendOrLocal("reset");
            Reload();
            _owner.RefreshIcon();
        }

        void CopyJson()
        {
            try { Clipboard.SetText(Baseline.Json(Ipc.IsServiceAvailable() ? "service" : "local")); }
            catch (Exception ex) { Store.Log("CopyJson: " + ex.Message); }
        }

        void OpenSettings()
        {
            try { Process.Start("ms-settings:baseline-protection"); }
            catch (Exception ex) { Store.Log("OpenSettings: " + ex.Message); }
        }

        public void AllowClose() { _allowClose = true; }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)Keys.B))
                Store.Log("RegisterHotKey(Ctrl+Alt+B) failed: " + Marshal.GetLastWin32Error());
            if (!RegisterHotKey(Handle, HotkeyIdToggle, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (int)Keys.D))
                Store.Log("RegisterHotKey(Ctrl+Alt+D) failed: " + Marshal.GetLastWin32Error());
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotKey(Handle, HotkeyId);
            UnregisterHotKey(Handle, HotkeyIdToggle);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyId) { ToggleVisibility(); return; }
                if (id == HotkeyIdToggle) { _owner.ToggleDetection(); return; }
            }
            base.WndProc(ref m);
        }

        void ToggleVisibility()
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                Hide();
            }
            else
            {
                Reload();
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
