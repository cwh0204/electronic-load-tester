using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
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
using OxyPlot.Series;

namespace ElectronicLoadTester.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDeviceManagerService _deviceManager;
        private readonly string _settingsFilePath;
        private AppTestSettings _appSettings = new();
        private bool _isLoadingSettings = false;
        private CancellationTokenSource? _testCts;

        // [핵심] 릴레이 스위칭 동시성 제어 및 최신 요청 예약 큐
        private bool _isRelaySwitching = false;
        private string? _pendingRailSwitch = null;

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

        [ObservableProperty] private double _manualPowerSetVoltage = 24.0;
        [ObservableProperty] private double _manualPowerSetCurrent = 2.0;
        [ObservableProperty] private bool _manualPowerOutputEnabled = false;

        [ObservableProperty] private double _manualLoadSetCurrent = 1.0;
        [ObservableProperty] private double _manualLoadSetVoltage = 0.0;
        [ObservableProperty] private bool _manualLoadInputEnabled = false;
        #endregion

        #region [대상 시스템 선택: TCMS / SIV / CI]
        [ObservableProperty] private bool _isTargetTcms = true;
        [ObservableProperty] private bool _isTargetSiv = false;
        [ObservableProperty] private bool _isTargetCi = false;
        [ObservableProperty] private string _selectedTargetSystem = "TCMS";

        [ObservableProperty] private bool _is24VSupported = true;
        [ObservableProperty] private bool _is30VSupported = false;
        #endregion

        #region [출력 레일 선택: 5V / 24V / 30V]
        [ObservableProperty] private bool _is5VSelected = true;
        [ObservableProperty] private bool _is24VSelected = false;
        [ObservableProperty] private bool _is30VSelected = false;
        [ObservableProperty] private string _activeTestingChannelName = "5V 레일";

        partial void OnIs5VSelectedChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                ActiveTestingChannelName = "5V 레일";
                SaveCurrentUiToProfile();
                _ = RequestRelaySwitchAsync("5V"); // 클릭 즉시 명시적 전달
            }
        }

        partial void OnIs24VSelectedChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                ActiveTestingChannelName = "24V 레일";
                SaveCurrentUiToProfile();
                _ = RequestRelaySwitchAsync("24V"); // 클릭 즉시 명시적 전달
            }
        }

        partial void OnIs30VSelectedChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                ActiveTestingChannelName = "30V 레일";
                SaveCurrentUiToProfile();
                _ = RequestRelaySwitchAsync("30V"); // 클릭 즉시 명시적 전달
            }
        }
        #endregion

        #region [시험 모드 선택: CC / CV]
        [ObservableProperty] private bool _isModeCc = true;
        [ObservableProperty] private bool _isModeCv = false;

        partial void OnIsModeCcChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                _isModeCv = false;
                OnPropertyChanged(nameof(IsModeCv));
                SaveCurrentUiToProfile();
            }
        }

        partial void OnIsModeCvChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                _isModeCc = false;
                OnPropertyChanged(nameof(IsModeCc));
                SaveCurrentUiToProfile();
            }
        }
        #endregion

        #region [CC 모드 규격값]
        [ObservableProperty] private double _spec5V_CcCurrent = 2.00;
        [ObservableProperty] private double _spec5V_CcMinVolt = 4.75;

        [ObservableProperty] private double _spec24V_CcCurrent = 2.50;
        [ObservableProperty] private double _spec24V_CcMinVolt = 23.00;

        [ObservableProperty] private double _spec30V_CcCurrent = 1.50;
        [ObservableProperty] private double _spec30V_CcMinVolt = 28.50;

        partial void OnSpec5V_CcCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec5V_CcMinVoltChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec24V_CcCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec24V_CcMinVoltChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec30V_CcCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec30V_CcMinVoltChanged(double value) => SaveCurrentUiToProfile();
        #endregion

        #region [CV 모드 규격값]
        [ObservableProperty] private double _spec5V_CvVolt = 5.00;
        [ObservableProperty] private double _spec5V_CvMinCurrent = 1.80;

        [ObservableProperty] private double _spec24V_CvVolt = 24.00;
        [ObservableProperty] private double _spec24V_CvMinCurrent = 2.20;

        [ObservableProperty] private double _spec30V_CvVolt = 30.00;
        [ObservableProperty] private double _spec30V_CvMinCurrent = 1.30;

        partial void OnSpec5V_CvVoltChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec5V_CvMinCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec24V_CvVoltChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec24V_CvMinCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec30V_CvVoltChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec30V_CvMinCurrentChanged(double value) => SaveCurrentUiToProfile();
        #endregion

        #region [공통 시간 & 차단 기준]
        [ObservableProperty] private double _spec5V_EnduranceTime = 5.0;
        [ObservableProperty] private double _spec5V_TripCurrent = 3.00;

        [ObservableProperty] private double _spec24V_EnduranceTime = 5.0;
        [ObservableProperty] private double _spec24V_TripCurrent = 3.50;

        [ObservableProperty] private double _spec30V_EnduranceTime = 5.0;
        [ObservableProperty] private double _spec30V_TripCurrent = 2.20;

        partial void OnSpec5V_EnduranceTimeChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec5V_TripCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec24V_EnduranceTimeChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec24V_TripCurrentChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec30V_EnduranceTimeChanged(double value) => SaveCurrentUiToProfile();
        partial void OnSpec30V_TripCurrentChanged(double value) => SaveCurrentUiToProfile();
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

        public MainViewModel(IDeviceManagerService deviceManager)
        {
            _deviceManager = deviceManager;
            _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "test_spec_config.json");

            InitializeOxyPlot();
            RefreshComPorts();
            InitializeSampleResults();

            LoadSettings();

            if (!string.IsNullOrEmpty(_deviceManager.RelayPortName))
            {
                RelayComPort = _deviceManager.RelayPortName;
            }
        }

        public MainViewModel() : this(DeviceManagerService.Instance)
        {
        }

        #region [물리 릴레이 실시간 스위칭 로직 (최신 요청 예약 큐 적용)]
        private async Task RequestRelaySwitchAsync(string targetRail)
        {
            if (_isLoadingSettings) return;

            // 이미 스위칭 통신이 진행 중이면 최신 목표로 예약하고 리턴 (이벤트 증발 방지)
            if (_isRelaySwitching)
            {
                _pendingRailSwitch = targetRail;
                return;
            }

            _isRelaySwitching = true;
            _pendingRailSwitch = null;

            try
            {
                string railToSwitch = targetRail;

                while (true)
                {
                    string system = SelectedTargetSystem;

                    // 릴레이 보드로 직접 선택된 시스템과 레일 명령 전송
                    bool success = await _deviceManager.ConnectTargetRailRelayAsync(system, railToSwitch);

                    if (success)
                    {
                        StatusMessage = $"릴레이 투입 완료: {system} {railToSwitch} 레일 연결";
                    }
                    else
                    {
                        StatusMessage = $"[안내] {system} {railToSwitch} 레일은 지원하지 않습니다. (릴레이 차단)";
                    }

                    // 통신 도중 사용자가 누른 더 최신 요청이 들어왔는지 확인
                    if (_pendingRailSwitch != null && _pendingRailSwitch != railToSwitch)
                    {
                        railToSwitch = _pendingRailSwitch;
                        _pendingRailSwitch = null;
                        continue; // 최신 요청으로 즉시 다시 스위칭
                    }

                    break;
                }
            }
            finally
            {
                _isRelaySwitching = false;
                _pendingRailSwitch = null;
            }
        }
        #endregion

        #region [설정 파일 JSON 입출력]
        public void LoadSettings()
        {
            try
            {
                _isLoadingSettings = true;

                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppTestSettings>(json);
                    if (loaded != null) _appSettings = loaded;
                }

                SelectedTargetSystem = _appSettings.LastSelectedSystem ?? "TCMS";
                IsTargetTcms = (SelectedTargetSystem == "TCMS");
                IsTargetSiv = (SelectedTargetSystem == "SIV");
                IsTargetCi = (SelectedTargetSystem == "CI");

                UpdateRailSupportFlags(SelectedTargetSystem);

                string lastMode = _appSettings.LastSelectedMode ?? "CC";
                IsModeCc = (lastMode == "CC");
                IsModeCv = (lastMode == "CV");

                string lastRail = _appSettings.LastSelectedRail ?? "5V";
                if (SelectedTargetSystem == "TCMS" && lastRail == "30V") lastRail = "5V";
                if (SelectedTargetSystem == "SIV" && lastRail == "24V") lastRail = "5V";

                _is5VSelected = (lastRail == "5V");
                _is24VSelected = (lastRail == "24V");
                _is30VSelected = (lastRail == "30V");
                OnPropertyChanged(nameof(Is5VSelected));
                OnPropertyChanged(nameof(Is24VSelected));
                OnPropertyChanged(nameof(Is30VSelected));

                ActiveTestingChannelName = $"{lastRail} 레일";

                LoadProfileToUi(GetProfileBySystem(SelectedTargetSystem));
                StatusMessage = $"{SelectedTargetSystem} 이전 검사 사양이 정상 복원되었습니다.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[설정 로드 실패] {ex.Message}");
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        public void SaveCurrentUiToProfile()
        {
            if (_isLoadingSettings) return;

            try
            {
                var profile = GetProfileBySystem(SelectedTargetSystem);

                profile.Rail5V.CcCurrent = Spec5V_CcCurrent;
                profile.Rail5V.CcMinVolt = Spec5V_CcMinVolt;
                profile.Rail5V.CvVolt = Spec5V_CvVolt;
                profile.Rail5V.CvMinCurrent = Spec5V_CvMinCurrent;
                profile.Rail5V.EnduranceTime = Spec5V_EnduranceTime;
                profile.Rail5V.TripCurrent = Spec5V_TripCurrent;

                profile.Rail24V.CcCurrent = Spec24V_CcCurrent;
                profile.Rail24V.CcMinVolt = Spec24V_CcMinVolt;
                profile.Rail24V.CvVolt = Spec24V_CvVolt;
                profile.Rail24V.CvMinCurrent = Spec24V_CvMinCurrent;
                profile.Rail24V.EnduranceTime = Spec24V_EnduranceTime;
                profile.Rail24V.TripCurrent = Spec24V_TripCurrent;

                profile.Rail30V.CcCurrent = Spec30V_CcCurrent;
                profile.Rail30V.CcMinVolt = Spec30V_CcMinVolt;
                profile.Rail30V.CvVolt = Spec30V_CvVolt;
                profile.Rail30V.CvMinCurrent = Spec30V_CvMinCurrent;
                profile.Rail30V.EnduranceTime = Spec30V_EnduranceTime;
                profile.Rail30V.TripCurrent = Spec30V_TripCurrent;

                _appSettings.LastSelectedSystem = SelectedTargetSystem;
                _appSettings.LastSelectedRail = Is30VSelected ? "30V" : (Is24VSelected ? "24V" : "5V");
                _appSettings.LastSelectedMode = IsModeCc ? "CC" : "CV";

                string json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[설정 저장 실패] {ex.Message}");
            }
        }

        private SystemProfile GetProfileBySystem(string system)
        {
            return system switch
            {
                "SIV" => _appSettings.SIV,
                "CI" => _appSettings.CI,
                _ => _appSettings.TCMS
            };
        }

        private void LoadProfileToUi(SystemProfile profile)
        {
            bool previousLock = _isLoadingSettings;
            _isLoadingSettings = true;
            try
            {
                Spec5V_CcCurrent = profile.Rail5V.CcCurrent;
                Spec5V_CcMinVolt = profile.Rail5V.CcMinVolt;
                Spec24V_CcCurrent = profile.Rail24V.CcCurrent;
                Spec24V_CcMinVolt = profile.Rail24V.CcMinVolt;
                Spec30V_CcCurrent = profile.Rail30V.CcCurrent;
                Spec30V_CcMinVolt = profile.Rail30V.CcMinVolt;

                Spec5V_CvVolt = profile.Rail5V.CvVolt;
                Spec5V_CvMinCurrent = profile.Rail5V.CvMinCurrent;
                Spec24V_CvVolt = profile.Rail24V.CvVolt;
                Spec24V_CvMinCurrent = profile.Rail24V.CvMinCurrent;
                Spec30V_CvVolt = profile.Rail30V.CvVolt;
                Spec30V_CvMinCurrent = profile.Rail30V.CvMinCurrent;

                Spec5V_EnduranceTime = profile.Rail5V.EnduranceTime;
                Spec5V_TripCurrent = profile.Rail5V.TripCurrent;
                Spec24V_EnduranceTime = profile.Rail24V.EnduranceTime;
                Spec24V_TripCurrent = profile.Rail24V.TripCurrent;
                Spec30V_EnduranceTime = profile.Rail30V.EnduranceTime;
                Spec30V_TripCurrent = profile.Rail30V.TripCurrent;
            }
            finally
            {
                _isLoadingSettings = previousLock;
            }
        }
        #endregion

        #region [타겟 시스템 전환 커맨드]
        [RelayCommand]
        private void ChangeTargetSystem(string systemName)
        {
            if (SelectedTargetSystem == systemName) return;

            SaveCurrentUiToProfile();

            SelectedTargetSystem = systemName;
            IsTargetTcms = (systemName == "TCMS");
            IsTargetSiv = (systemName == "SIV");
            IsTargetCi = (systemName == "CI");

            UpdateRailSupportFlags(systemName);

            // 미지원 레일 자동 안전 보정
            _isLoadingSettings = true;
            if (systemName == "TCMS" && Is30VSelected)
            {
                _is5VSelected = true;
                _is30VSelected = false;
                OnPropertyChanged(nameof(Is5VSelected));
                OnPropertyChanged(nameof(Is30VSelected));
                ActiveTestingChannelName = "5V 레일";
            }
            else if (systemName == "SIV" && Is24VSelected)
            {
                _is5VSelected = true;
                _is24VSelected = false;
                OnPropertyChanged(nameof(Is5VSelected));
                OnPropertyChanged(nameof(Is24VSelected));
                ActiveTestingChannelName = "5V 레일";
            }
            _isLoadingSettings = false;

            LoadProfileToUi(GetProfileBySystem(systemName));
            SaveCurrentUiToProfile();

            string currentRail = Is30VSelected ? "30V" : (Is24VSelected ? "24V" : "5V");
            _ = RequestRelaySwitchAsync(currentRail);

            StatusMessage = $"{systemName} 파워모듈 선택 ({ActiveTestingChannelName} 릴레이 경로 투입)";
        }

        private void UpdateRailSupportFlags(string systemName)
        {
            if (systemName == "TCMS")
            {
                Is24VSupported = true;
                Is30VSupported = false;
            }
            else if (systemName == "SIV")
            {
                Is24VSupported = false;
                Is30VSupported = true;
            }
            else // CI
            {
                Is24VSupported = true;
                Is30VSupported = true;
            }
        }
        #endregion

        #region [포트 목록 새로고침]
        public void RefreshComPorts()
        {
            AvailableComPorts.Clear();
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                AvailableComPorts.Add("COM1");
                AvailableComPorts.Add("COM2");
                AvailableComPorts.Add("COM3");
            }
            else
            {
                foreach (var port in ports)
                {
                    AvailableComPorts.Add(port);
                }
            }
        }
        #endregion

        #region [차트 초기화]
        private void InitializeOxyPlot()
        {
            var model = new PlotModel
            {
                Background = OxyColors.White,
                PlotAreaBorderColor = OxyColor.FromRgb(226, 232, 240)
            };

            var xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "시간 (초)",
                TextColor = OxyColor.FromRgb(100, 116, 139),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(241, 245, 249)
            };
            model.Axes.Add(xAxis);

            var yVoltAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Key = "VoltAxis",
                Title = "전압 (V)",
                TextColor = OxyColor.FromRgb(37, 99, 235),
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromRgb(226, 232, 240)
            };
            model.Axes.Add(yVoltAxis);

            var yCurrAxis = new LinearAxis
            {
                Position = AxisPosition.Right,
                Key = "CurrAxis",
                Title = "전류 (A)",
                TextColor = OxyColor.FromRgb(245, 158, 11),
                MajorGridlineStyle = LineStyle.None
            };
            model.Axes.Add(yCurrAxis);

            _voltageSeries = new LineSeries
            {
                Title = "전압",
                YAxisKey = "VoltAxis",
                Color = OxyColor.FromRgb(37, 99, 235),
                StrokeThickness = 2.0
            };

            _currentSeries = new LineSeries
            {
                Title = "전류",
                YAxisKey = "CurrAxis",
                Color = OxyColor.FromRgb(245, 158, 11),
                StrokeThickness = 2.0
            };

            model.Series.Add(_voltageSeries);
            model.Series.Add(_currentSeries);
            TrendPlotModel = model;
        }

        private void ResetPlot()
        {
            _chartElapsedSeconds = 0.0;
            _voltageSeries.Points.Clear();
            _currentSeries.Points.Clear();
            TrendPlotModel.InvalidatePlot(true);
        }

        private void AppendPlotPoint(double voltage, double current)
        {
            _chartElapsedSeconds += 0.1;
            _voltageSeries.Points.Add(new DataPoint(_chartElapsedSeconds, voltage));
            _currentSeries.Points.Add(new DataPoint(_chartElapsedSeconds, current));
            TrendPlotModel.InvalidatePlot(true);
        }
        #endregion

        #region [상단 헤더 버튼 커맨드]
        [RelayCommand]
        private async Task ScanAndConnectAllAsync()
        {
            StatusMessage = "장비 포트를 스캔하고 식별 중입니다...";
            RefreshComPorts();

            var (power, load, relay) = await _deviceManager.ScanAndIdentifyDevicesAsync();

            IsPowerConnected = power;
            IsLoadConnected = load;
            IsRelayConnected = relay;

            if (power) OwonComPort = _deviceManager.OwonPortName;
            if (load) RigolComPort = _deviceManager.RigolPortName;
            if (relay) RelayComPort = _deviceManager.RelayPortName;

            StatusMessage = (power && load && relay)
                ? $"모든 장비 연결 완료 (파워: {OwonComPort}, 부하기: {RigolComPort}, 릴레이: {RelayComPort})"
                : $"스캔 완료: [파워: {(power ? OwonComPort : "미연결")}], [부하기: {(load ? RigolComPort : "미연결")}], [릴레이: {(relay ? RelayComPort : "미연결")}]";

            if (relay)
            {
                string curRail = Is30VSelected ? "30V" : (Is24VSelected ? "24V" : "5V");
                await RequestRelaySwitchAsync(curRail);
            }
        }

        [RelayCommand]
        private void OpenSelfTestWindow()
        {
            try
            {
                var selfTestWin = App.Services.GetRequiredService<SelfTestWindow>();
                selfTestWin.Owner = Application.Current.MainWindow;
                selfTestWin.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"자가진단 창을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void OpenResultSearch()
        {
            try
            {
                var searchWin = new ResultSearchWindow { Owner = Application.Current.MainWindow };
                searchWin.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"결과 검색 창을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void OpenSettings()
        {
            try
            {
                RefreshComPorts();
                var settingsWin = new SettingsWindow
                {
                    Owner = Application.Current.MainWindow,
                    DataContext = this
                };
                settingsWin.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"통신 설정 창을 열 수 없습니다: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region [SettingsWindow 수동 제어 커맨드]
        [RelayCommand]
        private async Task ConnectOwonAsync()
        {
            StatusMessage = $"OWON 파워 연결 중... ({OwonComPort})";
            _deviceManager.OwonPortName = OwonComPort;
            await Task.Delay(300);
            IsPowerConnected = true;
            StatusMessage = $"OWON 파워가 {OwonComPort}에 연결되었습니다.";
        }

        [RelayCommand]
        private async Task ConnectRigolAsync()
        {
            StatusMessage = $"Rigol 부하기 연결 중... ({RigolComPort})";
            _deviceManager.RigolPortName = RigolComPort;
            await Task.Delay(300);
            IsLoadConnected = true;
            StatusMessage = $"Rigol 부하기가 {RigolComPort}에 연결되었습니다.";
        }

        [RelayCommand]
        private async Task ConnectRelayAsync()
        {
            StatusMessage = $"485 릴레이 보드 연결 중... ({RelayComPort})";
            _deviceManager.RelayPortName = RelayComPort;
            await Task.Delay(300);
            IsRelayConnected = true;
            StatusMessage = $"485 릴레이 보드가 {RelayComPort}에 연결되었습니다.";
            string curRail = Is30VSelected ? "30V" : (Is24VSelected ? "24V" : "5V");
            await RequestRelaySwitchAsync(curRail);
        }

        [RelayCommand]
        private void ApplyPowerSettings() => StatusMessage = $"OWON 수동 적용: {ManualPowerSetVoltage:F2}V / {ManualPowerSetCurrent:F2}A";

        [RelayCommand]
        private void ApplyLoadSettings() => StatusMessage = $"Rigol 수동 적용: {ManualLoadSetCurrent:F2}A";

        [RelayCommand]
        private void SelectChannelRelay(string channel) => StatusMessage = $"릴레이 제어: 채널 {channel} 선택 활성화";

        [RelayCommand]
        private void CloseSettings(Window? window) => window?.Close();
        #endregion

        #region [검사 시퀀스 제어]
        [RelayCommand]
        private async Task StartSequenceAsync()
        {
            SaveCurrentUiToProfile();

            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            var token = _testCts.Token;

            string railName = Is30VSelected ? "30V" : (Is24VSelected ? "24V" : "5V");
            string testMode = IsModeCc ? "CC" : "CV";

            double enduranceTime = Is5VSelected ? Spec5V_EnduranceTime : (Is24VSelected ? Spec24V_EnduranceTime : Spec30V_EnduranceTime);
            double tripCurr = Is5VSelected ? Spec5V_TripCurrent : (Is24VSelected ? Spec24V_TripCurrent : Spec30V_TripCurrent);

            double targetConditionVal;
            double specLimitVal;
            string conditionText;
            string specRangeText;

            if (IsModeCc)
            {
                targetConditionVal = Is5VSelected ? Spec5V_CcCurrent : (Is24VSelected ? Spec24V_CcCurrent : Spec30V_CcCurrent);
                specLimitVal = Is5VSelected ? Spec5V_CcMinVolt : (Is24VSelected ? Spec24V_CcMinVolt : Spec30V_CcMinVolt);
                conditionText = $"{targetConditionVal:F2} A ({enduranceTime:F0}s)";
                specRangeText = $"Min {specLimitVal:F2} V";
            }
            else
            {
                targetConditionVal = Is5VSelected ? Spec5V_CvVolt : (Is24VSelected ? Spec24V_CvVolt : Spec30V_CvVolt);
                specLimitVal = Is5VSelected ? Spec5V_CvMinCurrent : (Is24VSelected ? Spec24V_CvMinCurrent : Spec30V_CvMinCurrent);
                conditionText = $"{targetConditionVal:F2} V ({enduranceTime:F0}s)";
                specRangeText = $"Min {specLimitVal:F2} A";
            }

            ActiveTestingChannelName = $"{railName} 레일";
            FinalTestVerdict = "TESTING";
            ResetPlot();
            TestResults.Clear();

            try
            {
                // [1단계] 출력 레일 릴레이 투입 확인
                await RequestRelaySwitchAsync(railName);
                await Task.Delay(200, token);

                // [2단계] 메인 100V 전원 공급 (릴레이 7번 ON)
                CurrentStepDescription = "[전원 투입] 100V 메인 릴레이(7번) ON...";
                await _deviceManager.SetMain100VRelayAsync(true);
                LivePowerVoltage = 100.0;
                LivePowerCurrent = 0.50;
                await Task.Delay(300, token);

                StatusMessage = $"{SelectedTargetSystem} - {railName} 레일 {testMode} 자동 검사를 시작합니다.";

                // [3단계] 지속 부하 시험
                CurrentStepDescription = $"[1/2] {railName} 지속 부하 시험 ({testMode}) 중 ({conditionText})";
                SequenceProgressPercent = 20;

                bool endurancePass = true;
                double measuredVolt = 0.0;
                double measuredCurr = 0.0;

                int totalSteps = Math.Max(10, (int)(enduranceTime * 10));

                for (int i = 0; i < totalSteps; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(100, token);

                    if (IsModeCc)
                    {
                        double nominalVolt = Is5VSelected ? 4.98 : (Is24VSelected ? 23.85 : 29.75);
                        measuredVolt = nominalVolt + (Random.Shared.NextDouble() * 0.04 - 0.02);
                        measuredCurr = targetConditionVal + (Random.Shared.NextDouble() * 0.02 - 0.01);
                        if (measuredVolt < specLimitVal) endurancePass = false;
                    }
                    else
                    {
                        measuredVolt = targetConditionVal + (Random.Shared.NextDouble() * 0.02 - 0.01);
                        double nominalCurr = specLimitVal + 0.35;
                        measuredCurr = nominalCurr + (Random.Shared.NextDouble() * 0.04 - 0.02);
                        if (measuredCurr < specLimitVal) endurancePass = false;
                    }

                    LiveLoadVoltage = measuredVolt;
                    LiveLoadCurrent = measuredCurr;
                    AppendPlotPoint(measuredVolt, measuredCurr);

                    SequenceProgressPercent = 20 + ((double)i / totalSteps * 30.0);
                }

                TestResults.Add(new TestResultItem
                {
                    StepNo = 1,
                    ChannelName = railName,
                    StepName = $"{railName} 지속 부하 시험 ({testMode})",
                    Mode = testMode,
                    TargetCondition = conditionText,
                    SpecRange = specRangeText,
                    MeasuredVoltage = measuredVolt,
                    MeasuredCurrent = measuredCurr,
                    Result = endurancePass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                });

                if (!endurancePass)
                {
                    await _deviceManager.SetMain100VRelayAsync(false);
                    await _deviceManager.ResetAllRelaysAsync();
                    FinishSequence(false);
                    return;
                }

                // [4단계] 과전류 차단 시험
                CurrentStepDescription = $"[2/2] {railName} 과전류 차단 시험 중 (트립 목표: {tripCurr:F2}A)";
                SequenceProgressPercent = 60;

                double sweepCurr = IsModeCc ? targetConditionVal : 2.0;
                bool tripPass = false;

                while (sweepCurr <= tripCurr * 1.3)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(150, token);

                    sweepCurr += 0.2;
                    SequenceProgressPercent = Math.Min(95, SequenceProgressPercent + 3);

                    if (sweepCurr >= tripCurr)
                    {
                        LiveLoadVoltage = 0.05;
                        LiveLoadCurrent = 0.01;
                        AppendPlotPoint(LiveLoadVoltage, LiveLoadCurrent);
                        tripPass = true;
                        break;
                    }
                    else
                    {
                        measuredVolt = (Is5VSelected ? 4.90 : (Is24VSelected ? 23.50 : 29.30));
                        LiveLoadVoltage = measuredVolt;
                        LiveLoadCurrent = sweepCurr;
                        AppendPlotPoint(measuredVolt, sweepCurr);
                    }
                }

                TestResults.Add(new TestResultItem
                {
                    StepNo = 2,
                    ChannelName = railName,
                    StepName = $"{railName} 과전류 차단 시험",
                    Mode = "CC",
                    TargetCondition = "트립 점검",
                    SpecRange = $"Trip @ ~{tripCurr:F2} A",
                    MeasuredVoltage = LiveLoadVoltage,
                    MeasuredCurrent = sweepCurr,
                    Result = tripPass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                });

                await _deviceManager.SetMain100VRelayAsync(false);
                await _deviceManager.ResetAllRelaysAsync();
                LivePowerVoltage = 0.0;
                LivePowerCurrent = 0.0;

                FinishSequence(tripPass);
            }
            catch (OperationCanceledException)
            {
                await _deviceManager.SetMain100VRelayAsync(false);
                await _deviceManager.ResetAllRelaysAsync();
                LivePowerVoltage = 0.0;
                LivePowerCurrent = 0.0;

                StatusMessage = "작업자에 의해 검사가 긴급 중지되었습니다.";
                CurrentStepDescription = "긴급 정지됨";
                FinalTestVerdict = "FAIL";
            }
        }

        private void FinishSequence(bool isAllPassed)
        {
            SequenceProgressPercent = 100;
            TotalTestedCount++;

            if (isAllPassed)
            {
                TotalPassedCount++;
                FinalTestVerdict = "PASS";
                CurrentStepDescription = "검사 완료 (정상)";
                StatusMessage = $"{ActiveTestingChannelName} 검사 결과 정상(PASS) 판정되었습니다.";
            }
            else
            {
                TotalFailedCount++;
                FinalTestVerdict = "FAIL";
                CurrentStepDescription = "검사 불합격 (기준 규격 미달)";
                StatusMessage = $"{ActiveTestingChannelName} 검사 결과 불합격(FAIL) 판정되었습니다.";
            }
        }

        [RelayCommand]
        private async Task EmergencyStopAsync()
        {
            _testCts?.Cancel();
            LiveLoadVoltage = 0;
            LiveLoadCurrent = 0;
            LivePowerVoltage = 0;
            LivePowerCurrent = 0;

            await _deviceManager.SetMain100VRelayAsync(false);
            await _deviceManager.ResetAllRelaysAsync();

            StatusMessage = "긴급 정지 실행: 100V 메인 전원 및 모든 릴레이를 즉시 차단했습니다.";
        }
        #endregion

        #region [초기 샘플 데이터]
        private void InitializeSampleResults()
        {
            TestResults.Add(new TestResultItem
            {
                StepNo = 1,
                ChannelName = "5V",
                StepName = "5V 지속 부하 시험 (CC)",
                Mode = "CC",
                TargetCondition = "2.00 A (5s)",
                SpecRange = "Min 4.75 V",
                MeasuredVoltage = 0.0,
                MeasuredCurrent = 0.0,
                Result = "WAIT",
                Timestamp = "-"
            });
            TestResults.Add(new TestResultItem
            {
                StepNo = 2,
                ChannelName = "5V",
                StepName = "5V 과전류 차단 시험",
                Mode = "CC",
                TargetCondition = "트립 점검",
                SpecRange = "Trip @ ~3.00 A",
                MeasuredVoltage = 0.0,
                MeasuredCurrent = 0.0,
                Result = "WAIT",
                Timestamp = "-"
            });
        }
        #endregion
    }
}