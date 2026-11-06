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

        // [파워 제어]
        Task<bool> SetPowerSupplyOutputAsync(double voltage, double current, bool turnOn);
        Task<(double volt, double curr)> MeasurePowerSupplyAsync();

        // [전자로더 제어]
        Task<bool> ConfigureLoadAsync(string mode, double setVal);
        Task<bool> SetLoadInputAsync(bool turnOn);
        Task<(double volt, double curr)> MeasureLoadAsync();

        // [CC 과전류 차단 시험 전용 연속 통전 스위핑 메서드]
        Task<bool> PrepareLoadForCcSweepAsync(double startCurrent);
        Task<bool> SetLoadCurrentImmediateAsync(double currentA);

        // =========================================================================
        // [신규 추가] CV 과전압(OVP) 차단 시험 전용 연속 통전 스위핑 메서드
        // =========================================================================
        Task<bool> PrepareLoadForCvSweepAsync(double startVoltage);
        Task<bool> SetLoadVoltageImmediateAsync(double voltageV);
    }
}