using System;
using System.Collections.Generic;

namespace ElectronicLoadTester.Models
{
    public class TestMasterEntity
    {
        public long Id { get; set; }
        public string SerialNumber { get; set; } = string.Empty; // 자동 채번: TCMS-5V-20261028-001
        public string TargetSystem { get; set; } = string.Empty; // TCMS / SIV / CI
        public string RailName { get; set; } = string.Empty;     // 5V / 24V / 30V
        public string TestMode { get; set; } = string.Empty;     // CC / CV
        public string FinalVerdict { get; set; } = "READY";      // PASS / FAIL
        public int TotalTestedCount { get; set; } = 2;
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public string TestedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        public string? TrendDataJson { get; set; }               // List<ChartPointDto> 직렬화 문자열
        public string? Notes { get; set; }

        public List<TestStepDetailEntity> Steps { get; set; } = new();
    }
}