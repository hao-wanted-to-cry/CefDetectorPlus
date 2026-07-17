using Microsoft.UI.Xaml;
using System;

namespace GoodNewsBrowserAppDetector
{
    /// <summary>
    /// 应用程序入口
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            this.InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 检查命令行参数 --no-bgm
            var cmdArgs = Environment.GetCommandLineArgs();
            bool noBgm = false;
            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (cmdArgs[i].Equals("--no-bgm", StringComparison.OrdinalIgnoreCase))
                {
                    noBgm = true;
                    break;
                }
            }

            _window = new MainWindow(noBgm);
            _window.Activate();
        }
    }
}