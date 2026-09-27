using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using HttpFileServer.Server;
using QRCoder;

namespace HttpFileServer.UI;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly ServerHost _host = new();

    private readonly TextBox _rootBox = new()
    {
        PlaceholderText = "选择要共享的文件夹",
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly Button _browseBtn = new() { Text = "浏览…" };
    private readonly Button _startBtn = new() { Text = "启动服务", Padding = new Padding(16, 0, 16, 0) };
    private readonly NumericUpDown _portBox = new() { Minimum = 1024, Maximum = 65535, Value = 8600 };
    private readonly ComboBox _nicBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly CheckBox _uploadChk = new() { Text = "允许上传", AutoSize = true, Padding = new Padding(8, 0, 0, 0) };
    private readonly CheckBox _autoStartChk = new() { Text = "自动开始服务", AutoSize = true, Padding = new Padding(8, 0, 0, 0) };
    private readonly LinkLabel _firewallLink = new() { Text = "添加防火墙规则", AutoSize = true, Padding = new Padding(10, 6, 0, 0) };

    private readonly ListView _urlList = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Dock = DockStyle.Fill,
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly PictureBox _qrBox = new()
    {
        SizeMode = PictureBoxSizeMode.Zoom,
        Size = new Size(132, 132),
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
    };
    private readonly ListView _logView = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        Dock = DockStyle.Fill,
    };
    private readonly ListView _uploadView = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        Dock = DockStyle.Fill,
    };
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "已停止", ForeColor = Color.Gray, Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusHint = new() { Text = "", ForeColor = Color.DimGray };

    private readonly NotifyIcon _tray = new();
    private SplitContainer _mainSplit = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private bool _trayHintShown;
    private bool _reallyExit;

    public MainForm(AppSettings settings)
    {
        _settings = settings;

        Text = "HTTP 文件服务器";
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimumSize = new Size(860, 620);
        Size = new Size(1000, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TryLoadIcon();

        BuildLayout();
        BuildTray();
        LoadSettingsToControls();

        _browseBtn.Click += (_, _) => BrowseRoot();
        _startBtn.Click += async (_, _) => await ToggleServerAsync();
        _firewallLink.Click += (_, _) => AddFirewallRule();
        _urlList.DoubleClick += (_, _) => CopySelectedUrl();
        _nicBox.DropDown += (_, _) => RefreshNicList(true);

        _host.Log.Added += e => RunOnUI(() => AddLogEntry(e));
        _host.Uploads.Progress += e => RunOnUI(() => UpdateUploadRow(e));
        _host.Uploads.Finished += e => RunOnUI(() =>
        {
            RemoveUploadRow(e.Id);
            AddLogEntry(new TransferEntry(DateTime.Now, TransferKind.Upload, e.Name, e.Remote,
                e.Bytes, e.Duration, e.Success ? e.Message : $"失败：{e.Message}"));
        });
        _host.Started += port => RunOnUI(() => OnServerStarted(port));
        _host.Stopped += () => RunOnUI(OnServerStopped);

        Load += (_, _) =>
        {
            // SplitterDistance 需要在控件获得最终尺寸后设置，否则会被钳制到最小值
            _mainSplit.SplitterDistance = 224;
            RefreshNicList(false);
            if (_settings.AutoStart)
                _ = ToggleServerAsync();
        };

        FormClosing += OnFormClosing;
    }

    // ---------- 布局 ----------

    private void BuildLayout()
    {
        var rootLabel = new Label { Text = "共享目录：", AutoSize = true, Anchor = AnchorStyles.Left };
        var portLabel = new Label { Text = "端口：", AutoSize = true, Anchor = AnchorStyles.Left };
        var nicLabel = new Label { Text = "监听：", AutoSize = true, Anchor = AnchorStyles.Left };
        _browseBtn.AutoSize = true;
        _startBtn.AutoSize = true;

        var topTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(12, 8, 12, 2),
            Height = 100,
        };
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        topTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        topTable.Controls.Add(rootLabel, 0, 0);
        topTable.Controls.Add(_rootBox, 1, 0);
        var topRight = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        topRight.Controls.Add(_browseBtn);
        topRight.Controls.Add(_startBtn);
        topTable.Controls.Add(topRight, 2, 0);

        var secondRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Height = 38,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0),
        };
        secondRow.Controls.Add(portLabel);
        secondRow.Controls.Add(_portBox);
        secondRow.Controls.Add(nicLabel);
        secondRow.Controls.Add(_nicBox);
        secondRow.Controls.Add(_uploadChk);
        secondRow.Controls.Add(_autoStartChk);
        secondRow.Controls.Add(_firewallLink);
        topTable.Controls.Add(secondRow, 0, 1);
        topTable.SetColumnSpan(secondRow, 3);

        // 访问入口
        _urlList.Columns.Add("访问地址（双击复制）", 420);
        var urlGroup = new GroupBox
        {
            Text = "访问入口",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 8),
        };
        var urlPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
        urlPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        urlPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var qrLabel = new Label { Text = "手机扫码访问", AutoSize = true, Anchor = AnchorStyles.Top };
        var qrPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        qrPanel.Controls.Add(_qrBox);
        qrPanel.Controls.Add(qrLabel);
        var urlHostPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        urlHostPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        urlHostPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        urlHostPanel.Controls.Add(_urlList, 0, 0);
        urlHostPanel.Controls.Add(qrPanel, 1, 0);
        urlPanel.Controls.Add(urlHostPanel, 0, 0);
        urlGroup.Controls.Add(urlPanel);

        // 日志页
        _logView.Columns.Add("时间", 90);
        _logView.Columns.Add("类型", 52);
        _logView.Columns.Add("文件", 340);
        _logView.Columns.Add("对端", 120);
        _logView.Columns.Add("大小", 84);
        _logView.Columns.Add("耗时", 72);
        _logView.Columns.Add("备注", 150);
        var logPage = new TabPage("传输日志");
        logPage.Controls.Add(_logView);

        // 上传任务页
        _uploadView.Columns.Add("文件", 420);
        _uploadView.Columns.Add("进度", 260);
        _uploadView.Columns.Add("速度", 110);
        _uploadView.Columns.Add("对端", 120);
        var uploadPage = new TabPage("上传任务");
        uploadPage.Controls.Add(_uploadView);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(logPage);
        tabs.TabPages.Add(uploadPage);

        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
        };
        mainSplit.Panel1.Controls.Add(urlGroup);
        mainSplit.Panel2.Controls.Add(tabs);
        _mainSplit = mainSplit;

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(_statusHint);

        Controls.Add(mainSplit);
        Controls.Add(topTable);
        Controls.Add(statusStrip);
    }

    private void BuildTray()
    {
        var showItem = new ToolStripMenuItem("显示主窗体", null, (_, _) => RestoreFromTray());
        var openItem = new ToolStripMenuItem("打开主页", null, (_, _) => OpenHomepage());
        var exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApp());
        _trayMenu.Items.AddRange([openItem, showItem, new ToolStripSeparator(), exitItem]);

        _tray.Icon = Icon;
        _tray.Text = "HTTP 文件服务器（已停止）";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void LoadSettingsToControls()
    {
        _rootBox.Text = _settings.RootPath;
        _portBox.Value = _settings.Port is >= 1024 and <= 65535 ? _settings.Port : 8600;
        _uploadChk.Checked = _settings.AllowUpload;
        _autoStartChk.Checked = _settings.AutoStart;
        RefreshNicList(false);
        if (!string.IsNullOrEmpty(_settings.ListenAddress))
        {
            foreach (var item in _nicBox.Items.Cast<NicItem>())
            {
                if (item.Ip == _settings.ListenAddress)
                {
                    _nicBox.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private sealed record NicItem(string Ip, string Display)
    {
        public override string ToString() => Display;
    }

    private void RefreshNicList(bool keepSelection)
    {
        var selected = keepSelection ? (_nicBox.SelectedItem as NicItem)?.Ip : null;
        _nicBox.Items.Clear();
        _nicBox.Items.Add(new NicItem("", "所有网卡 (0.0.0.0)"));
        foreach (var nic in NetworkService.GetLanAddresses())
            _nicBox.Items.Add(new NicItem(nic.Ip, $"{nic.Ip}  ({nic.Name})"));
        _nicBox.SelectedIndex = 0;
        if (selected is not null)
        {
            foreach (var item in _nicBox.Items.Cast<NicItem>())
            {
                if (item.Ip == selected)
                {
                    _nicBox.SelectedItem = item;
                    break;
                }
            }
        }
    }

    // ---------- 启动 / 停止 ----------

    private async Task ToggleServerAsync()
    {
        if (_host.IsRunning)
        {
            _startBtn.Enabled = false;
            await _host.StopAsync();
            _startBtn.Enabled = true;
            return;
        }

        var root = _rootBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            MessageBox.Show("请选择一个存在的共享目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        root = Path.GetFullPath(root);
        _rootBox.Text = root;

        var requestedPort = (int)_portBox.Value;
        var listen = (_nicBox.SelectedItem as NicItem)?.Ip ?? "";
        var port = NetworkService.FindAvailablePort(requestedPort, listen);

        _startBtn.Enabled = false;
        try
        {
            _host.Start(root, port, listen, _uploadChk.Checked);
        }
        catch (Exception ex)
        {
            _startBtn.Enabled = true;
            MessageBox.Show("启动失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (port != requestedPort)
        {
            _host.Log.Add(new TransferEntry(DateTime.Now, TransferKind.Notice, "", "", 0, TimeSpan.Zero,
                $"端口 {requestedPort} 被占用，已自动改用 {port}"));
            _portBox.Value = port;
        }

        SaveSettingsFromControls();
        _startBtn.Enabled = true;
    }

    private void OnServerStarted(int port)
    {
        _startBtn.Text = "停止服务";
        _rootBox.Enabled = false;
        _portBox.Enabled = false;
        _nicBox.Enabled = false;
        _uploadChk.Enabled = false;

        var listen = (_nicBox.SelectedItem as NicItem)?.Ip ?? "";
        var urls = new List<string>();
        if (listen.Length == 0)
        {
            var nics = NetworkService.GetLanAddresses();
            var primary = NetworkService.PickPrimary(nics);
            if (primary is null)
                urls.Add($"http://127.0.0.1:{port}/");
            else
            {
                urls.Add($"http://{primary}:{port}/");
                foreach (var nic in nics.Where(n => n.Ip != primary))
                    urls.Add($"http://{nic.Ip}:{port}/");
                urls.Add($"http://127.0.0.1:{port}/");
            }
        }
        else
        {
            urls.Add($"http://{listen}:{port}/");
            urls.Add($"http://127.0.0.1:{port}/");
        }

        _urlList.BeginUpdate();
        _urlList.Items.Clear();
        foreach (var url in urls)
            _urlList.Items.Add(new ListViewItem(url) { ForeColor = Color.RoyalBlue });
        _urlList.EndUpdate();

        UpdateQr(urls[0]);

        _statusLabel.Text = $"● 运行中  http://*:{port}/  共享：{_host.RootPath}";
        _statusLabel.ForeColor = Color.ForestGreen;
        _statusHint.Text = $"本进程 PID：{Environment.ProcessId}";
        _tray.Text = $"HTTP 文件服务器（运行中 :{port}）";

        if (!_trayHintShown)
        {
            _tray.BalloonTipTitle = "服务已启动";
            _tray.BalloonTipText = $"局域网内设备可通过 {urls[0]} 访问共享文件。";
            _tray.ShowBalloonTip(3000);
            _trayHintShown = true;
        }

        if ( FirewallNeedsRule(port))
            _firewallLink.ForeColor = Color.Firebrick;
    }

    private void OnServerStopped()
    {
        _startBtn.Text = "启动服务";
        _rootBox.Enabled = true;
        _portBox.Enabled = true;
        _nicBox.Enabled = true;
        _uploadChk.Enabled = true;

        _urlList.Items.Clear();
        _qrBox.Image?.Dispose();
        _qrBox.Image = null;

        _statusLabel.Text = "已停止";
        _statusLabel.ForeColor = Color.Gray;
        _statusHint.Text = "";
        _tray.Text = "HTTP 文件服务器（已停止）";
        _firewallLink.ForeColor = SystemColors.HotTrack;
    }

    private void UpdateQr(string url)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            using var code = new QRCode(data);
            var bmp = code.GetGraphic(4, Color.FromArgb(30, 30, 30), Color.White, drawQuietZones: true);
            _qrBox.Image?.Dispose();
            _qrBox.Image = bmp;
        }
        catch
        {
            // 二维码生成失败不影响服务
        }
    }

    private bool FirewallNeedsRule(int port)
    {
        // 简单探测：若已有同名规则则不提醒（netsh 查询太重，仅按首次添加标记）
        return !FirewallRuleAdded;
    }

    private static bool FirewallRuleAdded
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(AppSettings.SettingsDir, "firewall.ok"));
            }
            catch
            {
                return false;
            }
        }
    }

    private void AddFirewallRule()
    {
        var port = _host.IsRunning ? _host.Port : (int)_portBox.Value;
        var args = $"advfirewall firewall add rule name=\"HttpFileServer\" dir=in action=allow protocol=TCP localport={port}";
        bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = true,
            CreateNoWindow = true,
        };
        if (!isAdmin)
            psi.Verb = "runas";

        try
        {
            var p = Process.Start(psi);
            if (p != null && p.WaitForExit(15000) && p.ExitCode == 0)
            {
                _host.Log.Add(new TransferEntry(DateTime.Now, TransferKind.Notice, "", "", 0, TimeSpan.Zero, "防火墙放行规则已添加"));
                _firewallLink.ForeColor = SystemColors.HotTrack;
                try
                {
                    Directory.CreateDirectory(AppSettings.SettingsDir);
                    File.WriteAllText(Path.Combine(AppSettings.SettingsDir, "firewall.ok"), port.ToString());
                }
                catch
                {
                }
            }
            else
            {
                _host.Log.Add(new TransferEntry(DateTime.Now, TransferKind.Notice, "", "", 0, TimeSpan.Zero, "防火墙规则添加失败，可稍后重试"));
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户拒绝了 UAC 提权
        }
        catch (Exception ex)
        {
            MessageBox.Show("添加防火墙规则失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- 日志 / 上传任务 ----------

    private void AddLogEntry(TransferEntry e)
    {
        var item = new ListViewItem(e.Time.ToString("HH:mm:ss"))
        {
            ForeColor = e.Kind switch
            {
                TransferKind.Upload => Color.Teal,
                TransferKind.Download => Color.RoyalBlue,
                TransferKind.Zip => Color.Purple,
                TransferKind.Notice => Color.DarkOrange,
                _ => SystemColors.WindowText,
            },
        };
        item.SubItems.Add(e.Kind switch
        {
            TransferKind.Upload => "上传",
            TransferKind.Download => "下载",
            TransferKind.Zip => "打包",
            _ => "提示",
        });
        item.SubItems.Add(e.Kind == TransferKind.Notice ? "" : e.FileName);
        item.SubItems.Add(e.Remote);
        item.SubItems.Add(e.Bytes > 0 ? HumanBytes(e.Bytes) : "");
        item.SubItems.Add(e.Duration > TimeSpan.Zero ? HumanDuration(e.Duration) : "");
        item.SubItems.Add(e.Detail);
        _logView.Items.Insert(0, item);
        while (_logView.Items.Count > 500)
            _logView.Items.RemoveAt(_logView.Items.Count - 1);
    }

    private readonly Dictionary<string, (DateTime Time, long Bytes)> _lastProgress = new();

    private void UpdateUploadRow(UploadProgressEventArgs e)
    {
        var item = _uploadView.Items.Cast<ListViewItem>().FirstOrDefault(i => (string?)i.Tag == e.Id);
        if (item is null)
        {
            item = new ListViewItem(e.Name) { Tag = e.Id };
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add("");
            _uploadView.Items.Insert(0, item);
        }
        var pct = e.Total > 0 ? (double)e.Received / e.Total : 0;
        item.SubItems[1].Text = $"{pct:P1}  ({HumanBytes(e.Received)} / {HumanBytes(e.Total)})";

        var speed = "";
        if (_lastProgress.TryGetValue(e.Id, out var prev) && e.Received > prev.Bytes)
        {
            var dt = (DateTime.Now - prev.Time).TotalSeconds;
            if (dt > 0.2)
                speed = $"{HumanBytes((long)((e.Received - prev.Bytes) / dt))}/s";
        }
        _lastProgress[e.Id] = (DateTime.Now, e.Received);
        item.SubItems[2].Text = speed;
        item.SubItems[3].Text = e.Remote;
    }

    private void RemoveUploadRow(string id)
    {
        var item = _uploadView.Items.Cast<ListViewItem>().FirstOrDefault(i => (string?)i.Tag == id);
        item?.Remove();
    }

    // ---------- 托盘 / 窗体行为 ----------

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyExit || e.CloseReason != CloseReason.UserClosing)
        {
            SaveSettingsFromControls();
            _tray.Visible = false;
            _ = _host.StopAsync();
            return;
        }

        if (_settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            WindowState = FormWindowState.Minimized;
            if (!_trayHintShown)
            {
                _tray.BalloonTipTitle = "仍在后台运行";
                _tray.BalloonTipText = "程序已最小化到系统托盘，双击托盘图标可重新打开。";
                _tray.ShowBalloonTip(3000);
                _trayHintShown = true;
            }
        }
        else
        {
            ExitApp();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        SaveSettingsFromControls();
        Close();
    }

    private void OpenHomepage()
    {
        var url = _urlList.Items.Count > 0 ? _urlList.Items[0].Text : null;
        if (url is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void CopySelectedUrl()
    {
        if (_urlList.SelectedItems.Count == 0)
            return;
        try
        {
            Clipboard.SetText(_urlList.SelectedItems[0].Text);
            _statusHint.Text = "链接已复制到剪贴板";
        }
        catch
        {
        }
    }

    private void SaveSettingsFromControls()
    {
        _settings.RootPath = _rootBox.Text.Trim();
        _settings.Port = (int)_portBox.Value;
        _settings.ListenAddress = (_nicBox.SelectedItem as NicItem)?.Ip ?? "";
        _settings.AllowUpload = _uploadChk.Checked;
        _settings.AutoStart = _autoStartChk.Checked;
        _settings.Save();
    }

    private void BrowseRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要共享的文件夹",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
        };
        if (Directory.Exists(_rootBox.Text))
            dialog.SelectedPath = _rootBox.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _rootBox.Text = dialog.SelectedPath;
    }

    // ---------- 工具 ----------

    private void RunOnUI(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Icon TryLoadIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    internal static string HumanBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int u = 0;
        while (value >= 1024 && u < units.Length - 1)
        {
            value /= 1024;
            u++;
        }
        return $"{value:0.##} {units[u]}";
    }

    private static string HumanDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.#}s" : $"{t.TotalMinutes:0}m{t.Seconds:00}s";
}
