using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// RS-485 버스 기반 다채널 릴레이 보드 제어 인터페이스입니다.
    /// </summary>
    public interface IRelayController : IDevice
    {
        /// <summary>
        /// 특정 릴레이 보드의 개별 채널을 ON/OFF 합니다.
        /// </summary>
        /// <param name="slaveAddress">RS-485 슬레이브 보드 주소 (예: 1 또는 2)</param>
        /// <param name="channel">릴레이 채널 번호 (1부터 시작)</param>
        /// <param name="state">true: 접점 통전(ON/Close), false: 접점 차단(OFF/Open)</param>
        Task<bool> SetChannelAsync(byte slaveAddress, int channel, bool state, CancellationToken cancellationToken = default);

        /// <summary>
        /// 특정 릴레이 보드의 모든 채널을 일괄 ON 또는 OFF 합니다.
        /// </summary>
        /// <param name="slaveAddress">RS-485 슬레이브 보드 주소</param>
        /// <param name="state">true: 전체 ON, false: 전체 OFF</param>
        Task<bool> SetAllChannelsAsync(byte slaveAddress, bool state, CancellationToken cancellationToken = default);

        /// <summary>
        /// 특정 릴레이 보드의 전체 접점 상태 비트마스크를 조회합니다.
        /// </summary>
        /// <param name="slaveAddress">RS-485 슬레이브 보드 주소</param>
        /// <returns>각 비트가 채널 상태를 나타내는 정수값 (예: Bit 0 = Ch 1)</returns>
        Task<int> GetChannelStatusMaskAsync(byte slaveAddress, CancellationToken cancellationToken = default);
    }
}