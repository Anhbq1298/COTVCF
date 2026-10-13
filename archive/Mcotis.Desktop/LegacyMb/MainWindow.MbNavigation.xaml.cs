// ARCHIVED 2026-10-13
// Former MainWindow navigation handler for the legacy MB pages.
// This source fragment is outside the desktop project and is not compiled.
    private void MbMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && MenuTag(sender) is >= 0 and var tab) vm.SelectedTab = tab;
        OpenPane();
    }

    private void MainMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) == MainMenu)
            SapConnectionControls.Visibility = Equals(item.Header, "_MB") ? Visibility.Visible : Visibility.Collapsed;
    }
