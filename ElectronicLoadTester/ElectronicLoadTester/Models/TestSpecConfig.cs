namespace ElectronicLoadTester.Models
{
    public class RailSpecItem
    {
        // [1] CC 모드 (정전류 부하 시험)
        public double CcCurrent { get; set; } = 2.00;      // 인가: 설정 부하 전류 (A)
        public double CcMinVolt { get; set; } = 4.75;      // 판정: 최저 허용 전압 (V)

        // [2] CV 모드 (정전압 부하 시험)
        public double CvVolt { get; set; } = 5.00;         // 인가: 설정 부하 전압 (V)
        public double CvMinCurrent { get; set; } = 1.80;   // 판정: 최저 출력 전류 (A)

        // [3] 공통 검사 항목
        public double EnduranceTime { get; set; } = 5.0;  // 시험 시간 (초)
        public double TripCurrent { get; set; } = 3.00;    // 과전류 차단 기준 (A)

        public RailSpecItem() { }

        public RailSpecItem(double ccA, double ccMinV, double cvV, double cvMinA, double time, double trip)
        {
            CcCurrent = ccA;
            CcMinVolt = ccMinV;
            CvVolt = cvV;
            CvMinCurrent = cvMinA;
            EnduranceTime = time;
            TripCurrent = trip;
        }
    }

    public class SystemProfile
    {
        public RailSpecItem Rail5V { get; set; } = new();
        public RailSpecItem Rail24V { get; set; } = new();
        public RailSpecItem Rail30V { get; set; } = new();
    }

    public class AppTestSettings
    {
        public string LastSelectedSystem { get; set; } = "TCMS";
        public string LastSelectedRail { get; set; } = "5V";
        public string LastSelectedMode { get; set; } = "CC"; // "CC" 또는 "CV"

        public SystemProfile TCMS { get; set; } = new()
        {
            Rail5V = new RailSpecItem(2.00, 4.75, 5.00, 1.80, 5.0, 3.00),
            Rail24V = new RailSpecItem(2.50, 23.00, 24.00, 2.20, 5.0, 3.50),
            Rail30V = new RailSpecItem(1.50, 28.50, 30.00, 1.30, 5.0, 2.20)
        };

        public SystemProfile SIV { get; set; } = new()
        {
            Rail5V = new RailSpecItem(3.00, 4.80, 5.00, 2.70, 5.0, 4.50),
            Rail24V = new RailSpecItem(4.00, 22.50, 24.00, 3.60, 5.0, 6.00),
            Rail30V = new RailSpecItem(2.00, 28.00, 30.00, 1.80, 5.0, 3.00)
        };

        public SystemProfile CI { get; set; } = new()
        {
            Rail5V = new RailSpecItem(2.50, 4.70, 5.00, 2.20, 5.0, 3.80),
            Rail24V = new RailSpecItem(5.00, 22.80, 24.00, 4.50, 5.0, 7.50),
            Rail30V = new RailSpecItem(3.00, 28.20, 30.00, 2.70, 5.0, 4.50)
        };
    }
}