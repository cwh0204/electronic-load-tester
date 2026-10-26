using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// 전자 부하기 동작 모드 열거형
    /// </summary>
    public enum LoadMode
    {
        ConstantCurrent,    // CC (정전류)
        ConstantVoltage,    // CV (정전압)
        ConstantResistance, // CR (정저항)
        ConstantPower       // CP (정전력)
    }

    /// <summary>
    /// 프로그램 제어형 전자 부하기 인터페이스입니다.
    /// </summary>
    public interface IElectronicLoad : IDevice
    {
        /// <summary>
        /// 전자부하 입력단(Sink Input) 활성화 여부
        /// </summary>
        bool IsInputOn { get; }

        /// <summary>
        /// 현재 설정된 동작 모드
        /// </summary>
        LoadMode CurrentMode { get; }

        /// <summary>
        /// 전자부하 동작 모드를 설정합니다.
        /// </summary>
        Task<bool> SetModeAsync(LoadMode mode, CancellationToken cancellationToken = default);

        /// <summary>
        /// 정전류(CC) 모드 목표 소모 전류(A)를 지정합니다.
        /// </summary>
        Task<bool> SetConstantCurrentAsync(double current, CancellationToken cancellationToken = default);

        /// <summary>
        /// 정전압(CV) 모드 목표 전압(V)을 지정합니다.
        /// </summary>
        Task<bool> SetConstantVoltageAsync(double voltage, CancellationToken cancellationToken = default);

        /// <summary>
        /// 부하 입력(Sink)을 인가(ON)하거나 차단(OFF)합니다.
        /// </summary>
        /// <param name="enable">true: Load ON, false: Load OFF</param>
        Task<bool> SetInputAsync(bool enable, CancellationToken cancellationToken = default);

        /// <summary>
        /// 부하기 단자에 인가된 실제 전압을 측정합니다. (V)
        /// </summary>
        Task<double> ReadMeasuredVoltageAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 부하기로 유입되는 실제 소모 전류를 측정합니다. (A)
        /// </summary>
        Task<double> ReadMeasuredCurrentAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 단자 전압, 유입 전류, 소비 전력을 일괄 조회합니다.
        /// </summary>
        Task<(double Voltage, double Current, double Power)> ReadTelemetryAsync(CancellationToken cancellationToken = default);
    }
}