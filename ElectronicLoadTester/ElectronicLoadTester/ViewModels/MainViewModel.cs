using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Hardware;

namespace ElectronicLoadTester.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IPowerSupply _powerSupply;
        private readonly IElectronicLoad _electronicLoad;
        private readonly IRelayController _relayController;

        [ObservableProperty]
        private string _statusMessage = "준비 완료";

        [ObservableProperty]
        private bool _isPowerConnected;

        [ObservableProperty]
        private bool _isLoadConnected;

        [ObservableProperty]
        private bool _isRelayConnected;

        public MainViewModel(
            IPowerSupply powerSupply,
            IElectronicLoad electronicLoad,
            IRelayController relayController)
        {
            _powerSupply = powerSupply;
            _electronicLoad = electronicLoad;
            _relayController = relayController;

            // 소스 생성기 에러 방지: 내부에서는 필드를 통해 갱신하거나 프로퍼티 호출
            _powerSupply.ConnectionChanged += (_, connected) => IsPowerConnected = connected;
            _electronicLoad.ConnectionChanged += (_, connected) => IsLoadConnected = connected;
            _relayController.ConnectionChanged += (_, connected) => IsRelayConnected = connected;
        }

        [RelayCommand]
        private async Task ScanAndConnectAllAsync()
        {
            StatusMessage = "장비 포트 탐색 및 연결 시도 중...";
            await Task.Delay(100);
        }
    }
}