using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Models;
using ElectronicLoadTester.Views;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace ElectronicLoadTester.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private CancellationTokenSource? _testCts;

        // OxyPlot 차트 시리즈
        private LineSeries _voltageSeries = null!;
        private LineSeries _currentSeries = null!;
        private double _chartElapsedSeconds;

        #region [하드웨어 연결 상태]
        [ObservableProperty]
        private bool _isPowerConnected = false;

        [ObservableProperty]
        private bool _isLoadConnected = false;

        [ObservableProperty]
        private bool _isRelayConnected = false;

        [ObservableProperty]
        private string _statusMessage = "시스템 대기 중 (장비 연결 필요)";
        #endregion

        #region [SettingsWindow 바인딩: 통신 포트 및 수동 조작 프로퍼티]
        // PC에서 인식된 시리얼 COM 포트 목록
        public ObservableCollection<string> AvailableComPorts { get; } = new();

        // 1. OWON 파워 통신 포트
        [ObservableProperty]
        private string _owonComPort = "COM1";

        // 2. Rigol 부하기 통신 포트
        [ObservableProperty]
        private string _rigolComPort = "COM2";

        // 3. 485 릴레이 보드 통신 포트
        [ObservableProperty]
        private string _relayComPort = "COM3";

        // 수동 조작 - OWON 파워
        [ObservableProperty]
        private double _manualPowerSetVoltage = 24.0;

        [ObservableProperty]
        private double _manualPowerSetCurrent = 2.0;

        [ObservableProperty]
        private bool _manualPowerOutputEnabled = false;

        // 수동 조작 - Rigol 부하기
        [ObservableProperty]
        private double _manualLoadSetCurrent = 1.0;

        [ObservableProperty]
        private double _manualLoadSetVoltage = 0.0;

        [ObservableProperty]
        private bool _manualLoadInputEnabled = false;

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

        #region [대상 시스템 선택: TCMS / SIV / CI]
        [ObservableProperty]
        private bool _isTargetTcms = true;

        [ObservableProperty]
        private bool _isTargetSiv = false;

        [ObservableProperty]
        private bool _isTargetCi = false;

        [ObservableProperty]
        private string _selectedTargetSystem = "TCMS";
        #endregion

        #region [출력 레일 단일 선택: 5V / 24V / 30V]
        [ObservableProperty]
        private bool _is5VSelected = true;

        [ObservableProperty]
        private bool _is24VSelected = false;

        [ObservableProperty]
        private bool _is30VSelected = false;

        [ObservableProperty]
        private string _activeTestingChannelName = "5V 레일";

        partial void OnIs5VSelectedChanged(bool value)
        {
            if (value) ActiveTestingChannelName = "5V 레일";
        }

        partial void OnIs24VSelectedChanged(bool value)
        {
            if (value) ActiveTestingChannelName = "24V 레일";
        }

        partial void OnIs30VSelectedChanged(bool value)
        {
            if (value) ActiveTestingChannelName = "30V 레일";
        }
        #endregion

        #region [레일별 기준 규격값 (전류A / 전압MinV / 시간s / 트립A)]
        // 5V 레일
        [ObservableProperty]
        private double _spec5V_RatedCurrent = 2.00;

        [ObservableProperty]
        private double _spec5V_MinVoltage = 4.75;

        [ObservableProperty]
        private double _spec5V_EnduranceTime = 5.0;

        [ObservableProperty]
        private double _spec5V_TripCurrent = 3.00;

        // 24V 레일
        [ObservableProperty]
        private double _spec24V_RatedCurrent = 2.50;

        [ObservableProperty]
        private double _spec24V_MinVoltage = 23.00;

        [ObservableProperty]
        private double _spec24V_EnduranceTime = 5.0;

        [ObservableProperty]
        private double _spec24V_TripCurrent = 3.50;

        // 30V 레일
        [ObservableProperty]
        private double _spec30V_RatedCurrent = 1.50;

        [ObservableProperty]
        private double _spec30V_MinVoltage = 28.50;

        [ObservableProperty]
        private double _spec30V_EnduranceTime = 5.0;

        [ObservableProperty]
        private double _spec30V_TripCurrent = 2.20;
        #endregion

        #region [실시간 계측 모니터]
        // 공급 파워 (OWON)
        [ObservableProperty]
        private double _livePowerVoltage = 0.0;

        [ObservableProperty]
        private double _livePowerCurrent = 0.0;

        // 출력 측정 (RIGOL 부하)
        [ObservableProperty]
        private double _liveLoadVoltage = 0.0;

        [ObservableProperty]
        private double _liveLoadCurrent = 0.0;
        #endregion

        #region [시퀀스 진행률 및 집계]
        [ObservableProperty]
        private string _currentStepDescription = "검사 대기 중";

        [ObservableProperty]
        private double _sequenceProgressPercent = 0.0;

        [ObservableProperty]
        private int _totalTestedCount = 0;

        [ObservableProperty]
        private int _totalPassedCount = 0;

        [ObservableProperty]
        private int _totalFailedCount = 0;

        [ObservableProperty]
        private string _finalTestVerdict = "READY";
        #endregion

        #region [차트 및 데이터그리드 컬렉션]
        [ObservableProperty]
        private PlotModel _trendPlotModel = null!;

        public ObservableCollection<TestResultItem> TestResults { get; } = new();
        #endregion

        public MainViewModel()
        {
            InitializeOxyPlot();
            ApplyPreset("TCMS");
            RefreshComPorts();
            InitializeSampleResults();
        }

        #region [OxyPlot 초기화 및 실시간 업데이트]
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

        #region [타겟 시스템 프리셋 전환 커맨드]
        [RelayCommand]
        private void ChangeTargetSystem(string systemName)
        {
            SelectedTargetSystem = systemName;
            IsTargetTcms = (systemName == "TCMS");
            IsTargetSiv = (systemName == "SIV");
            IsTargetCi = (systemName == "CI");

            ApplyPreset(systemName);
            StatusMessage = $"{systemName} 파워모듈 시험 사양이 로딩되었습니다.";
        }

        private void ApplyPreset(string system)
        {
            switch (system)
            {
                case "TCMS":
                    Spec5V_RatedCurrent = 2.00; Spec5V_MinVoltage = 4.75; Spec5V_TripCurrent = 3.00; Spec5V_EnduranceTime = 5.0;
                    Spec24V_RatedCurrent = 2.50; Spec24V_MinVoltage = 23.00; Spec24V_TripCurrent = 3.50; Spec24V_EnduranceTime = 5.0;
                    Spec30V_RatedCurrent = 1.50; Spec30V_MinVoltage = 28.50; Spec30V_TripCurrent = 2.20; Spec30V_EnduranceTime = 5.0;
                    break;

                case "SIV":
                    Spec5V_RatedCurrent = 3.00; Spec5V_MinVoltage = 4.80; Spec5V_TripCurrent = 4.50; Spec5V_EnduranceTime = 5.0;
                    Spec24V_RatedCurrent = 4.00; Spec24V_MinVoltage = 22.50; Spec24V_TripCurrent = 6.00; Spec24V_EnduranceTime = 5.0;
                    Spec30V_RatedCurrent = 2.00; Spec30V_MinVoltage = 28.00; Spec30V_TripCurrent = 3.00; Spec30V_EnduranceTime = 5.0;
                    break;

                case "CI":
                    Spec5V_RatedCurrent = 2.50; Spec5V_MinVoltage = 4.70; Spec5V_TripCurrent = 3.80; Spec5V_EnduranceTime = 5.0;
                    Spec24V_RatedCurrent = 5.00; Spec24V_MinVoltage = 22.80; Spec24V_TripCurrent = 7.50; Spec24V_EnduranceTime = 5.0;
                    Spec30V_RatedCurrent = 3.00; Spec30V_MinVoltage = 28.20; Spec30V_TripCurrent = 4.50; Spec30V_EnduranceTime = 5.0;
                    break;
            }
        }
        #endregion

        #region [상단 헤더 버튼 커맨드 (스캔 / 자가진단 / 검색 / 설정)]
        [RelayCommand]
        private async Task ScanAndConnectAllAsync()
        {
            StatusMessage = "장비 포트를 스캔하고 연결 중입니다...";
            await Task.Delay(800);

            IsPowerConnected = true;
            IsLoadConnected = true;
            IsRelayConnected = true;

            LivePowerVoltage = 24.02;
            LivePowerCurrent = 0.35;
            StatusMessage = "모든 계측 장비(OWON, Rigol, 485 Relay)가 정상 연결되었습니다.";
        }

        [RelayCommand]
        private void OpenSelfTestWindow()
        {
            try
            {
                var selfTestWin = new SelfTestWindow
                {
                    Owner = Application.Current.MainWindow
                };
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
                var searchWin = new ResultSearchWindow
                {
                    Owner = Application.Current.MainWindow
                };
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

        #region [SettingsWindow 바인딩 커맨드: 장비별 연결 및 수동 제어]
        [RelayCommand]
        private async Task ConnectOwonAsync()
        {
            StatusMessage = $"OWON 파워 연결 중... ({OwonComPort})";
            await Task.Delay(300);
            IsPowerConnected = true;
            StatusMessage = $"OWON 파워가 {OwonComPort}에 연결되었습니다.";
        }

        [RelayCommand]
        private async Task ConnectRigolAsync()
        {
            StatusMessage = $"Rigol 부하기 연결 중... ({RigolComPort})";
            await Task.Delay(300);
            IsLoadConnected = true;
            StatusMessage = $"Rigol 부하기가 {RigolComPort}에 연결되었습니다.";
        }

        [RelayCommand]
        private async Task ConnectRelayAsync()
        {
            StatusMessage = $"485 릴레이 보드 연결 중... ({RelayComPort})";
            await Task.Delay(300);
            IsRelayConnected = true;
            StatusMessage = $"485 릴레이 보드가 {RelayComPort}에 연결되었습니다.";
        }

        [RelayCommand]
        private void ApplyPowerSettings()
        {
            StatusMessage = $"OWON 파워 수동 설정 적용: {ManualPowerSetVoltage:F2}V / {ManualPowerSetCurrent:F2}A";
        }

        [RelayCommand]
        private void ApplyLoadSettings()
        {
            StatusMessage = $"Rigol 부하기 수동 설정 적용: {ManualLoadSetCurrent:F2}A";
        }

        [RelayCommand]
        private void SelectChannelRelay(string channel)
        {
            StatusMessage = $"릴레이 제어: 채널 {channel} 선택 활성화";
        }

        [RelayCommand]
        private void CloseSettings(Window? window)
        {
            window?.Close();
        }
        #endregion

        #region [검사 시퀀스 제어 (시작 / 긴급정지)]
        [RelayCommand]
        private async Task StartSequenceAsync()
        {
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            var token = _testCts.Token;

            string railName = Is5VSelected ? "5V" : (Is24VSelected ? "24V" : "30V");
            double ratedCurr = Is5VSelected ? Spec5V_RatedCurrent : (Is24VSelected ? Spec24V_RatedCurrent : Spec30V_RatedCurrent);
            double minVolt = Is5VSelected ? Spec5V_MinVoltage : (Is24VSelected ? Spec24V_MinVoltage : Spec30V_MinVoltage);
            double enduranceTime = Is5VSelected ? Spec5V_EnduranceTime : (Is24VSelected ? Spec24V_EnduranceTime : Spec30V_EnduranceTime);
            double tripCurr = Is5VSelected ? Spec5V_TripCurrent : (Is24VSelected ? Spec24V_TripCurrent : Spec30V_TripCurrent);

            ActiveTestingChannelName = $"{railName} 레일";
            FinalTestVerdict = "TESTING";
            ResetPlot();
            TestResults.Clear();

            try
            {
                StatusMessage = $"{SelectedTargetSystem} - {railName} 레일 자동 검사를 시작합니다.";

                // [STEP 1: 지속 부하 시험 (설정 시간 연동)]
                CurrentStepDescription = $"[1/2] {railName} 지속 부하 시험 중 ({enduranceTime:F0}초, 정격: {ratedCurr:F2}A)";
                SequenceProgressPercent = 20;

                bool endurancePass = true;
                double measuredVolt = 0.0;
                double measuredCurr = 0.0;

                int totalSteps = Math.Max(10, (int)(enduranceTime * 10));

                for (int i = 0; i < totalSteps; i++)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(100, token);

                    measuredVolt = (Is5VSelected ? 4.98 : (Is24VSelected ? 23.85 : 29.75)) + (Random.Shared.NextDouble() * 0.04 - 0.02);
                    measuredCurr = ratedCurr + (Random.Shared.NextDouble() * 0.02 - 0.01);

                    LiveLoadVoltage = measuredVolt;
                    LiveLoadCurrent = measuredCurr;
                    AppendPlotPoint(measuredVolt, measuredCurr);

                    if (measuredVolt < minVolt)
                    {
                        endurancePass = false;
                    }

                    SequenceProgressPercent = 20 + ((double)i / totalSteps * 30.0);
                }

                TestResults.Add(new TestResultItem
                {
                    StepNo = 1,
                    ChannelName = railName,
                    StepName = $"{railName} 지속 부하 시험",
                    Mode = "CC",
                    TargetCondition = $"{ratedCurr:F2} A ({enduranceTime:F0}s)",
                    SpecRange = $"Min {minVolt:F2} V",
                    MeasuredVoltage = measuredVolt,
                    MeasuredCurrent = measuredCurr,
                    Result = endurancePass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                });

                if (!endurancePass)
                {
                    FinishSequence(false);
                    return;
                }

                // [STEP 2: 과전류 차단 시험 (트립)]
                CurrentStepDescription = $"[2/2] {railName} 과전류 차단 시험 중 (트립 목표: {tripCurr:F2}A)";
                SequenceProgressPercent = 60;

                double sweepCurr = ratedCurr;
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

                FinishSequence(tripPass);
            }
            catch (OperationCanceledException)
            {
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
        private void EmergencyStop()
        {
            _testCts?.Cancel();
            LiveLoadVoltage = 0;
            LiveLoadCurrent = 0;
            StatusMessage = "긴급 정지 실행: 모든 부하 인가를 즉시 차단했습니다.";
        }
        #endregion

        #region [결과 초기화 샘플]
        private void InitializeSampleResults()
        {
            TestResults.Add(new TestResultItem
            {
                StepNo = 1,
                ChannelName = "5V",
                StepName = "5V 지속 부하 시험",
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