namespace ElectronicLoadTester.Models
{
    public class DeviceConnectionConfig
    {
        public string OwonPort { get; set; } = string.Empty;
        public string RigolPort { get; set; } = string.Empty;
        public string RelayPort { get; set; } = string.Empty;
        public int BaudRate { get; set; } = 9600;
    }
}