using Avalonia;
using Avalonia.ReactiveUI;
using Wabbajack.App.UI.ViewModels;

namespace Wabbajack.App.UI.Views
{
    public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();

            #if DEBUG
            this.AttachDevTools();
            #endif
        }
    }
}