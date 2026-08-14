# DSH 浏览器（DshBrowser）

> 一个极简的 DeepSeek Harness 专用浏览器外壳：只打开 `http://127.0.0.1:3080`，
> 与其他浏览器（Chrome / Edge）完全区分开。

## 特性

- 🐟 **独立身份**：大肥鱼图标、独立窗口标题「DSH 浏览器」，不再混在浏览器标签里
- 🗂️ **完全隔离**：独立的 WebView2 数据目录（`%LOCALAPPDATA%\DshBrowser`），
  登录态与存储和 Chrome / Edge 互不影响
- 🚀 **自动拉起**：启动时若 dsh web 未运行，自动调用 `start-dsh-web.ps1` 拉起
  并等待就绪（实测：停止 web 后打开本程序，约 15 秒自动恢复），超时 50 秒给出提示
- 🔗 **链接拦截**：页面内的外部链接交给系统默认浏览器，本窗口只服务 DSH
- 📶 **连接状态条**：底部状态栏实时显示连接状态（绿=已连接 / 红=失败），
  加载失败时可一键「重新连接」
- 🪶 **轻量**：WebView2（Edge 内核）运行时随 Windows 自带，exe 仅约 190 KB

## 操作

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+R` / `F5` | 刷新 |
| `Ctrl+H` | 回到主页（http://127.0.0.1:3080） |

关闭窗口只退出浏览器，**不影响 dsh web 进程**。

## 环境要求

- Windows 10/11（含 WebView2 Runtime，Windows 11 自带，旧系统可从
  [Microsoft 官网](https://developer.microsoft.com/microsoft-edge/webview2/) 安装）
- .NET 9 SDK 或运行时（构建需要 SDK；运行发布产物只需 Runtime）
- dsh（DeepSeek Harness）web profile 已配置（`dsh web` 监听 3080）

## 构建

```powershell
cd src\dsh-browser
dotnet restore --configfile NuGet.Config   # 项目自带 NuGet.Config，绕过本机失效的本地源
dotnet publish -c Release -r win-x64 --self-contained false --configfile NuGet.Config
# 产物: bin\Release\net9.0-windows\win-x64\publish\DshBrowser.exe
```

> 说明：`NuGet.Config` 使用 `<clear/>` 只保留 nuget.org 官方源——
> 若你的机器 NuGet 配置链里有失效的本地源（如 `D:\VSSDK\NuGetPackages`），
> 必须用 `--configfile` 指定它才能 restore 成功。

## 部署

- 发布产物直接拷到任意目录（exe + 同目录 DLL 一起）
- 可选：为 `DshBrowser.exe` 创建桌面快捷方式
- 自动拉起依赖脚本 `%USERPROFILE%\.dsh\src\scripts\start-dsh-web.ps1`
  （程序运行时按 `%USERPROFILE%` 动态解析该路径，无需改代码；换机器只要
  该脚本存在于相同相对位置即可）

## 技术要点

- WinForms + `Microsoft.Web.WebView2`（1.0.2903.40）
- `CoreWebView2Environment.CreateAsync(userDataFolder: ...)` 指定独立用户数据目录
- 端口探测用 `TcpClient.Connect("127.0.0.1", 3080)`；未就绪时轮询（800ms 间隔）
- `NewWindowRequested` 事件把外部链接转交系统默认浏览器（`UseShellExecute = true`）

## 许可证

BSD-3-Clause
