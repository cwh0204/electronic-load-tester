using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Models;
using ElectronicLoadTester.Services;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace ElectronicLoadTester.ViewModels
{
    public partial class ResultSearchViewModel : ObservableObject
    {
        private readonly ITestResultHistoryService _historyService;

        private LinearAxis _xAxis = null!;
        private LinearAxis _yVoltAxis = null!;
        private LinearAxis _yCurrAxis = null!;
        private LineSeries _historyVoltageSeries = null!;
        private LineSeries _historyCurrentSeries = null!;

        // 스텝별 파형 캐시 ("1" -> 차단 시험 파형, "2" -> 지속 부하 파형)
        private Dictionary<string, List<ChartPointDto>> _loadedStepWaves = new();

        #region [검색 조건 필터]
        [ObservableProperty] private DateTime _startDate = DateTime.Today.AddDays(-7);
        [ObservableProperty] private DateTime _endDate = DateTime.Today;

        public ObservableCollection<string> TargetSystemOptions { get; } = new()
        {
            "전체",
            "TCMS-CC",
            "TCMS-TC",
            "CI-123",
            "CI-4",
            "SIV-123",
            "SIV-4"
        };
        [ObservableProperty] private string _selectedTargetSystem = "전체";

        public ObservableCollection<string> VerdictOptions { get; } = new() { "전체", "PASS", "FAIL" };
        [ObservableProperty] private string _selectedVerdict = "전체";

        [ObservableProperty] private string _searchText = string.Empty;
        #endregion

        #region [목록 및 상세 바인딩]
        public ObservableCollection<TestMasterEntity> SearchResults { get; } = new();

        [ObservableProperty] private TestMasterEntity? _selectedMaster;
        public ObservableCollection<TestStepDetailEntity> SelectedMasterSteps { get; } = new();

        // 사용자가 표에서 클릭한 개별 스텝 (스텝 1: 차단 시험 또는 스텝 2: 지속 시험)
        [ObservableProperty] private TestStepDetailEntity? _selectedStep;

        [ObservableProperty] private PlotModel _historyPlotModel = null!;
        [ObservableProperty] private string _statusMessage = "조회할 조건을 설정하고 [검색]을 누르세요.";
        [ObservableProperty] private bool _hasChartData = false;
        #endregion

        public ResultSearchViewModel(ITestResultHistoryService historyService)
        {
            _historyService = historyService;

            InitializeHistoryPlot();
            _ = SearchAsync(); // 기본 1주일치 자동 조회
        }

        public ResultSearchViewModel() : this(
            App.Services.GetRequiredService<ITestResultHistoryService>())
        {
        }

        #region [차트 초기화 및 동적 스케일 엔진]
        private void InitializeHistoryPlot()
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
                MinorGridlineStyle = LineStyle.None,
                StringFormat = "0.0",
                Minimum = 0.0,
                Maximum = 12.0
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
                Minimum = 0.0,
                Maximum = 5.0
            };
            model.Axes.Add(_yCurrAxis);

            _historyVoltageSeries = new LineSeries
            {
                Title = "출력 전압(V)",
                XAxisKey = "TimeAxis",
                YAxisKey = "VoltAxis",
                Color = OxyColor.FromRgb(37, 99, 235),
                StrokeThickness = 2.2
            };

            _historyCurrentSeries = new LineSeries
            {
                Title = "부하 전류(A)",
                XAxisKey = "TimeAxis",
                YAxisKey = "CurrAxis",
                Color = OxyColor.FromRgb(245, 158, 11),
                StrokeThickness = 2.2
            };

            model.Series.Add(_historyVoltageSeries);
            model.Series.Add(_historyCurrentSeries);
            HistoryPlotModel = model;
        }

        // [핵심] 전압/전류 실측 최대치에 따라 축을 동적으로 확장 (잘림 방지)
        private void UpdateHistoryPlotAxes(string railName, double maxRecordedVoltage, double maxRecordedCurrent)
        {
            if (_yVoltAxis == null || _yCurrAxis == null) return;

            string rail = railName.Trim().ToUpper();

            _yVoltAxis.Minimum = 0.0;
            switch (rail)
            {
                case "5V": _yVoltAxis.Maximum = 12.0; _yVoltAxis.MajorStep = 2.0; _yVoltAxis.MinorStep = 0.5; break;
                case "24V": _yVoltAxis.Maximum = 32.0; _yVoltAxis.MajorStep = 5.0; _yVoltAxis.MinorStep = 1.0; break;
                case "30V": _yVoltAxis.Maximum = 40.0; _yVoltAxis.MajorStep = 5.0; _yVoltAxis.MinorStep = 1.0; break;
                case "48V": _yVoltAxis.Maximum = 60.0; _yVoltAxis.MajorStep = 10.0; _yVoltAxis.MinorStep = 2.0; break;
                default: _yVoltAxis.Maximum = 15.0; _yVoltAxis.MajorStep = 2.0; _yVoltAxis.MinorStep = 0.5; break;
            }

            // OVP 스위핑 등으로 전압 상한 돌파 시 20% 마진 확장
            if (maxRecordedVoltage > _yVoltAxis.Maximum * 0.95)
            {
                _yVoltAxis.Maximum = Math.Ceiling(maxRecordedVoltage * 1.2);
            }

            // OCP 스위핑 등으로 전류 상한 돌파 시 20% 마진 확장
            _yCurrAxis.Minimum = 0.0;
            double targetMax = Math.Max(4.0, Math.Ceiling(maxRecordedCurrent * 1.2));
            _yCurrAxis.Maximum = targetMax;
            _yCurrAxis.MajorStep = (targetMax <= 5.0) ? 0.5 : (targetMax > 10.0 ? 2.0 : 1.0);
            _yCurrAxis.MinorStep = 0.1;
        }

        private void ClearPlot()
        {
            _historyVoltageSeries.Points.Clear();
            _historyCurrentSeries.Points.Clear();
            HistoryPlotModel.InvalidatePlot(true);
            HasChartData = false;
        }
        #endregion

        #region [목록 및 스텝 선택 변경 시 파형 복원]
        partial void OnSelectedMasterChanged(TestMasterEntity? value)
        {
            SelectedMasterSteps.Clear();
            _loadedStepWaves.Clear();
            ClearPlot();

            if (value == null) return;

            _ = LoadDetailsAndRestoreChartAsync(value.Id);
        }

        private async Task LoadDetailsAndRestoreChartAsync(long masterId)
        {
            try
            {
                var fullMaster = await _historyService.GetMasterWithDetailsByIdAsync(masterId);
                if (fullMaster == null) return;

                // 1. 상세 스텝 목록 복원
                foreach (var step in fullMaster.Steps)
                {
                    SelectedMasterSteps.Add(step);
                }

                // 2. 파형 JSON 복원
                if (!string.IsNullOrWhiteSpace(fullMaster.TrendDataJson))
                {
                    try
                    {
                        var stepMap = JsonSerializer.Deserialize<Dictionary<string, List<ChartPointDto>>>(fullMaster.TrendDataJson);
                        if (stepMap != null)
                        {
                            _loadedStepWaves = stepMap;
                        }
                    }
                    catch
                    {
                        var legacyPoints = JsonSerializer.Deserialize<List<ChartPointDto>>(fullMaster.TrendDataJson);
                        if (legacyPoints != null)
                        {
                            _loadedStepWaves["1"] = legacyPoints;
                        }
                    }
                }

                // 3. 첫 번째 스텝 기본 선택
                SelectedStep = SelectedMasterSteps.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[상세 로드 실패] {ex.Message}");
            }
        }

        // 스텝 표에서 특정 행(스텝 1: 차단 시험 또는 스텝 2: 지속 시험) 클릭 시 독립 파형 렌더링
        partial void OnSelectedStepChanged(TestStepDetailEntity? value)
        {
            ClearPlot();

            if (value == null) return;

            string key = value.StepNo.ToString();
            if (_loadedStepWaves.TryGetValue(key, out var points) && points.Count > 0)
            {
                double maxCurrentInPoints = 0.0;
                double maxVoltageInPoints = 0.0;

                foreach (var pt in points)
                {
                    _historyVoltageSeries.Points.Add(new DataPoint(pt.Time, pt.Volt));
                    _historyCurrentSeries.Points.Add(new DataPoint(pt.Time, pt.Curr));

                    if (pt.Curr > maxCurrentInPoints) maxCurrentInPoints = pt.Curr;
                    if (pt.Volt > maxVoltageInPoints) maxVoltageInPoints = pt.Volt;
                }

                string rail = SelectedMaster?.RailName ?? "5V";
                UpdateHistoryPlotAxes(rail, maxVoltageInPoints, maxCurrentInPoints);

                // X축 범위: 0초부터 해당 스텝 최대 시간까지 설정
                double maxTime = points.Max(p => p.Time);
                _xAxis.Minimum = 0;
                _xAxis.Maximum = Math.Max(10.0, Math.Ceiling(maxTime * 1.1));

                // [핵심] 시간축 눈금 숫자 겹침 방지 (측정 시간에 비례한 자동 스텝 설정)
                double xSpan = _xAxis.Maximum - _xAxis.Minimum;
                if (xSpan <= 15.0)
                {
                    _xAxis.MajorStep = 2.0;  // 15초 이하: 2초 간격
                    _xAxis.MinorStep = 0.5;
                }
                else if (xSpan <= 35.0)
                {
                    _xAxis.MajorStep = 5.0;  // 35초 이하: 5초 간격
                    _xAxis.MinorStep = 1.0;
                }
                else if (xSpan <= 80.0)
                {
                    _xAxis.MajorStep = 10.0; // 60초 내외: 10초 간격 (0, 10, 20, 30, 40, 50, 60)
                    _xAxis.MinorStep = 2.0;
                }
                else
                {
                    _xAxis.MajorStep = 20.0; // 장시간: 20초 간격
                    _xAxis.MinorStep = 5.0;
                }

                HistoryPlotModel.InvalidatePlot(true);
                HasChartData = true;
                StatusMessage = $"[{value.StepName}] 구간 파형 표시 완료 (측정 포인트: {points.Count}개)";
            }
            else
            {
                StatusMessage = $"[{value.StepName}]에 저장된 파형 데이터가 없습니다.";
            }
        }
        #endregion

        #region [검색 / 삭제 커맨드]
        [RelayCommand]
        private async Task SearchAsync()
        {
            StatusMessage = "이력 조회 중...";
            SearchResults.Clear();
            SelectedMasterSteps.Clear();
            _loadedStepWaves.Clear();
            ClearPlot();

            var filter = new TestHistoryFilter
            {
                StartDate = StartDate,
                EndDate = EndDate,
                TargetSystem = SelectedTargetSystem,
                FinalVerdict = SelectedVerdict,
                SearchText = SearchText
            };

            try
            {
                var list = await _historyService.SearchHistoryAsync(filter);
                int count = 0;
                foreach (var item in list)
                {
                    SearchResults.Add(item);
                    count++;
                }

                StatusMessage = $"총 {count}건의 검사 이력이 조회되었습니다.";
                if (count > 0)
                {
                    SelectedMaster = SearchResults[0];
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"조회 실패: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task DeleteSelectedAsync()
        {
            if (SelectedMaster == null) return;

            var confirm = MessageBox.Show(
                $"시리얼 [{SelectedMaster.SerialNumber}] 검사 이력을 삭제하시겠습니까?\n하위 상세 데이터도 모두 삭제됩니다.",
                "이력 삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            long id = SelectedMaster.Id;
            bool success = await _historyService.DeleteTestResultAsync(id);
            if (success)
            {
                SearchResults.Remove(SelectedMaster);
                SelectedMaster = null;
                StatusMessage = "이력이 삭제되었습니다.";
            }
        }

        [RelayCommand]
        private void Close(Window? window) => window?.Close();
        #endregion
    }
}