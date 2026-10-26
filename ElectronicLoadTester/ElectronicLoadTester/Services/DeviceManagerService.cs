using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ElectronicLoadTester.Models;

namespace ElectronicLoadTester.Services
{
    public class DeviceManagerService : IDeviceManagerService, IDisposable
    {
        private static readonly Lazy<DeviceManagerService> _instance = new(() => new DeviceManagerService());
        public static DeviceManagerService Instance => _instance.Value;

        public const int TargetBaudRate = 9600;
        private readonly string _configFilePath;

        public SerialPort? PowerPort { get; private set; }
        public SerialPort? LoadPort { get; private set; }
        public SerialPort? RelayPort { get; private set; }

        public string OwonPortName { get; set; } = string.Empty;
        public string RigolPortName { get; set; } = string.Empty;
        public string RelayPortName { get; set; } = string.Empty;

        // 릴레이 통신 고속화를 위한 상시 오픈 시리얼 포트 및 현재 활성 릴레이 번호 추적
        private SerialPort? _persistentRelayPort = null;
        private int _currentActiveRelay = 0;
        private readonly SemaphoreSlim _relayLock = new(1, 1);

        public DeviceManagerService()
        {
            _configFilePath = Path.Combine(AppContext.BaseDirectory, "device_config.json");
            LoadSavedConfig();
        }

        #region [설정 파일 JSON 입출력]
        private void LoadSavedConfig()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<DeviceConnectionConfig>(json);
                    if (config != null)
                    {
                        OwonPortName = config.OwonPort ?? string.Empty;
                        RigolPortName = config.RigolPort ?? string.Empty;
                        RelayPortName = config.RelayPort ?? string.Empty;
                        Debug.WriteLine($"[Config 로드] 저장된 포트 -> 파워:{OwonPortName}, 부하:{RigolPortName}, 릴레이:{RelayPortName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Config 로드 에러] {ex.Message}");
            }
        }

        public void SaveCurrentConfig()
        {
            try
            {
                var config = new DeviceConnectionConfig
                {
                    OwonPort = OwonPortName,
                    RigolPort = RigolPortName,
                    RelayPort = RelayPortName,
                    BaudRate = TargetBaudRate
                };

                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFilePath, json);
                Debug.WriteLine($"[Config 저장 완료] {_configFilePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Config 저장 에러] {ex.Message}");
            }
        }
        #endregion

        #region [저수준 명령 송수신]
        public async Task<string> QueryCommandAsync(string portName, int baudRate, string command, int timeoutMs = 600)
        {
            if (string.IsNullOrWhiteSpace(portName)) return string.Empty;

            if (_persistentRelayPort != null && _persistentRelayPort.IsOpen && _persistentRelayPort.PortName == portName)
            {
                ClosePersistentRelayPort();
            }

            return await Task.Run(() =>
            {
                SerialPort? sp = null;
                try
                {
                    sp = new SerialPort(portName, TargetBaudRate, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = timeoutMs,
                        WriteTimeout = timeoutMs,
                        DtrEnable = true,
                        RtsEnable = true,
                        Encoding = Encoding.ASCII
                    };

                    sp.Open();
                    sp.DiscardInBuffer();
                    sp.DiscardOutBuffer();

                    byte[] txBytes = Encoding.ASCII.GetBytes(command);
                    sp.Write(txBytes, 0, txBytes.Length);

                    var sb = new StringBuilder();
                    var stopwatch = Stopwatch.StartNew();

                    while (stopwatch.ElapsedMilliseconds < timeoutMs)
                    {
                        if (sp.BytesToRead > 0)
                        {
                            string chunk = sp.ReadExisting();
                            sb.Append(chunk);

                            if (sb.ToString().Contains("\n") || sb.ToString().Contains("\r"))
                            {
                                break;
                            }
                        }
                        Thread.Sleep(20);
                    }

                    string rx = sb.ToString().Trim();
                    Debug.WriteLine($"[TX] {portName} -> {command.Trim()} | [RX]: {rx}");
                    return rx;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[통신 예외] {portName}: {ex.Message}");
                    return string.Empty;
                }
                finally
                {
                    if (sp != null && sp.IsOpen)
                    {
                        sp.Close();
                        sp.Dispose();
                    }
                }
            });
        }
        #endregion

        #region [우선 연결 기반 장비 스캔]
        public async Task<(bool power, bool load, bool relay)> ScanAndIdentifyDevicesAsync()
        {
            bool powerFound = false;
            bool loadFound = false;
            bool relayFound = false;

            ClosePersistentRelayPort();

            Debug.WriteLine("[1단계: 저장된 포트 우선 점검 시작]");
            if (!string.IsNullOrEmpty(OwonPortName))
            {
                powerFound = await VerifyOwonPortAsync(OwonPortName);
            }
            if (!string.IsNullOrEmpty(RigolPortName))
            {
                loadFound = await VerifyRigolPortAsync(RigolPortName);
            }
            if (!string.IsNullOrEmpty(RelayPortName))
            {
                relayFound = await VerifyRelayPortAsync(RelayPortName);
            }

            if (powerFound && loadFound && relayFound)
            {
                Debug.WriteLine("[우선 점검 완료] 저장된 모든 포트가 정상 응답하여 스캔을 즉시 종료합니다.");
                return (true, true, true);
            }

            Debug.WriteLine("[2단계: 미연결 장비 검색을 위한 전체 COM 포트 순회 시작]");
            string[] ports = SerialPort.GetPortNames();

            foreach (var port in ports)
            {
                if ((powerFound && port == OwonPortName) ||
                    (loadFound && port == RigolPortName) ||
                    (relayFound && port == RelayPortName))
                {
                    continue;
                }

                if (!powerFound || !loadFound)
                {
                    string idnRes = await QueryCommandAsync(port, TargetBaudRate, "*IDN?\n", 500);
                    if (string.IsNullOrEmpty(idnRes))
                    {
                        idnRes = await QueryCommandAsync(port, TargetBaudRate, "*IDN?\r\n", 500);
                    }

                    if (!string.IsNullOrEmpty(idnRes))
                    {
                        if (!loadFound && (idnRes.Contains("RIGOL", StringComparison.OrdinalIgnoreCase) ||
                                           idnRes.Contains("DL3021", StringComparison.OrdinalIgnoreCase)))
                        {
                            RigolPortName = port;
                            loadFound = true;
                            Debug.WriteLine($"==> 전자 로더 새로 발견: {port}");
                            continue;
                        }
                        else if (!powerFound && (idnRes.Contains("OWON", StringComparison.OrdinalIgnoreCase) ||
                                                 idnRes.Contains("SPE15054", StringComparison.OrdinalIgnoreCase)))
                        {
                            OwonPortName = port;
                            powerFound = true;
                            Debug.WriteLine($"==> 파워 서플라이 새로 발견: {port}");
                            continue;
                        }
                    }
                }

                if (!relayFound)
                {
                    string relayRes = await QueryCommandAsync(port, TargetBaudRate, "get.version.1\r\n", 500);
                    if (string.IsNullOrEmpty(relayRes))
                    {
                        relayRes = await QueryCommandAsync(port, TargetBaudRate, "get.version.0\r\n", 500);
                    }

                    if (!string.IsNullOrEmpty(relayRes) && relayRes.Contains("Version.", StringComparison.OrdinalIgnoreCase))
                    {
                        RelayPortName = port;
                        relayFound = true;
                        Debug.WriteLine($"==> 릴레이 보드 새로 발견: {port}");
                    }
                }

                if (powerFound && loadFound && relayFound) break;
            }

            SaveCurrentConfig();
            return (powerFound, loadFound, relayFound);
        }
        #endregion

        #region [개별 포트 유효성 검증 프라이빗 헬퍼]
        private async Task<bool> VerifyOwonPortAsync(string port)
        {
            string res = await QueryCommandAsync(port, TargetBaudRate, "*IDN?\n", 500);
            if (string.IsNullOrEmpty(res)) res = await QueryCommandAsync(port, TargetBaudRate, "*IDN?\r\n", 500);
            return res.Contains("OWON", StringComparison.OrdinalIgnoreCase) || res.Contains("SPE15054", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> VerifyRigolPortAsync(string port)
        {
            string res = await QueryCommandAsync(port, TargetBaudRate, "*IDN?\n", 500);
            if (string.IsNullOrEmpty(res)) res = await QueryCommandAsync(port, TargetBaudRate, "*IDN?\r\n", 500);
            return res.Contains("RIGOL", StringComparison.OrdinalIgnoreCase) || res.Contains("DL3021", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> VerifyRelayPortAsync(string port)
        {
            string res = await QueryCommandAsync(port, TargetBaudRate, "get.version.1\r\n", 500);
            if (string.IsNullOrEmpty(res)) res = await QueryCommandAsync(port, TargetBaudRate, "get.version.0\r\n", 500);
            return res.Contains("Version.", StringComparison.OrdinalIgnoreCase);
        }
        #endregion

        #region [개별 핑 (자가진단용)]
        public async Task<bool> CheckOwonPingAsync()
        {
            if (string.IsNullOrEmpty(OwonPortName) || !await VerifyOwonPortAsync(OwonPortName))
            {
                await ScanAndIdentifyDevicesAsync();
            }
            return !string.IsNullOrEmpty(OwonPortName) && await VerifyOwonPortAsync(OwonPortName);
        }

        public async Task<bool> CheckRigolPingAsync()
        {
            if (string.IsNullOrEmpty(RigolPortName) || !await VerifyRigolPortAsync(RigolPortName))
            {
                await ScanAndIdentifyDevicesAsync();
            }
            return !string.IsNullOrEmpty(RigolPortName) && await VerifyRigolPortAsync(RigolPortName);
        }

        public async Task<bool> CheckRelayPingAsync()
        {
            if (string.IsNullOrEmpty(RelayPortName) || !await VerifyRelayPortAsync(RelayPortName))
            {
                await ScanAndIdentifyDevicesAsync();
            }
            return !string.IsNullOrEmpty(RelayPortName) && await VerifyRelayPortAsync(RelayPortName);
        }
        #endregion

        #region [릴레이 제어 로직 (0~3 인덱스 정정 및 고속화)]

        // 릴레이 1~8번 번호 -> (레이어, 0~3 채널 인덱스) 맵핑
        private (int layer, int channel)? GetRelayAddress(int relayNumber)
        {
            return relayNumber switch
            {
                // [위층 레이어 0: 채널 0 ~ 3]
                1 => (0, 0), // 릴레이 1번: set.do.0.0 (CI 5V)
                2 => (0, 1), // 릴레이 2번: set.do.0.1 (CI 30V)
                3 => (0, 2), // 릴레이 3번: set.do.0.2 (SIV 5V)
                4 => (0, 3), // 릴레이 4번: set.do.0.3 (SIV 30V)

                // [아래층 레이어 1: 채널 0 ~ 3]
                5 => (1, 0), // 릴레이 5번: set.do.1.0 (TCMS 5V)
                6 => (1, 1), // 릴레이 6번: set.do.1.1 (TCMS 24V)
                7 => (1, 2), // 릴레이 7번: set.do.1.2 (100V 메인 투입)
                8 => (1, 3), // 릴레이 8번: set.do.1.3 (CI 24V)
                _ => null
            };
        }

        // 상시 오픈 포트를 통한 고속 명령 전송 (RS-485 회선 충돌 방지 적용)
        private async Task<bool> SendRelayFastCommandAsync(string command)
        {
            if (string.IsNullOrEmpty(RelayPortName)) return false;

            await _relayLock.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        if (_persistentRelayPort == null || _persistentRelayPort.PortName != RelayPortName || !_persistentRelayPort.IsOpen)
                        {
                            ClosePersistentRelayPort();
                            _persistentRelayPort = new SerialPort(RelayPortName, TargetBaudRate, Parity.None, 8, StopBits.One)
                            {
                                ReadTimeout = 150,
                                WriteTimeout = 150,
                                DtrEnable = true,
                                RtsEnable = true,
                                Encoding = Encoding.ASCII
                            };
                            _persistentRelayPort.Open();
                            _persistentRelayPort.DiscardInBuffer();
                            _persistentRelayPort.DiscardOutBuffer();
                        }

                        // 1. 송신 전 남아있는 잔류 수신 데이터 청소
                        _persistentRelayPort.DiscardInBuffer();

                        // 2. 명령어 송신
                        byte[] txBytes = Encoding.ASCII.GetBytes(command);
                        _persistentRelayPort.Write(txBytes, 0, txBytes.Length);

                        // 3. RS-485 반이중 충돌 방지: 보드가 응답을 마칠 때까지 최대 50ms 대기 후 회선 안정화
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 50)
                        {
                            if (_persistentRelayPort.BytesToRead > 0)
                            {
                                _persistentRelayPort.ReadExisting();
                                break;
                            }
                            Thread.Sleep(5);
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[릴레이 고속 전송 에러] {ex.Message}");
                        ClosePersistentRelayPort();
                        return false;
                    }
                });
            }
            finally
            {
                _relayLock.Release();
            }
        }

        private void ClosePersistentRelayPort()
        {
            if (_persistentRelayPort != null)
            {
                try
                {
                    if (_persistentRelayPort.IsOpen)
                    {
                        _persistentRelayPort.Close();
                    }
                    _persistentRelayPort.Dispose();
                }
                catch { }
                _persistentRelayPort = null;
            }
        }

        // 단일 릴레이 On / Off 제어 (set.do.X.Y.on / off)
        public async Task<bool> SetRelayAsync(int relayNumber, bool turnOn)
        {
            var addr = GetRelayAddress(relayNumber);
            if (!addr.HasValue) return false;

            string action = turnOn ? "on" : "off";
            string cmd = $"set.do.{addr.Value.layer}.{addr.Value.channel}.{action}\r\n";

            bool ok = await SendRelayFastCommandAsync(cmd);
            if (ok)
            {
                if (turnOn) _currentActiveRelay = relayNumber;
                else if (_currentActiveRelay == relayNumber) _currentActiveRelay = 0;
            }

            Debug.WriteLine($"[릴레이 제어] 번호:{relayNumber} ({addr.Value.layer}.{addr.Value.channel}) -> {action.ToUpper()}");
            return ok;
        }

        // 전체 릴레이 일괄 즉시 차단 (set.do.0.alloff + set.do.1.alloff)
        public async Task ResetAllRelaysAsync()
        {
            if (string.IsNullOrEmpty(RelayPortName)) return;

            Debug.WriteLine("[릴레이 제어] 전체 레이어 일괄 차단 (alloff)");
            await SendRelayFastCommandAsync("set.do.0.alloff\r\n");
            await Task.Delay(20);
            await SendRelayFastCommandAsync("set.do.1.alloff\r\n");
            _currentActiveRelay = 0;
        }

        // 시험 대상(CI / SIV / TCMS) 및 레일(5V / 24V / 30V)에 따른 릴레이 고속 전환
        public async Task<bool> ConnectTargetRailRelayAsync(string systemName, string railName)
        {
            int targetRelay = (systemName.ToUpper(), railName.ToUpper()) switch
            {
                ("CI", "5V") => 1, // set.do.0.0
                ("CI", "30V") => 2, // set.do.0.1
                ("CI", "24V") => 8, // set.do.1.3

                ("SIV", "5V") => 3, // set.do.0.2
                ("SIV", "30V") => 4, // set.do.0.3

                ("TCMS", "5V") => 5, // set.do.1.0
                ("TCMS", "24V") => 6, // set.do.1.1

                _ => 0
            };

            // 1. 미할당 레일 선택 시 기존에 켜져 있던 릴레이만 끄고 종료
            if (targetRelay == 0)
            {
                if (_currentActiveRelay > 0)
                {
                    await SetRelayAsync(_currentActiveRelay, false);
                }
                return false;
            }

            // 2. 이미 해당 릴레이가 켜져 있으면 추가 통신 생략
            if (_currentActiveRelay == targetRelay) return true;

            // 3. 기존에 켜져 있던 릴레이가 있었다면 단일 off 명령으로 즉시 해제 (Break-Before-Make)
            if (_currentActiveRelay > 0)
            {
                await SetRelayAsync(_currentActiveRelay, false);
                await Task.Delay(30);
            }

            // 4. 대상 릴레이 단독 투입
            bool ok = await SetRelayAsync(targetRelay, true);
            Debug.WriteLine($"[릴레이 투입 완료] {systemName} {railName} -> 릴레이 {targetRelay}번 ON");
            return ok;
        }

        // 릴레이 7번 (100V 메인 파워 인터록 스위치) 제어
        public async Task<bool> SetMain100VRelayAsync(bool turnOn)
        {
            return await SetRelayAsync(7, turnOn); // set.do.1.2.on / off
        }
        #endregion

        public void Dispose()
        {
            ClosePersistentRelayPort();
            _relayLock.Dispose();
        }
    }
}