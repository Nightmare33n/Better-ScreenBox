using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Screenbox.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Screenbox.Pages;

/// <summary>
/// Paste a video or playlist link and keep it for offline viewing.
/// </summary>
public sealed partial class DownloadsPage : Page
{
    internal DownloadsPageViewModel ViewModel => (DownloadsPageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    public DownloadsPage()
    {
        InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<DownloadsPageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.OnNavigatedToAsync();
    }

    private void LinkBox_OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        // Push the text to the view model first; TwoWay x:Bind updates it on every keystroke, but be explicit.
        ViewModel.Url = LinkBox.Text;
        if (ViewModel.AddCommand.CanExecute(null))
        {
            ViewModel.AddCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Reads the clipboard in the view: clipboard access is a UI concern and must happen on the UI thread.
    /// </summary>
    private async void PasteButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            string? text = null;
            if (content.Contains(StandardDataFormats.WebLink))
            {
                text = (await content.GetWebLinkAsync()).ToString();
            }
            else if (content.Contains(StandardDataFormats.Text))
            {
                text = await content.GetTextAsync();
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                ViewModel.Url = text.Trim();
                LinkBox.Focus(FocusState.Programmatic);
                LinkBox.SelectionStart = LinkBox.Text.Length;
            }
        }
        catch (Exception)
        {
            // The clipboard can be locked by another app for a moment; pasting again works.
        }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static DownloadItemViewModel? GetItem(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItemViewModel;

    private void PlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item) ViewModel.PlayCommand.Execute(item);
    }

    private void OpenFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item) ViewModel.OpenFileLocationCommand.Execute(item);
    }

    private void RetryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item) ViewModel.RetryCommand.Execute(item);
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item) ViewModel.CancelCommand.Execute(item);
    }

    private void RemoveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item) ViewModel.RemoveCommand.Execute(item);
    }
}
