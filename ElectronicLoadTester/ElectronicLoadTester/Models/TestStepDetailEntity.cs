namespace ElectronicLoadTester.Models
{
    public class TestStepDetailEntity
    {
        public long Id { get; set; }
        public long MasterId { get; set; }
        public int StepNo { get; set; }
        public string StepName { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string TargetCondition { get; set; } = string.Empty;
        public string SpecRange { get; set; } = string.Empty;
        public double MeasuredVoltage { get; set; }
        public double MeasuredCurrent { get; set; }
        public string Result { get; set; } = "WAIT"; // PASS / FAIL
        public string ExecutedAt { get; set; } = string.Empty;
    }
}