using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshBrowser;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

/// <summary>
/// DSH 专用浏览器外壳：仅加载 DeepSeek Harness Web GUI（http://127.0.0.1:3080）。
/// web 未运行时自动调用 start-dsh-web.ps1 拉起；独立 WebView2 数据目录，与其他浏览器完全隔离。
/// </summary>
internal sealed class MainForm : Form
{
    private const string HomeUrl = "http://127.0.0.1:3080";
    private const int Port = 3080;

    /// <summary>启动脚本路径：%USERPROFILE%\.dsh\src\scripts\start-dsh-web.ps1（动态解析，不写死用户目录）。</summary>
    private static string StartScript =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "src", "scripts", "start-dsh-web.ps1");

    private readonly WebView2 _web = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripButton _retryButton = new("重新连接");
    private bool _startupRequested;
    private bool _loaded;

    public MainForm()
    {
        Text = "DSH 浏览器";
        Icon = LoadAppIcon();
        Width = 1400;
        Height = 900;
        MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;

        // 底部状态条：连接状态 + 重新连接按钮
        _status.Items.Add(_statusLabel);
        _status.Items.Add(new ToolStripSpring());
        _status.Items.Add(_retryButton);
        _retryButton.Visible = false;
        _retryButton.Click += (_, _) =>
        {
            _retryButton.Visible = false;
            ReloadAsync();
        };
        _status.Dock = DockStyle.Bottom;
        Controls.Add(_status);

        // WebView2 铺满剩余区域
        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        KeyPreview = true;
        KeyDown += OnKeyDown;
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshBrowser");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, args) =>
            {
                // 外部链接交给系统默认浏览器，本窗口只服务 DSH
                args.Handled = true;
                if (!string.IsNullOrEmpty(args.Uri))
                    Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
            };
            core.NavigationCompleted += OnNavigationCompleted;

            await EnsureWebAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"初始化失败: {ex.Message}", false);
        }
    }

    /// <summary>确保 dsh web 在线后加载主页。</summary>
    private async Task EnsureWebAsync()
    {
        if (IsPortOpen())
        {
            LoadHomeAsync();
            return;
        }
        if (_startupRequested)
        {
            await WaitForWebAsync(50);
            return;
        }
        _startupRequested = true;
        SetStatus("dsh web 未运行，正在自动启动…", false);
        var launchKind = TryStartDshWeb();
        if (launchKind == "none")
        {
            SetStatus("未找到 dsh：请先安装 DSH（npm i -g @deepseek-ai/dsh）或在终端运行 dsh web，然后点「重新连接」", false);
            _retryButton.Visible = true;
            return;
        }
        await WaitForWebAsync(launchKind == "script" ? 50 : 90);
    }

    private async Task WaitForWebAsync(int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortOpen())
            {
                LoadHomeAsync();
                return;
            }
            await Task.Delay(800);
        }
        SetStatus("等待 dsh web 就绪超时。已安装 dsh 的话请检查安装状态，或手动运行 dsh web 后点「重新连接」", false);
        _retryButton.Visible = true;
    }

    /// <summary>
    /// 尝试拉起 dsh web，返回启动方式：script=外部脚本 / builtin=内置探测启动 / none=未找到 dsh。
    /// 优先使用 %USERPROFILE%\.dsh\src\scripts\start-dsh-web.ps1（本机环境已验证）；
    /// 不存在时内置探测 node + npm 全局 @deepseek-ai/dsh 直接启动，让绿色版开箱即用。
    /// </summary>
    private static string TryStartDshWeb()
    {
        if (File.Exists(StartScript))
        {
            try
            {
                var psi = new ProcessStartInfo("powershell",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + StartScript + "\" -NoBrowser")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
                return "script";
            }
            catch
            {
                // 脚本方式失败，降级到内置探测
            }
        }
        var node = FindOnPath("node.exe");
        if (node == null) return "none";
        var dshBin = FindDshBin();
        if (dshBin == null) return "none";
        try
        {
            var psi = new ProcessStartInfo(node, "\"" + dshBin + "\" web")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            Process.Start(psi);
            return "builtin";
        }
        catch
        {
            return "none";
        }
    }

    /// <summary>在 PATH 中查找可执行文件（node.exe 等）。</summary>
    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var cand = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(cand)) return cand;
            }
            catch
            {
                // 跳过不可访问目录
            }
        }
        return null;
    }

    /// <summary>通过 `npm root -g` 定位 @deepseek-ai/dsh/lib/bin.js。</summary>
    private static string? FindDshBin()
    {
        try
        {
            var psi = new ProcessStartInfo("npm", "root -g")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000)) return null;
            var root = output.Split('\n')[0].Trim();
            if (root.Length == 0) return null;
            var cand = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
            return File.Exists(cand) ? cand : null;
        }
        catch
        {
            return null;
        }
    }

    private void LoadHomeAsync()
    {
        SetStatus("已连接 dsh web", true);
        try
        {
            _web.CoreWebView2.Navigate(HomeUrl);
        }
        catch (Exception ex)
        {
            SetStatus($"导航失败: {ex.Message}", false);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _loaded = e.IsSuccess;
        if (e.IsSuccess)
        {
            SetStatus("已连接 dsh web", true);
            _retryButton.Visible = false;
        }
        else
        {
            SetStatus("页面加载失败（web 可能已停止），Ctrl+R 或点「重新连接」", false);
            _retryButton.Visible = true;
        }
    }

    private async void ReloadAsync()
    {
        if (!_loaded)
        {
            _startupRequested = false;
            await EnsureWebAsync();
            return;
        }
        try
        {
            _web.CoreWebView2.Reload();
        }
        catch
        {
            // 忽略：下次按键再试
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Control && e.KeyCode == Keys.R) || e.KeyCode == Keys.F5)
        {
            e.Handled = true;
            ReloadAsync();
        }
        else if (e.Control && e.KeyCode == Keys.H)
        {
            e.Handled = true;
            LoadHomeAsync();
        }
    }

    private static bool IsPortOpen()
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", Port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetStatus(string text, bool ok)
    {
        _statusLabel.Text = " ● " + text;
        _statusLabel.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
    }

    private static Icon LoadAppIcon()
    {
        var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(ico))
        {
            try
            {
                return new Icon(ico);
            }
            catch
            {
                // 回退默认图标
            }
        }
        return SystemIcons.Application;
    }
}

/// <summary>状态条弹簧占位，把右侧按钮推到最右。</summary>
internal sealed class ToolStripSpring : ToolStripStatusLabel
{
    public ToolStripSpring()
    {
        Spring = true;
    }
}
