using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.Media.Core;
using Windows.Media.Playback;
using GoodNewsBrowserAppDetector.Models;
using GoodNewsBrowserAppDetector.Services;

namespace GoodNewsBrowserAppDetector
{
    public sealed partial class MainWindow : Window
    {
        private readonly AppDetector _detector = new();
        private readonly bool _noBgm;
        private MediaPlayer? _mediaPlayer;
        private DispatcherTimer? _syncTimer;
        private readonly ObservableCollection<BrowserBasedApp> _displayedApps = new();
        private BitmapImage? _defaultIconSource;
        private bool _isProgrammaticChange; // 代码修改 Slider 值时跳过 ValueChanged
        private double _lastMutedVolume = 50;
        private int _imageWidth = 600;
        private int _imageHeight = 450;
        private int _minWindowWidth = 560;
        private int _minWindowHeight = 500;
        private Microsoft.UI.Windowing.AppWindow? _appWindow;
        private bool _isUpdatingWindowSize;
        private const int DefaultTopMargin = 130;
        private const int MaximizedExtraMargin = 112;

        // 滚动状态标记，滚动中暂停卡片效果
        private bool _isScrolling;

        private static readonly SolidColorBrush _blackBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
        private static readonly SolidColorBrush _blueBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x41, 0x69, 0xE1));
        private static readonly SolidColorBrush _whiteBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        // 卡片背景色：降低白色不透明度，营造高级透明玻璃感
        // 正常态：18% 不透明度 → 更透明
        private static readonly Windows.UI.Color CardNormalColor = Windows.UI.Color.FromArgb(0x2D, 0xFF, 0xFF, 0xFF);
        // 悬停态：35% 不透明度 → 微微提亮
        private static readonly Windows.UI.Color CardHoverColor = Windows.UI.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);

        public MainWindow(bool noBgm = false)
        {
            _noBgm = noBgm;
            this.InitializeComponent();

            SystemBackdrop = new MicaBackdrop
            {
                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt
            };

            LoadBackgroundImage();
            ConfigureWindow();

            if (!_noBgm)
                InitializeMusicPlayer();

            // 适配深色/浅色主题
            var root = (FrameworkElement)this.Content;
            root.ActualThemeChanged += (_, _) => ApplyMusicControlTheme();
            ApplyMusicControlTheme();

            AppsRepeater.ItemsSource = _displayedApps;
            _ = StartDetectionAsync();
        }

        private void ConfigureWindow()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            double ar = (double)_imageWidth / _imageHeight;
            _minWindowWidth = Math.Max(_imageWidth, 560);
            _minWindowHeight = Math.Max(_imageHeight, 500);
            _appWindow.Resize(new Windows.Graphics.SizeInt32((int)(600 * ar), 600));
            _appWindow.Changed += AppWindow_Changed;
        }

        private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
        {
            if (_isUpdatingWindowSize) return;
            if (!args.DidSizeChange) return;
            var size = sender.Size;
            bool need = false;
            int w = size.Width, h = size.Height;
            if (w < _minWindowWidth) { w = _minWindowWidth; need = true; }
            if (h < _minWindowHeight) { h = _minWindowHeight; need = true; }
            if (need) { _isUpdatingWindowSize = true; sender.Resize(new Windows.Graphics.SizeInt32(w, h)); _isUpdatingWindowSize = false; return; }
            AdjustTitleMargin(h);
        }

        private void AdjustTitleMargin(int windowHeight)
        {
            // 背景图片使用 Stretch="Fill" 缩放，"喜报"文字区域约占图片高度的 22%
            // 标题需要根据窗口高度等比下移，避免被图片中的"喜报"文字遮挡
            // 超宽屏（1080×1920 等）下窗口变高，"喜报"文字也会等比变大
            int top = Math.Max(DefaultTopMargin, (int)(windowHeight * 0.22));
            DispatcherQueue.TryEnqueue(() =>
            {
                TitleArea.Margin = new Thickness(20, top, 20, 16);
            });
        }

        private void LoadBackgroundImage()
        {
            try
            {
                var bgPath = Path.Combine(AppContext.BaseDirectory, "Assets", "goodnews_bg.png");
                if (File.Exists(bgPath))
                {
                    using var sysBmp = new Bitmap(bgPath);
                    _imageWidth = sysBmp.Width;
                    _imageHeight = sysBmp.Height;
                    var bmp = new BitmapImage();
                    bmp.SetSource(File.OpenRead(bgPath).AsRandomAccessStream());
                    BgImage.Source = bmp;
                    return;
                }
            }
            catch { }
            BgImage.Source = GenerateFestiveBackground();
        }

        private static BitmapImage GenerateFestiveBackground()
        {
            try
            {
                using var bitmap = new Bitmap(600, 450);
                using var g = Graphics.FromImage(bitmap);
                using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(0, 0, 600, 450),
                    System.Drawing.Color.DarkRed, System.Drawing.Color.Red,
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical);
                g.FillRectangle(brush, 0, 0, 600, 450);
                using var gold = new SolidBrush(System.Drawing.Color.FromArgb(80, 255, 215, 0));
                var rng = new Random(42);
                for (int i = 0; i < 40; i++)
                    g.FillEllipse(gold, rng.Next(0, 600), rng.Next(0, 450), rng.Next(20, 80), rng.Next(20, 80));
                var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.SetSource(ms.AsRandomAccessStream());
                return bmp;
            }
            catch { return null!; }
        }

        private void InitializeMusicPlayer()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "goodnews_music.aac");
                if (!File.Exists(path)) return;

                _mediaPlayer = new MediaPlayer
                {
                    Source = MediaSource.CreateFromUri(new Uri(path)),
                    IsLoopingEnabled = true,
                    Volume = 0.5
                };
                _mediaPlayer.Play();
                PlayPauseIcon.Glyph = "\uE769"; // 暂停图标

                // 初始化音量 (50%)
                _isProgrammaticChange = true;
                VolumeSlider.Value = 50;
                _isProgrammaticChange = false;

                // Timer：更新 TimeText + 进度条 Slider
                _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _syncTimer.Tick += (_, _) =>
                {
                    var session = _mediaPlayer?.PlaybackSession;
                    if (session == null || session.NaturalDuration.TotalSeconds <= 0) return;
                    var dur = session.NaturalDuration.TotalSeconds;
                    var pos = session.Position.TotalSeconds;

                    TimeText.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";

                    // 代码更新进度条，跳过 ValueChanged
                    _isProgrammaticChange = true;
                    ProgressSlider.Value = pos / dur * 100;
                    _isProgrammaticChange = false;
                };
                _syncTimer.Start();
            }
            catch { }
        }

        // ============ Slider 事件 ============

        private void ProgressSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticChange) return;
            if (_mediaPlayer?.PlaybackSession?.NaturalDuration.TotalSeconds > 0)
            {
                var dur = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
                var pos = e.NewValue / 100.0 * dur;
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(pos);
                TimeText.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";
            }
        }

        private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticChange) return;
            var ratio = e.NewValue / 100.0;
            if (_mediaPlayer != null) _mediaPlayer.Volume = ratio;
            VolumeIcon.Foreground = GetVolumeIconColor(ratio == 0);
        }

        private void PlayPauseBtn_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 动作已由 PointerReleased 处理，避免双击冲突
        }

        // 按钮 & 图标颜色（实例字段，随主题切换）
        private SolidColorBrush _btnHoverBg  = new(Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00));
        private SolidColorBrush _btnPressedBg = new(Windows.UI.Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        private SolidColorBrush _iconHoverCol  = new(Windows.UI.Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
        private SolidColorBrush _iconNormalCol = new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));

        // ============ 主题适配 ============

        private void ApplyMusicControlTheme()
        {
            var isDark = ((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark;

            // 音乐控制栏背景/边框
            MusicControlBorder.Background = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x1A, 0x1A, 0x1A))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            // 文字/图标颜色
            var fgColor = isDark ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                                 : Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            TimeText.Foreground = new SolidColorBrush(fgColor);
            PlayPauseIcon.Foreground = _iconNormalCol = new SolidColorBrush(fgColor);
            VolumeIcon.Foreground = GetVolumeIconColor(VolumeSlider?.Value == 0);

            // 按钮 hover/pressed 背景
            _btnHoverBg = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00));
            _btnPressedBg = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x00, 0x00, 0x00));
            PlayPauseHover.Background = _btnHoverBg;
            VolumeHover.Background = _btnHoverBg;

            // 图标 hover 颜色
            _iconHoverCol = isDark
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
        }

        private SolidColorBrush GetVolumeIconColor(bool muted)
        {
            if (muted)
                return new SolidColorBrush(((FrameworkElement)this.Content).ActualTheme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(0xFF, 0x66, 0x66, 0x66)
                    : Microsoft.UI.Colors.Gray);
            return _iconNormalCol;
        }

        private void PlayPauseBtn_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            FadeBtnOpacity(PlayPauseHover, 1);
            PlayPauseIcon.Foreground = _iconHoverCol;
        }

        private void PlayPauseBtn_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            FadeBtnOpacity(PlayPauseHover, 0);
            PlayPauseIcon.Foreground = _iconNormalCol;
        }

        private void PlayPauseBtn_PointerPressed(object sender, PointerRoutedEventArgs e)
            => PlayPauseHover.Background = _btnPressedBg;

        private void PlayPauseBtn_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            PlayPauseHover.Background = _btnHoverBg;

            // PointerReleased 零延迟响应，避免 Tapped 手势识别延迟
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
            {
                _mediaPlayer.Pause();
                PlayPauseIcon.Glyph = "\uE768";
            }
            else
            {
                _mediaPlayer.Play();
                PlayPauseIcon.Glyph = "\uE769";
            }
        }

        // ============ 按钮 hover fade（DispatcherTimer 手动插值）============
        private DispatcherTimer? _fadeTimer;
        private DateTime _fadeStart;
        private const int FadeMs = 200;

        // 按钮 Opacity fade
        private Border? _fadeBtn;
        private double _fadeBtnFrom, _fadeBtnTo;

        private void EnsureFadeTimer()
        {
            if (_fadeTimer != null) return;
            _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _fadeTimer.Tick += (_, _) =>
            {
                var t = Math.Min((DateTime.UtcNow - _fadeStart).TotalMilliseconds / FadeMs, 1.0);
                var ease = 1.0 - (1.0 - t) * (1.0 - t); // ease-out quad

                if (_fadeBtn != null)
                    _fadeBtn.Opacity = _fadeBtnFrom + (_fadeBtnTo - _fadeBtnFrom) * ease;

                if (t >= 1.0)
                {
                    _fadeBtn = null;
                    _fadeTimer?.Stop();
                }
            };
        }

        private void FadeBtnOpacity(Border target, double to)
        {
            EnsureFadeTimer();
            _fadeBtn = target;
            _fadeBtnFrom = target.Opacity;
            _fadeBtnTo = to;
            _fadeStart = DateTime.UtcNow;
            if (!_fadeTimer!.IsEnabled) _fadeTimer.Start();
        }

        private void VolumeIconBtn_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 动作已由 PointerReleased 处理，避免双击冲突
        }

        private void VolumeIconBtn_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            FadeBtnOpacity(VolumeHover, 1);
            VolumeIcon.Foreground = _iconHoverCol;
        }

        private void VolumeIconBtn_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            FadeBtnOpacity(VolumeHover, 0);
            VolumeIcon.Foreground = _iconNormalCol;
        }

        private void VolumeIconBtn_PointerPressed(object sender, PointerRoutedEventArgs e)
            => VolumeHover.Background = _btnPressedBg;

        private void VolumeIconBtn_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            VolumeHover.Background = _btnHoverBg;

            // PointerReleased 零延迟响应，避免 Tapped 手势识别延迟
            if (_mediaPlayer == null) return;
            if (VolumeSlider.Value > 0)
            {
                _lastMutedVolume = VolumeSlider.Value;
                _isProgrammaticChange = true;
                VolumeSlider.Value = 0;
                _isProgrammaticChange = false;
                _mediaPlayer.Volume = 0;
                VolumeIcon.Foreground = GetVolumeIconColor(true);
            }
            else
            {
                var restore = _lastMutedVolume > 0 ? _lastMutedVolume : 50;
                _isProgrammaticChange = true;
                VolumeSlider.Value = restore;
                _isProgrammaticChange = false;
                _mediaPlayer.Volume = restore / 100.0;
                VolumeIcon.Foreground = GetVolumeIconColor(false);
            }
        }

        private static string FormatTime(double seconds)
        {
            if (seconds <= 0) return "0:00";
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private async Task StartDetectionAsync()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                TitleGrid.Visibility = Visibility.Visible;
                SetTitleColor(_blackBrush);
                UpdateTitleText(0, 0);
            });

            List<BrowserBasedApp> allApps = new();

            try
            {
                var appProgress = new Progress<BrowserBasedApp>(app =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        app.IconSource = LoadAppIconInternal(app);
                        _displayedApps.Add(app);
                        UpdateTitle();
                    });
                });

                var statusProgress = new Progress<string>(msg =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // 不显示"正在搜索..."，让标题实时显示数量
                if (_displayedApps.Count == 0 && !string.IsNullOrEmpty(msg))
                {
                    SetTitleText($"正在搜索... ({msg})");
                }
            });
        });

                allApps = await _detector.DetectAsync(appProgress, statusProgress);

                DispatcherQueue.TryEnqueue(() =>
                {
                    var sorted = allApps.OrderByDescending(a => a.SizeBytes).ToList();

                    var runningExeNames = GetRunningExeNames();
                    foreach (var app in sorted)
                    {
                        if (!string.IsNullOrEmpty(app.ExePath))
                        {
                            var exeName = Path.GetFileName(app.ExePath);
                            app.IsRunning = runningExeNames.Contains(exeName);
                        }
                    }

                    _displayedApps.Clear();
                    foreach (var app in sorted)
                        _displayedApps.Add(app);

                    SetTitleColor(_blueBrush);
                    UpdateTitle();
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    SetTitleColor(_blueBrush);
                    UpdateTitleText(_displayedApps.Count, 0);
                    var msg = ex is AggregateException ae
                        ? $"检测部分失败: {ae.Flatten().InnerExceptions.Count} 个错误"
                        : $"检测失败: {ex.Message}";
                    TitleText.Text = msg;
                });
            }
        }

        private static HashSet<string> GetRunningExeNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try { names.Add(proc.ProcessName + ".exe"); }
                    catch { }
                }
            }
            catch { }
            return names;
        }

        private void SetTitleColor(SolidColorBrush color)
        {
            TitleText.Foreground = color;
            TitleStroke1.Foreground = _whiteBrush;
            TitleStroke2.Foreground = _whiteBrush;
            TitleStroke3.Foreground = _whiteBrush;
            TitleStroke4.Foreground = _whiteBrush;
        }

        private void SetTitleText(string text)
        {
            TitleText.Text = text;
            TitleStroke1.Text = text;
            TitleStroke2.Text = text;
            TitleStroke3.Text = text;
            TitleStroke4.Text = text;
        }

        private void UpdateTitle()
        {
            var apps = _displayedApps;
            var totalBytes = apps.Sum(a => (double)a.SizeBytes);
            var totalGB = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 2);
            UpdateTitleText(apps.Count, totalGB);
        }

        private void UpdateTitleText(int count, double totalGB)
        {
            var text = totalGB > 0
                ? $"这台电脑总共有 {count} 个 Chromium 内核的应用（{totalGB:F2} GB）"
                : $"这台电脑总共有 {count} 个 Chromium 内核的应用";
            if (count == 0)
                text += "（也有可能是你没安装Everything）";
            SetTitleText(text);
        }

        // ============ 卡片交互 ============

        /// <summary>
        /// 鼠标/手指进入卡片：高亮背景。
        /// 创建新的 SolidColorBrush 实例以触发 BrushTransition 淡入动画。
        /// 滚动中跳过悬停效果，避免重绘导致卡顿。
        /// </summary>
        private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_isScrolling) return;
            if (sender is Border border)
            {
                // 必须创建新的 Brush 实例才能触发 BrushTransition 动画
                // 直接修改 SolidColorBrush.Color 不会触发 Background 属性变更
                border.Background = new SolidColorBrush(CardHoverColor);
            }
        }

        /// <summary>
        /// 鼠标/手指离开卡片：恢复普通背景。
        /// 同时绑定 PointerCaptureLost 和 PointerCanceled，处理触控场景下
        /// 手指离开屏幕但未触发 PointerExited 的情况。
        /// </summary>
        private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ResetCardBackground(sender);
        }

        /// <summary>
        /// 触控：手指在卡片上抬起后，重置背景（触控场景下 PointerExited 可能不触发）。
        /// </summary>
        private void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            ResetCardBackground(sender);
        }

        private static void ResetCardBackground(object sender)
        {
            if (sender is Border border)
            {
                // 必须创建新的 Brush 实例才能触发 BrushTransition 动画
                border.Background = new SolidColorBrush(CardNormalColor);
            }
        }

        private void Card_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border border) return;
            if (border.DataContext is not BrowserBasedApp app) return;

            var path = app.ExePath ?? app.InstallLocation;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", path);
            }
            catch { }
        }

        // ============ 滚动性能优化 ============

        /// <summary>
        /// 滚动开始时标记状态，暂停卡片悬停效果。
        /// 避免滚动过程中频繁改变背景色导致重绘。
        /// </summary>
        private void ScrollViewer_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        {
            _isScrolling = true;
        }

        /// <summary>
        /// 滚动结束后恢复状态，允许卡片悬停效果。
        /// </summary>
        private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            _isScrolling = false;
        }

        // ============ 图标加载 ============

        private BitmapImage LoadAppIconInternal(BrowserBasedApp app)
        {
            if (!string.IsNullOrEmpty(app.IconPath))
            {
                var icon = LoadIconFromPath(app.IconPath);
                if (icon != null) return icon;
            }
            if (!string.IsNullOrEmpty(app.ExePath))
            {
                var icon = LoadIconFromExe(app.ExePath);
                if (icon != null) return icon;
            }
            return GetDefaultIcon();
        }

        private static BitmapImage? LoadIconFromPath(string iconPath)
        {
            try
            {
                var p = iconPath.Contains(',') ? iconPath.Split(',')[0].Trim() : iconPath;
                if (!File.Exists(p)) return null;
                using var icon = Icon.ExtractAssociatedIcon(p);
                return icon == null ? null : ConvertIconToBitmapImage(icon);
            }
            catch { return null; }
        }

        private static BitmapImage? LoadIconFromExe(string exePath)
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                return icon == null ? null : ConvertIconToBitmapImage(icon);
            }
            catch { return null; }
        }

        private static BitmapImage ConvertIconToBitmapImage(Icon icon)
        {
            using var bmp = icon.ToBitmap();
            var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.DecodePixelWidth = 48;
            bi.SetSource(ms.AsRandomAccessStream());
            return bi;
        }

        private BitmapImage GetDefaultIcon()
        {
            if (_defaultIconSource != null) return _defaultIconSource;
            try
            {
                using var bitmap = new Bitmap(32, 32);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new SolidBrush(System.Drawing.Color.FromArgb(64, 128, 192));
                g.FillRectangle(bg, 2, 2, 28, 28);
                using var fg = new SolidBrush(System.Drawing.Color.White);
                g.FillRectangle(fg, 10, 8, 12, 3);
                g.FillRectangle(fg, 10, 14, 8, 3);
                g.FillRectangle(fg, 10, 20, 14, 3);
                var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.SetSource(ms.AsRandomAccessStream());
                _defaultIconSource = bi;
                return bi;
            }
            catch { return null!; }
        }
    }
}