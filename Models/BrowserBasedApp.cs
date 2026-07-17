using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace GoodNewsBrowserAppDetector.Models
{
    public class BrowserBasedApp : INotifyPropertyChanged
    {
        private long _sizeBytes;
        private bool _isRunning;
        private string _engineType = string.Empty;
        private string _displayName = string.Empty;
        private string _installLocation = string.Empty;
        private string? _exePath;

        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }
        public string? Publisher { get; set; }
        public string InstallLocation
        {
            get => _installLocation;
            set { if (_installLocation != value) { _installLocation = value; OnPropertyChanged(); } }
        }
        public string? IconPath { get; set; }
        public string? ExePath
        {
            get => _exePath;
            set { if (_exePath != value) { _exePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } }
        }
        public string EngineType
        {
            get => _engineType;
            set { if (_engineType != value) { _engineType = value; OnPropertyChanged(); } }
        }

        /// <summary>检测到的浏览器内核引擎类型（如 "Electron", "CEF", "WebView2" 等）</summary>
        public string DetectedEngine
        {
            get => _engineType;
            set => EngineType = value;
        }

        public object? IconSource { get; set; }

        public long SizeBytes
        {
            get => _sizeBytes;
            set { if (_sizeBytes != value) { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeDisplay)); OnPropertyChanged(nameof(SizeGB)); } }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(NameForeground)); } }
        }

        public double SizeGB => Math.Round(SizeBytes / (1024.0 * 1024.0 * 1024.0), 2);

        public SolidColorBrush NameForeground => _isRunning ? _runningBrush : _normalBrush;
        public string TooltipText => string.IsNullOrEmpty(ExePath) ? InstallLocation : ExePath;

        private static readonly SolidColorBrush _runningBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x80, 0x00));
        private static readonly SolidColorBrush _normalBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00));

        public string SizeDisplay
        {
            get
            {
                if (SizeBytes <= 0) return "计算中...";
                if (SizeBytes >= 1024L * 1024 * 1024)
                    return $"{SizeBytes / (1024.0 * 1024 * 1024):F2} GB";
                if (SizeBytes >= 1024 * 1024)
                    return $"{SizeBytes / (1024.0 * 1024):F2} MB";
                if (SizeBytes >= 1024)
                    return $"{SizeBytes / 1024.0:F1} KB";
                return $"{SizeBytes} B";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}