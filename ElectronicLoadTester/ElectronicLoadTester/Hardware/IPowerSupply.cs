using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// DC 전원 공급기 제어 및 계측 인터페이스입니다.
    /// </summary>
    public interface IPowerSupply : IDevice
    {
        /// <summary>
        /// 현재 전원 출력단(Output) 활성화 여부
        /// </summary>
        bool IsOutputOn { get; }

        /// <summary>
        /// 목표 출력 전압(V)을 설정합니다.
        /// </summary>
        /// <param name="voltage">설정 전압 단위 (V)</param>
        Task<bool> SetVoltageAsync(double voltage, CancellationToken cancellationToken = default);

        /// <summary>
        /// 과전류 보호 및 제한 전류값(A)을 설정합니다.
        /// </summary>
        /// <param name="currentLimit">설정 전류 단위 (A)</param>
        Task<bool> SetCurrentLimitAsync(double currentLimit, CancellationToken cancellationToken = default);

        /// <summary>
        /// 출력단(Output)을 켜거나 끕니다.
        /// </summary>
        /// <param name="enable">true: ON, false: OFF</param>
        Task<bool> SetOutputAsync(bool enable, CancellationToken cancellationToken = default);

        /// <summary>
        /// 장치 단자에서 측정된 실제 출력 전압을 읽어옵니다. (V)
        /// </summary>
        Task<double> ReadVoltageAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 장치 단자에서 측정된 실제 출력 전류를 읽어옵니다. (A)
        /// </summary>
        Task<double> ReadCurrentAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 전압, 전류, 전력 측정값을 단일 패킷으로 한 번에 조회합니다.
        /// </summary>
        Task<(double Voltage, double Current, double Power)> ReadTelemetryAsync(CancellationToken cancellationToken = default);
    }
}