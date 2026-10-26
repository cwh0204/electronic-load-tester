using System;
using System.Threading;
using System.Threading.Tasks;

namespace ElectronicLoadTester.Hardware
{
    /// <summary>
    /// 하드웨어 계측기 및 제어 장비의 공통 수명 주기를 정의하는 기본 인터페이스입니다.
    /// </summary>
    public interface IDevice : IAsyncDisposable, IDisposable
    {
        /// <summary>
        /// 장비 식별 명칭 (예: "OWON SPE3103", "Rigol DL3021", "Relay Controller #1")
        /// </summary>
        string DeviceName { get; }

        /// <summary>
        /// 현재 물리 포트 연결 여부
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 연결 상태 변경 이벤트
        /// </summary>
        event EventHandler<bool>? ConnectionChanged;

        /// <summary>
        /// 지정된 직렬 포트 및 통신 속도로 장치에 연결합니다.
        /// </summary>
        /// <param name="portName">시리얼 포트 이름 (예: "COM8")</param>
        /// <param name="baudRate">통신 속도 (기본: 9600)</param>
        /// <param name="cancellationToken">취소 토큰</param>
        Task<bool> ConnectAsync(string portName, int baudRate = 9600, CancellationToken cancellationToken = default);

        /// <summary>
        /// 장치와의 직렬 포트 연결을 안전하게 해제합니다.
        /// </summary>
        Task DisconnectAsync();
    }
}