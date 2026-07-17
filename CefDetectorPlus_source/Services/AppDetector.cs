using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Management.Deployment;
using GoodNewsBrowserAppDetector.Models;

namespace GoodNewsBrowserAppDetector.Services
{
    public class AppDetector
    {
        // ============================================================
        // 精确的标志文件特征库
        // 文件名 → 引擎类型，不区分大小写，仅匹配文件名即可
        // 注意：resources/app.asar 通过 ScanFilesForEngine 中的路径模式匹配处理
        // ============================================================
        private static readonly Dictionary<string, string> EngineSignatures = new(StringComparer.OrdinalIgnoreCase)
        {
            // Electron 主标志
            { "electron.exe", "Electron" },
            // Electron 辅助标志（打包后 electron.exe 被重命名时仍可识别，如 VS Code、Discord、QQ NT 等）
            { "snapshot_blob.bin", "Electron" },
            { "v8_context_snapshot.bin", "Electron" },
            // NW.js
            { "nw.exe", "NW.js" }, { "nw.dll", "NW.js" },
            // CEF (所有变种: CEF/CefSharp/JCEF 等，统一使用 libcef.dll)
            { "libcef.dll", "CEF" },
            // WebView2
            { "msedgewebview2.exe", "WebView2" },
            { "WebView2Loader.dll", "WebView2" },
            // Qt WebEngine (精确匹配，不含通配)
            { "QtWebEngineProcess.exe", "Qt WebEngine" },
            { "Qt5WebEngine.dll", "Qt WebEngine" },
            { "Qt6WebEngine.dll", "Qt WebEngine" },
            // JavaFX WebView (WebKit)
            { "jfxwebkit.dll", "JavaFX WebView" },
            // MiniBlink / MiniElectron
            { "mini_blink.dll", "MiniBlink" },
            { "miniblink.dll", "MiniBlink" },
            { "minielectron.exe", "MiniElectron" },
            // CefSharp 专属文件（用于区分纯 CEF 和 CefSharp）
            { "cefsharp.dll", "CefSharp" },
            { "CefSharp.BrowserSubprocess.exe", "CefSharp" },
            { "CefSharp.Core.dll", "CefSharp" },
        };

        // ============================================================
        // Qt WebEngine 通配符模式：Qt*WebEngineProcess.exe
        // 覆盖 Qt5WebEngineProcess.exe, Qt6WebEngineProcess.exe 等
        // ============================================================
        private static bool IsQtWebEngineProcess(string fileName)
        {
            return fileName.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith("WebEngineProcess.exe", StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // 误报排除：浏览器主程序名列表
        // 如果在安装根目录找到这些文件，且 DisplayName 匹配浏览器关键字，则排除
        // ============================================================
        private static readonly HashSet<string> BrowserExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe",
            "brave.exe", "vivaldi.exe", "chromium.exe", "iexplore.exe",
            "safari.exe", "seamonkey.exe", "waterfox.exe", "palemoon.exe",
            "tor.exe", "yandex.exe", "naverwhale.exe", "sogouexplorer.exe",
            "360chrome.exe", "360se.exe", "maxthon.exe", "qqbrowser.exe",
            "liebao.exe", "ucbrowser.exe", "sleipnir.exe", "lunascape.exe",
        };

        // ============================================================
        // 误报排除：浏览器 DisplayName 关键字
        // ============================================================
        private static readonly string[] BrowserKeywords = new[]
        {
            "浏览器", "Browser", "Chrome", "Edge", "Firefox", "Opera", "Brave",
            "Vivaldi", "Chromium", "Internet Explorer", "Safari", "Waterfox",
            "Pale Moon", "SeaMonkey", "Tor Browser", "Yandex", "Whale",
            "搜狗", "360", "QQ浏览器", "傲游", "猎豹", "UC浏览器",
        };

        // ============================================================
        // 误报排除：系统目录前缀（这些目录下的文件不可能是用户应用）
        // ============================================================
        private static readonly string[] SystemPathPrefixes = new[]
        {
            @"C:\Windows\",
            @"C:\Program Files\Common Files\",
            @"C:\Program Files (x86)\Common Files\",
        };

        // ============================================================
        // 误报排除：WebView2 运行时 DisplayName 关键字
        // ============================================================
        private const string WebView2RuntimeKeyword = "WebView2 Runtime";

        // ============================================================
        // 性能控制：每个应用目录最多扫描的文件数
        // 防止在大型目录（如 Steam 库）中性能恶化
        // ============================================================
        private const int MaxFilesPerApp = 2000;

        // ============================================================
        // 内部状态
        // ============================================================
        private readonly ConcurrentQueue<BrowserBasedApp> _results = new();
        private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _seenLocations = new(StringComparer.OrdinalIgnoreCase);
        private bool _everythingAvailable;
        private string? _esExePath;
        private IProgress<BrowserBasedApp>? _appProgress;

        // ============================================================
        // 自检测排除
        // ============================================================
        private static readonly string _selfDir = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private static readonly string _selfExeName =
            Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? typeof(AppDetector).Assembly.Location) + ".exe";
        private static readonly string _assemblyName =
            typeof(AppDetector).Assembly.GetName().Name ?? "GoodNewsBrowserAppDetector";
        private static readonly string? _packageName = GetPackageName();

        private static string? GetPackageName()
        {
            try { return Windows.ApplicationModel.Package.Current?.Id?.Name; }
            catch { return null; }
        }

        private static readonly ParallelOptions _parallelOpts = new()
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
        };

        // ============================================================
        // 公开 API
        // ============================================================

        public async Task<List<BrowserBasedApp>> DetectAsync(
            IProgress<BrowserBasedApp>? appProgress = null,
            IProgress<string>? statusProgress = null)
        {
            _results.Clear();
            _seen.Clear();
            _seenLocations.Clear();
            _appProgress = appProgress;
            _everythingAvailable = EverythingSdk.TryInitialize();
            _esExePath = FindEsExe();

            try
            {
                await Task.Run(() => DetectInternal(statusProgress));
            }
            catch (AggregateException ae)
            {
                System.Diagnostics.Debug.WriteLine($"扫描异常: {ae.Flatten().Message}");
            }

            return _results.ToList();
        }

        /// <summary>
        /// 并行计算所有 App 的磁盘占用大小。
        /// 跳过重解析点（ReparsePoint）避免循环和权限问题。
        /// </summary>
        public async Task CalculateSizesAsync(List<BrowserBasedApp> apps, IProgress<BrowserBasedApp>? progress = null)
        {
            await Task.Run(() =>
            {
                try
                {
                    Parallel.ForEach(apps, _parallelOpts, app =>
                    {
                        try
                        {
                            app.SizeBytes = CalcSize(app.InstallLocation);
                            progress?.Report(app);
                        }
                        catch { /* 单个 App 计算失败不影响整体 */ }
                    });
                }
                catch (AggregateException ae)
                {
                    System.Diagnostics.Debug.WriteLine($"大小计算异常: {ae.Flatten().Message}");
                }
            });
        }

        // ============================================================
        // 检测主流程
        // ============================================================

        // 注册表元数据缓存：路径 → (DisplayName, Publisher, Icon)
        private readonly ConcurrentDictionary<string, (string Name, string? Publisher, string? Icon)> _registryMeta =
            new(StringComparer.OrdinalIgnoreCase);

        private void DetectInternal(IProgress<string>? progress)
        {
            // 参考 CefDetectorX 的搜索策略：
            // 1. Everything 搜索（毫秒级，全盘覆盖，是主要检测手段）
            // 2. 注册表扫描（仅收集元数据，不做文件检测）
            // 3. MSIX 包扫描（仅收集元数据）
            // 4. 文件系统兜底扫描（Everything 不可用或返回 0 结果时）

            int everythingResultCount = 0;

            // 并行：Everything 搜索 + 注册表元数据收集
            Parallel.Invoke(_parallelOpts,
                () =>
                {
                    // 注册表元数据收集（仅读注册表，不做文件检测）
                    CollectRegistryMetadata();
                },
                () =>
                {
                    // MSIX 包元数据收集
                    CollectMsixMetadata();
                },
                () =>
                {
                    if (_esExePath != null)
                    {
                        var before = _results.Count;
                        ScanWithEverything(progress);
                        everythingResultCount = _results.Count - before;
                    }
                }
            );

            // Everything 不可用或返回 0 结果 → 文件系统兜底
            if (_esExePath == null || everythingResultCount == 0)
            {
                progress?.Report("正在扫描文件系统...");
                ScanFileSystem(progress);
                // 注册表也做一轮检测
                DetectFromRegistryLocations(progress);
            }
        }

        /// <summary>
        /// 仅收集注册表元数据（DisplayName, Publisher, Icon），不做文件检测。
        /// 存入 _registryMeta 字典，供 Everything 命中后补充元数据。
        /// </summary>
        private void CollectRegistryMetadata()
        {
            CollectRegistryMetadata(RegistryHive.LocalMachine, RegistryView.Registry64);
            CollectRegistryMetadata(RegistryHive.LocalMachine, RegistryView.Registry32);
            CollectRegistryMetadataCurrentUser();
        }

        private void CollectRegistryMetadata(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey == null) return;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName);
                        if (sub == null) continue;

                        var displayName = sub.GetValue("DisplayName") as string;
                        var installLocation = sub.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
                            continue;

                        var normalizedPath = Path.GetFullPath(installLocation)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var publisher = sub.GetValue("Publisher") as string;
                        var icon = sub.GetValue("DisplayIcon") as string;
                        _registryMeta.TryAdd(normalizedPath, (displayName, publisher, icon));
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void CollectRegistryMetadataCurrentUser()
        {
            try
            {
                using var uninstallKey = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey == null) return;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName);
                        if (sub == null) continue;

                        var displayName = sub.GetValue("DisplayName") as string;
                        var installLocation = sub.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
                            continue;

                        var normalizedPath = Path.GetFullPath(installLocation)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var publisher = sub.GetValue("Publisher") as string;
                        var icon = sub.GetValue("DisplayIcon") as string;
                        _registryMeta.TryAdd(normalizedPath, (displayName, publisher, icon));
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 仅收集 MSIX 包元数据，不做文件检测。
        /// </summary>
        private void CollectMsixMetadata()
        {
            try
            {
                var pm = new PackageManager();
                var packages = pm.FindPackagesForUser("");

                foreach (var package in packages)
                {
                    try
                    {
                        var path = package.InstalledLocation?.Path;
                        if (string.IsNullOrEmpty(path)) continue;
                        var normalizedPath = Path.GetFullPath(path)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        _registryMeta.TryAdd(normalizedPath,
                            (package.DisplayName, package.PublisherDisplayName, null));
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 兜底：当 Everything 不可用时，用注册表收集到的路径做快速检测。
        /// </summary>
        private void DetectFromRegistryLocations(IProgress<string>? progress)
        {
            foreach (var kvp in _registryMeta)
            {
                var path = kvp.Key;
                var (name, publisher, icon) = kvp.Value;

                if (!Directory.Exists(path)) continue;
                if (!_seen.TryAdd(path, 0)) continue;
                if (IsSelfOrChild(path)) continue;

                var engine = DetectEngineComprehensive(path);
                if (engine == null) continue;

                progress?.Report($"注册表: {name}");
                AddResult(name, publisher, path, icon, FindExe(path), engine);
            }
        }

        // ============================================================
        // 误报排除规则
        // ============================================================

        /// <summary>
        /// 判断是否为系统目录。
        /// 排除 C:\Windows\、C:\Program Files\Common Files\ 等系统路径。
        /// </summary>
        private static bool IsSystemPath(string path)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "\\";
                foreach (var prefix in SystemPathPrefixes)
                {
                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 判断是否为浏览器本体应用。
        /// 规则：完整路径包含浏览器关键字，且安装目录下有浏览器主程序 exe。
        /// 例如：Google Chrome 的路径含 "Chrome"，且目录下有 chrome.exe → 排除。
        /// 注意：Edge 等浏览器安装在版本号子目录（如 Application\150.0.4078.65），
        /// 仅检查叶子名会漏掉，必须检查完整路径。
        /// </summary>
        private static bool IsBrowserApplication(string displayName, string installLocation)
        {
            // 检查完整路径是否包含浏览器关键字（而非仅叶子名）
            bool hasKeyword = false;
            foreach (var kw in BrowserKeywords)
            {
                if (installLocation.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    hasKeyword = true;
                    break;
                }
            }
            if (!hasKeyword) return false;

            // 检查安装根目录下是否有浏览器主程序
            try
            {
                foreach (var file in Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var fn = Path.GetFileName(file);
                    if (BrowserExeNames.Contains(fn))
                        return true; // 确认是浏览器本体
                }
            }
            catch { /* 无法访问目录，保守起见不排除 */ }

            return false;
        }

        /// <summary>
        /// 判断给定目录是否为本应用自身目录或其子目录。
        /// </summary>
        private static bool IsSelfOrChild(string path)
        {
            try
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.StartsWith(_selfDir, StringComparison.OrdinalIgnoreCase)) return true;
                if (full.Contains(_assemblyName, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(_packageName) &&
                    full.Contains(_packageName, StringComparison.OrdinalIgnoreCase)) return true;
                try { if (File.Exists(Path.Combine(full, _selfExeName))) return true; }
                catch { }
            }
            catch { }
            return false;
        }

        // ============================================================
        // 综合引擎检测
        // 完全对齐 CefDetectorX 的 search() 函数逻辑：
        // 1. 仅检查当前目录（不递归！）
        // 2. 先快速文件名签名匹配
        // 3. 再扫描 exe 二进制内容
        // 4. 第一个匹配的 exe 立即返回，不继续扫描其他 exe
        // ============================================================

        /// <summary>
        /// 综合检测：先快速签名，再 exe 二进制扫描。
        /// 与 CefDetectorX 的 search() 完全对齐：只检查当前目录，不递归。
        /// </summary>
        private static string? DetectEngineComprehensive(string dir)
        {
            try
            {
                // 第一步：快速文件签名（仅当前目录）
                var engine = QuickSignatureCheck(dir);
                if (engine != null) return engine;

                // 第二步：exe 二进制扫描（仅当前目录，与 CefDetectorX 的 search() 一致）
                return ScanExesInDir(dir);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 快速签名检查：仅扫描当前目录中的文件名，不递归。
        /// 检查 Electron 的 resources/app.asar、Qt WebEngine、签名文件。
        /// </summary>
        private static string? QuickSignatureCheck(string dir)
        {
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(dir))
                {
                    var fileName = Path.GetFileName(filePath);

                    // Electron resources/app.asar
                    if (fileName.Equals("app.asar", StringComparison.OrdinalIgnoreCase))
                    {
                        var parentDir = Path.GetFileName(Path.GetDirectoryName(filePath));
                        if (string.Equals(parentDir, "resources", StringComparison.OrdinalIgnoreCase))
                            return "Electron";
                    }

                    // Qt WebEngine 通配符
                    if (IsQtWebEngineProcess(fileName)) return "Qt WebEngine";

                    // 签名文件精确匹配
                    if (EngineSignatures.TryGetValue(fileName, out var engine))
                        return engine;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 扫描当前目录中的 exe 文件，引擎搜索。
        /// 完全对齐 CefDetectorX 的 search() 函数：
        /// - 跳过 unins/setup/install/update/crash/report 等系统 exe
        /// - 读取每个 exe 的二进制内容搜索引擎特征
        /// - 第一个匹配的立即返回
        /// </summary>
        private static string? ScanExesInDir(string dir)
        {
            try
            {
                foreach (var exePath in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(exePath).ToLowerInvariant();
                    if (name.Contains("unins") || name.Contains("setup") ||
                        name.Contains("install") || name.Contains("update") ||
                        name.Contains("crash") || name.Contains("report"))
                        continue;

                    var engine = ScanExeBinary(exePath);
                    if (engine != null) return engine;
                }
            }
            catch { }
            return null;
        }

        // ============================================================
        // exe 二进制内容扫描（参考 CefDetectorX 的 fs.readFile + data.includes）
        // 使用 ReadOnlySpan<byte>.IndexOf 硬件加速（SSE/AVX 向量化搜索）
        // ============================================================

        // 引擎特征字符串（ASCII 编码，参考 CefDetectorX/src/index.js）
        private static readonly byte[] CefSharpPattern = Encoding.ASCII.GetBytes("CefSharp.Internals");
        private static readonly byte[] CefPattern = Encoding.ASCII.GetBytes("cef_string_utf8_to_utf16");
        private static readonly byte[] ElectronPattern1 = Encoding.ASCII.GetBytes("third_party/electron_node");
        private static readonly byte[] ElectronPattern2 = Encoding.ASCII.GetBytes("register_atom_browser_web_contents");
        private static readonly byte[] NwjsPattern = Encoding.ASCII.GetBytes("url-nwjs");
        private static readonly byte[] MiniElectronPattern = Encoding.ASCII.GetBytes("napi_create_buffer");
        private static readonly byte[] MiniBlinkPattern = Encoding.ASCII.GetBytes("miniblink");

        /// <summary>
        /// 扫描 exe 二进制内容搜索引擎特征。
        /// 使用 File.ReadAllBytes 读取整个文件 + ReadOnlySpan.IndexOf 硬件加速搜索。
        /// 跳过超过 200MB 和小于 1KB 的文件。
        /// </summary>
        private static string? ScanExeBinary(string exePath)
        {
            const int maxSize = 200 * 1024 * 1024;
            try
            {
                var fi = new FileInfo(exePath);
                long length = fi.Length;
                if (length > maxSize || length < 1024) return null;

                var bytes = File.ReadAllBytes(exePath);
                var span = new ReadOnlySpan<byte>(bytes);

                // 优先级：CefSharp > CEF > Electron > NW.js > MiniElectron > MiniBlink
                // 使用 MemoryExtensions.IndexOf 进行硬件加速搜索（SSE/AVX 向量化）
                if (span.IndexOf(CefSharpPattern) >= 0) return "CefSharp";
                if (span.IndexOf(CefPattern) >= 0) return "CEF";
                if (span.IndexOf(ElectronPattern1) >= 0 ||
                    span.IndexOf(ElectronPattern2) >= 0) return "Electron";
                if (span.IndexOf(NwjsPattern) >= 0) return "NW.js";
                if (span.IndexOf(MiniElectronPattern) >= 0) return "Mini Electron";
                if (span.IndexOf(MiniBlinkPattern) >= 0) return "Mini Blink";
            }
            catch { }
            return null;
        }

        // 保留 EverythingSdk 作为后备（用于获取 es.exe 路径等）
        private static class EverythingSdk
        {
            private static bool _initialized;
            private static bool _available;

            public static bool IsAvailable => _available && _initialized;

            public static bool TryInitialize()
            {
                if (_initialized) return _available;
                _initialized = true;

                var dllPath = FindEverything64Dll();
                if (dllPath == null) return false;

                try
                {
                    NativeLibrary.SetDllImportResolver(typeof(AppDetector).Assembly,
                        (name, assembly, path) =>
                        {
                            if (name.Equals("Everything64.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                if (NativeLibrary.TryLoad(dllPath, out var handle))
                                    return handle;
                            }
                            return IntPtr.Zero;
                        });

                    Everything_SetSearchW("test");
                    Everything_QueryW(true);
                    Everything_CleanUp();
                    _available = true;
                }
                catch { _available = false; }

                return _available;
            }

            public static string? FindEverything64Dll()
            {
                var dirs = new[]
                {
                    @"C:\Program Files\Everything",
                    @"C:\Program Files (x86)\Everything",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything"),
                };

                foreach (var dir in dirs)
                {
                    var dll = Path.Combine(dir, "Everything64.dll");
                    if (File.Exists(dll)) return dll;
                }

                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
                    var exePath = key?.GetValue("") as string;
                    if (exePath != null)
                    {
                        var dll = Path.Combine(Path.GetDirectoryName(exePath)!, "Everything64.dll");
                        if (File.Exists(dll)) return dll;
                    }
                }
                catch { }

                return null;
            }

            [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
            private static extern void Everything_SetSearchW(string lpSearchString);

            [DllImport("Everything64.dll")]
            private static extern bool Everything_QueryW(bool bWait);

            [DllImport("Everything64.dll")]
            private static extern void Everything_CleanUp();
        }

        /// <summary>
        /// 查找 es.exe（Everything 命令行工具）。
        /// es.exe 通过 IPC 与 Everything 服务通信，不需要 Everything64.dll。
        /// 搜索优先级：自身目录 → Everything 进程目录 → 常见安装目录 → PATH → Everything SDK 目录
        /// </summary>
        private static string? FindEsExe()
        {
            // 1. 自身目录（项目自带的 es.exe，最优先）
            var selfDirEs = Path.Combine(AppContext.BaseDirectory, "es.exe");
            if (File.Exists(selfDirEs)) return selfDirEs;

            // 2. Everything 进程目录
            try
            {
                var everythingProc = System.Diagnostics.Process.GetProcessesByName("Everything")
                    .FirstOrDefault();
                if (everythingProc != null)
                {
                    var procDir = Path.GetDirectoryName(everythingProc.MainModule?.FileName);
                    if (!string.IsNullOrEmpty(procDir))
                    {
                        var es = Path.Combine(procDir, "es.exe");
                        if (File.Exists(es)) return es;
                    }
                }
            }
            catch { }

            // 3. 常见 Everything 安装目录
            var commonDirs = new[]
            {
                @"D:\Everything",
                @"C:\Program Files\Everything",
                @"C:\Program Files (x86)\Everything",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything"),
            };
            foreach (var dir in commonDirs)
            {
                try
                {
                    var es = Path.Combine(dir, "es.exe");
                    if (File.Exists(es)) return es;
                }
                catch { }
            }

            // 4. 注册表中 Everything.exe 的安装路径
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Everything.exe");
                var exePath = key?.GetValue("") as string;
                if (exePath != null)
                {
                    var es = Path.Combine(Path.GetDirectoryName(exePath)!, "es.exe");
                    if (File.Exists(es)) return es;
                }
            }
            catch { }

            // 5. PATH 环境变量
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var es = Path.Combine(dir.Trim(), "es.exe");
                    if (File.Exists(es)) return es;
                }
            }
            catch { }

            // 6. Everything SDK 目录（Everything64.dll 所在目录）
            var dllPath = EverythingSdk.FindEverything64Dll();
            if (dllPath != null)
            {
                var es = Path.Combine(Path.GetDirectoryName(dllPath)!, "es.exe");
                if (File.Exists(es)) return es;
            }

            return null;
        }

        /// <summary>
        /// 运行 es.exe 搜索，返回匹配的文件路径列表。
        /// 参数与 CefDetectorX 完全一致：-regex 或 -s。
        /// </summary>
        private static List<string> RunEsSearch(string esExePath, string arguments)
        {
            var results = new List<string>();
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = esExePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30000);

                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim('\r', '\n', ' ');
                    if (!string.IsNullOrEmpty(trimmed) && File.Exists(trimmed))
                        results.Add(trimmed);
                }
            }
            catch { }
            return results;
        }

        private void ScanWithEverything(IProgress<string>? progress)
        {
            if (_esExePath == null) return;

            // 参考 CefDetectorX 的搜索策略（使用 es.exe，与 CefDetectorX 完全一致）：
            // 1. es.exe -regex _100_(.+?)\.pak$  → 匹配所有 Chromium 资源包
            // 2. es.exe -s libcef                 → 匹配 CEF 库
            // 3. es.exe -regex node(.*?)\.dll     → 匹配 MiniBlink/MiniElectron
            var searchTasks = new (string Args, string Pattern)[]
            {
                (@"-regex _100_(.+?)\.pak$", "_100_*.pak"),
                ("-s libcef", "libcef"),
                (@"-regex node(.*?)\.dll", "node*.dll")
            };

            // 第一步：并行运行 es.exe 搜索，收集所有候选目录
            var candidates = new ConcurrentBag<(string Dir, string Pattern)>();
            Parallel.ForEach(searchTasks, _parallelOpts, search =>
            {
                try
                {
                    var files = RunEsSearch(_esExePath!, search.Args);
                    foreach (var filePath in files)
                    {
                        if (filePath.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                            filePath.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
                            filePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var dir = Path.GetDirectoryName(filePath);
                        if (string.IsNullOrEmpty(dir)) continue;

                        if (!_seenLocations.TryAdd(dir, 0)) continue;
                        if (IsSelfOrChild(dir)) continue;

                        candidates.Add((dir, search.Pattern));
                    }
                }
                catch { }
            });

            if (candidates.Count == 0) return;

            // 第二步：并行检测所有候选目录
            progress?.Report($"正在分析 {candidates.Count} 个候选目录...");

            Parallel.ForEach(candidates, _parallelOpts, candidate =>
            {
                try
                {
                    var (dir, pattern) = candidate;

                    // 完全对齐 CefDetectorX 的 searchCef 回退逻辑：
                    // 1. search(dir) → 找到引擎 → 添加
                    // 2. search(dir) 无引擎但有 firstExe → 用默认类型添加
                    // 3. search(dir) 无引擎无 firstExe → search(parentDir)
                    // 4. search(parentDir) 找到 → 添加
                    // 5. search(parentDir) 有 firstExe → 用默认类型添加
                    // 6. 全部失败 → 仍用默认类型添加（标记为目录）

                    var engine = DetectEngineComprehensive(dir);
                    var displayDir = dir;

                    // 步骤2-4：当前目录无引擎 → 尝试父目录
                    if (engine == null)
                    {
                        var parentDir = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parentDir) && !_seenLocations.ContainsKey(parentDir))
                        {
                            _seenLocations.TryAdd(parentDir, 0);
                            if (!IsSelfOrChild(parentDir))
                            {
                                var parentEngine = DetectEngineComprehensive(parentDir);
                                if (parentEngine != null)
                                {
                                    engine = parentEngine;
                                    displayDir = parentDir;
                                }
                                else
                                {
                                    // 父目录有 firstExe → 用默认类型（对齐 CefDetectorX）
                                    var parentExe = FindFirstNonSystemExe(parentDir);
                                    if (parentExe != null)
                                    {
                                        engine = ScanExeBinary(parentExe) ?? GetDefaultTypeForPattern(pattern);
                                        displayDir = parentDir;
                                    }
                                }
                            }
                        }
                    }

                    // 步骤5：当前目录有 firstExe → 用默认类型
                    if (engine == null)
                    {
                        var firstExe = FindFirstNonSystemExe(dir);
                        if (firstExe != null)
                        {
                            engine = ScanExeBinary(firstExe) ?? GetDefaultTypeForPattern(pattern);
                        }
                    }

                    // 步骤6：最终兜底 → 仍用默认类型（对齐 CefDetectorX 的 addApp(dir, defaultType, true)）
                    if (engine == null)
                        engine = GetDefaultTypeForPattern(pattern);

                    // node*.dll 额外验证：需要 exe 包含 MiniElectron/MiniBlink 特征
                    // 对齐 CefDetectorX 的 node*.dll 单独处理逻辑
                    if (pattern == "node*.dll")
                    {
                        var hasMiniBlink = File.Exists(Path.Combine(displayDir, "mini_blink.dll")) ||
                                           File.Exists(Path.Combine(displayDir, "miniblink.dll"));
                        if (!hasMiniBlink)
                        {
                            // 也检查 exe 二进制内容（对齐 CefDetectorX 的 node*.dll 单独处理）
                            var foundExe = false;
                            foreach (var exePath in Directory.EnumerateFiles(displayDir, "*.exe", SearchOption.TopDirectoryOnly))
                            {
                                var exeEngine = ScanExeBinary(exePath);
                                if (exeEngine == "Mini Electron" || exeEngine == "Mini Blink")
                                {
                                    engine = exeEngine;
                                    foundExe = true;
                                    break;
                                }
                            }
                            if (!foundExe) return;
                        }
                    }

                    // 查找注册表元数据
                    var normalizedPath = Path.GetFullPath(displayDir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    _registryMeta.TryGetValue(normalizedPath, out var meta);
                    var dirName = Path.GetFileName(displayDir);
                    var foundExePath = FindExe(displayDir);
                    // 对齐 CefDetectorX：优先用注册表名，其次用exe名，最后用目录名
                    var displayName = meta.Name
                        ?? (foundExePath != null ? Path.GetFileNameWithoutExtension(foundExePath) : dirName);

                    AddResult(displayName, meta.Publisher, displayDir, meta.Icon, foundExePath, engine);
                }
                catch { }
            });
        }

        /// <summary>
        /// 在目录中查找第一个非系统 exe（跳过卸载、安装、更新、崩溃报告程序）。
        /// 参考 CefDetectorX 的 firstExe 回退逻辑。
        /// </summary>
        private static string? FindFirstNonSystemExe(string dir)
        {
            try
            {
                var exeFiles = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                foreach (var exe in exeFiles)
                {
                    var name = Path.GetFileName(exe).ToLowerInvariant();
                    if (!name.Contains("unins") && !name.Contains("setup") &&
                        !name.Contains("install") && !name.Contains("update") &&
                        !name.Contains("crash") && !name.Contains("report"))
                    {
                        return exe;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 文件系统兜底扫描（Everything 不可用时）。
        /// 策略：扫描系统特殊目录 + 各驱动器一级目录（跳过系统目录），每目录深度 4 层。
        /// 使用 _seen 去重，避免重复扫描。
        /// </summary>
        private void ScanFileSystem(IProgress<string>? progress)
        {
            var scanDirs = new List<string>();

            // 系统特殊目录（覆盖绝大多数已安装应用）
            scanDirs.AddRange(new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            }.Where(Directory.Exists)!);

            // 被系统目录覆盖的路径（跳过，避免重复扫描）
            var coveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"C:\$Recycle.Bin",
                @"C:\System Volume Information",
                @"C:\Config.Msi",
                @"C:\Recovery",
                @"C:\ProgramData",
                @"C:\Users",
            };

            // 各驱动器一级目录（便携软件常用路径，排除已覆盖的）
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                try
                {
                    foreach (var topDir in Directory.EnumerateDirectories(drive.RootDirectory.FullName))
                    {
                        if (!coveredPaths.Contains(topDir))
                            scanDirs.Add(topDir);
                    }
                }
                catch { }
            }

            // 并行扫描，每个一级目录深度 4 层
            Parallel.ForEach(scanDirs, _parallelOpts, rootDir =>
            {
                try
                {
                    ScanDirRecursive(rootDir, 0, progress);
                }
                catch { }
            });
        }

        private void ScanDirRecursive(string dir, int depth, IProgress<string>? progress)
        {
            const int maxDepth = 4;
            const int maxSubDirs = 500;
            if (depth > maxDepth) return;
            if (!_seen.TryAdd(dir, 0)) return;
            if (IsSelfOrChild(dir)) return;

            try
            {
                var di = new DirectoryInfo(dir);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return;

                // 检查当前目录的签名文件
                var engine = QuickSignatureCheck(dir);
                if (engine == null)
                    engine = ScanExesInDir(dir);
                if (engine == null)
                {
                    var firstExe = FindFirstNonSystemExe(dir);
                    if (firstExe != null)
                        engine = ScanExeBinary(firstExe);
                }

                if (engine != null)
                {
                    var dirName = Path.GetFileName(dir);
                    var foundExe = FindExe(dir);
                    var displayName = foundExe != null
                        ? Path.GetFileNameWithoutExtension(foundExe)
                        : dirName;
                    AddResult(displayName, null, dir, null, foundExe, engine);
                }

                // 继续扫描子目录
                int count = 0;
                foreach (var subDir in di.EnumerateDirectories())
                {
                    if (++count > maxSubDirs) break;
                    ScanDirRecursive(subDir.FullName, depth + 1, progress);
                }
            }
            catch { }
        }

        /// <summary>
        /// 获取 es.exe 搜索模式对应的默认引擎类型。
        /// 对齐 CefDetectorX：_100_*.pak → "Unknown", libcef → "CEF", node*.dll → "Unknown"
        /// </summary>
        private static string GetDefaultTypeForPattern(string pattern)
        {
            return pattern switch
            {
                "libcef" => "CEF",
                _ => "Chromium"
            };
        }

        // ============================================================
        // 辅助方法
        // ============================================================

        private void AddResult(string name, string? publisher, string location,
            string? icon, string? exe, string engine)
        {
            // 立即计算大小，实现实时显示
            var size = CalcSize(location);
            var app = new BrowserBasedApp
            {
                DisplayName = name,
                Publisher = publisher,
                InstallLocation = location,
                IconPath = icon,
                ExePath = exe,
                SizeBytes = size,
                DetectedEngine = engine
            };
            _results.Enqueue(app);
            _seenLocations.TryAdd(location, 0);
            _appProgress?.Report(app);
        }

        /// <summary>
        /// 计算目录磁盘占用大小（递归）。
        /// 使用递归遍历，每个子目录失败不影响整体。
        /// 跳过重解析点（ReparsePoint）避免循环和双倍计数。
        /// 深度限制防止无限循环。
        /// </summary>
        private static long CalcSize(string path)
        {
            long total = 0;
            CalcSizeRecursive(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, ref total);
            return total;
        }

        private static void CalcSizeRecursive(string path, HashSet<string> visited, int depth, ref long total)
        {
            const int maxDepth = 10;
            if (depth > maxDepth) return;

            try
            {
                var di = new DirectoryInfo(path);
                var normalizedPath = di.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // 防止循环（通过路径去重，替代 CefDetectorX 的 inode 去重）
                if (!visited.Add(normalizedPath)) return;

                // 根目录本身是重解析点 → 跳过（如 OneDrive 的 junction）
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return;

                // 遍历文件
                try
                {
                    foreach (var file in di.EnumerateFiles())
                    {
                        try
                        {
                            if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                                total += file.Length;
                        }
                        catch { }
                    }
                }
                catch { }

                // 遍历子目录
                try
                {
                    foreach (var subDir in di.EnumerateDirectories())
                    {
                        CalcSizeRecursive(subDir.FullName, visited, depth + 1, ref total);
                    }
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// 查找目录中真正的应用程序主 exe（跳过卸载程序等）。
        /// </summary>
        private static string? FindExe(string path)
        {
            try
            {
                var allExes = Directory.EnumerateFiles(path, "*.exe", SearchOption.AllDirectories)
                    .Select(f => new { Path = f, Name = Path.GetFileName(f) })
                    .Where(f => !f.Name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.Equals("install.exe", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.StartsWith("update", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.StartsWith("crash", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (allExes.Count == 0) return null;

                var dirName = Path.GetFileName(path);
                var match = allExes.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f.Name), dirName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Path;

                match = allExes.FirstOrDefault(f =>
                    f.Name.Contains(dirName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match.Path;

                var rootExe = allExes.FirstOrDefault(f =>
                    string.Equals(Path.GetDirectoryName(f.Path), path, StringComparison.OrdinalIgnoreCase));
                if (rootExe != null) return rootExe.Path;

                return allExes[0].Path;
            }
            catch { return null; }
        }
    }
}