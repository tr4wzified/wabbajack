// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using Wabbajack.App.WpfUI.ViewModels.Pages;
using Wpf.Ui.Controls;

namespace Wabbajack.App.WpfUI.Views.Pages
{
    public partial class DashboardPage : INavigableView<HomeVM>
    {
        public HomeVM ViewModel { get; }

        public DashboardPage(HomeVM viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;

            InitializeComponent();
        }
    }
}
