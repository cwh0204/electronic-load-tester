using System;
using System.Windows;
using ElectronicLoadTester.Hardware;
using ElectronicLoadTester.ViewModels;
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
                    // 1. 하드웨어 드라이버 싱글톤 등록
                    services.AddSingleton<IPowerSupply, OwonPowerSupply>();
                    services.AddSingleton<IElectronicLoad, RigolElectronicLoad>();
                    services.AddSingleton<IRelayController, SerialRelayController>();

                    // 2. 뷰모델 등록
                    services.AddSingleton<MainViewModel>();

                    // 3. 메인 윈도우 등록
                    services.AddSingleton<MainWindow>();
                })
                .Build();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            await _host!.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_host != null)
            {
                var power = _host.Services.GetService<IPowerSupply>();
                var load = _host.Services.GetService<IElectronicLoad>();
                var relay = _host.Services.GetService<IRelayController>();

                if (power != null) await power.DisposeAsync();
                if (load != null) await load.DisposeAsync();
                if (relay != null) await relay.DisposeAsync();

                await _host.StopAsync();
                _host.Dispose();
            }

            base.OnExit(e);
        }
    }
}