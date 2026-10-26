using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElectronicLoadTester.Services;

namespace ElectronicLoadTester.ViewModels
{
    public partial class SelfTestViewModel : ObservableObject
    {
        private readonly IDeviceManagerService _deviceManager;

        #region [바인딩 프로퍼티]
        // 상단 우측 상태 요약 텍스트
        private string _diagnosisSummaryText = "점검할 항목을 클릭하거나 [전체 확인]을 누르세요";
        public string DiagnosisSummaryText
        {
            get => _diagnosisSummaryText;
            set => SetProperty(ref _diagnosisSummaryText, value);
        }

        // 1. 파워 서플라이 (OWON) 통신 상태 (대기, 확인 중, PASS, FAIL)
        private string _owonStatus = "대기";
        public string OwonStatus
        {
            get => _owonStatus;
            set => SetProperty(ref _owonStatus, value);
        }

        // 2. 전자 로더 (Rigol) 통신 상태
        private string _rigolStatus = "대기";
        public string RigolStatus
        {
            get => _rigolStatus;
            set => SetProperty(ref _rigolStatus, value);
        }

        // 3. 릴레이 보드 (485) 통신 상태
        private string _relayStatus = "대기";
        public string RelayStatus
        {
            get => _relayStatus;
            set => SetProperty(ref _relayStatus, value);
        }

        // 점검 중 중복 클릭 방지 플래그
        private bool _isBusy = false;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }
        #endregion

        // 1. DI 컨테이너 주입 생성자
        public SelfTestViewModel(IDeviceManagerService deviceManager)
        {
            _deviceManager = deviceManager;
        }

        // 2. 기본 생성자 (디자이너 호환용)
        public SelfTestViewModel() : this(DeviceManagerService.Instance)
        {
        }

        #region [개별 카드 바 클릭 진단 커맨드]

        // [개별 1] 파워 서플라이 바 클릭 (*IDN? -> OWON,SPE15054...)
        [RelayCommand]
        private async Task TestOwonAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                OwonStatus = "확인 중";
                DiagnosisSummaryText = "파워 서플라이 통신 점검 중...";

                // 포트 미할당 시 자동 감지 시도
                if (string.IsNullOrEmpty(_deviceManager.OwonPortName))
                {
                    await _deviceManager.ScanAndIdentifyDevicesAsync();
                }

                bool isOk = await _deviceManager.CheckOwonPingAsync();
                OwonStatus = isOk ? "PASS" : "FAIL";
                DiagnosisSummaryText = isOk ? "파워 서플라이 통신 정상" : "파워 서플라이 응답 없음 (포트/전원 확인)";
            }
            catch (Exception ex)
            {
                OwonStatus = "FAIL";
                DiagnosisSummaryText = $"파워 점검 에러: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                UpdateSummaryIfAllTested();
            }
        }

        // [개별 2] 전자 로더 바 클릭 (*IDN? -> RIGOL TECHNOLOGIES,DL3021...)
        [RelayCommand]
        private async Task TestRigolAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                RigolStatus = "확인 중";
                DiagnosisSummaryText = "전자 로더 통신 점검 중...";

                if (string.IsNullOrEmpty(_deviceManager.RigolPortName))
                {
                    await _deviceManager.ScanAndIdentifyDevicesAsync();
                }

                bool isOk = await _deviceManager.CheckRigolPingAsync();
                RigolStatus = isOk ? "PASS" : "FAIL";
                DiagnosisSummaryText = isOk ? "전자 로더 통신 정상" : "전자 로더 응답 없음 (포트/전원 확인)";
            }
            catch (Exception ex)
            {
                RigolStatus = "FAIL";
                DiagnosisSummaryText = $"전자 로더 점검 에러: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                UpdateSummaryIfAllTested();
            }
        }

        // [개별 3] 릴레이 보드 바 클릭 (get.version.1 / get.version.0 -> Version.1...)
        [RelayCommand]
        private async Task TestRelayAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                RelayStatus = "확인 중";
                DiagnosisSummaryText = "릴레이 보드 통신 점검 중...";

                if (string.IsNullOrEmpty(_deviceManager.RelayPortName))
                {
                    await _deviceManager.ScanAndIdentifyDevicesAsync();
                }

                bool isOk = await _deviceManager.CheckRelayPingAsync();
                RelayStatus = isOk ? "PASS" : "FAIL";
                DiagnosisSummaryText = isOk ? "릴레이 보드 통신 정상" : "릴레이 보드 응답 없음 (포트 확인)";
            }
            catch (Exception ex)
            {
                RelayStatus = "FAIL";
                DiagnosisSummaryText = $"릴레이 점검 에러: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                UpdateSummaryIfAllTested();
            }
        }

        #endregion

        #region [하단 전체 확인 버튼 커맨드]

        [RelayCommand]
        private async Task RunAllSelfTestAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                DiagnosisSummaryText = "전체 장비 통신 점검 중...";
                OwonStatus = "확인 중";
                RigolStatus = "확인 중";
                RelayStatus = "확인 중";

                // 전체 포트 전수 조사
                if (string.IsNullOrEmpty(_deviceManager.OwonPortName) ||
                    string.IsNullOrEmpty(_deviceManager.RigolPortName) ||
                    string.IsNullOrEmpty(_deviceManager.RelayPortName))
                {
                    await _deviceManager.ScanAndIdentifyDevicesAsync();
                }

                // 1. 파워 서플라이 점검
                bool owonOk = await _deviceManager.CheckOwonPingAsync();
                OwonStatus = owonOk ? "PASS" : "FAIL";

                // 2. 전자 로더 점검
                bool rigolOk = await _deviceManager.CheckRigolPingAsync();
                RigolStatus = rigolOk ? "PASS" : "FAIL";

                // 3. 릴레이 보드 점검
                bool relayOk = await _deviceManager.CheckRelayPingAsync();
                RelayStatus = relayOk ? "PASS" : "FAIL";

                if (owonOk && rigolOk && relayOk)
                {
                    DiagnosisSummaryText = "모든 장비 연결 정상 (PASS)";
                }
                else
                {
                    DiagnosisSummaryText = "일부 장비 통신 실패 (FAIL)";
                }
            }
            catch (Exception ex)
            {
                DiagnosisSummaryText = $"점검 중 오류 발생: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        #endregion

        private void UpdateSummaryIfAllTested()
        {
            if (OwonStatus != "대기" && OwonStatus != "확인 중" &&
                RigolStatus != "대기" && RigolStatus != "확인 중" &&
                RelayStatus != "대기" && RelayStatus != "확인 중")
            {
                bool allPass = (OwonStatus == "PASS" && RigolStatus == "PASS" && RelayStatus == "PASS");
                DiagnosisSummaryText = allPass ? "모든 장비 연결 정상 (PASS)" : "일부 장비 통신 실패 (FAIL)";
            }
        }
    }
}