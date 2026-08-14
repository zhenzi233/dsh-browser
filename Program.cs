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
        // 全局异常兜底：任何未处理异常都写入日志文件（%LOCALAPPDATA%\DshBrowser\crash.log），
        // UI 线程异常尝试恢复，进程级异常记录后退出——绝不静默消失。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log("UI 线程异常: " + e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log("进程级异常: " + e.ExceptionObject);
        };
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Log("启动 DSH 浏览器 " + Application.ProductVersion);
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Log("主循环异常: " + ex);
            throw;
        }
    }

    /// <summary>追加一行运行日志到 %LOCALAPPDATA%\DshBrowser\dsh-browser.log。</summary>
    internal static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshBrowser");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "dsh-browser.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响运行
        }
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
        FormClosing += (_, _) => Program.Log("窗口关闭（正常退出）");

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
                try
                {
                    // 外部链接交给系统默认浏览器，本窗口只服务 DSH
                    args.Handled = true;
                    if (!string.IsNullOrEmpty(args.Uri))
                        Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Program.Log("新窗口请求处理异常: " + ex.Message);
                }
            };
            core.ProcessFailed += (_, e) =>
            {
                // 渲染/GPU 等子进程崩溃：不关窗口，记录日志并尝试自动恢复
                Program.Log("WebView2 子进程失败: " + e.ProcessFailedKind);
                try
                {
                    SetStatus("渲染进程异常，正在自动恢复…", false);
                    _web.CoreWebView2.Reload();
                }
                catch (Exception ex)
                {
                    Program.Log("自动恢复失败: " + ex.Message);
                }
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
            SetStatus("未找到 dsh（" + DiagnoseEnvironment() + "）。请安装 DSH 或在终端运行 dsh web 后点「重新连接」", false);
            _retryButton.Visible = true;
            return;
        }
        await WaitForWebAsync(launchKind == "script" ? 50 : 90);
    }

    /// <summary>生成排障诊断摘要：node / dsh 命令 / npm 全局各自是否存在。</summary>
    private static string DiagnoseEnvironment()
    {
        var node = FindOnPath("node.exe") != null;
        var dshCmd = FindOnPath("dsh.cmd") != null || FindOnPath("dsh.ps1") != null || FindOnPath("dsh") != null;
        var npmGlobal = FindDshBin() != null;
        return $"node:{Bool(node)} dsh命令:{Bool(dshCmd)} npm全局:{Bool(npmGlobal)}";
    }

    private static string Bool(bool value) => value ? "有" : "无";

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
    /// 探测顺序：外部脚本 → node + npm 全局 @deepseek-ai/dsh → PATH 中的 dsh 命令（cmd /c）→ 常见 npm 全局根。
    /// </summary>
    private static string TryStartDshWeb()
    {
        // 1. 外部脚本（本机完整 .dsh 环境）
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
        // 2. node + npm 全局 @deepseek-ai/dsh（标准 npm 安装）
        var node = FindOnPath("node.exe");
        var dshBin = FindDshBin();
        if (node != null && dshBin != null)
        {
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
                return "builtin-npm";
            }
            catch
            {
                // 继续降级
            }
        }
        // 3. PATH 中的 dsh 命令（终端能跑 dsh 就一定存在；兼容 pnpm/volta/克隆仓库等任意安装方式）
        if (FindOnPath("dsh.cmd") != null || FindOnPath("dsh.ps1") != null || FindOnPath("dsh") != null)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c dsh web")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                };
                Process.Start(psi);
                return "builtin-cmd";
            }
            catch
            {
                // 继续降级
            }
        }
        // 4. 常见 npm 全局根兜底（%APPDATA%\npm\node_modules）
        if (node != null)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "npm", "node_modules");
                var cand = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(cand))
                {
                    var psi = new ProcessStartInfo(node, "\"" + cand + "\" web")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    };
                    Process.Start(psi);
                    return "builtin-fallback";
                }
            }
            catch
            {
                // 兜底失败
            }
        }
        return "none";
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

    /// <summary>
    /// 定位 @deepseek-ai/dsh/lib/bin.js：npm 全局 → pnpm 全局 → %LOCALAPPDATA%\pnpm\global 搜索。
    /// Windows 上 npm/pnpm 是 .cmd 脚本，必须经 cmd.exe /c 调用。
    /// </summary>
    private static string? FindDshBin()
    {
        // 1. npm root -g
        var root = RunGlobalRoot("npm");
        if (root != null)
        {
            var cand = Path.Combine(root, "@deepseek-ai", "dsh", "lib", "bin.js");
            if (File.Exists(cand)) return cand;
        }
        // 2. pnpm root -g（若 pnpm 在 PATH）
        if (FindOnPath("pnpm.cmd") != null || FindOnPath("pnpm") != null)
        {
            var proot = RunGlobalRoot("pnpm");
            if (proot != null)
            {
                var cand = Path.Combine(proot, "@deepseek-ai", "dsh", "lib", "bin.js");
                if (File.Exists(cand)) return cand;
            }
        }
        // 3. %LOCALAPPDATA%\pnpm\global 下常见布局搜索（pnpm 7/8 的 global/5 等）
        try
        {
            var pnpmBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pnpm", "global");
            if (Directory.Exists(pnpmBase))
            {
                foreach (var vdir in Directory.GetDirectories(pnpmBase))
                {
                    var cand = Path.Combine(vdir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(cand)) return cand;
                }
            }
        }
        catch
        {
            // 忽略搜索失败
        }
        return null;
    }

    // 运行 "npm root -g" / "pnpm root -g" 并返回首行输出（全局 node_modules 根），失败返回 null。
    private static string? RunGlobalRoot(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd + " root -g")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(8000)) return null;
            var root = output.Split('\n')[0].Trim();
            return root.Length == 0 ? null : root;
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
        try
        {
            if (!_loaded)
            {
                _startupRequested = false;
                await EnsureWebAsync();
                return;
            }
            _web.CoreWebView2.Reload();
        }
        catch (Exception ex)
        {
            Program.Log("刷新异常: " + ex.Message);
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
