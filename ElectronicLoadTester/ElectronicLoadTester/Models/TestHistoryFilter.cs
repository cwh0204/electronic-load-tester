using System;

namespace ElectronicLoadTester.Models
{
    public class TestHistoryFilter
    {
        public DateTime StartDate { get; set; } = DateTime.Today.AddDays(-7);
        public DateTime EndDate { get; set; } = DateTime.Today;
        public string TargetSystem { get; set; } = "전체";   // 전체 / TCMS / SIV / CI
        public string FinalVerdict { get; set; } = "전체";   // 전체 / PASS / FAIL
        public string SearchText { get; set; } = string.Empty; // 시리얼 번호 또는 비고 검색
    }
}