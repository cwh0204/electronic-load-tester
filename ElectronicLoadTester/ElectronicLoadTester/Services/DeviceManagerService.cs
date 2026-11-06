using System;
using System.Diagnostics;
using System.Globalization;
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
                        Thread.Sleep(15);
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

        #region [파워 서플라이 제어 (SCPI)]
        public async Task<bool> SetPowerSupplyOutputAsync(double voltage, double current, bool turnOn)
        {
            if (string.IsNullOrEmpty(OwonPortName)) return false;

            await QueryCommandAsync(OwonPortName, TargetBaudRate, $"VOLT {voltage:F2}\n", 300);
            await QueryCommandAsync(OwonPortName, TargetBaudRate, $"CURR {current:F2}\n", 300);

            string cmd = turnOn ? "OUTP ON\n" : "OUTP OFF\n";
            await QueryCommandAsync(OwonPortName, TargetBaudRate, cmd, 300);

            Debug.WriteLine($"[파워 제어] 전압:{voltage:F2}V, 전류제한:{current:F2}A -> 출력을 {(turnOn ? "ON" : "OFF")} 하였습니다.");
            return true;
        }

        public async Task<(double volt, double curr)> MeasurePowerSupplyAsync()
        {
            if (string.IsNullOrEmpty(OwonPortName)) return (0, 0);

            string vStr = await QueryCommandAsync(OwonPortName, TargetBaudRate, "MEAS:VOLT?\n", 300);
            string cStr = await QueryCommandAsync(OwonPortName, TargetBaudRate, "MEAS:CURR?\n", 300);

            return (ParseScpiDouble(vStr), ParseScpiDouble(cStr));
        }
        #endregion

        #region [Rigol 전자 부하기 제어 (검증된 SCPI 및 물리 LED 점등 보장)]

        // 1. 단독 부하 입력 ON/OFF 제어 (전면 물리 버튼 LED 점등/소등)
        public async Task<bool> SetLoadInputAsync(bool turnOn)
        {
            if (string.IsNullOrEmpty(RigolPortName)) return false;

            string cmd = turnOn ? ":SOUR:INP ON\n" : ":SOUR:INP OFF\n";
            await QueryCommandAsync(RigolPortName, TargetBaudRate, cmd, 200);

            Debug.WriteLine($"[전자로더 입력 상태] -> {(turnOn ? "ON (LED 점등, 통전)" : "OFF (LED 소등, 차단)")}");
            return true;
        }

        // 2. 지속 부하 시험 및 일반 설정 적용
        public async Task<bool> ConfigureLoadAsync(string mode, double setVal)
        {
            if (string.IsNullOrEmpty(RigolPortName)) return false;

            try
            {
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SYST:REM\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, "*CLS\n", 100);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:INP OFF\n", 150);

                if (mode.Equals("CC", StringComparison.OrdinalIgnoreCase))
                {
                    await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:FUNC CURR\n", 150);
                    double range = (setVal > 4.0) ? 40.0 : 4.0;
                    await QueryCommandAsync(RigolPortName, TargetBaudRate, $":SOUR:CURR:RANG {range:F1}\n", 150);
                    await QueryCommandAsync(RigolPortName, TargetBaudRate, $":SOUR:CURR:LEV {setVal:F3}\n", 150);
                }
                else
                {
                    await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:FUNC VOLT\n", 150);
                    double range = (setVal > 15.0) ? 150.0 : 15.0;
                    await QueryCommandAsync(RigolPortName, TargetBaudRate, $":SOUR:VOLT:RANG {range:F1}\n", 150);
                    await QueryCommandAsync(RigolPortName, TargetBaudRate, $":SOUR:VOLT:LEV {setVal:F3}\n", 150);
                }

                Debug.WriteLine($"[전자로더 설정 성공] 모드:{mode}, 인가값:{setVal:F3}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[전자로더 설정 예외]: {ex.Message}");
                return false;
            }
        }

        // 3. [CC 과전류 차단 시험] 사전 준비 (40A 고정 레인지, 시작전류 인가 후 INPUT ON)
        public async Task<bool> PrepareLoadForCcSweepAsync(double startCurrent)
        {
            if (string.IsNullOrEmpty(RigolPortName)) return false;

            try
            {
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SYST:REM\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, "*CLS\n", 100);

                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:INP OFF\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:FUNC CURR\n", 150);

                // 스위핑 중 레인지 변경으로 인한 순간 차단 방지 (40A 고정)
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:CURR:RANG 40.0\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, $":SOUR:CURR:LEV {startCurrent:F3}\n", 150);

                // 전면 물리 [On/Off] 버튼 LED 점등 및 실제 부하 통전
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:INP ON\n", 250);

                Debug.WriteLine($"[Rigol CC 스위핑 사전 준비] 40A 레인지, 시작전류:{startCurrent:F3}A -> :SOUR:INP ON (LED 점등)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Rigol CC 스위핑 준비 예외]: {ex.Message}");
                return false;
            }
        }

        // 4. [CC 과전류 차단 시험] 연속 전류 변경 (부하 입력을 끄지 않고 전류만 계단식 상향)
        public async Task<bool> SetLoadCurrentImmediateAsync(double currentA)
        {
            if (string.IsNullOrEmpty(RigolPortName)) return false;

            string cmd = $":SOUR:CURR:LEV {currentA:F3}\n";
            await QueryCommandAsync(RigolPortName, TargetBaudRate, cmd, 100);
            return true;
        }

        // =========================================================================
        // 5. [신규: CV 과전압 차단 시험] 사전 준비 (150V 고정 레인지, 시작전압 인가 후 INPUT ON)
        // =========================================================================
        public async Task<bool> PrepareLoadForCvSweepAsync(double startVoltage)
        {
            if (string.IsNullOrEmpty(RigolPortName)) return false;

            try
            {
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SYST:REM\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, "*CLS\n", 100);

                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:INP OFF\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:FUNC VOLT\n", 150);

                // 스위핑 중 레인지 변경으로 인한 순간 차단 방지 (150V 고정)
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:VOLT:RANG 150.0\n", 150);
                await QueryCommandAsync(RigolPortName, TargetBaudRate, $":SOUR:VOLT:LEV {startVoltage:F3}\n", 150);

                // 전면 물리 [On/Off] 버튼 LED 점등 및 실제 부하 통전
                await QueryCommandAsync(RigolPortName, TargetBaudRate, ":SOUR:INP ON\n", 250);

                Debug.WriteLine($"[Rigol CV 스위핑 사전 준비] 150V 레인지, 시작전압:{startVoltage:F3}V -> :SOUR:INP ON (LED 점등)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Rigol CV 스위핑 준비 예외]: {ex.Message}");
                return false;
            }
        }

        // =========================================================================
        // 6. [신규: CV 과전압 차단 시험] 연속 전압 변경 (부하 입력을 끄지 않고 전압만 계단식 상향)
        // =========================================================================
        public async Task<bool> SetLoadVoltageImmediateAsync(double voltageV)
        {
            if (string.IsNullOrEmpty(RigolPortName)) return false;

            string cmd = $":SOUR:VOLT:LEV {voltageV:F3}\n";
            await QueryCommandAsync(RigolPortName, TargetBaudRate, cmd, 100);
            return true;
        }

        // 복합 쿼리(:MEAS:VOLT?;:MEAS:CURR?)를 통한 고속 1회 실측 계측
        public async Task<(double volt, double curr)> MeasureLoadAsync()
        {
            if (string.IsNullOrEmpty(RigolPortName)) return (0, 0);

            string rx = await QueryCommandAsync(RigolPortName, TargetBaudRate, ":MEAS:VOLT?;:MEAS:CURR?\n", 250);

            if (!string.IsNullOrWhiteSpace(rx))
            {
                string[] tokens = rx.Split(';');
                if (tokens.Length >= 2)
                {
                    double v = ParseScpiDouble(tokens[0]);
                    double c = ParseScpiDouble(tokens[1]);
                    return (v, c);
                }
                else if (tokens.Length == 1)
                {
                    return (ParseScpiDouble(tokens[0]), 0.0);
                }
            }

            return (0, 0);
        }

        private static double ParseScpiDouble(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0.0;
            raw = raw.Trim();
            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double val))
            {
                return val;
            }
            return 0.0;
        }
        #endregion

        #region [릴레이 제어 로직 (위층 0~3 레일 단일화 적용)]
        private (int layer, int channel)? GetRelayAddress(int relayNumber)
        {
            return relayNumber switch
            {
                // [위층 레이어 0: 4대 출력 레일 전용]
                1 => (0, 0), // 릴레이 1번: set.do.0.0 (5V 레일 통합 투입)
                2 => (0, 1), // 릴레이 2번: set.do.0.1 (24V 레일 통합 투입)
                3 => (0, 2), // 릴레이 3번: set.do.0.2 (30V 레일 통합 투입)
                4 => (0, 3), // 릴레이 4번: set.do.0.3 (48V 레일 통합 투입)

                // [아래층 레이어 1: 메인 전원 및 예비]
                5 => (1, 0), // 릴레이 5번: set.do.1.0 (예비)
                6 => (1, 1), // 릴레이 6번: set.do.1.1 (예비)
                7 => (1, 2), // 릴레이 7번: set.do.1.2 (100V 메인 투입 전용)
                8 => (1, 3), // 릴레이 8번: set.do.1.3 (예비)
                _ => null
            };
        }

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

                        _persistentRelayPort.DiscardInBuffer();

                        byte[] txBytes = Encoding.ASCII.GetBytes(command);
                        _persistentRelayPort.Write(txBytes, 0, txBytes.Length);

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

        public async Task ResetAllRelaysAsync()
        {
            if (string.IsNullOrEmpty(RelayPortName)) return;

            Debug.WriteLine("[릴레이 제어] 전체 레이어 일괄 차단 (alloff)");
            await SendRelayFastCommandAsync("set.do.0.alloff\r\n");
            await Task.Delay(20);
            await SendRelayFastCommandAsync("set.do.1.alloff\r\n");
            _currentActiveRelay = 0;
        }

        public async Task<bool> ConnectTargetRailRelayAsync(string systemName, string railName)
        {
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
                Debug.WriteLine($"[릴레이 거부] {systemName} 모듈은 {railName} 레일을 지원하지 않습니다.");
                if (_currentActiveRelay >= 1 && _currentActiveRelay <= 4)
                {
                    await SetRelayAsync(_currentActiveRelay, false);
                }
                return false;
            }

            int targetRelay = rail switch
            {
                "5V" => 1,
                "24V" => 2,
                "30V" => 3,
                "48V" => 4,
                _ => 0
            };

            if (targetRelay == 0) return false;
            if (_currentActiveRelay == targetRelay) return true;

            if (_currentActiveRelay >= 1 && _currentActiveRelay <= 4)
            {
                await SetRelayAsync(_currentActiveRelay, false);
                await Task.Delay(30);
            }

            bool ok = await SetRelayAsync(targetRelay, true);
            Debug.WriteLine($"[릴레이 투입 성공] {systemName} {railName} -> 릴레이 {targetRelay}번 ON");
            return ok;
        }

        public async Task<bool> SetMain100VRelayAsync(bool turnOn)
        {
            return await SetRelayAsync(7, turnOn); // 릴레이 7번: set.do.1.2 (100V 메인 투입 전용)
        }
        #endregion

        public void Dispose()
        {
            ClosePersistentRelayPort();
            _relayLock.Dispose();
        }
    }
}