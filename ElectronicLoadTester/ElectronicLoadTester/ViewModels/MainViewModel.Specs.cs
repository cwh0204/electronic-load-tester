using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Models;

namespace ElectronicLoadTester.ViewModels
{
    public partial class MainViewModel
    {
        #region [모듈 및 레일 선택 상태]
        [ObservableProperty] private bool _isTargetTcmsCc = true;
        [ObservableProperty] private bool _isTargetTcmsTc = false;
        [ObservableProperty] private bool _isTargetCi123 = false;
        [ObservableProperty] private bool _isTargetCi4 = false;
        [ObservableProperty] private bool _isTargetSiv123 = false;
        [ObservableProperty] private bool _isTargetSiv4 = false;
        [ObservableProperty] private string _selectedTargetSystem = "TCMS-CC";

        [ObservableProperty] private bool _is5VSupported = true;
        [ObservableProperty] private bool _is24VSupported = true;
        [ObservableProperty] private bool _is30VSupported = false;
        [ObservableProperty] private bool _is48VSupported = false;

        [ObservableProperty] private bool _is5VSelected = true;
        [ObservableProperty] private bool _is24VSelected = false;
        [ObservableProperty] private bool _is30VSelected = false;
        [ObservableProperty] private bool _is48VSelected = false;
        [ObservableProperty] private string _activeTestingChannelName = "5V 레일";

        [ObservableProperty] private bool _isModeCc = true;
        [ObservableProperty] private bool _isModeCv = false;

        [ObservableProperty] private string _testIdentifierName = string.Empty;
        [ObservableProperty] private string _summaryTripCondition = string.Empty;
        [ObservableProperty] private string _summaryEnduranceCondition = string.Empty;

        // 시험 중 UI 잠금 제어 플래그
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotTesting))]
        private bool _isTesting = false;
        public bool IsNotTesting => !IsTesting && FinalTestVerdict != "TESTING";

        partial void OnFinalTestVerdictChanged(string value)
        {
            if (value != "TESTING")
            {
                IsTesting = false;
            }
            OnPropertyChanged(nameof(IsNotTesting));
        }
        #endregion

        #region [레일별 규격 프로퍼티 (CC / CV / OCP / OVP / 시간)]
        // [1] CC 지속 부하 인가 전류 및 최저 판정 전압
        [ObservableProperty] private double _spec5V_CcCurrent = 2.00;
        [ObservableProperty] private double _spec5V_CcMinVolt = 4.75;
        [ObservableProperty] private double _spec24V_CcCurrent = 2.50;
        [ObservableProperty] private double _spec24V_CcMinVolt = 23.00;
        [ObservableProperty] private double _spec30V_CcCurrent = 1.50;
        [ObservableProperty] private double _spec30V_CcMinVolt = 28.50;
        [ObservableProperty] private double _spec48V_CcCurrent = 1.00;
        [ObservableProperty] private double _spec48V_CcMinVolt = 46.50;

        // [2] CV 지속 부하 인가 전압 및 최저 판정 전류
        [ObservableProperty] private double _spec5V_CvVolt = 5.00;
        [ObservableProperty] private double _spec5V_CvMinCurrent = 1.80;
        [ObservableProperty] private double _spec24V_CvVolt = 24.00;
        [ObservableProperty] private double _spec24V_CvMinCurrent = 2.20;
        [ObservableProperty] private double _spec30V_CvVolt = 30.00;
        [ObservableProperty] private double _spec30V_CvMinCurrent = 1.30;
        [ObservableProperty] private double _spec48V_CvVolt = 48.00;
        [ObservableProperty] private double _spec48V_CvMinCurrent = 0.80;

        // [3] CC 과전류 차단 시험 (OCP) - 시작 전류 및 목표 트립 전류
        [ObservableProperty] private double _spec5V_OcpStartCurrent = 2.00;
        [ObservableProperty] private double _spec5V_TripCurrent = 3.00;
        [ObservableProperty] private double _spec24V_OcpStartCurrent = 2.50;
        [ObservableProperty] private double _spec24V_TripCurrent = 3.50;
        [ObservableProperty] private double _spec30V_OcpStartCurrent = 1.50;
        [ObservableProperty] private double _spec30V_TripCurrent = 2.20;
        [ObservableProperty] private double _spec48V_OcpStartCurrent = 1.00;
        [ObservableProperty] private double _spec48V_TripCurrent = 1.80;

        // [4] CV 과전압 차단 시험 (OVP) - 시작 전압 및 목표 트립 전압
        [ObservableProperty] private double _spec5V_OvpStartVoltage = 5.00;
        [ObservableProperty] private double _spec5V_TripVoltage = 6.50;
        [ObservableProperty] private double _spec24V_OvpStartVoltage = 24.00;
        [ObservableProperty] private double _spec24V_TripVoltage = 30.00;
        [ObservableProperty] private double _spec30V_OvpStartVoltage = 30.00;
        [ObservableProperty] private double _spec30V_TripVoltage = 38.00;
        [ObservableProperty] private double _spec48V_OvpStartVoltage = 48.00;
        [ObservableProperty] private double _spec48V_TripVoltage = 58.00;

        // [5] 지속 시험 시간 (초)
        [ObservableProperty] private double _spec5V_EnduranceTime = 5.0;
        [ObservableProperty] private double _spec24V_EnduranceTime = 5.0;
        [ObservableProperty] private double _spec30V_EnduranceTime = 5.0;
        [ObservableProperty] private double _spec48V_EnduranceTime = 5.0;
        #endregion

        #region [속성 변경 리스너]
        partial void OnIs5VSelectedChanged(bool value)
        {
            if (_isLoadingSettings) return;
            if (value)
            {
                _isLoadingSettings = true;
                Is24VSelected = false; Is30VSelected = false; Is48VSelected = false;
                _isLoadingSettings = false;
                SwitchRailInternal("5V");
            }
            else EnsureAtLeastOneRailSelected();
        }

        partial void OnIs24VSelectedChanged(bool value)
        {
            if (_isLoadingSettings) return;
            if (value)
            {
                _isLoadingSettings = true;
                Is5VSelected = false; Is30VSelected = false; Is48VSelected = false;
                _isLoadingSettings = false;
                SwitchRailInternal("24V");
            }
            else EnsureAtLeastOneRailSelected();
        }

        partial void OnIs30VSelectedChanged(bool value)
        {
            if (_isLoadingSettings) return;
            if (value)
            {
                _isLoadingSettings = true;
                Is5VSelected = false; Is24VSelected = false; Is48VSelected = false;
                _isLoadingSettings = false;
                SwitchRailInternal("30V");
            }
            else EnsureAtLeastOneRailSelected();
        }

        partial void OnIs48VSelectedChanged(bool value)
        {
            if (_isLoadingSettings) return;
            if (value)
            {
                _isLoadingSettings = true;
                Is5VSelected = false; Is24VSelected = false; Is30VSelected = false;
                _isLoadingSettings = false;
                SwitchRailInternal("48V");
            }
            else EnsureAtLeastOneRailSelected();
        }

        private void EnsureAtLeastOneRailSelected()
        {
            if (_isLoadingSettings) return;
            if (!Is5VSelected && !Is24VSelected && !Is30VSelected && !Is48VSelected)
            {
                string fallback = _appSettings?.LastSelectedRail ?? "5V";
                SelectRailInternal(fallback);
            }
        }

        public void EnsureValidRailSelection()
        {
            EnsureAtLeastOneRailSelected();
        }

        [RelayCommand] public void SelectRail(string rail) => SelectRailInternal(rail);
        [RelayCommand] public void SwitchRail(string rail) => SelectRailInternal(rail);

        private void SelectRailInternal(string targetRail)
        {
            if (IsTesting) return;

            string rail = string.IsNullOrWhiteSpace(targetRail) ? "5V" : targetRail.Trim().ToUpper();
            bool supported = rail switch
            {
                "24V" => Is24VSupported,
                "30V" => Is30VSupported,
                "48V" => Is48VSupported,
                _ => true
            };

            if (!supported) rail = "5V";

            _isLoadingSettings = true;
            Is5VSelected = (rail == "5V");
            Is24VSelected = (rail == "24V");
            Is30VSelected = (rail == "30V");
            Is48VSelected = (rail == "48V");
            _isLoadingSettings = false;

            SwitchRailInternal(rail);
        }

        // ==================== [핵심] 레일 변경 시 클린 리셋 실행 ====================
        private void SwitchRailInternal(string rail)
        {
            if (IsTesting) return;

            ActiveTestingChannelName = $"{rail} 레일";

            // 1. 차트 파형 완전 초기화 및 축 재설정
            ResetPlot();
            _currentRunStepWaves.Clear();
            _lastDisplayedStepNo = -1;
            SelectedTestResult = null;

            // 2. 대시보드 상태 배너 및 진행률 리셋
            FinalTestVerdict = "READY";
            SequenceProgressPercent = 0;
            CurrentStepDescription = "시험 대기 중";

            UpdatePlotAxesForRail(rail);
            TrendPlotModel?.InvalidatePlot(true);

            // 3. 설정 저장, 신규 대기표 구성, 새 S/N 채번
            SaveCurrentUiToProfile();
            UpdateStandbyResults();
            UpdateDefaultTestIdentifier();

            // 4. 상단 통계 카운트 0으로 초기화
            TotalTestedCount = 0;
            TotalPassedCount = 0;
            TotalFailedCount = 0;

            // 5. 물리 릴레이 스위칭 요청
            _ = RequestRelaySwitchAsync(rail);
        }

        partial void OnIsModeCcChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                _isModeCv = false;
                OnPropertyChanged(nameof(IsModeCv));
                SaveCurrentUiToProfile();
                UpdateStandbyResults();
            }
        }

        partial void OnIsModeCvChanged(bool value)
        {
            if (value && !_isLoadingSettings)
            {
                _isModeCc = false;
                OnPropertyChanged(nameof(IsModeCc));
                SaveCurrentUiToProfile();
                UpdateStandbyResults();
            }
        }

        // CC 규격 변경 리스너
        partial void OnSpec5V_CcCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec5V_CcMinVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_CcCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_CcMinVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_CcCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_CcMinVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_CcCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_CcMinVoltChanged(double value) => SyncSpecChanged();

        // CV 규격 변경 리스너
        partial void OnSpec5V_CvVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec5V_CvMinCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_CvVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_CvMinCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_CvVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_CvMinCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_CvVoltChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_CvMinCurrentChanged(double value) => SyncSpecChanged();

        // OCP 시작치 및 트립치 변경 리스너
        partial void OnSpec5V_OcpStartCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec5V_TripCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_OcpStartCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_TripCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_OcpStartCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_TripCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_OcpStartCurrentChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_TripCurrentChanged(double value) => SyncSpecChanged();

        // OVP 시작치 및 트립치 변경 리스너
        partial void OnSpec5V_OvpStartVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec5V_TripVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_OvpStartVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_TripVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_OvpStartVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_TripVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_OvpStartVoltageChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_TripVoltageChanged(double value) => SyncSpecChanged();

        // 시간 변경 리스너
        partial void OnSpec5V_EnduranceTimeChanged(double value) => SyncSpecChanged();
        partial void OnSpec24V_EnduranceTimeChanged(double value) => SyncSpecChanged();
        partial void OnSpec30V_EnduranceTimeChanged(double value) => SyncSpecChanged();
        partial void OnSpec48V_EnduranceTimeChanged(double value) => SyncSpecChanged();

        private void SyncSpecChanged()
        {
            SaveCurrentUiToProfile();
            UpdateStandbyResults();
        }
        #endregion

        #region [프로필 JSON 입출력 & 헬퍼]
        private void UpdateDefaultTestIdentifier()
        {
            string rail = GetSelectedRailName();
            TestIdentifierName = $"{SelectedTargetSystem}_{rail}_{DateTime.Now:yyyyMMdd}";
        }

        public string GetSelectedRailName()
        {
            if (Is48VSelected) return "48V";
            if (Is30VSelected) return "30V";
            if (Is24VSelected) return "24V";
            return "5V";
        }

        // ==================== [핵심] 시험 대상 모듈 변경 시에도 클린 리셋 연동 ====================
        [RelayCommand]
        public void ChangeTargetSystem(string systemName)
        {
            if (IsTesting || SelectedTargetSystem == systemName) return;

            SaveCurrentUiToProfile();
            SelectedTargetSystem = systemName;
            SyncTargetSystemBooleans(systemName);
            UpdateRailSupportFlags(systemName);

            LoadProfileToUi(GetProfileBySystem(systemName));
            EnsureAtLeastOneRailSelected();

            // 모듈이 변경되었으므로 현재 선택된 레일에 맞춰 즉시 클린 리셋 실행
            string currentRail = GetSelectedRailName();
            SwitchRailInternal(currentRail);

            StatusMessage = $"{systemName} 선택 ({ActiveTestingChannelName} 릴레이 경로 투입)";
        }

        private void SyncTargetSystemBooleans(string sys)
        {
            IsTargetTcmsCc = (sys == "TCMS-CC");
            IsTargetTcmsTc = (sys == "TCMS-TC");
            IsTargetCi123 = (sys == "CI-123");
            IsTargetCi4 = (sys == "CI-4");
            IsTargetSiv123 = (sys == "SIV-123");
            IsTargetSiv4 = (sys == "SIV-4");
        }

        private void UpdateRailSupportFlags(string sys)
        {
            Is5VSupported = true;
            Is24VSupported = (sys == "TCMS-CC" || sys == "TCMS-TC" || sys == "SIV-123");
            Is30VSupported = (sys.StartsWith("CI") || sys.StartsWith("SIV"));
            Is48VSupported = sys.StartsWith("CI");

            if ((Is24VSelected && !Is24VSupported) || (Is30VSelected && !Is30VSupported) || (Is48VSelected && !Is48VSupported))
            {
                SelectRailInternal("5V");
            }
        }

        private void ValidateRailSelection(string targetRail)
        {
            bool supported = targetRail switch
            {
                "24V" => Is24VSupported,
                "30V" => Is30VSupported,
                "48V" => Is48VSupported,
                _ => true
            };

            string finalRail = supported ? targetRail : "5V";
            _isLoadingSettings = true;
            Is5VSelected = (finalRail == "5V");
            Is24VSelected = (finalRail == "24V");
            Is30VSelected = (finalRail == "30V");
            Is48VSelected = (finalRail == "48V");
            _isLoadingSettings = false;
        }

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

                SelectedTargetSystem = _appSettings.LastSelectedSystem ?? "TCMS-CC";
                SyncTargetSystemBooleans(SelectedTargetSystem);
                UpdateRailSupportFlags(SelectedTargetSystem);

                string lastMode = _appSettings.LastSelectedMode ?? "CC";
                IsModeCc = (lastMode == "CC");
                IsModeCv = (lastMode == "CV");

                string lastRail = _appSettings.LastSelectedRail ?? "5V";
                ValidateRailSelection(lastRail);
                ActiveTestingChannelName = $"{GetSelectedRailName()} 레일";

                LoadProfileToUi(GetProfileBySystem(SelectedTargetSystem));
            }
            catch (Exception ex) { Debug.WriteLine($"[설정 로드 실패] {ex.Message}"); }
            finally
            {
                _isLoadingSettings = false;
                EnsureAtLeastOneRailSelected();
                UpdateStandbyResults();
            }
        }

        public void SaveCurrentUiToProfile()
        {
            if (_isLoadingSettings) return;
            try
            {
                var profile = GetProfileBySystem(SelectedTargetSystem);

                // 5V
                profile.Rail5V.CcCurrent = Spec5V_CcCurrent; profile.Rail5V.CcMinVolt = Spec5V_CcMinVolt;
                profile.Rail5V.CvVolt = Spec5V_CvVolt; profile.Rail5V.CvMinCurrent = Spec5V_CvMinCurrent;
                profile.Rail5V.EnduranceTime = Spec5V_EnduranceTime;
                profile.Rail5V.OcpStartCurrent = Spec5V_OcpStartCurrent; profile.Rail5V.TripCurrent = Spec5V_TripCurrent;
                profile.Rail5V.OvpStartVoltage = Spec5V_OvpStartVoltage; profile.Rail5V.TripVoltage = Spec5V_TripVoltage;

                // 24V
                profile.Rail24V.CcCurrent = Spec24V_CcCurrent; profile.Rail24V.CcMinVolt = Spec24V_CcMinVolt;
                profile.Rail24V.CvVolt = Spec24V_CvVolt; profile.Rail24V.CvMinCurrent = Spec24V_CvMinCurrent;
                profile.Rail24V.EnduranceTime = Spec24V_EnduranceTime;
                profile.Rail24V.OcpStartCurrent = Spec24V_OcpStartCurrent; profile.Rail24V.TripCurrent = Spec24V_TripCurrent;
                profile.Rail24V.OvpStartVoltage = Spec24V_OvpStartVoltage; profile.Rail24V.TripVoltage = Spec24V_TripVoltage;

                // 30V
                profile.Rail30V.CcCurrent = Spec30V_CcCurrent; profile.Rail30V.CcMinVolt = Spec30V_CcMinVolt;
                profile.Rail30V.CvVolt = Spec30V_CvVolt; profile.Rail30V.CvMinCurrent = Spec30V_CvMinCurrent;
                profile.Rail30V.EnduranceTime = Spec30V_EnduranceTime;
                profile.Rail30V.OcpStartCurrent = Spec30V_OcpStartCurrent; profile.Rail30V.TripCurrent = Spec30V_TripCurrent;
                profile.Rail30V.OvpStartVoltage = Spec30V_OvpStartVoltage; profile.Rail30V.TripVoltage = Spec30V_TripVoltage;

                // 48V
                profile.Rail48V.CcCurrent = Spec48V_CcCurrent; profile.Rail48V.CcMinVolt = Spec48V_CcMinVolt;
                profile.Rail48V.CvVolt = Spec48V_CvVolt; profile.Rail48V.CvMinCurrent = Spec48V_CvMinCurrent;
                profile.Rail48V.EnduranceTime = Spec48V_EnduranceTime;
                profile.Rail48V.OcpStartCurrent = Spec48V_OcpStartCurrent; profile.Rail48V.TripCurrent = Spec48V_TripCurrent;
                profile.Rail48V.OvpStartVoltage = Spec48V_OvpStartVoltage; profile.Rail48V.TripVoltage = Spec48V_TripVoltage;

                _appSettings.LastSelectedSystem = SelectedTargetSystem;
                _appSettings.LastSelectedRail = GetSelectedRailName();
                _appSettings.LastSelectedMode = IsModeCc ? "CC" : "CV";

                string json = JsonSerializer.Serialize(_appSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex) { Debug.WriteLine($"[설정 저장 실패] {ex.Message}"); }
        }

        private SystemProfile GetProfileBySystem(string system) => system switch
        {
            "TCMS-TC" => _appSettings.TCMS_TC ?? _appSettings.TCMS,
            "CI-123" => _appSettings.CI_123 ?? _appSettings.CI,
            "CI-4" => _appSettings.CI_4 ?? _appSettings.CI,
            "SIV-123" => _appSettings.SIV_123 ?? _appSettings.SIV,
            "SIV-4" => _appSettings.SIV_4 ?? _appSettings.SIV,
            _ => _appSettings.TCMS_CC ?? _appSettings.TCMS
        };

        private void LoadProfileToUi(SystemProfile profile)
        {
            bool previousLock = _isLoadingSettings;
            _isLoadingSettings = true;
            try
            {
                // 5V
                Spec5V_CcCurrent = profile.Rail5V.CcCurrent; Spec5V_CcMinVolt = profile.Rail5V.CcMinVolt;
                Spec5V_CvVolt = profile.Rail5V.CvVolt; Spec5V_CvMinCurrent = profile.Rail5V.CvMinCurrent;
                Spec5V_EnduranceTime = profile.Rail5V.EnduranceTime;
                Spec5V_OcpStartCurrent = profile.Rail5V.OcpStartCurrent > 0 ? profile.Rail5V.OcpStartCurrent : 2.0;
                Spec5V_TripCurrent = profile.Rail5V.TripCurrent;
                Spec5V_OvpStartVoltage = profile.Rail5V.OvpStartVoltage > 0 ? profile.Rail5V.OvpStartVoltage : 5.0;
                Spec5V_TripVoltage = profile.Rail5V.TripVoltage > 0 ? profile.Rail5V.TripVoltage : 6.5;

                // 24V
                Spec24V_CcCurrent = profile.Rail24V.CcCurrent; Spec24V_CcMinVolt = profile.Rail24V.CcMinVolt;
                Spec24V_CvVolt = profile.Rail24V.CvVolt; Spec24V_CvMinCurrent = profile.Rail24V.CvMinCurrent;
                Spec24V_EnduranceTime = profile.Rail24V.EnduranceTime;
                Spec24V_OcpStartCurrent = profile.Rail24V.OcpStartCurrent > 0 ? profile.Rail24V.OcpStartCurrent : 2.5;
                Spec24V_TripCurrent = profile.Rail24V.TripCurrent;
                Spec24V_OvpStartVoltage = profile.Rail24V.OvpStartVoltage > 0 ? profile.Rail24V.OvpStartVoltage : 24.0;
                Spec24V_TripVoltage = profile.Rail24V.TripVoltage > 0 ? profile.Rail24V.TripVoltage : 30.0;

                // 30V
                Spec30V_CcCurrent = profile.Rail30V.CcCurrent; Spec30V_CcMinVolt = profile.Rail30V.CcMinVolt;
                Spec30V_CvVolt = profile.Rail30V.CvVolt; Spec30V_CvMinCurrent = profile.Rail30V.CvMinCurrent;
                Spec30V_EnduranceTime = profile.Rail30V.EnduranceTime;
                Spec30V_OcpStartCurrent = profile.Rail30V.OcpStartCurrent > 0 ? profile.Rail30V.OcpStartCurrent : 1.5;
                Spec30V_TripCurrent = profile.Rail30V.TripCurrent;
                Spec30V_OvpStartVoltage = profile.Rail30V.OvpStartVoltage > 0 ? profile.Rail30V.OvpStartVoltage : 30.0;
                Spec30V_TripVoltage = profile.Rail30V.TripVoltage > 0 ? profile.Rail30V.TripVoltage : 38.0;

                // 48V
                Spec48V_CcCurrent = profile.Rail48V.CcCurrent; Spec48V_CcMinVolt = profile.Rail48V.CcMinVolt;
                Spec48V_CvVolt = profile.Rail48V.CvVolt; Spec48V_CvMinCurrent = profile.Rail48V.CvMinCurrent;
                Spec48V_EnduranceTime = profile.Rail48V.EnduranceTime;
                Spec48V_OcpStartCurrent = profile.Rail48V.OcpStartCurrent > 0 ? profile.Rail48V.OcpStartCurrent : 1.0;
                Spec48V_TripCurrent = profile.Rail48V.TripCurrent;
                Spec48V_OvpStartVoltage = profile.Rail48V.OvpStartVoltage > 0 ? profile.Rail48V.OvpStartVoltage : 48.0;
                Spec48V_TripVoltage = profile.Rail48V.TripVoltage > 0 ? profile.Rail48V.TripVoltage : 58.0;
            }
            finally { _isLoadingSettings = previousLock; }
        }

        public void UpdateStandbyResults()
        {
            string railName = GetSelectedRailName();
            string testMode = IsModeCc ? "CC" : "CV";

            double enduranceTime = GetEnduranceTimeByRail(railName);
            double targetConditionVal = IsModeCc ? GetCcCurrentByRail(railName) : GetCvVoltByRail(railName);
            double specLimitVal = IsModeCc ? GetCcMinVoltByRail(railName) : GetCvMinCurrentByRail(railName);

            // 좌측 요약 카드 표기 정돈
            if (IsModeCc)
            {
                double startA = GetOcpStartCurrentByRail(railName);
                double tripA = GetTripCurrentByRail(railName);
                SummaryTripCondition = $"{startA:F2}A 시작 / 기준 {tripA:F2}A";
                SummaryEnduranceCondition = $"{targetConditionVal:F2}A 인가 (≥ {specLimitVal:F2}V, {enduranceTime:F0}초)";
            }
            else
            {
                double startV = GetOvpStartVoltageByRail(railName);
                double tripV = GetTripVoltageByRail(railName);
                SummaryTripCondition = $"{startV:F2}V 시작 / 기준 {tripV:F2}V";
                SummaryEnduranceCondition = $"{targetConditionVal:F2}V 인가 (≥ {specLimitVal:F2}A, {enduranceTime:F0}초)";
            }

            OnPropertyChanged(nameof(SummaryTripCondition));
            OnPropertyChanged(nameof(SummaryEnduranceCondition));

            if (FinalTestVerdict == "TESTING") return;

            TestResults.Clear();

            double tripSpecVal = IsModeCc ? GetTripCurrentByRail(railName) : GetTripVoltageByRail(railName);
            double startVal = IsModeCc ? GetOcpStartCurrentByRail(railName) : GetOvpStartVoltageByRail(railName);
            string tripSpecUnit = IsModeCc ? "A" : "V";

            // 1단계 차단 시험 (수치 범위 직관적 표기)
            TestResults.Add(new TestResultItem
            {
                StepNo = 1,
                ChannelName = railName,
                StepName = IsModeCc ? $"{railName} 과전류 차단 시험" : $"{railName} 과전압 차단 시험",
                Mode = testMode,
                TargetCondition = $"{startVal:F2}{tripSpecUnit} 스위핑",
                SpecRange = $"{tripSpecVal * 0.85:F2} ~ {tripSpecVal * 1.15:F2} {tripSpecUnit}",
                MeasuredVoltage = 0.0,
                MeasuredCurrent = 0.0,
                Result = "WAIT",
                Timestamp = "-"
            });

            // 2단계 지속 부하 시험 (부등호 기반 직관적 표기)
            TestResults.Add(new TestResultItem
            {
                StepNo = 2,
                ChannelName = railName,
                StepName = $"{railName} 지속 부하 시험 ({testMode})",
                Mode = testMode,
                TargetCondition = IsModeCc ? $"{targetConditionVal:F2}A ({enduranceTime:F0}초)" : $"{targetConditionVal:F2}V ({enduranceTime:F0}초)",
                SpecRange = IsModeCc ? $"≥ {specLimitVal:F2} V" : $"≥ {specLimitVal:F2} A",
                MeasuredVoltage = 0.0,
                MeasuredCurrent = 0.0,
                Result = "WAIT",
                Timestamp = "-"
            });
        }

        // 지속 부하 시험 시간
        public double GetEnduranceTimeByRail(string rail) => rail switch { "24V" => Spec24V_EnduranceTime, "30V" => Spec30V_EnduranceTime, "48V" => Spec48V_EnduranceTime, _ => Spec5V_EnduranceTime };

        // OCP 시작치 & 트립치
        public double GetOcpStartCurrentByRail(string rail) => rail switch { "24V" => Spec24V_OcpStartCurrent, "30V" => Spec30V_OcpStartCurrent, "48V" => Spec48V_OcpStartCurrent, _ => Spec5V_OcpStartCurrent };
        public double GetTripCurrentByRail(string rail) => rail switch { "24V" => Spec24V_TripCurrent, "30V" => Spec30V_TripCurrent, "48V" => Spec48V_TripCurrent, _ => Spec5V_TripCurrent };

        // OVP 시작치 & 트립치
        public double GetOvpStartVoltageByRail(string rail) => rail switch { "24V" => Spec24V_OvpStartVoltage, "30V" => Spec30V_OvpStartVoltage, "48V" => Spec48V_OvpStartVoltage, _ => Spec5V_OvpStartVoltage };
        public double GetTripVoltageByRail(string rail) => rail switch { "24V" => Spec24V_TripVoltage, "30V" => Spec30V_TripVoltage, "48V" => Spec48V_TripVoltage, _ => Spec5V_TripVoltage };

        // 지속 부하 인가치 & 최저 판정치
        public double GetCcCurrentByRail(string rail) => rail switch { "24V" => Spec24V_CcCurrent, "30V" => Spec30V_CcCurrent, "48V" => Spec48V_CcCurrent, _ => Spec5V_CcCurrent };
        public double GetCcMinVoltByRail(string rail) => rail switch { "24V" => Spec24V_CcMinVolt, "30V" => Spec30V_CcMinVolt, "48V" => Spec48V_CcMinVolt, _ => Spec5V_CcMinVolt };
        public double GetCvVoltByRail(string rail) => rail switch { "24V" => Spec24V_CvVolt, "30V" => Spec30V_CvVolt, "48V" => Spec48V_CvVolt, _ => Spec5V_CvVolt };
        public double GetCvMinCurrentByRail(string rail) => rail switch { "24V" => Spec24V_CvMinCurrent, "30V" => Spec30V_CvMinCurrent, "48V" => Spec48V_CvMinCurrent, _ => Spec5V_CvMinCurrent };
        #endregion
    }
}