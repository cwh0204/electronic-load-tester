namespace ElectronicLoadTester.Models
{
    public class ChartPointDto
    {
        public double Time { get; set; }
        public double Volt { get; set; }
        public double Curr { get; set; }

        public ChartPointDto() { }

        public ChartPointDto(double time, double volt, double curr)
        {
            Time = time;
            Volt = volt;
            Curr = curr;
        }
    }
}