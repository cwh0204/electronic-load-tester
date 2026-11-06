using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Models;
using OxyPlot;

namespace ElectronicLoadTester.ViewModels
{
    public partial class MainViewModel
    {
        // 현재 검사의 스텝별 파형 캐시 (1 -> 차단 파형, 2 -> 지속 파형)
        private readonly Dictionary<int, List<ChartPointDto>> _currentRunStepWaves = new();

        // 직전에 렌더링된 스텝 번호 캐시 (중복 연산 방지)
        private int _lastDisplayedStepNo = -1;

        // 사용자가 하단 "단계별 검사 결과 명세" 표에서 선택한 행
        [ObservableProperty]
        private TestResultItem? _selectedTestResult;

        #region [표 클릭 시 해당 스텝 파형 차트 복원 (비동기 디스패칭 최적화)]
        partial void OnSelectedTestResultChanged(TestResultItem? value)
        {
            // 시험 진행 중이거나 null이면 무시
            if (IsTesting || value == null) return;

            // 이미 동일한 스텝이 렌더링되어 있다면 중복 연산 차단 (씹힘 방지)
            if (_lastDisplayedStepNo == value.StepNo) return;

            // UI 스레드의 마우스/키보드 입력 처리가 밀리지 않도록 Background 우선순위로 비동기 렌더링
            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => DisplayStepWaveform(value.StepNo)));
        }

        private void DisplayStepWaveform(int stepNo)
        {
            if (!_currentRunStepWaves.TryGetValue(stepNo, out var points) || points == null || points.Count == 0)
            {
                StatusMessage = $"[{SelectedTestResult?.StepName}]에 저장된 실시간 파형이 없습니다.";
                return;
            }

            _lastDisplayedStepNo = stepNo;

            ResetPlot();

            double maxCurrentInPoints = 0.0;
            double maxVoltageInPoints = 0.0;

            // 대량 포인트 메모리 일괄 생성 및 AddRange 적용
            var vPoints = new List<DataPoint>(points.Count);
            var cPoints = new List<DataPoint>(points.Count);

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                vPoints.Add(new DataPoint(pt.Time, pt.Volt));
                cPoints.Add(new DataPoint(pt.Time, pt.Curr));

                if (pt.Curr > maxCurrentInPoints) maxCurrentInPoints = pt.Curr;
                if (pt.Volt > maxVoltageInPoints) maxVoltageInPoints = pt.Volt;
            }

            _voltageSeries.Points.AddRange(vPoints);
            _currentSeries.Points.AddRange(cPoints);

            // 전체 순회(Max) 대신 정렬된 시계열 데이터의 마지막 인덱스 직접 참조
            double maxTime = points[^1].Time;
            _xAxis.Minimum = 0;
            _xAxis.Maximum = Math.Max(10.0, Math.Ceiling(maxTime * 1.1));

            double xSpan = _xAxis.Maximum - _xAxis.Minimum;
            if (xSpan <= 15.0) { _xAxis.MajorStep = 2.0; _xAxis.MinorStep = 0.5; }
            else if (xSpan <= 35.0) { _xAxis.MajorStep = 5.0; _xAxis.MinorStep = 1.0; }
            else if (xSpan <= 80.0) { _xAxis.MajorStep = 10.0; _xAxis.MinorStep = 2.0; }
            else { _xAxis.MajorStep = 20.0; _xAxis.MinorStep = 5.0; }

            // Y축 범위 동적 피팅
            string rail = GetSelectedRailName();
            UpdatePlotAxesForRail(rail);
            if (maxVoltageInPoints > _yVoltAxis.Maximum * 0.95)
                _yVoltAxis.Maximum = Math.Ceiling(maxVoltageInPoints * 1.2);
            if (maxCurrentInPoints > _yCurrAxis.Maximum * 0.95)
                _yCurrAxis.Maximum = Math.Max(4.0, Math.Ceiling(maxCurrentInPoints * 1.2));

            TrendPlotModel?.InvalidatePlot(true);
            StatusMessage = $"[{SelectedTestResult?.StepName}] 구간 파형 표출 완료 (측정 포인트: {points.Count}개)";
        }
        #endregion

        #region [물리 릴레이 실시간 스위칭]
        private async Task RequestRelaySwitchAsync(string targetRail)
        {
            if (_isLoadingSettings) return;

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
                    bool success = await _deviceManager.ConnectTargetRailRelayAsync(system, railToSwitch);
                    StatusMessage = success
                        ? $"릴레이 투입 완료: {system} {railToSwitch} 레일 연결"
                        : $"[안내] {system} {railToSwitch} 레일은 지원하지 않습니다. (릴레이 차단)";

                    if (_pendingRailSwitch != null && _pendingRailSwitch != railToSwitch)
                    {
                        railToSwitch = _pendingRailSwitch;
                        _pendingRailSwitch = null;
                        continue;
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

        #region [하드웨어 부팅 및 리부팅 헬퍼]
        private async Task<bool> BootModulePowerAsync(string railName, CancellationToken token)
        {
            CurrentStepDescription = $"[준비] {SelectedTargetSystem} {railName} 릴레이 경로 투입 중...";
            await RequestRelaySwitchAsync(railName);
            await Task.Delay(200, token);

            CurrentStepDescription = "[전원 투입] 파워 100V 공급 및 메인 릴레이 ON...";
            await _deviceManager.SetPowerSupplyOutputAsync(100.0, 1.5, true);
            await _deviceManager.SetMain100VRelayAsync(true);

            CurrentStepDescription = "[기동 대기] 전원 모듈 부팅 및 무부하 출력 안정화 대기 중...";
            await Task.Delay(1300, token);

            var (initPVolt, initPCurr) = await _deviceManager.MeasurePowerSupplyAsync();
            var (initLVolt, _) = await _deviceManager.MeasureLoadAsync();

            LivePowerVoltage = initPVolt; LivePowerCurrent = initPCurr;
            LiveLoadVoltage = initLVolt;

            if (initLVolt < 1.0)
            {
                StatusMessage = $"[기동 실패] {railName} 무부하 전압 미발생 (실측: {initLVolt:F2}V)";
                return false;
            }
            return true;
        }

        private async Task RebootModulePowerAsync(string railName, CancellationToken token)
        {
            CurrentStepDescription = "[재부팅] 트립 해제를 위한 전원 차단 및 방전 대기 중...";
            await _deviceManager.SetLoadInputAsync(false);
            await _deviceManager.SetPowerSupplyOutputAsync(0, 0, false);
            await _deviceManager.SetMain100VRelayAsync(false);
            await Task.Delay(1500, token);

            CurrentStepDescription = "[재부팅] 100V 전원 재인가 및 모듈 재기동 중...";
            await _deviceManager.SetPowerSupplyOutputAsync(100.0, 1.5, true);
            await _deviceManager.SetMain100VRelayAsync(true);
            await Task.Delay(1300, token);
        }
        #endregion

        #region [SCPI 시험 엔진: 차단 스위핑 & 지속 통전]
        private async Task<(bool pass, double tripVal, double finalOtherVal, List<ChartPointDto> wave)> RunTripTestInternalAsync(
            string railName, CancellationToken token, double basePercent = 10.0, double targetPercent = 50.0)
        {
            var wave = new List<ChartPointDto>();
            ResetPlot();

            if (IsModeCc)
            {
                // ==================== CC 모드: 과전류 차단 (OCP) ====================
                double startCurr = GetOcpStartCurrentByRail(railName);
                double tripTargetCurr = GetTripCurrentByRail(railName);
                double stepCurr = 0.10;
                const double cutoffVolt = 1.00; // 0V 급락 감지 기준
                const double hardwareSafetyMax = 30.0;

                await _deviceManager.PrepareLoadForCcSweepAsync(startCurr);
                await Task.Delay(200, token);

                double currentTestValue = startCurr;
                double actualTripCurrent = 0.0;
                double finalVolt = 0.0;
                bool isDead = false;

                double expectedMaxCurr = Math.Min(hardwareSafetyMax, tripTargetCurr * 1.25);
                double sweepSpan = Math.Max(0.5, expectedMaxCurr - startCurr);

                while (currentTestValue <= hardwareSafetyMax)
                {
                    token.ThrowIfCancellationRequested();

                    double sweepFraction = Math.Clamp((currentTestValue - startCurr) / sweepSpan, 0.0, 1.0);
                    SequenceProgressPercent = Math.Round(basePercent + (sweepFraction * (targetPercent - basePercent)), 1);

                    await _deviceManager.SetLoadCurrentImmediateAsync(currentTestValue);
                    // [수정] '스위핑' 제거 -> 직관적인 '인가 중' 표기
                    CurrentStepDescription = $"[차단 시험] {railName} CC 과전류: {currentTestValue:F2} A 인가 중... ({SequenceProgressPercent:F0}%)";

                    int elapsed = 0;
                    bool stepFailed = false;
                    double lastVolt = 0.0;
                    double lastCurr = 0.0;

                    while (elapsed < 1000)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(200, token);
                        elapsed += 200;

                        var (pVolt, pCurr) = await _deviceManager.MeasurePowerSupplyAsync();
                        var (lVolt, lCurr) = await _deviceManager.MeasureLoadAsync();

                        lastVolt = lVolt; lastCurr = lCurr;
                        LivePowerVoltage = pVolt; LivePowerCurrent = pCurr;
                        LiveLoadVoltage = lastVolt; LiveLoadCurrent = lastCurr;

                        if (lastVolt < cutoffVolt)
                        {
                            stepFailed = true;
                            finalVolt = lastVolt;
                            actualTripCurrent = currentTestValue;
                            break;
                        }
                    }

                    if (stepFailed)
                    {
                        isDead = true;
                        AppendPlotPoint(finalVolt, lastCurr);
                        wave.Add(new ChartPointDto(_chartElapsedSeconds, finalVolt, lastCurr));

                        await _deviceManager.SetLoadInputAsync(false);
                        await Task.Delay(500, token);
                        AppendPlotPoint(finalVolt, 0.0);
                        wave.Add(new ChartPointDto(_chartElapsedSeconds, finalVolt, 0.0));
                        break;
                    }
                    else
                    {
                        AppendPlotPoint(lastVolt, lastCurr);
                        wave.Add(new ChartPointDto(_chartElapsedSeconds, lastVolt, lastCurr));
                        currentTestValue += stepCurr;
                    }
                }

                bool tripPass = isDead && (actualTripCurrent >= tripTargetCurr * 0.85 && actualTripCurrent <= tripTargetCurr * 1.15);
                return (tripPass, actualTripCurrent, finalVolt, wave);
            }
            else
            {
                // ==================== CV 모드: 과전압 차단 (OVP) ====================
                double startVolt = GetOvpStartVoltageByRail(railName);
                double tripTargetVolt = GetTripVoltageByRail(railName);
                double endVolt = Math.Max(tripTargetVolt * 1.25, startVolt + 3.0);
                double stepVolt = 0.20;
                const double cutoffCurr = 0.05; // 0A 급락 감지 기준

                await _deviceManager.PrepareLoadForCvSweepAsync(startVolt);
                await Task.Delay(200, token);

                double currentTestValue = startVolt;
                double actualTripVolt = 0.0;
                double finalCurr = 0.0;
                bool isDead = false;

                double sweepSpan = Math.Max(1.0, endVolt - startVolt);

                while (currentTestValue <= endVolt)
                {
                    token.ThrowIfCancellationRequested();

                    double sweepFraction = Math.Clamp((currentTestValue - startVolt) / sweepSpan, 0.0, 1.0);
                    SequenceProgressPercent = Math.Round(basePercent + (sweepFraction * (targetPercent - basePercent)), 1);

                    await _deviceManager.SetLoadVoltageImmediateAsync(currentTestValue);
                    // [수정] '스위핑' 제거 -> 직관적인 '인가 중' 표기
                    CurrentStepDescription = $"[차단 시험] {railName} CV 과전압: {currentTestValue:F2} V 인가 중... ({SequenceProgressPercent:F0}%)";

                    int elapsed = 0;
                    bool stepFailed = false;
                    double lastVolt = 0.0;
                    double lastCurr = 0.0;

                    while (elapsed < 1000)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(200, token);
                        elapsed += 200;

                        var (pVolt, pCurr) = await _deviceManager.MeasurePowerSupplyAsync();
                        var (lVolt, lCurr) = await _deviceManager.MeasureLoadAsync();

                        lastVolt = lVolt; lastCurr = lCurr;
                        LivePowerVoltage = pVolt; LivePowerCurrent = pCurr;
                        LiveLoadVoltage = lastVolt; LiveLoadCurrent = lastCurr;

                        if (currentTestValue > startVolt && lastCurr < cutoffCurr)
                        {
                            stepFailed = true;
                            actualTripVolt = currentTestValue;
                            finalCurr = lastCurr;
                            break;
                        }
                    }

                    if (stepFailed)
                    {
                        isDead = true;
                        AppendPlotPoint(lastVolt, finalCurr);
                        wave.Add(new ChartPointDto(_chartElapsedSeconds, lastVolt, finalCurr));

                        await _deviceManager.SetLoadInputAsync(false);
                        await Task.Delay(500, token);
                        AppendPlotPoint(0.0, 0.0);
                        wave.Add(new ChartPointDto(_chartElapsedSeconds, 0.0, 0.0));
                        break;
                    }
                    else
                    {
                        AppendPlotPoint(lastVolt, lastCurr);
                        wave.Add(new ChartPointDto(_chartElapsedSeconds, lastVolt, lastCurr));
                        currentTestValue += stepVolt;
                    }
                }

                bool tripPass = isDead && (actualTripVolt >= tripTargetVolt * 0.85 && actualTripVolt <= tripTargetVolt * 1.15);
                return (tripPass, actualTripVolt, finalCurr, wave);
            }
        }

        private async Task<(bool pass, double lastV, double lastA, List<ChartPointDto> wave)> RunEnduranceTestInternalAsync(
            string railName, CancellationToken token, double basePercent = 60.0, double targetPercent = 95.0)
        {
            var wave = new List<ChartPointDto>();
            ResetPlot();

            string testMode = IsModeCc ? "CC" : "CV";
            double enduranceTime = GetEnduranceTimeByRail(railName);
            double targetConditionVal = IsModeCc ? GetCcCurrentByRail(railName) : GetCvVoltByRail(railName);
            double specLimitVal = IsModeCc ? GetCcMinVoltByRail(railName) : GetCvMinCurrentByRail(railName);
            int enduranceSeconds = Math.Max(1, (int)enduranceTime);

            CurrentStepDescription = $"[지속 시험] {railName} 지속 부하 ({testMode}) 인가 준비 중...";
            await _deviceManager.ConfigureLoadAsync(testMode, targetConditionVal);
            await _deviceManager.SetLoadInputAsync(true);

            bool pass = true;
            double lastMeasuredVolt = 0.0;
            double lastMeasuredCurr = 0.0;

            for (int sec = 1; sec <= enduranceSeconds; sec++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(1000, token);

                var (pVolt, pCurr) = await _deviceManager.MeasurePowerSupplyAsync();
                var (lVolt, lCurr) = await _deviceManager.MeasureLoadAsync();

                lastMeasuredVolt = lVolt; lastMeasuredCurr = lCurr;
                LivePowerVoltage = pVolt; LivePowerCurrent = pCurr;
                LiveLoadVoltage = lastMeasuredVolt; LiveLoadCurrent = lastMeasuredCurr;

                AppendPlotPoint(lastMeasuredVolt, lastMeasuredCurr);
                wave.Add(new ChartPointDto(_chartElapsedSeconds, lastMeasuredVolt, lastMeasuredCurr));

                double progressFraction = (double)sec / enduranceSeconds;
                SequenceProgressPercent = Math.Round(basePercent + (progressFraction * (targetPercent - basePercent)), 1);
                CurrentStepDescription = $"[지속 시험] {railName} {testMode} 인가 중... ({sec} / {enduranceSeconds}초)";

                if (IsModeCc && lastMeasuredVolt < specLimitVal) pass = false;
                if (!IsModeCc && lastMeasuredCurr < specLimitVal) pass = false;
            }

            return (pass, lastMeasuredVolt, lastMeasuredCurr, wave);
        }
        #endregion

        #region [실행 커맨드 3종]

        // ==================== [1] 통합 자동 시험 ====================
        [RelayCommand]
        private async Task StartIntegratedSequenceAsync()
        {
            SaveCurrentUiToProfile();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            var token = _testCts.Token;

            IsTesting = true;
            _lastDisplayedStepNo = -1; // 이전 선택 캐시 리셋
            _currentRunStepWaves.Clear();

            string railName = GetSelectedRailName();
            string testMode = IsModeCc ? "CC" : "CV";

            double enduranceTime = GetEnduranceTimeByRail(railName);
            double tripTargetCurr = GetTripCurrentByRail(railName);
            double tripTargetVolt = GetTripVoltageByRail(railName);
            double ocpStartCurr = GetOcpStartCurrentByRail(railName);
            double ovpStartVolt = GetOvpStartVoltageByRail(railName);

            double targetConditionVal = IsModeCc ? GetCcCurrentByRail(railName) : GetCvVoltByRail(railName);
            double specLimitVal = IsModeCc ? GetCcMinVoltByRail(railName) : GetCvMinCurrentByRail(railName);

            ActiveTestingChannelName = $"{railName} 레일";
            FinalTestVerdict = "TESTING";
            SequenceProgressPercent = 0;

            // 대기 테이블 유지 및 즉시 동기화
            UpdateStandbyResults();

            string tripStepName = IsModeCc ? $"{railName} 과전류 차단 시험" : $"{railName} 과전압 차단 시험";
            string tripCondition = IsModeCc ? $"{ocpStartCurr:F2}A 스위핑" : $"{ovpStartVolt:F2}V 스위핑";
            string tripSpec = IsModeCc
                ? $"{tripTargetCurr * 0.85:F2} ~ {tripTargetCurr * 1.15:F2} A"
                : $"{tripTargetVolt * 0.85:F2} ~ {tripTargetVolt * 1.15:F2} V";

            string endurStepName = $"{railName} 지속 부하 시험 ({testMode})";
            string endurCondition = IsModeCc
                ? $"{targetConditionVal:F2}A ({enduranceTime:F0}초)"
                : $"{targetConditionVal:F2}V ({enduranceTime:F0}초)";
            string endurSpec = IsModeCc ? $"≥ {specLimitVal:F2} V" : $"≥ {specLimitVal:F2} A";

            // 1단계는 RUN(진행 중), 2단계는 WAIT(대기)로 세팅
            if (TestResults.Count >= 2)
            {
                TestResults[0] = new TestResultItem
                {
                    StepNo = 1,
                    ChannelName = railName,
                    StepName = tripStepName,
                    Mode = testMode,
                    TargetCondition = tripCondition,
                    SpecRange = tripSpec,
                    MeasuredVoltage = 0.0,
                    MeasuredCurrent = 0.0,
                    Result = "RUN",
                    Timestamp = "-"
                };
                TestResults[1] = new TestResultItem
                {
                    StepNo = 2,
                    ChannelName = railName,
                    StepName = endurStepName,
                    Mode = testMode,
                    TargetCondition = endurCondition,
                    SpecRange = endurSpec,
                    MeasuredVoltage = 0.0,
                    MeasuredCurrent = 0.0,
                    Result = "WAIT",
                    Timestamp = "-"
                };
            }
            UpdateCurrentRunCounts();

            var tripWave = new List<ChartPointDto>();
            var endurWave = new List<ChartPointDto>();

            try
            {
                if (!await BootModulePowerAsync(railName, token))
                {
                    await CleanupRelaysAsync();
                    await FinishSequenceAsync(false, "무부하 출력 전압 미발생", new(), new());
                    return;
                }

                // ==================== [1단계] 차단 시험 수행 (10% ~ 50%) ====================
                SequenceProgressPercent = 10;
                var (tripPass, tripVal, otherVal, tWave) = await RunTripTestInternalAsync(railName, token, basePercent: 10.0, targetPercent: 50.0);
                tripWave = tWave;
                _currentRunStepWaves[1] = tripWave;

                // 1단계 결과 반영 (PASS 또는 FAIL)
                TestResults[0] = new TestResultItem
                {
                    StepNo = 1,
                    ChannelName = railName,
                    StepName = tripStepName,
                    Mode = testMode,
                    TargetCondition = tripCondition,
                    SpecRange = tripSpec,
                    MeasuredVoltage = IsModeCc ? otherVal : tripVal,
                    MeasuredCurrent = IsModeCc ? tripVal : otherVal,
                    Result = tripPass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                UpdateCurrentRunCounts();

                // ==================== [중간 리셋] 모듈 전원 재부팅 (50% ~ 60%) ====================
                // [개선] 1단계 결과(PASS/FAIL)와 관계없이 트립을 풀고 재부팅을 진행합니다.
                SequenceProgressPercent = 55;
                await RebootModulePowerAsync(railName, token);

                // 안전 검증: 모듈이 과부하로 영구 파손되어 재기동 자체가 안 되는 경우에만 중단
                var (_, _) = await _deviceManager.MeasurePowerSupplyAsync();
                var (rebootVolt, _) = await _deviceManager.MeasureLoadAsync();
                if (rebootVolt < 1.0)
                {
                    await CleanupRelaysAsync();
                    await FinishSequenceAsync(false, "차단 시험 후 모듈 재기동 실패 (무부하 전압 미발생)", tripWave, new List<ChartPointDto>());
                    return;
                }

                // ==================== [2단계] 지속 부하 시험 수행 (60% ~ 95%) ====================
                SequenceProgressPercent = 60;

                // 2단계 시작 시 2번 행을 RUN으로 전환
                TestResults[1] = new TestResultItem
                {
                    StepNo = 2,
                    ChannelName = railName,
                    StepName = endurStepName,
                    Mode = testMode,
                    TargetCondition = endurCondition,
                    SpecRange = endurSpec,
                    MeasuredVoltage = 0.0,
                    MeasuredCurrent = 0.0,
                    Result = "RUN",
                    Timestamp = "-"
                };

                var (endurPass, lastV, lastA, eWave) = await RunEnduranceTestInternalAsync(railName, token, basePercent: 60.0, targetPercent: 95.0);
                endurWave = eWave;
                _currentRunStepWaves[2] = endurWave;

                // 2단계 최종 결과 반영 (PASS 또는 FAIL)
                TestResults[1] = new TestResultItem
                {
                    StepNo = 2,
                    ChannelName = railName,
                    StepName = endurStepName,
                    Mode = testMode,
                    TargetCondition = endurCondition,
                    SpecRange = endurSpec,
                    MeasuredVoltage = lastV,
                    MeasuredCurrent = lastA,
                    Result = endurPass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                UpdateCurrentRunCounts();

                await CleanupRelaysAsync();

                // ==================== 최종 판정 및 불합격 사유 확정 ====================
                bool allPassed = tripPass && endurPass;
                string noteReason = (tripPass, endurPass) switch
                {
                    (true, true) => "통합 시험 정상 통과",
                    (false, true) => "차단 시험 규격 미달",
                    (true, false) => "지속 부하 시험 기준 미달",
                    (false, false) => "차단 및 지속 부하 기준 동시 미달"
                };

                await FinishSequenceAsync(allPassed, noteReason, tripWave, endurWave);
            }
            catch (OperationCanceledException)
            {
                await HandleCancelledAsync();
            }
        }

        // ==================== [2] 차단 시험 단독 실행 ====================
        [RelayCommand]
        private async Task StartTripOnlySequenceAsync()
        {
            SaveCurrentUiToProfile();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            var token = _testCts.Token;

            IsTesting = true;
            _lastDisplayedStepNo = -1;
            _currentRunStepWaves.Clear();

            string railName = GetSelectedRailName();
            string testMode = IsModeCc ? "CC" : "CV";
            double tripTargetCurr = GetTripCurrentByRail(railName);
            double tripTargetVolt = GetTripVoltageByRail(railName);
            double ocpStartCurr = GetOcpStartCurrentByRail(railName);
            double ovpStartVolt = GetOvpStartVoltageByRail(railName);

            ActiveTestingChannelName = $"{railName} 레일";
            FinalTestVerdict = "TESTING";
            SequenceProgressPercent = 0;

            string tripStepName = IsModeCc ? $"{railName} 과전류 차단 시험" : $"{railName} 과전압 차단 시험";
            string tripCondition = IsModeCc ? $"{ocpStartCurr:F2}A 스위핑" : $"{ovpStartVolt:F2}V 스위핑";
            string tripSpec = IsModeCc
                ? $"{tripTargetCurr * 0.85:F2} ~ {tripTargetCurr * 1.15:F2} A"
                : $"{tripTargetVolt * 0.85:F2} ~ {tripTargetVolt * 1.15:F2} V";

            // 단독 시험: 1개 행을 미리 RUN 상태로 확보
            TestResults.Clear();
            TestResults.Add(new TestResultItem
            {
                StepNo = 1,
                ChannelName = railName,
                StepName = tripStepName,
                Mode = testMode,
                TargetCondition = tripCondition,
                SpecRange = tripSpec,
                MeasuredVoltage = 0.0,
                MeasuredCurrent = 0.0,
                Result = "RUN",
                Timestamp = "-"
            });
            UpdateCurrentRunCounts();

            try
            {
                if (!await BootModulePowerAsync(railName, token)) { await CleanupRelaysAsync(); return; }

                var (tripPass, tripVal, otherVal, tripWave) = await RunTripTestInternalAsync(railName, token, basePercent: 10.0, targetPercent: 95.0);
                _currentRunStepWaves[1] = tripWave;

                TestResults[0] = new TestResultItem
                {
                    StepNo = 1,
                    ChannelName = railName,
                    StepName = tripStepName,
                    Mode = testMode,
                    TargetCondition = tripCondition,
                    SpecRange = tripSpec,
                    MeasuredVoltage = IsModeCc ? otherVal : tripVal,
                    MeasuredCurrent = IsModeCc ? tripVal : otherVal,
                    Result = tripPass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                UpdateCurrentRunCounts();

                await CleanupRelaysAsync();
                await FinishSequenceAsync(tripPass, tripPass ? "차단 동작 합격" : "차단 규격 이탈", tripWave, new List<ChartPointDto>());
            }
            catch (OperationCanceledException) { await HandleCancelledAsync(); }
        }

        // ==================== [3] 지속 부하 시험 단독 실행 ====================
        [RelayCommand]
        private async Task StartEnduranceOnlySequenceAsync()
        {
            SaveCurrentUiToProfile();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource();
            var token = _testCts.Token;

            IsTesting = true;
            _lastDisplayedStepNo = -1;
            _currentRunStepWaves.Clear();

            string railName = GetSelectedRailName();
            string testMode = IsModeCc ? "CC" : "CV";

            double enduranceTime = GetEnduranceTimeByRail(railName);
            double targetConditionVal = IsModeCc ? GetCcCurrentByRail(railName) : GetCvVoltByRail(railName);
            double specLimitVal = IsModeCc ? GetCcMinVoltByRail(railName) : GetCvMinCurrentByRail(railName);

            ActiveTestingChannelName = $"{railName} 레일";
            FinalTestVerdict = "TESTING";
            SequenceProgressPercent = 0;

            string endurStepName = $"{railName} 지속 부하 시험 ({testMode})";
            string endurCondition = IsModeCc ? $"{targetConditionVal:F2}A ({enduranceTime:F0}초)" : $"{targetConditionVal:F2}V ({enduranceTime:F0}초)";
            string endurSpec = IsModeCc ? $"≥ {specLimitVal:F2} V" : $"≥ {specLimitVal:F2} A";

            // 단독 시험: 1개 행을 미리 RUN 상태로 확보
            TestResults.Clear();
            TestResults.Add(new TestResultItem
            {
                StepNo = 1,
                ChannelName = railName,
                StepName = endurStepName,
                Mode = testMode,
                TargetCondition = endurCondition,
                SpecRange = endurSpec,
                MeasuredVoltage = 0.0,
                MeasuredCurrent = 0.0,
                Result = "RUN",
                Timestamp = "-"
            });
            UpdateCurrentRunCounts();

            try
            {
                if (!await BootModulePowerAsync(railName, token)) { await CleanupRelaysAsync(); return; }

                var (endurPass, lastV, lastA, endurWave) = await RunEnduranceTestInternalAsync(railName, token, basePercent: 10.0, targetPercent: 95.0);
                _currentRunStepWaves[1] = endurWave;

                TestResults[0] = new TestResultItem
                {
                    StepNo = 1,
                    ChannelName = railName,
                    StepName = endurStepName,
                    Mode = testMode,
                    TargetCondition = endurCondition,
                    SpecRange = endurSpec,
                    MeasuredVoltage = lastV,
                    MeasuredCurrent = lastA,
                    Result = endurPass ? "PASS" : "FAIL",
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                };
                UpdateCurrentRunCounts();

                await CleanupRelaysAsync();
                await FinishSequenceAsync(endurPass, endurPass ? "지속 부하 합격" : "지속 부하 기준 미달", endurWave, new List<ChartPointDto>());
            }
            catch (OperationCanceledException) { await HandleCancelledAsync(); }
        }

        [RelayCommand]
        private Task StartSequenceAsync() => StartIntegratedSequenceAsync();

        [RelayCommand]
        private async Task EmergencyStopAsync()
        {
            _testCts?.Cancel();
            await CleanupRelaysAsync();
            IsTesting = false;
            _lastDisplayedStepNo = -1;
            FinalTestVerdict = "READY";
            CurrentStepDescription = "긴급 정지됨 (미저장)";
            StatusMessage = "긴급 정지 실행: 100V 메인 전원 및 모든 릴레이를 즉시 차단했습니다.";
            SequenceProgressPercent = 0;
            EnsureValidRailSelection();
            UpdateStandbyResults();
            UpdateCurrentRunCounts();
        }

        private async Task HandleCancelledAsync()
        {
            await CleanupRelaysAsync();
            IsTesting = false;
            _lastDisplayedStepNo = -1;
            FinalTestVerdict = "READY";
            CurrentStepDescription = "검사가 작업자에 의해 취소되었습니다. (미저장)";
            StatusMessage = "검사 취소: 장비 전원 및 릴레이를 모두 차단했습니다.";
            SequenceProgressPercent = 0;
            EnsureValidRailSelection();
            UpdateStandbyResults();
            UpdateCurrentRunCounts();
        }

        private async Task CleanupRelaysAsync()
        {
            await _deviceManager.SetLoadInputAsync(false);
            if (!string.IsNullOrEmpty(_deviceManager.RigolPortName))
                await _deviceManager.QueryCommandAsync(_deviceManager.RigolPortName, 9600, ":SYST:LOC\n", 200);

            await _deviceManager.SetPowerSupplyOutputAsync(0, 0, false);
            await _deviceManager.SetMain100VRelayAsync(false);
            await _deviceManager.ResetAllRelaysAsync();

            LivePowerVoltage = 0.0; LivePowerCurrent = 0.0;
            LiveLoadVoltage = 0.0; LiveLoadCurrent = 0.0;
        }

        private void UpdateCurrentRunCounts()
        {
            TotalTestedCount = TestResults.Count;
            TotalPassedCount = TestResults.Count(r => r.Result == "PASS");
            TotalFailedCount = TestResults.Count(r => r.Result == "FAIL");
        }

        private async Task FinishSequenceAsync(bool isAllPassed, string noteReason, List<ChartPointDto> step1Wave, List<ChartPointDto> step2Wave)
        {
            SequenceProgressPercent = 100;
            IsTesting = false;
            EnsureValidRailSelection();
            UpdateCurrentRunCounts();

            if (isAllPassed)
            {
                FinalTestVerdict = "PASS";
                CurrentStepDescription = "검사 완료 (정상)";
                StatusMessage = $"{ActiveTestingChannelName} 검사 결과 정상(PASS) 판정되었습니다.";
            }
            else
            {
                FinalTestVerdict = "FAIL";
                CurrentStepDescription = string.IsNullOrWhiteSpace(noteReason) ? "검사 불합격 (기준 규격 미달)" : $"검사 불합격 ({noteReason})";
                StatusMessage = $"{ActiveTestingChannelName} 검사 결과 불합격(FAIL) 판정되었습니다.";
            }

            // 시험 완료 후 마지막 완료 스텝을 기본 선택하여 파형 표출
            SelectedTestResult = TestResults.LastOrDefault();

            await SaveCurrentTestToDbAsync(isAllPassed, noteReason, step1Wave, step2Wave);
        }

        private async Task SaveCurrentTestToDbAsync(bool isAllPassed, string noteReason, List<ChartPointDto> step1Wave, List<ChartPointDto> step2Wave)
        {
            try
            {
                string railName = GetSelectedRailName();
                string system = SelectedTargetSystem;
                string testMode = IsModeCc ? "CC" : "CV";

                string finalSn = await _historyService.GenerateNextSerialNumberAsync(system, railName);
                if (!string.IsNullOrWhiteSpace(TestIdentifierName) && !TestIdentifierName.Contains(finalSn))
                    finalSn = $"{TestIdentifierName.Trim()}-{DateTime.Now:HHmmss}";

                var trendMap = new Dictionary<string, List<ChartPointDto>> { ["1"] = step1Wave, ["2"] = step2Wave };
                string trendJson = JsonSerializer.Serialize(trendMap);

                var master = new TestMasterEntity
                {
                    SerialNumber = finalSn,
                    TargetSystem = system,
                    RailName = railName,
                    TestMode = testMode,
                    FinalVerdict = isAllPassed ? "PASS" : "FAIL",
                    TotalTestedCount = TestResults.Count,
                    PassCount = TestResults.Count(r => r.Result == "PASS"),
                    FailCount = TestResults.Count(r => r.Result == "FAIL"),
                    TestedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TrendDataJson = trendJson,
                    Notes = string.IsNullOrWhiteSpace(noteReason) ? (isAllPassed ? "정상 통과" : "규격 미달") : noteReason
                };

                var detailEntities = TestResults.Select(r => new TestStepDetailEntity
                {
                    StepNo = r.StepNo,
                    StepName = r.StepName,
                    Mode = r.Mode,
                    TargetCondition = r.TargetCondition,
                    SpecRange = r.SpecRange,
                    MeasuredVoltage = r.MeasuredVoltage,
                    MeasuredCurrent = r.MeasuredCurrent,
                    Result = r.Result,
                    ExecutedAt = r.Timestamp
                });

                await _historyService.SaveTestResultAsync(master, detailEntities);
                StatusMessage = $"{system} {railName} 저장 완료 (S/N: {finalSn})";
                UpdateDefaultTestIdentifier();
            }
            catch (Exception ex) { Debug.WriteLine($"[DB 저장 실패] {ex.Message}"); }
        }
        #endregion
    }
}