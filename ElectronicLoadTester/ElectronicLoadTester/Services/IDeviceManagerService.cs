using System.IO.Ports;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Services
{
    public interface IDeviceManagerService
    {
        SerialPort? PowerPort { get; }
        SerialPort? LoadPort { get; }
        SerialPort? RelayPort { get; }

        string OwonPortName { get; set; }
        string RigolPortName { get; set; }
        string RelayPortName { get; set; }

        Task<string> QueryCommandAsync(string portName, int baudRate, string command, int timeoutMs = 600);
        Task<(bool power, bool load, bool relay)> ScanAndIdentifyDevicesAsync();
        Task<bool> CheckOwonPingAsync();
        Task<bool> CheckRigolPingAsync();
        Task<bool> CheckRelayPingAsync();

        // [릴레이 제어 핵심 메서드]
        Task<bool> SetRelayAsync(int relayNumber, bool turnOn);
        Task ResetAllRelaysAsync();
        Task<bool> ConnectTargetRailRelayAsync(string systemName, string railName);
        Task<bool> SetMain100VRelayAsync(bool turnOn);
    }
}