using ElectronicLoadTester.ViewModels;
using MahApps.Metro.Controls;

namespace ElectronicLoadTester.Views
{
    public partial class ResultSearchWindow : MetroWindow
    {
        public ResultSearchWindow(ResultSearchViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        public ResultSearchWindow() : this(new ResultSearchViewModel())
        {
        }
    }
}