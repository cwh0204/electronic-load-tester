using System;
using System.IO.Ports;
using System.Windows;
using ElectronicLoadTester.Services;
using ElectronicLoadTester.ViewModels;
using ElectronicLoadTester.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ElectronicLoadTester
{
    public partial class App : Application
    {
        private static IHost? _host;

        public static IServiceProvider Services => _host!.Services;

        public App()
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    // 1. 하드웨어 통합 관리 서비스 (싱글톤)
                    services.AddSingleton<IDeviceManagerService, DeviceManagerService>();

                    // 2. 뷰모델 등록
                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<SelfTestViewModel>(); // 팝업 시마다 상태 초기화

                    // 3. 뷰(윈도우) 등록
                    services.AddSingleton<MainWindow>();
                    services.AddTransient<SelfTestWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // base 호출을 먼저 실행하여 WPF Application 라이프사이클을 정상 초기화
            base.OnStartup(e);

            await _host!.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                // 1. 마지막 UI 설정값 영구 저장
                try
                {
                    var mainVm = _host.Services.GetService<MainViewModel>();
                    mainVm?.SaveCurrentUiToProfile();
                }
                catch { }

                // 2. 열려 있는 시리얼 통신 포트 안전 닫기
                try
                {
                    var devManager = _host.Services.GetService<IDeviceManagerService>();
                    if (devManager != null)
                    {
                        if (devManager.PowerPort?.IsOpen == true) devManager.PowerPort.Close();
                        if (devManager.LoadPort?.IsOpen == true) devManager.LoadPort.Close();
                        if (devManager.RelayPort?.IsOpen == true) devManager.RelayPort.Close();
                    }
                }
                catch { }

                await _host.StopAsync();
                _host.Dispose();
            }

            base.OnExit(e);
        }
    }
}