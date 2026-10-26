using CommunityToolkit.Mvvm.ComponentModel;

namespace ElectronicLoadTester.Models
{
    public partial class TestResultItem : ObservableObject
    {
        [ObservableProperty]
        private int _stepNo;

        [ObservableProperty]
        private string _channelName = string.Empty; // 5V, 24V, 30V (추가됨)

        [ObservableProperty]
        private string _stepName = string.Empty;

        [ObservableProperty]
        private string _mode = "CC";

        [ObservableProperty]
        private string _targetCondition = string.Empty;

        [ObservableProperty]
        private string _specRange = string.Empty;

        [ObservableProperty]
        private double _measuredVoltage;

        [ObservableProperty]
        private double _measuredCurrent;

        [ObservableProperty]
        private string _result = "WAIT"; // WAIT, PASS, FAIL

        [ObservableProperty]
        private string _timestamp = "-";
    }
}