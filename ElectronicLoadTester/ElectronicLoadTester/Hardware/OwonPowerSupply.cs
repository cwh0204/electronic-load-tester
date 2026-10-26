using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// OWON SPE 시리즈 파워서플라이 제어 드라이버 (SCPI 텍스트 통신 기반)
    /// </summary>
    public class OwonPowerSupply : IPowerSupply
    {
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private bool _disposed;

        public string DeviceName => "OWON SPE Power Supply (SCPI)";
        public bool IsConnected => _serialPort?.IsOpen ?? false;
        public bool IsOutputOn { get; private set; }

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
                if (string.IsNullOrWhiteSpace(idn) || (!idn.Contains("OWON", StringComparison.OrdinalIgnoreCase) && !idn.Contains("SPE", StringComparison.OrdinalIgnoreCase)))
                {
                    await DisconnectInternalAsync();
                    return false;
                }

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

            IsOutputOn = false;
            ConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        public async Task<bool> SetVoltageAsync(double voltage, CancellationToken cancellationToken = default)
        {
            return await ExecuteCommandAsync(string.Format(CultureInfo.InvariantCulture, "VOLT {0:F3}", voltage), cancellationToken);
        }

        public async Task<bool> SetCurrentLimitAsync(double currentLimit, CancellationToken cancellationToken = default)
        {
            return await ExecuteCommandAsync(string.Format(CultureInfo.InvariantCulture, "CURR {0:F3}", currentLimit), cancellationToken);
        }

        public async Task<bool> SetOutputAsync(bool enable, CancellationToken cancellationToken = default)
        {
            string command = enable ? "OUTP ON" : "OUTP OFF";
            bool success = await ExecuteCommandAsync(command, cancellationToken);
            if (success)
            {
                IsOutputOn = enable;
            }
            return success;
        }

        public async Task<double> ReadVoltageAsync(CancellationToken cancellationToken = default)
        {
            string response = await QueryAsync("MEAS:VOLT?", cancellationToken);
            return double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0.0;
        }

        public async Task<double> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            string response = await QueryAsync("MEAS:CURR?", cancellationToken);
            return double.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0.0;
        }

        public async Task<(double Voltage, double Current, double Power)> ReadTelemetryAsync(CancellationToken cancellationToken = default)
        {
            // 전압, 전류, 전력 연속 측정
            double v = await ReadVoltageAsync(cancellationToken);
            double i = await ReadCurrentAsync(cancellationToken);
            string pRes = await QueryAsync("MEAS:POW?", cancellationToken);
            double p = double.TryParse(pRes, NumberStyles.Float, CultureInfo.InvariantCulture, out double powValue) ? powValue : (v * i);

            return (Math.Round(v, 3), Math.Round(i, 3), Math.Round(p, 3));
        }

        #region SCPI 통신 헬퍼
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
                throw new InvalidOperationException("장비가 연결되어 있지 않습니다.");
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