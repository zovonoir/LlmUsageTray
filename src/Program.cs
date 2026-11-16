using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LlmUsageTray
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LlmUsageTray");
            int dirIndex = Array.IndexOf(args, "--data-dir");
            if (dirIndex >= 0 && dirIndex + 1 < args.Length) dataDirectory = Path.GetFullPath(args[dirIndex + 1]);
            if (args.Contains("--self-test")) return SelfTests.Run();
            if (args.Contains("--render-icons")) { BudgetIcon.SavePreview(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray-icon-preview.png")); return 0; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try {
                Store store = new Store(dataDirectory);
                if (args.Contains("--configure-stdin")) {
                    string key = Console.In.ReadLine();
                    string user = Console.In.ReadLine();
                    if (String.IsNullOrWhiteSpace(key) || key.Contains("\r") || key.Contains("\n")) return 2;
                    store.Settings.User = String.IsNullOrWhiteSpace(user) ? "ntid" : user.Trim();
                    store.SaveSettings(store.Settings, key.Trim());
                    return 0;
                }
                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\LlmUsageTray-" + Environment.UserName, out created)) {
                    if (!created) { MessageBox.Show("LLM 用量已在运行。点击系统托盘图标即可展开。", "LLM 用量"); return 0; }
                    Application.Run(new UsageApplication(store, args.Contains("--show-details")));
                }
                return 0;
            } catch {
                MessageBox.Show("LLM 用量无法启动。请确认 .NET Framework 4.8 可用，且本机数据目录可以写入。", "LLM 用量", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(18, 23, 33);
        public static readonly Color Surface = Color.FromArgb(28, 35, 48);
        public static readonly Color Border = Color.FromArgb(48, 60, 79);
        public static readonly Color Text = Color.FromArgb(234, 241, 250);
        public static readonly Color Muted = Color.FromArgb(160, 174, 194);
        public static readonly Color Accent = Color.FromArgb(109, 199, 236);
        public static readonly Color Good = Color.FromArgb(95, 207, 159);
        public static readonly Color Warn = Color.FromArgb(244, 183, 93);
        public static Font Font(float size = 10, FontStyle style = FontStyle.Regular) { return new Font("Microsoft YaHei UI", size, style); }
        public static Button Button(string text)
        {
            Button b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(72, 34), BackColor = Surface, ForeColor = Text, FlatStyle = FlatStyle.Flat, Font = Font(9), Cursor = Cursors.Hand, Margin = new Padding(5, 0, 0, 0) };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 59, 78);
            return b;
        }
        public static Label Label(string text, float size = 10, Color? color = null, FontStyle style = FontStyle.Regular)
        {
            return new Label { Text = text, Font = Font(size, style), ForeColor = color ?? Text, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        }
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static Icon Icon()
        {
            using (Bitmap b = new Bitmap(32, 32)) using (Graphics g = Graphics.FromImage(b)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush brush = new SolidBrush(Surface)) g.FillEllipse(brush, 0, 0, 31, 31);
                using (Pen pen = new Pen(Accent, 3)) {
                    g.DrawLine(pen, 8, 23, 8, 15); g.DrawLine(pen, 15, 23, 15, 10); g.DrawLine(pen, 22, 23, 22, 6);
                }
                IntPtr h = b.GetHicon();
                try { using (Icon temp = System.Drawing.Icon.FromHandle(h)) return (Icon)temp.Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }

    public sealed class UsageApplication : ApplicationContext
    {
        public readonly Store Store;
        public Snapshot Snapshot;
        public string Status = "正在载入…";
        public bool Busy;
        public bool Failed;
        public DateTime NextRefreshUtc;
        readonly UsageClient client = new UsageClient();
        readonly NotifyIcon tray;
        Icon icon;
        readonly Control dispatcher = new Control();
        DetailsForm details;
        readonly System.Windows.Forms.Timer timer;
        bool closing;
        int failures;
        string currentDate;
        public UsageApplication(Store store, bool showDetails)
        {
            Store = store;
            Snapshot = store.LoadCache();
            currentDate = Calendar.Range(DateTime.UtcNow).LocalDate;
            if (Snapshot != null) Status = "已载入上次数据，正在刷新…";
            dispatcher.CreateControl();
            icon = BudgetIcon.Create(null, store.Settings.MonthlyBudget, false);
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("展开用量详情", null, delegate { ShowDetails(); });
            menu.Items.Add("立即刷新", null, delegate { Refresh(); });
            menu.Items.Add("设置", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            tray = new NotifyIcon { Icon = icon, Text = "LLM 用量 · 正在加载", ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleDetails(); };
            UpdateViews();
            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate {
                string date = Calendar.Range(DateTime.UtcNow).LocalDate;
                if (date != currentDate) { currentDate = date; NextRefreshUtc = DateTime.MinValue; UpdateViews(); }
                if (!Busy && DateTime.UtcNow >= NextRefreshUtc) Refresh();
            };
            timer.Start();
            SystemEvents.PowerModeChanged += OnPowerMode;
            if (showDetails) ShowDetails();
            dispatcher.BeginInvoke(new Action(delegate {
                if (String.IsNullOrWhiteSpace(Store.Key)) ShowSettings();
                else Refresh();
            }));
        }
        public bool HasToday { get { return Calendar.IsToday(Snapshot, DateTime.UtcNow); } }
        public bool HasMonth { get { return Calendar.IsMonth(Snapshot, DateTime.UtcNow); } }
        public void ToggleDetails() { if (details != null && details.Visible) details.Hide(); else ShowDetails(); }
        public void ShowDetails()
        {
            if (details == null || details.IsDisposed) details = new DetailsForm(this);
            details.RefreshData();
            details.Show();
            details.Activate();
        }
        public void ShowSettings()
        {
            string oldScope = Store.ScopeId;
            using (SettingsForm form = new SettingsForm(Store)) {
                if (form.ShowDialog(details != null && details.Visible ? (IWin32Window)details : null) != DialogResult.OK) return;
            }
            if (oldScope != Store.ScopeId) Snapshot = null;
            UpdateViews();
            failures = 0;
            Refresh();
        }
        public async void Refresh()
        {
            if (Busy || closing) return;
            if (String.IsNullOrWhiteSpace(Store.Key)) {
                Status = "请设置 API key"; Failed = true; NextRefreshUtc = DateTime.UtcNow.AddMinutes(5); UpdateViews(); return;
            }
            Busy = true; Status = "正在刷新…"; UpdateViews();
            QueryRange range = Calendar.Range(DateTime.UtcNow);
            string key = Store.Key;
            string user = Store.Settings.User;
            try {
                Snapshot fresh = await client.Read(key, user, range);
                if (closing) return;
                if (key != Store.Key || user != Store.Settings.User) { NextRefreshUtc = DateTime.MinValue; return; }
                Snapshot = fresh;
                fresh.ScopeId = Store.ScopeId;
                Failed = false; failures = 0;
                Status = "已更新";
                try { Store.SaveCache(fresh); } catch { Status = "已更新 · 本机缓存保存失败"; }
                NextRefreshUtc = Calendar.IsToday(fresh, DateTime.UtcNow) ? DateTime.UtcNow.AddMinutes(Store.Settings.RefreshMinutes) : DateTime.MinValue;
            } catch (ApiFailure ex) {
                if (closing) return;
                Failed = true; failures++;
                Status = ex.Message.Trim();
                int delay = Math.Max(ex.RetrySeconds, (int)Math.Min(900, 60 * Math.Pow(2, Math.Min(failures - 1, 4))));
                NextRefreshUtc = DateTime.UtcNow.AddSeconds(delay);
            } catch {
                if (closing) return;
                Failed = true; Status = "查询暂时失败，稍后自动重试。"; NextRefreshUtc = DateTime.UtcNow.AddMinutes(5);
            } finally {
                Busy = false;
                if (!closing) UpdateViews();
            }
        }
        void UpdateViews()
        {
            if (details != null && !details.IsDisposed) details.RefreshData();
            Icon nextIcon = BudgetIcon.Create(HasMonth ? (decimal?)Snapshot.Month.Cost : null, Store.Settings.MonthlyBudget, Failed);
            Icon oldIcon = icon;
            tray.Icon = nextIcon; icon = nextIcon; oldIcon.Dispose();
            tray.Text = Budget.Tooltip(HasToday ? Snapshot.Today : null, HasMonth ? Snapshot.Month : null, Store.Settings.MonthlyBudget, Failed);

        }
        void OnPowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && !closing && dispatcher.IsHandleCreated) dispatcher.BeginInvoke(new Action(delegate { NextRefreshUtc = DateTime.MinValue; }));
        }
        protected override void ExitThreadCore()
        {
            closing = true;
            timer.Stop(); timer.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerMode;
            tray.Visible = false; tray.Dispose(); icon.Dispose();
            client.Dispose();
            if (details != null) { details.AllowClose = true; details.Close(); details.Dispose(); }
            dispatcher.Dispose();
            base.ExitThreadCore();
        }
    }

    public sealed class SummaryCard : Panel
    {
        readonly Label title = Theme.Label("", 11, Theme.Muted);
        readonly Label cost = Theme.Label("—", 27, Theme.Accent, FontStyle.Bold);
        readonly Label volume = Theme.Label("", 11);
        readonly Label average = Theme.Label("", 9, Theme.Muted);
        public SummaryCard()
        {
            Dock = DockStyle.Fill; BackColor = Theme.Surface; Padding = new Padding(18, 15, 16, 14); Margin = new Padding(0);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 59));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(title, 0, 0); layout.Controls.Add(cost, 0, 1); layout.Controls.Add(volume, 0, 2); layout.Controls.Add(average, 0, 3);
            Controls.Add(layout);
        }
        public void Update(string label, Usage data)
        {
            title.Text = label;
            cost.Text = data == null ? "—" : Format.Money(data.Cost);
            volume.Text = data == null ? "等待查询" : Format.Tokens(data.Tokens) + " tokens    ·    " + data.Requests.ToString("N0") + " 次请求";
            average.Text = data == null ? "" : "平均每次  " + Format.AverageCost(data) + "  /  " + Format.AverageTokens(data) + " tokens";
        }
    }

    public sealed class DetailsForm : Form
    {
        readonly UsageApplication app;
        readonly Label status = Theme.Label("", 9, Theme.Muted);
        readonly SummaryCard today = new SummaryCard();
        readonly SummaryCard month = new SummaryCard();
        readonly BudgetPanel budgetPanel = new BudgetPanel();
        readonly ComboBox period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        readonly DataGridView grid = new DataGridView();
        readonly Button refresh = Theme.Button("刷新");
        readonly Label hint = Theme.Label("", 9, Theme.Muted);
        public bool AllowClose;
        public DetailsForm(UsageApplication context)
        {
            app = context;
            Text = "LLM 用量 · 详情";
            BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font();
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(920, 700); MinimumSize = new Size(740, 590);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.Icon();
            Padding = new Padding(22, 18, 22, 16);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, BackColor = Theme.Background };
            foreach (int height in new[] { 46, 32, 188, 80, 49 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.Controls.Add(Theme.Label("LLM 用量", 20, Theme.Text, FontStyle.Bold), 0, 0);
            FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, Margin = Padding.Empty };
            Button settings = Theme.Button("设置"), hide = Theme.Button("收起");
            refresh.Click += delegate { app.Refresh(); }; settings.Click += delegate { app.ShowSettings(); }; hide.Click += delegate { Hide(); };
            buttons.Controls.Add(refresh); buttons.Controls.Add(settings); buttons.Controls.Add(hide); heading.Controls.Add(buttons, 1, 0);
            root.Controls.Add(heading, 0, 0);
            status.Dock = DockStyle.Fill; status.AutoSize = false; root.Controls.Add(status, 0, 1);
            TableLayoutPanel cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cards.Controls.Add(today, 0, 0); cards.Controls.Add(month, 2, 0); root.Controls.Add(cards, 0, 2);
            root.Controls.Add(budgetPanel, 0, 3);
            TableLayoutPanel section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 13, 0, 6), Margin = Padding.Empty };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); section.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            section.Controls.Add(Theme.Label("模型明细 · 按估算费用排序", 10, Theme.Text, FontStyle.Bold), 0, 0);
            period.Items.AddRange(new object[] { "本月", "今日" }); period.SelectedIndex = 0; period.Font = Theme.Font(9); period.SelectedIndexChanged += delegate { PopulateModels(); };
            section.Controls.Add(period, 1, 0); root.Controls.Add(section, 0, 4);
            ConfigureGrid(); root.Controls.Add(grid, 0, 5);
            hint.AutoSize = false; hint.Dock = DockStyle.Fill; hint.Padding = new Padding(0, 10, 0, 0); root.Controls.Add(hint, 0, 6);
            Controls.Add(root);
            KeyPreview = true; KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
            Shown += delegate {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, work.Width - 24), Math.Min(Height, work.Height - 24));
                Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
            };
        }
        void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = Theme.Surface; grid.BorderStyle = BorderStyle.None; grid.GridColor = Theme.Border;
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersHeight = 34; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Muted, Font = Theme.Font(9), SelectionBackColor = Theme.Surface };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Color.FromArgb(40, 72, 94), SelectionForeColor = Theme.Text, Font = Theme.Font(9), Padding = new Padding(4, 0, 4, 0) };
            grid.RowTemplate.Height = 32;
            string[] names = { "模型", "请求数", "总 token", "输入 token", "输出 token", "估算费用", "费用占比" };
            int[] widths = { 186, 65, 130, 114, 114, 91, 83 };
            for (int i = 0; i < names.Length; i++) {
                int index = grid.Columns.Add("c" + i, names[i]);
                grid.Columns[index].Width = widths[i];
                grid.Columns[index].SortMode = DataGridViewColumnSortMode.NotSortable;
                if (i > 0) grid.Columns[index].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; grid.Columns[0].MinimumWidth = 170;
        }
        public void RefreshData()
        {
            DateTime local = Calendar.LocalNow;
            today.Update("今日 · " + local.ToString("M月d日"), app.HasToday ? app.Snapshot.Today : null);
            month.Update("本月 · " + local.ToString("yyyy年M月"), app.HasMonth ? app.Snapshot.Month : null);
            budgetPanel.UpdateBudget(app.Store.Settings.MonthlyBudget, app.HasMonth ? (decimal?)app.Snapshot.Month.Cost : null, app.Failed);
            status.ForeColor = app.Failed ? Theme.Warn : Theme.Muted;
            string updated = app.Snapshot != null ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(app.Snapshot.UpdatedUtc, DateTimeKind.Utc), Calendar.Zone).ToString("MM-dd HH:mm:ss") : "—";
            status.Text = app.Status + "    ·    上次成功更新 " + updated + "    ·    每 " + app.Store.Settings.RefreshMinutes + " 分钟刷新" + (app.Failed && app.Snapshot != null ? "    ·    当前为旧数据" : "");
            refresh.Enabled = !app.Busy;
            string application = app.Snapshot != null ? app.Snapshot.Month.Application : "等待查询";
            hint.Text = "应用：" + application + "    ·    日期按北京时间 UTC+8，金额为美元估算费用。\n费用与 TokenOps 预算账单可能不同；输入与输出之和不一定等于总 token。";
            PopulateModels();
        }
        void PopulateModels()
        {
            if (grid.Columns.Count == 0) return;
            grid.Rows.Clear();
            Usage usage = period.SelectedIndex == 1 ? (app.HasToday ? app.Snapshot.Today : null) : (app.HasMonth ? app.Snapshot.Month : null);
            if (usage == null) return;
            foreach (ModelUsage model in usage.Models.OrderByDescending(m => m.Cost).ThenBy(m => m.Model, StringComparer.Ordinal)) {
                int index = grid.Rows.Add(model.Model, model.Requests.ToString("N0"), model.Tokens.ToString("N0"), model.PromptTokens.ToString("N0"), model.CompletionTokens.ToString("N0"), Format.Money(model.Cost), Format.Share(model.Cost, usage.Cost).ToString("0.0") + "%");
                grid.Rows[index].Cells[0].ToolTipText = "网关分类：" + model.Category + "\n完整 token 数按接口返回值展示。";
            }
            grid.ClearSelection();
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
    }

    public sealed class BudgetPanel : Panel
    {
        readonly Label total = Theme.Label("", 10, Theme.Muted);
        readonly Label used = Theme.Label("", 10);
        readonly Label remaining = Theme.Label("", 10);
        readonly Panel track = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border };
        decimal percent;
        Color fill = Theme.Accent;
        public BudgetPanel()
        {
            Dock = DockStyle.Fill; Padding = new Padding(0, 12, 0, 0); Margin = Padding.Empty;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 3, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            total.AutoSize = used.AutoSize = remaining.AutoSize = false;
            total.Dock = used.Dock = remaining.Dock = DockStyle.Fill;
            used.TextAlign = ContentAlignment.TopCenter; remaining.TextAlign = ContentAlignment.TopRight;
            layout.Controls.Add(total, 0, 0); layout.Controls.Add(used, 1, 0); layout.Controls.Add(remaining, 2, 0);
            layout.Controls.Add(track, 0, 1); layout.SetColumnSpan(track, 3); Controls.Add(layout);
            track.Paint += delegate(object sender, PaintEventArgs e) {
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, 0, 0, track.Width * (float)Math.Max(0, Math.Min(100, percent)) / 100f, track.Height);
            };
        }
        public void UpdateBudget(decimal limit, decimal? cost, bool stale)
        {
            total.Text = "月度参考预算（自设）\n" + Format.Money(limit);
            percent = cost.HasValue ? Budget.RemainingPercent(cost.Value, limit) : 0;
            fill = cost.HasValue ? BudgetIcon.ColorForRemaining(percent, stale) : Theme.Muted;
            used.ForeColor = remaining.ForeColor = fill;
            used.Text = cost.HasValue ? "已用 " + Budget.UsedPercent(cost.Value, limit).ToString("0.0") + "%\n" + Format.Money(cost.Value) : "已用 —\n等待查询";
            remaining.Text = cost.HasValue ? "剩余 " + percent.ToString("0.0") + "%\n" + Format.Money(Budget.RemainingMoney(cost.Value, limit)) : "剩余 —";
            track.Invalidate();
        }
    }

    public sealed class SettingsForm : Form
    {
        readonly Store store;
        readonly TextBox user = new TextBox();
        readonly TextBox key = new TextBox { UseSystemPasswordChar = true };
        readonly NumericUpDown interval = new NumericUpDown { Minimum = 1, Maximum = 60 };
        readonly NumericUpDown budget = new NumericUpDown { Minimum = 0.01m, Maximum = 1000000000m, DecimalPlaces = 2, ThousandsSeparator = true, Increment = 100m };
        public SettingsForm(Store data)
        {
            store = data;
            Text = "LLM 用量 · 设置"; BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font();
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 390); Padding = new Padding(22);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            user.Text = store.Settings.User; interval.Value = store.Settings.RefreshMinutes; budget.Value = store.Settings.MonthlyBudget;
            layout.Controls.Add(Theme.Label("user 请求头"), 0, 0); layout.Controls.Add(user, 1, 0);
            layout.Controls.Add(Theme.Label("API key"), 0, 1); layout.Controls.Add(key, 1, 1);
            layout.Controls.Add(Theme.Label("刷新间隔"), 0, 2); layout.Controls.Add(interval, 1, 2);
            layout.Controls.Add(Theme.Label("月度预算 USD"), 0, 3); layout.Controls.Add(budget, 1, 3);
            user.Dock = key.Dock = interval.Dock = budget.Dock = DockStyle.Top;
            Label keyHint = Theme.Label(String.IsNullOrWhiteSpace(store.Key) ? "请输入密钥。刷新间隔单位：分钟。" : "API key 留空保留现有密钥。间隔单位：分钟。", 9, Theme.Muted);
            layout.Controls.Add(keyHint, 0, 4); layout.SetColumnSpan(keyHint, 2);
            Label security = Theme.Label("密钥使用 Windows DPAPI 加密，仅当前 Windows 用户可解密。\n接口：https://llm-api.amd.com/api/UsageStats\n预算为自行设置的参考值；超过预算仍继续统计。", 9, Theme.Muted); security.AutoSize = false; security.Dock = DockStyle.Fill;
            layout.Controls.Add(security, 0, 5); layout.SetColumnSpan(security, 2);
            FlowLayoutPanel buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            Button save = Theme.Button("保存"), cancel = Theme.Button("取消");
            cancel.DialogResult = DialogResult.Cancel; save.Click += Save;
            buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        }
        void Save(object sender, EventArgs e)
        {
            string newKey = key.Text.Trim();
            string newUser = user.Text.Trim();
            if (newKey.Length == 0 && String.IsNullOrWhiteSpace(store.Key)) { MessageBox.Show(this, "请输入 API key。", "设置"); return; }
            if (newKey.Contains("\r") || newKey.Contains("\n") || newUser.Contains("\r") || newUser.Contains("\n")) { MessageBox.Show(this, "请求头不能包含换行符。", "设置"); return; }
            try {
                Settings settings = new Settings { User = newUser, RefreshMinutes = (int)interval.Value, MonthlyBudget = budget.Value };
                store.SaveSettings(settings, newKey.Length > 0 ? newKey : null);
                DialogResult = DialogResult.OK; Close();
            } catch { MessageBox.Show(this, "设置无法保存，请检查本机数据目录权限。", "设置"); }
        }
    }
}
