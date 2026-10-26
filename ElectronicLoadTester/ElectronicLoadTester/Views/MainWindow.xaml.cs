using ElectronicLoadTester.ViewModels;
using MahApps.Metro.Controls;

namespace ElectronicLoadTester
{
    /// <summary>
    /// MainWindow.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        // 1. 기본 생성자 (XAML StartupUri 또는 직접 인스턴스화 지원)
        public MainWindow() : this(new MainViewModel())
        {
        }

        // 2. ViewModel 주입 생성자 (DI 컨테이너 지원)
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}