using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Services
{
    public class MockDeviceManagerService : IDeviceManagerService
    {
        private static readonly Lazy<MockDeviceManagerService> _instance =
            new(() => new MockDeviceManagerService());
        public static MockDeviceManagerService Instance => _instance.Value;

        public SerialPort? PowerPort => null;
        public SerialPort? LoadPort => null;
        public SerialPort? RelayPort => null;

        public string OwonPortName { get; set; } = "COM1 (MOCK)";
        public string RigolPortName { get; set; } = "COM2 (MOCK)";
        public string RelayPortName { get; set; } = "COM3 (MOCK)";

        private bool _is100VMainOn = false;
        private string _activeRail = "NONE";
        private bool _mockPowerOutputOn = false;
        private double _mockPowerSetVoltage = 0.0;
        private double _mockPowerSetCurrent = 0.0;
        private bool _mockLoadInputOn = false;
        private string _mockLoadMode = "CC";
        private double _mockLoadSetValue = 0.0;

        #region [명령어 질의 및 핑 시뮬레이션]
        public async Task<string> QueryCommandAsync(string portName, int baudRate, string command, int timeoutMs = 600)
        {
            await Task.Delay(50);
            string trimmed = command.Trim().ToUpper();

            if (trimmed == "*IDN?")
            {
                if (portName == OwonPortName) return "OWON,SPE3103,2138123,V1.2";
                if (portName == RigolPortName) return "RIGOL TECHNOLOGIES,DL3021,DL3A2200001,00.01.04";
            }

            return "OK";
        }

        public async Task<bool> CheckOwonPingAsync()
        {
            await Task.Delay(100);
            return true;
        }

        public async Task<bool> CheckRigolPingAsync()
        {
            await Task.Delay(100);
            return true;
        }

        public async Task<bool> CheckRelayPingAsync()
        {
            await Task.Delay(100);
            return true;
        }

        public async Task<(bool power, bool load, bool relay)> ScanAndIdentifyDevicesAsync()
        {
            Debug.WriteLine("[가상 장비] 포트 스캔 및 응답 시뮬레이션 중...");
            await Task.Delay(400);

            OwonPortName = "COM1 (MOCK)";
            RigolPortName = "COM2 (MOCK)";
            RelayPortName = "COM3 (MOCK)";

            return (true, true, true);
        }
        #endregion

        #region [릴레이 제어 시뮬레이션]
        public async Task<bool> SetRelayAsync(int relayNumber, bool turnOn)
        {
            await Task.Delay(50);
            Debug.WriteLine($"[가상 릴레이] 번호:{relayNumber} -> {(turnOn ? "ON" : "OFF")}");
            return true;
        }

        public async Task<bool> SetMain100VRelayAsync(bool turnOn)
        {
            await Task.Delay(50);
            _is100VMainOn = turnOn;
            Debug.WriteLine($"[가상 릴레이] 100V 메인 전원(CH7) -> {(turnOn ? "ON (통전)" : "OFF (차단)")}");
            return true;
        }

        public async Task<bool> ConnectTargetRailRelayAsync(string systemName, string railName)
        {
            await Task.Delay(100);

            string sys = systemName.Trim().ToUpper();
            string rail = railName.Trim().ToUpper();

            bool isSupported = (sys, rail) switch
            {
                ("TCMS-CC", "5V") or ("TCMS-CC", "24V") => true,
                ("TCMS-TC", "5V") or ("TCMS-TC", "24V") => true,
                ("TCMS", "5V") or ("TCMS", "24V") => true,

                ("CI-123", "5V") or ("CI-123", "30V") or ("CI-123", "48V") => true,
                ("CI-4", "5V") or ("CI-4", "30V") or ("CI-4", "48V") => true,
                ("CI", "5V") or ("CI", "30V") or ("CI", "48V") => true,

                ("SIV-123", "5V") or ("SIV-123", "24V") or ("SIV-123", "30V") => true,
                ("SIV-4", "5V") or ("SIV-4", "30V") => true,
                ("SIV", "5V") or ("SIV", "30V") => true,

                _ => false
            };

            if (!isSupported)
            {
                Debug.WriteLine($"[가상 릴레이 거부] {systemName} 모듈은 {railName} 레일을 지원하지 않습니다.");
                _activeRail = "NONE";
                return false;
            }

            _activeRail = rail;
            Debug.WriteLine($"[가상 릴레이 투입] {systemName} {railName} 레일 투입 완료");
            return true;
        }

        public async Task ResetAllRelaysAsync()
        {
            await Task.Delay(100);
            _is100VMainOn = false;
            _activeRail = "NONE";
            Debug.WriteLine("[가상 릴레이] 모든 채널 차단 (초기화)");
        }
        #endregion

        #region [파워 제어 시뮬레이션]
        public async Task<bool> SetPowerSupplyOutputAsync(double voltage, double current, bool turnOn)
        {
            await Task.Delay(40);
            _mockPowerSetVoltage = voltage;
            _mockPowerSetCurrent = current;
            _mockPowerOutputOn = turnOn;
            Debug.WriteLine($"[가상 파워] 전압:{voltage:F2}V, 전류리밋:{current:F2}A -> 출력을 {(turnOn ? "ON" : "OFF")} 하였습니다.");
            return true;
        }

        public async Task<(double volt, double curr)> MeasurePowerSupplyAsync()
        {
            await Task.Delay(20);
            if (_mockPowerOutputOn && _is100VMainOn)
            {
                double jitterV = (Random.Shared.NextDouble() * 0.2 - 0.1);
                double jitterA = (Random.Shared.NextDouble() * 0.04 - 0.02);
                return (_mockPowerSetVoltage + jitterV, 0.52 + jitterA);
            }

            return (0.0, 0.0);
        }
        #endregion

        #region [전자로더 제어 시뮬레이션]
        public async Task<bool> ConfigureLoadAsync(string mode, double setVal)
        {
            await Task.Delay(40);
            _mockLoadMode = mode.ToUpper();
            _mockLoadSetValue = setVal;
            Debug.WriteLine($"[가상 전자로더] 모드:{mode}, 인가값:{setVal:F3} 설정 완료");
            return true;
        }

        public async Task<bool> SetLoadInputAsync(bool turnOn)
        {
            await Task.Delay(40);
            _mockLoadInputOn = turnOn;
            Debug.WriteLine($"[가상 전자로더] 입력을 {(turnOn ? "ON (통전)" : "OFF (차단)")} 하였습니다.");
            return true;
        }

        // ==================== [CC 과전류 스위핑 모의] ====================
        public async Task<bool> PrepareLoadForCcSweepAsync(double startCurrent)
        {
            await Task.Delay(50);
            _mockLoadMode = "CC";
            _mockLoadSetValue = startCurrent;
            _mockLoadInputOn = true;
            Debug.WriteLine($"[가상 전자로더] CC 스위핑 준비 완료 (40A 레인지, 시작전류:{startCurrent:F3}A, 통전 ON)");
            return true;
        }

        public async Task<bool> SetLoadCurrentImmediateAsync(double currentA)
        {
            await Task.Delay(20);
            _mockLoadSetValue = currentA;
            return true;
        }

        // ==================== [CV 과전압 스위핑 모의] ====================
        public async Task<bool> PrepareLoadForCvSweepAsync(double startVoltage)
        {
            await Task.Delay(50);
            _mockLoadMode = "CV";
            _mockLoadSetValue = startVoltage;
            _mockLoadInputOn = true;
            Debug.WriteLine($"[가상 전자로더] CV 스위핑 준비 완료 (150V 레인지, 시작전압:{startVoltage:F3}V, 통전 ON)");
            return true;
        }

        public async Task<bool> SetLoadVoltageImmediateAsync(double voltageV)
        {
            await Task.Delay(20);
            _mockLoadSetValue = voltageV;
            return true;
        }

        // ==================== [실시간 실측값 모사 엔진] ====================
        public async Task<(double volt, double curr)> MeasureLoadAsync()
        {
            await Task.Delay(20);

            // 100V 메인 전원이 차단되었거나 릴레이가 연결되지 않은 경우 0V, 0A
            if (!_is100VMainOn || _activeRail == "NONE")
            {
                return (0.0, 0.0);
            }

            double nominalV = _activeRail switch
            {
                "5V" => 5.00,
                "24V" => 24.00,
                "30V" => 30.00,
                "48V" => 48.00,
                _ => 0.0
            };

            double jitterV = (Random.Shared.NextDouble() * 0.04 - 0.02);

            // [수정 핵심] 부하기 입력(통전)이 꺼져 있는 무부하(No-Load) 상태:
            // 전압은 모듈의 정격 출력 전압(~5V/24V/30V/48V)이 계측되고, 전류만 0.0A 반환
            if (!_mockLoadInputOn)
            {
                return (Math.Max(0.0, nominalV + jitterV), 0.0);
            }

            // ================= 이하 부하기 통전(Input ON) 상태 =================
            // 1. [CC 모드 (정전류) 동작 시]
            if (_mockLoadMode == "CC")
            {
                // 레일별 모의 OCP 트립 기준치 (A)
                double mockOcpTripA = _activeRail switch
                {
                    "5V" => 3.00,
                    "24V" => 3.50,
                    "30V" => 2.20,
                    "48V" => 1.80,
                    _ => 5.00
                };

                // 설정 전류가 트립 기준치 도달 시 전압 급락(0.02V) 셧다운 모사
                if (_mockLoadSetValue >= mockOcpTripA)
                {
                    return (0.02, 0.0);
                }

                double jitterA = (Random.Shared.NextDouble() * 0.02 - 0.01);
                return (Math.Max(0.0, nominalV - 0.05 + jitterV), Math.Max(0.0, _mockLoadSetValue + jitterA));
            }
            // 2. [CV 모드 (정전압) 동작 시]
            else
            {
                // 레일별 모의 OVP 트립 기준치 (V)
                double mockOvpTripV = _activeRail switch
                {
                    "5V" => 6.50,
                    "24V" => 30.00,
                    "30V" => 38.00,
                    "48V" => 58.00,
                    _ => nominalV * 1.3
                };

                // 설정 전압이 모듈 보호 상한 도달 시 전류 셧다운(0.01A) 트립 모사
                if (_mockLoadSetValue >= mockOvpTripV)
                {
                    return (_mockLoadSetValue, 0.01);
                }

                double jitterA = (Random.Shared.NextDouble() * 0.04 - 0.02);
                // 정상 구간 통전 전류 모사
                return (Math.Max(0.0, _mockLoadSetValue + jitterV), Math.Max(0.1, 2.00 + jitterA));
            }
        }
        #endregion
    }
}