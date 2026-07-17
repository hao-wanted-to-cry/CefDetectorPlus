# 喜报 · Chromium 内核应用检测器 (GoodNewsBrowserAppDetector)

一个用 C# WinUI 3 开发的 Windows 桌面应用，自动扫描系统已安装的桌面应用程序，识别出哪些是基于 Chromium 内核开发的（如 LibCEF、Electron、CefSharp、NW.js、MiniBlink、MiniElectron、WebView2、Edge、Chrome、Qt WebEngine 等），并以喜庆的"喜报"风格展示结果。

## 运行环境

- Windows 10 1809+ / Windows 11
- .NET 10 SDK
- Windows App SDK 2.2

## 功能

- **全盘扫描**：扫描注册表 + 所有硬盘驱动器的 Program Files 目录
- **多引擎识别**：LibCEF、Electron、CefSharp、NW.js、MiniBlink、MiniElectron、WebView2、Edge、Chrome、Qt WebEngine、Chromium
- **按大小排序**：结果按占用空间从大到小排列
- **逐秒动画**：卡片以每秒一个的速度逐条显示
- **喜庆 UI**：喜报背景、金色标题（白色描边）、液态玻璃卡片
- **音乐控制**：右下角圆形音量按钮，点击弹出垂直滑块
- **命令行参数**：支持 `--no-bgm` 关闭背景音乐
- **窗口适配**：窗口大小按图片比例自动调整，图片支持最大化裁剪

## 构建

```bash
dotnet restore
dotnet build -c Release
dotnet run
```

## 使用

```bash
# 正常运行（带背景音乐）
dotnet run

# 关闭背景音乐
dotnet run -- --no-bgm
```

在 Visual Studio 中打开 `GoodNewsBrowserAppDetector.sln` 即可。

## 命令行参数

| 参数 | 说明 |
|------|------|
| `--no-bgm` | 关闭背景音乐 |

## 项目结构

```
GoodNewsBrowserAppDetector/
├── GoodNewsBrowserAppDetector.csproj
├── GoodNewsBrowserAppDetector.sln
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs
├── Models/
│   └── BrowserBasedApp.cs
├── Services/
│   └── AppDetector.cs
├── Assets/
│   ├── goodnews_bg.png
│   └── goodnews_music.aac
├── app.manifest
└── .gitignore
```

## 参考

- [CefDetectorX](https://github.com/ShirasawaSama/CefDetectorX)

## 许可

MIT