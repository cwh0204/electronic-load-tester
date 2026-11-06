using System;

namespace ElectronicLoadTester.Models
{
    public class RailSpecItem
    {
        // [1] CC 모드 (정전류 지속 부하 시험)
        public double CcCurrent { get; set; } = 2.00;      // 인가: 지속 부하 전류 (A)
        public double CcMinVolt { get; set; } = 4.75;      // 판정: 최저 허용 전압 (V)

        // [2] CV 모드 (정전압 지속 부하 시험)
        public double CvVolt { get; set; } = 5.00;         // 인가: 지속 부하 전압 (V)
        public double CvMinCurrent { get; set; } = 1.80;   // 판정: 최저 허용 전류 (A)

        // [3] 공통 지속 통전 시간
        public double EnduranceTime { get; set; } = 5.0;  // 지속 시험 시간 (초)

        // [4] CC 과전류 차단 시험 (OCP) - 사용자가 직접 지정하는 시작치 & 목표치
        public double OcpStartCurrent { get; set; } = 2.00; // [신규] 스위핑 시작 전류 (A)
        public double TripCurrent { get; set; } = 3.00;     // 판정: 목표 트립 차단 전류 (A)

        // [5] CV 과전압 차단 시험 (OVP) - 사용자가 직접 지정하는 시작치 & 목표치
        public double OvpStartVoltage { get; set; } = 5.00; // [신규] 스위핑 시작 전압 (V)
        public double TripVoltage { get; set; } = 6.50;     // 판정: 목표 트립 차단 전압 (V)

        public RailSpecItem() { }

        // [레거시 호환 1] 기존 6개 파라미터 생성자
        public RailSpecItem(double ccA, double ccMinV, double cvV, double cvMinA, double time, double tripA)
            : this(ccA, ccMinV, cvV, cvMinA, time, ccA, tripA, cvV, Math.Round(cvV * 1.3, 2))
        {
        }

        // [레거시 호환 2] 과전압 트립값만 포함하던 7개 파라미터 생성자
        public RailSpecItem(double ccA, double ccMinV, double cvV, double cvMinA, double time, double tripA, double tripV)
            : this(ccA, ccMinV, cvV, cvMinA, time, ccA, tripA, cvV, tripV)
        {
        }

        // [마스터 생성자] 시작 전류 및 시작 전압까지 독립 지정하는 9개 파라미터 생성자
        public RailSpecItem(double ccA, double ccMinV, double cvV, double cvMinA, double time,
                            double ocpStartA, double tripA,
                            double ovpStartV, double tripV)
        {
            CcCurrent = ccA;
            CcMinVolt = ccMinV;
            CvVolt = cvV;
            CvMinCurrent = cvMinA;
            EnduranceTime = time;
            OcpStartCurrent = ocpStartA;
            TripCurrent = tripA;
            OvpStartVoltage = ovpStartV;
            TripVoltage = tripV;
        }
    }

    public class SystemProfile
    {
        // 기본 레일 규격 (5V, 24V, 30V, 48V) - 9개 파라미터 완전 초기화
        public RailSpecItem Rail5V { get; set; } = new(2.00, 4.75, 5.00, 1.80, 5.0, 2.00, 3.00, 5.00, 6.50);
        public RailSpecItem Rail24V { get; set; } = new(2.50, 23.00, 24.00, 2.20, 5.0, 2.50, 3.50, 24.00, 30.00);
        public RailSpecItem Rail30V { get; set; } = new(1.50, 28.50, 30.00, 1.30, 5.0, 1.50, 2.20, 30.00, 38.00);
        public RailSpecItem Rail48V { get; set; } = new(1.00, 46.50, 48.00, 0.80, 5.0, 1.00, 1.80, 48.00, 58.00);
    }

    public class AppTestSettings
    {
        public string LastSelectedSystem { get; set; } = "TCMS-CC";
        public string LastSelectedRail { get; set; } = "5V";
        public string LastSelectedMode { get; set; } = "CC"; // "CC" 또는 "CV"

        #region [신규 6종 세분화 모듈 프로파일 (시작값 / 트립값 완비)]

        // 1. TCMS-CC: 5V, 24V 지원
        public SystemProfile TCMS_CC { get; set; } = new()
        {
            Rail5V = new RailSpecItem(2.00, 4.75, 5.00, 1.80, 5.0, 2.00, 3.00, 5.00, 6.50),
            Rail24V = new RailSpecItem(2.50, 23.00, 24.00, 2.20, 5.0, 2.50, 3.50, 24.00, 30.00),
            Rail30V = new RailSpecItem(1.50, 28.50, 30.00, 1.30, 5.0, 1.50, 2.20, 30.00, 38.00),
            Rail48V = new RailSpecItem(1.00, 46.50, 48.00, 0.80, 5.0, 1.00, 1.80, 48.00, 58.00)
        };

        // 2. TCMS-TC: 5V, 24V 지원
        public SystemProfile TCMS_TC { get; set; } = new()
        {
            Rail5V = new RailSpecItem(2.00, 4.75, 5.00, 1.80, 5.0, 2.00, 3.00, 5.00, 6.50),
            Rail24V = new RailSpecItem(2.50, 23.00, 24.00, 2.20, 5.0, 2.50, 3.50, 24.00, 30.00),
            Rail30V = new RailSpecItem(1.50, 28.50, 30.00, 1.30, 5.0, 1.50, 2.20, 30.00, 38.00),
            Rail48V = new RailSpecItem(1.00, 46.50, 48.00, 0.80, 5.0, 1.00, 1.80, 48.00, 58.00)
        };

        // 3. CI-123: 5V, 30V, 48V 지원
        public SystemProfile CI_123 { get; set; } = new()
        {
            Rail5V = new RailSpecItem(2.50, 4.70, 5.00, 2.20, 5.0, 2.50, 3.80, 5.00, 6.50),
            Rail24V = new RailSpecItem(5.00, 22.80, 24.00, 4.50, 5.0, 5.00, 7.50, 24.00, 30.00),
            Rail30V = new RailSpecItem(3.00, 28.20, 30.00, 2.70, 5.0, 3.00, 4.50, 30.00, 38.00),
            Rail48V = new RailSpecItem(1.50, 46.00, 48.00, 1.20, 5.0, 1.50, 2.50, 48.00, 58.00)
        };

        // 4. CI-4: 5V, 30V, 48V 지원
        public SystemProfile CI_4 { get; set; } = new()
        {
            Rail5V = new RailSpecItem(2.50, 4.70, 5.00, 2.20, 5.0, 2.50, 3.80, 5.00, 6.50),
            Rail24V = new RailSpecItem(5.00, 22.80, 24.00, 4.50, 5.0, 5.00, 7.50, 24.00, 30.00),
            Rail30V = new RailSpecItem(3.00, 28.20, 30.00, 2.70, 5.0, 3.00, 4.50, 30.00, 38.00),
            Rail48V = new RailSpecItem(1.50, 46.00, 48.00, 1.20, 5.0, 1.50, 2.50, 48.00, 58.00)
        };

        // 5. SIV-123: 5V, 24V, 30V 지원
        public SystemProfile SIV_123 { get; set; } = new()
        {
            Rail5V = new RailSpecItem(3.00, 4.80, 5.00, 2.70, 5.0, 3.00, 4.50, 5.00, 6.50),
            Rail24V = new RailSpecItem(4.00, 22.50, 24.00, 3.60, 5.0, 4.00, 6.00, 24.00, 30.00),
            Rail30V = new RailSpecItem(2.00, 28.00, 30.00, 1.80, 5.0, 2.00, 3.00, 30.00, 38.00),
            Rail48V = new RailSpecItem(1.00, 46.50, 48.00, 0.80, 5.0, 1.00, 1.80, 48.00, 58.00)
        };

        // 6. SIV-4: 5V, 30V 지원
        public SystemProfile SIV_4 { get; set; } = new()
        {
            Rail5V = new RailSpecItem(3.00, 4.80, 5.00, 2.70, 5.0, 3.00, 4.50, 5.00, 6.50),
            Rail24V = new RailSpecItem(4.00, 22.50, 24.00, 3.60, 5.0, 4.00, 6.00, 24.00, 30.00),
            Rail30V = new RailSpecItem(2.00, 28.00, 30.00, 1.80, 5.0, 2.00, 3.00, 30.00, 38.00),
            Rail48V = new RailSpecItem(1.00, 46.50, 48.00, 0.80, 5.0, 1.00, 1.80, 48.00, 58.00)
        };
        #endregion

        #region [구버전 호환용 레거시 프로파일]
        public SystemProfile TCMS { get; set; } = new();
        public SystemProfile SIV { get; set; } = new();
        public SystemProfile CI { get; set; } = new();
        #endregion
    }
}