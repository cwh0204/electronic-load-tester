using System.Windows;
using MahApps.Metro.Controls; // MetroWindow 사용을 위해 필수
using ElectronicLoadTester.ViewModels;

namespace ElectronicLoadTester.Views
{
    // : Window 로 되어 있다면 반드시 : MetroWindow 로 변경해야 합니다.
    public partial class SelfTestWindow : MetroWindow
    {
        public SelfTestWindow()
        {
            InitializeComponent();
            DataContext = new SelfTestViewModel();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}