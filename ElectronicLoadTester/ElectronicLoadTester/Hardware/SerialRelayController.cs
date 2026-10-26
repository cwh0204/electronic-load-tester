using System;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// ASCII 텍스트 프로토콜 기반 RS-485 2층 릴레이 보드 제어 드라이버
    /// </summary>
    public class SerialRelayController : IRelayController
    {
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly ConcurrentDictionary<byte, int> _channelStatusMaskMap = new();
        private bool _disposed;

        public string DeviceName => "RS-485 Dual Relay Controller";
        public bool IsConnected => _serialPort?.IsOpen ?? false;

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
                    NewLine = "\r\n", // 통신 사양에 맞춰 필요 시 "\n"으로 조정
                    ReadTimeout = 800,
                    WriteTimeout = 800,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.Open();

                // 1번 보드 펌웨어 버전 조회로 연결 확인 (Ping)
                string version = await SendCommandInternalAsync("get.version.1");
                if (string.IsNullOrWhiteSpace(version))
                {
                    // 1번 응답 실패 시 2번 보드 확인
                    version = await SendCommandInternalAsync("get.version.2");
                }

                if (string.IsNullOrWhiteSpace(version))
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

            _channelStatusMaskMap.Clear();
            ConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 특정 보드의 릴레이 채널 개별 제어 (set.do.X.Y.on / set.do.X.Y.off)
        /// </summary>
        public async Task<bool> SetChannelAsync(byte slaveAddress, int channel, bool state, CancellationToken cancellationToken = default)
        {
            string stateStr = state ? "on" : "off";
            string command = $"set.do.{slaveAddress}.{channel}.{stateStr}";

            string response = await SendCommandAsync(command, cancellationToken);
            bool success = !string.IsNullOrWhiteSpace(response);

            if (success)
            {
                // 성공 시 내부 비트마스크 상태 갱신
                _channelStatusMaskMap.AddOrUpdate(slaveAddress,
                    state ? (1 << (channel - 1)) : 0,
                    (_, currentMask) => state ? (currentMask | (1 << (channel - 1))) : (currentMask & ~(1 << (channel - 1))));
            }

            return success;
        }

        /// <summary>
        /// 특정 보드의 전체 릴레이 일괄 제어 (set.do.X.allon / set.do.X.alloff)
        /// </summary>
        public async Task<bool> SetAllChannelsAsync(byte slaveAddress, bool state, CancellationToken cancellationToken = default)
        {
            string command = state ? $"set.do.{slaveAddress}.allon" : $"set.do.{slaveAddress}.alloff";

            string response = await SendCommandAsync(command, cancellationToken);
            bool success = !string.IsNullOrWhiteSpace(response);

            if (success)
            {
                // 채널 비트마스크 전체 갱신 (0 또는 0xFFFF)
                _channelStatusMaskMap[slaveAddress] = state ? 0xFFFF : 0x0000;
            }

            return success;
        }

        /// <summary>
        /// 특정 보드의 펌웨어 버전 조회 (get.version.X)
        /// </summary>
        public async Task<string> GetVersionAsync(byte slaveAddress, CancellationToken cancellationToken = default)
        {
            return await SendCommandAsync($"get.version.{slaveAddress}", cancellationToken);
        }

        /// <summary>
        /// 현재 보드의 릴레이 출력 상태 비트마스크 반환 (소프트웨어 캐시)
        /// </summary>
        public Task<int> GetChannelStatusMaskAsync(byte slaveAddress, CancellationToken cancellationToken = default)
        {
            _channelStatusMaskMap.TryGetValue(slaveAddress, out int mask);
            return Task.FromResult(mask);
        }

        #region Serial 통신 헬퍼
        private async Task<string> SendCommandAsync(string command, CancellationToken ct)
        {
            await _syncLock.WaitAsync(ct);
            try
            {
                ThrowIfNotConnected();
                return await SendCommandInternalAsync(command);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private async Task<string> SendCommandInternalAsync(string command)
        {
            _serialPort!.DiscardInBuffer();
            _serialPort.WriteLine(command);

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
                throw new InvalidOperationException("릴레이 보드가 연결되어 있지 않습니다.");
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