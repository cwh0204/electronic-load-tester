using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// Rigol DL3000 시리즈 전자부하기 제어 드라이버 (SCPI 기반)
    /// </summary>
    public class RigolElectronicLoad : IElectronicLoad
    {
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private bool _disposed;

        public string DeviceName => "Rigol DL3000 Electronic Load";
        public bool IsConnected => _serialPort?.IsOpen ?? false;
        public bool IsInputOn { get; private set; }
        public LoadMode CurrentMode { get; private set; } = LoadMode.ConstantCurrent;

        public event EventHandler<bool>? ConnectionChanged;

        public async Task<bool> ConnectAsync(string portName, int baudRate = 115200, CancellationToken cancellationToken = default)
        {
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                if (IsConnected)
                    await DisconnectInternalAsync();

                _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    NewLine = "\n",
                    ReadTimeout = 1000,
                    WriteTimeout = 1000
                };

                _serialPort.Open();

                // 식별 쿼리 전송 (*IDN?)
                string idn = await QueryInternalAsync("*IDN?");
                if (string.IsNullOrWhiteSpace(idn) || (!idn.Contains("Rigol", StringComparison.OrdinalIgnoreCase) && !idn.Contains("DL3", StringComparison.OrdinalIgnoreCase)))
                {
                    await DisconnectInternalAsync();
                    return false;
                }

                // 현재 입력 상태 및 모드 동기화
                await RefreshStatusInternalAsync();

                ConnectionChanged?.Invoke(this, true);
                return true;
            }
            catch
            {
                await DisconnectInternalAsync();
                return false;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _syncLock.WaitAsync();
            try
            {
                await DisconnectInternalAsync();
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private Task DisconnectInternalAsync()
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                        _serialPort.Close();
                }
                catch { }

                _serialPort.Dispose();
                _serialPort = null;
            }

            IsInputOn = false;
            ConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        public async Task<bool> SetModeAsync(LoadMode mode, CancellationToken cancellationToken = default)
        {
            string modeStr = mode switch
            {
                LoadMode.ConstantCurrent => "CURR",
                LoadMode.ConstantVoltage => "VOLT",
                LoadMode.ConstantResistance => "RES",
                LoadMode.ConstantPower => "POW",
                _ => "CURR"
            };

            bool success = await ExecuteCommandAsync($":SOUR:FUNC {modeStr}", cancellationToken);
            if (success)
            {
                CurrentMode = mode;
            }
            return success;
        }

        public async Task<bool> SetConstantCurrentAsync(double current, CancellationToken cancellationToken = default)
        {
            // CC 모드로 자동 전환 후 전류값 설정
            if (CurrentMode != LoadMode.ConstantCurrent)
            {
                if (!await SetModeAsync(LoadMode.ConstantCurrent, cancellationToken))
                    return false;
            }

            return await ExecuteCommandAsync(string.Format(CultureInfo.InvariantCulture, ":SOUR:CURR:LEV:IMM {0:F4}", current), cancellationToken);
        }

        public async Task<bool> SetConstantVoltageAsync(double voltage, CancellationToken cancellationToken = default)
        {
            // CV 모드로 자동 전환 후 전압값 설정
            if (CurrentMode != LoadMode.ConstantVoltage)
            {
                if (!await SetModeAsync(LoadMode.ConstantVoltage, cancellationToken))
                    return false;
            }

            return await ExecuteCommandAsync(string.Format(CultureInfo.InvariantCulture, ":SOUR:VOLT:LEV:IMM {0:F4}", voltage), cancellationToken);
        }

        public async Task<bool> SetInputAsync(bool enable, CancellationToken cancellationToken = default)
        {
            string command = enable ? ":SOUR:INP:STAT ON" : ":SOUR:INP:STAT OFF";
            bool success = await ExecuteCommandAsync(command, cancellationToken);
            if (success)
            {
                IsInputOn = enable;
            }
            return success;
        }

        public async Task<double> ReadMeasuredVoltageAsync(CancellationToken cancellationToken = default)
        {
            string response = await QueryAsync(":MEAS:VOLT:DC?", cancellationToken);
            return double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
        }

        public async Task<double> ReadMeasuredCurrentAsync(CancellationToken cancellationToken = default)
        {
            string response = await QueryAsync(":MEAS:CURR:DC?", cancellationToken);
            return double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
        }

        public async Task<(double Voltage, double Current, double Power)> ReadTelemetryAsync(CancellationToken cancellationToken = default)
        {
            double v = await ReadMeasuredVoltageAsync(cancellationToken);
            double i = await ReadMeasuredCurrentAsync(cancellationToken);
            string pRes = await QueryAsync(":MEAS:POW:DC?", cancellationToken);
            double p = double.TryParse(pRes, NumberStyles.Float, CultureInfo.InvariantCulture, out double pVal) ? pVal : (v * i);

            return (Math.Round(v, 4), Math.Round(i, 4), Math.Round(p, 4));
        }

        #region SCPI 헬퍼 및 내부 메서드
        private async Task RefreshStatusInternalAsync()
        {
            try
            {
                // 입력단 활성화 여부 확인
                string inpStatus = await QueryInternalAsync(":SOUR:INP:STAT?");
                IsInputOn = inpStatus.Trim() == "1" || inpStatus.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase);

                // 현재 모드 확인
                string modeStr = await QueryInternalAsync(":SOUR:FUNC?");
                CurrentMode = modeStr.Trim().ToUpperInvariant() switch
                {
                    "CURR" or "CURRENT" => LoadMode.ConstantCurrent,
                    "VOLT" or "VOLTAGE" => LoadMode.ConstantVoltage,
                    "RES" or "RESISTANCE" => LoadMode.ConstantResistance,
                    "POW" or "POWER" => LoadMode.ConstantPower,
                    _ => LoadMode.ConstantCurrent
                };
            }
            catch { }
        }

        private async Task<bool> ExecuteCommandAsync(string command, CancellationToken ct)
        {
            await _syncLock.WaitAsync(ct);
            try
            {
                ThrowIfNotConnected();
                _serialPort!.WriteLine(command);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private async Task<string> QueryAsync(string query, CancellationToken ct)
        {
            await _syncLock.WaitAsync(ct);
            try
            {
                ThrowIfNotConnected();
                return await QueryInternalAsync(query);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private async Task<string> QueryInternalAsync(string query)
        {
            _serialPort!.DiscardInBuffer();
            _serialPort.WriteLine(query);

            return await Task.Run(() =>
            {
                try
                {
                    return _serialPort.ReadLine().Trim();
                }
                catch (TimeoutException)
                {
                    return string.Empty;
                }
            });
        }

        private void ThrowIfNotConnected()
        {
            if (!IsConnected || _serialPort == null)
                throw new InvalidOperationException("Rigol 전자부하기가 연결되어 있지 않습니다.");
        }
        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            DisconnectAsync().GetAwaiter().GetResult();
            _syncLock.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            await DisconnectAsync();
            _syncLock.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}