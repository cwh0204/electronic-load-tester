using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Models;
using ElectronicLoadTester.Services;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace ElectronicLoadTester.ViewModels
{
    public partial class ResultSearchViewModel : ObservableObject
    {
        private readonly ITestResultHistoryService _historyService;

        private LineSeries _historyVoltageSeries = null!;
        private LineSeries _historyCurrentSeries = null!;

        #region [검색 조건 필터]
        [ObservableProperty] private DateTime _startDate = DateTime.Today.AddDays(-7);
        [ObservableProperty] private DateTime _endDate = DateTime.Today;

        public ObservableCollection<string> TargetSystemOptions { get; } = new() { "전체", "TCMS", "SIV", "CI" };
        [ObservableProperty] private string _selectedTargetSystem = "전체";

        public ObservableCollection<string> VerdictOptions { get; } = new() { "전체", "PASS", "FAIL" };
        [ObservableProperty] private string _selectedVerdict = "전체";

        [ObservableProperty] private string _searchText = string.Empty;
        #endregion

        #region [목록 및 상세 바인딩]
        public ObservableCollection<TestMasterEntity> SearchResults { get; } = new();

        [ObservableProperty] private TestMasterEntity? _selectedMaster;
        public ObservableCollection<TestStepDetailEntity> SelectedMasterSteps { get; } = new();

        [ObservableProperty] private PlotModel _historyPlotModel = null!;
        [ObservableProperty] private string _statusMessage = "조회할 조건을 설정하고 [검색]을 누르세요.";
        [ObservableProperty] private bool _hasChartData = false;
        #endregion

        public ResultSearchViewModel(ITestResultHistoryService historyService)
        {
            _historyService = historyService;

            InitializeHistoryPlot();
            _ = SearchAsync(); // 창 열릴 때 기본 1주일치 자동 조회
        }

        public ResultSearchViewModel() : this(
            App.Services.GetRequiredService<ITestResultHistoryService>())
        {
        }

        #region [차트 초기화]
        private void InitializeHistoryPlot()
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

            _historyVoltageSeries = new LineSeries
            {
                Title = "출력 전압",
                YAxisKey = "VoltAxis",
                Color = OxyColor.FromRgb(37, 99, 235),
                StrokeThickness = 2.0
            };

            _historyCurrentSeries = new LineSeries
            {
                Title = "부하 전류",
                YAxisKey = "CurrAxis",
                Color = OxyColor.FromRgb(245, 158, 11),
                StrokeThickness = 2.0
            };

            model.Series.Add(_historyVoltageSeries);
            model.Series.Add(_historyCurrentSeries);
            HistoryPlotModel = model;
        }

        private void ClearPlot()
        {
            _historyVoltageSeries.Points.Clear();
            _historyCurrentSeries.Points.Clear();
            HistoryPlotModel.InvalidatePlot(true);
            HasChartData = false;
        }
        #endregion

        #region [목록 선택 변경 시 상세 스텝 및 파형 복원]
        partial void OnSelectedMasterChanged(TestMasterEntity? value)
        {
            SelectedMasterSteps.Clear();
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

                // 2. 파형 JSON 파싱 후 OxyPlot 차트 복원
                if (!string.IsNullOrWhiteSpace(fullMaster.TrendDataJson))
                {
                    var points = JsonSerializer.Deserialize<List<ChartPointDto>>(fullMaster.TrendDataJson);
                    if (points != null && points.Count > 0)
                    {
                        foreach (var pt in points)
                        {
                            _historyVoltageSeries.Points.Add(new DataPoint(pt.Time, pt.Volt));
                            _historyCurrentSeries.Points.Add(new DataPoint(pt.Time, pt.Curr));
                        }
                        HistoryPlotModel.InvalidatePlot(true);
                        HasChartData = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[상세 로드 실패] {ex.Message}");
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