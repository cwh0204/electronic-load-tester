using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Models;
using ElectronicLoadTester.Services;
using ElectronicLoadTester.Views;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace ElectronicLoadTester.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDeviceManagerService _deviceManager;
        private readonly ITestResultHistoryService _historyService;
        private readonly string _settingsFilePath;
        private AppTestSettings _appSettings = new();
        private bool _isLoadingSettings = false;
        private CancellationTokenSource? _testCts;

        private bool _isRelaySwitching = false;
        private string? _pendingRailSwitch = null;

        // 차트 엔진
        private LinearAxis _xAxis = null!;
        private LinearAxis _yVoltAxis = null!;
        private LinearAxis _yCurrAxis = null!;
        private LineSeries _voltageSeries = null!;
        private LineSeries _currentSeries = null!;
        private double _chartElapsedSeconds;

        #region [하드웨어 연결 상태]
        [ObservableProperty] private bool _isPowerConnected = false;
        [ObservableProperty] private bool _isLoadConnected = false;
        [ObservableProperty] private bool _isRelayConnected = false;
        [ObservableProperty] private string _statusMessage = "시스템 대기 중 (장비 연결 필요)";
        #endregion

        #region [통신 포트 설정]
        public ObservableCollection<string> AvailableComPorts { get; } = new();
        [ObservableProperty] private string _owonComPort = "COM1";
        [ObservableProperty] private string _rigolComPort = "COM2";
        [ObservableProperty] private string _relayComPort = "COM3";
        #endregion

        #region [실시간 계측 HUD]
        [ObservableProperty] private double _livePowerVoltage = 0.0;
        [ObservableProperty] private double _livePowerCurrent = 0.0;
        [ObservableProperty] private double _liveLoadVoltage = 0.0;
        [ObservableProperty] private double _liveLoadCurrent = 0.0;

        [ObservableProperty] private string _currentStepDescription = "검사 대기 중";
        [ObservableProperty] private double _sequenceProgressPercent = 0.0;
        [ObservableProperty] private int _totalTestedCount = 0;
        [ObservableProperty] private int _totalPassedCount = 0;
        [ObservableProperty] private int _totalFailedCount = 0;
        [ObservableProperty] private string _finalTestVerdict = "READY";

        [ObservableProperty] private PlotModel _trendPlotModel = null!;
        public ObservableCollection<TestResultItem> TestResults { get; } = new();
        #endregion

        public MainViewModel(IDeviceManagerService deviceManager, ITestResultHistoryService historyService)
        {
            _deviceManager = deviceManager;
            _historyService = historyService;
            _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "test_spec_config.json");

            InitializeOxyPlot();
            RefreshComPorts();

            LoadSettings();
            UpdateStandbyResults();
            UpdateDefaultTestIdentifier();

            if (!string.IsNullOrEmpty(_deviceManager.RelayPortName))
                RelayComPort = _deviceManager.RelayPortName;
        }

        public MainViewModel() : this(
            App.Services.GetRequiredService<IDeviceManagerService>(),
            App.Services.GetRequiredService<ITestResultHistoryService>())
        {
        }

        #region [차트 초기화 및 동적 스케일링 엔진 (잘림 방지)]
        private void InitializeOxyPlot()
        {
            var model = new PlotModel
            {
                Background = OxyColors.White,
                PlotAreaBorderColor = OxyColor.FromRgb(226, 232, 240)
            };

            model.Legends.Add(new Legend
            {
                LegendPosition = LegendPosition.TopRight,
                LegendPlacement = LegendPlacement.Inside,
                LegendOrientation = LegendOrientation.Horizontal,
                LegendBackground = OxyColor.FromAColor(220, OxyColors.White),
                LegendBorder = OxyColor.FromRgb(226, 232, 240),
                LegendTextColor = OxyColor.FromRgb(71, 85, 105),
                LegendFontSize = 11
            });

            _xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Key = "TimeAxis",
                Title = "시간 (초)",
                TextColor = OxyColor.FromRgb(100, 116, 139),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(241, 245, 249),
                Minimum = 0,
                Maximum = 60,
                MajorStep = 10,
                MinorStep = 2,
                StringFormat = "0"
            };
            model.Axes.Add(_xAxis);

            _yVoltAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Key = "VoltAxis",
                Title = "전압 (V)",
                TextColor = OxyColor.FromRgb(37, 99, 235),
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromRgb(226, 232, 240),
                StringFormat = "0.0",
                Minimum = 0.0
            };
            model.Axes.Add(_yVoltAxis);

            _yCurrAxis = new LinearAxis
            {
                Position = AxisPosition.Right,
                Key = "CurrAxis",
                Title = "전류 (A)",
                TextColor = OxyColor.FromRgb(245, 158, 11),
                MajorGridlineStyle = LineStyle.None,
                StringFormat = "0.0",
                Minimum = 0.0
            };
            model.Axes.Add(_yCurrAxis);

            _voltageSeries = new LineSeries { Title = "전압(V)", XAxisKey = "TimeAxis", YAxisKey = "VoltAxis", Color = OxyColor.FromRgb(37, 99, 235), StrokeThickness = 2.0 };
            _currentSeries = new LineSeries { Title = "전류(A)", XAxisKey = "TimeAxis", YAxisKey = "CurrAxis", Color = OxyColor.FromRgb(245, 158, 11), StrokeThickness = 2.0 };

            model.Series.Add(_voltageSeries);
            model.Series.Add(_currentSeries);
            TrendPlotModel = model;

            UpdatePlotAxesForRail("5V");
        }

        private void UpdatePlotAxesForRail(string railName)
        {
            if (_yVoltAxis == null || _yCurrAxis == null) return;

            _yVoltAxis.Minimum = 0.0;
            switch (railName)
            {
                case "5V": _yVoltAxis.Maximum = 12.0; _yVoltAxis.MajorStep = 2.0; _yVoltAxis.MinorStep = 0.5; break;
                case "24V": _yVoltAxis.Maximum = 32.0; _yVoltAxis.MajorStep = 5.0; _yVoltAxis.MinorStep = 1.0; break;
                case "30V": _yVoltAxis.Maximum = 40.0; _yVoltAxis.MajorStep = 5.0; _yVoltAxis.MinorStep = 1.0; break;
                case "48V": _yVoltAxis.Maximum = 60.0; _yVoltAxis.MajorStep = 10.0; _yVoltAxis.MinorStep = 2.0; break;
                default: _yVoltAxis.Maximum = 15.0; _yVoltAxis.MajorStep = 2.0; _yVoltAxis.MinorStep = 0.5; break;
            }

            double baseTrip = GetTripCurrentByRail(railName);
            double baseEndur = GetCcCurrentByRail(railName);
            double maxExpectCurr = Math.Max(baseTrip, baseEndur);

            _yCurrAxis.Minimum = 0.0;
            _yCurrAxis.Maximum = Math.Max(4.0, Math.Ceiling(maxExpectCurr * 1.3));
            _yCurrAxis.MajorStep = (_yCurrAxis.Maximum <= 5.0) ? 0.5 : 1.0;
            _yCurrAxis.MinorStep = 0.1;

            TrendPlotModel.InvalidatePlot(true);
        }

        private void ResetPlot()
        {
            _chartElapsedSeconds = 0.0;
            _voltageSeries.Points.Clear();
            _currentSeries.Points.Clear();

            if (_xAxis != null)
            {
                _xAxis.Minimum = 0;
                _xAxis.Maximum = 60;
            }

            string currentRail = GetSelectedRailName();
            UpdatePlotAxesForRail(currentRail);
            TrendPlotModel.InvalidatePlot(true);
        }

        // [핵심] 상한 돌파 시 Y축을 실시간으로 확장하여 짤림 방지
        private void AppendPlotPoint(double voltage, double current)
        {
            _chartElapsedSeconds += 1.0;
            _voltageSeries.Points.Add(new DataPoint(_chartElapsedSeconds, voltage));
            _currentSeries.Points.Add(new DataPoint(_chartElapsedSeconds, current));

            // 전압 상한 돌파 감지
            if (voltage > _yVoltAxis.Maximum * 0.95)
            {
                _yVoltAxis.Maximum = Math.Ceiling(voltage * 1.25);
            }

            // 전류 상한 돌파 감지 (예: 6A 설정인데 9A까지 스위핑되는 경우 자동 확장)
            if (current > _yCurrAxis.Maximum * 0.95)
            {
                _yCurrAxis.Maximum = Math.Ceiling(current * 1.25);
                _yCurrAxis.MajorStep = (_yCurrAxis.Maximum > 10.0) ? 2.0 : 1.0;
            }

            // 시간축 60초 롤링 윈도우
            const double windowSpan = 60.0;
            if (_chartElapsedSeconds > windowSpan)
            {
                _xAxis.Minimum = _chartElapsedSeconds - windowSpan;
                _xAxis.Maximum = _chartElapsedSeconds;
            }
            else
            {
                _xAxis.Minimum = 0;
                _xAxis.Maximum = windowSpan;
            }

            TrendPlotModel.InvalidatePlot(true);
        }
        #endregion

        #region [헤더 커맨드]
        public void RefreshComPorts()
        {
            AvailableComPorts.Clear();
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0) { AvailableComPorts.Add("COM1"); AvailableComPorts.Add("COM2"); AvailableComPorts.Add("COM3"); }
            else { foreach (var port in ports) AvailableComPorts.Add(port); }
        }

        [RelayCommand]
        private async Task ScanAndConnectAllAsync()
        {
            StatusMessage = "장비 포트를 스캔하고 식별 중입니다...";
            RefreshComPorts();
            var (power, load, relay) = await _deviceManager.ScanAndIdentifyDevicesAsync();
            IsPowerConnected = power; IsLoadConnected = load; IsRelayConnected = relay;
            if (power) OwonComPort = _deviceManager.OwonPortName;
            if (load) RigolComPort = _deviceManager.RigolPortName;
            if (relay) RelayComPort = _deviceManager.RelayPortName;
            StatusMessage = (power && load && relay)
                ? $"장비 연결 완료 (파워: {OwonComPort}, 부하기: {RigolComPort}, 릴레이: {RelayComPort})"
                : $"스캔 완료: [파워: {(power ? OwonComPort : "미연결")}], [부하기: {(load ? RigolComPort : "미연결")}], [릴레이: {(relay ? RelayComPort : "미연결")}]";

            if (relay) await RequestRelaySwitchAsync(GetSelectedRailName());
        }

        [RelayCommand]
        private void OpenSettings()
        {
            try
            {
                RefreshComPorts();

                // [수정] 4개 레일 선택이 모두 풀려있는 상태면 직전 레일(5V)로 즉시 복원
                EnsureValidRailSelection();
                LoadProfileToUi(GetProfileBySystem(SelectedTargetSystem));

                var settingsWin = new SettingsWindow
                {
                    Owner = Application.Current.MainWindow,
                    DataContext = this
                };

                settingsWin.ShowDialog();

                // 설정창 종료 후 메인 UI 상태 및 요약 복원
                SyncTargetSystemBooleans(SelectedTargetSystem);
                UpdateRailSupportFlags(SelectedTargetSystem);
                EnsureValidRailSelection();
                UpdateStandbyResults();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"설정 창을 여는 중 오류 발생: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand] private void OpenSelfTestWindow() { var w = App.Services.GetRequiredService<SelfTestWindow>(); w.Owner = Application.Current.MainWindow; w.ShowDialog(); }
        [RelayCommand] private void OpenResultSearch() { var w = App.Services.GetRequiredService<ResultSearchWindow>(); w.Owner = Application.Current.MainWindow; w.ShowDialog(); }
        [RelayCommand] private void CloseSettings(Window? window) => window?.Close();
        #endregion
    }
}