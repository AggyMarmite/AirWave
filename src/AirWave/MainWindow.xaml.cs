using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirWave.Core;

namespace AirWave;

public partial class MainWindow : Window
{
    private readonly AppController _app;
    private bool _hintShown;

    public MainWindow(AppController app)
    {
        InitializeComponent();
        _app = app;
        DataContext = app;
    }

    // ------------------------------------------------------------------ title bar

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (_app.Settings.CloseToTray && !_quitting)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown && _app.IsStreaming)
            {
                _hintShown = true;
                app.Tray?.ShowHint("AirWave is still playing", "It's in the notification area by the clock. Right-click it to quit.");
            }
            return;
        }
        base.OnClosing(e);
        if (!_quitting) app.Quit();
    }

    private bool _quitting;

    /// <summary>The app is quitting: let the window close instead of hiding.</summary>
    public void MarkQuitting() => _quitting = true;

    // ------------------------------------------------------------------ main page

    private async void StopAll_Click(object sender, RoutedEventArgs e) => await _app.StopAllAsync();

    private void Rescan_Click(object sender, RoutedEventArgs e) => _app.Rescan();

    private void Devices_DropDownOpened(object sender, EventArgs e)
    {
        // Pick up headphones etc. plugged in since the list was made, keeping the current choice
        var keep = _app.SelectedDevice;
        _app.RefreshDevices();
        if (keep != null)
        {
            var match = _app.Devices.FirstOrDefault(d => d.Id == keep.Id);
            if (match != null) _app.SelectedDevice = match;
        }
    }

    private void RemoveSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is SpeakerViewModel vm)
        {
            if (vm.Info.Txt.ContainsKey("__manual")) _app.RemoveManualSpeaker(vm);
            else vm.IsOn = false;
        }
    }

    // ------------------------------------------------------------------ add by IP

    private void AddByIp_Click(object sender, RoutedEventArgs e)
    {
        AddError.Visibility = Visibility.Collapsed;
        AddPanel.Visibility = Visibility.Visible;
        AddIp.Focus();
        AddIp.CaretIndex = AddIp.Text.Length;
    }

    private void AddCancel_Click(object sender, RoutedEventArgs e) => AddPanel.Visibility = Visibility.Collapsed;

    private void AddPanelBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => AddPanel.Visibility = Visibility.Collapsed;

    private void StopBubble(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void AddConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.AddManualSpeaker(AddIp.Text, 7000, AddName.Text))
        {
            AddError.Visibility = Visibility.Visible;
            return;
        }
        AddPanel.Visibility = Visibility.Collapsed;
        AddName.Text = "";
    }

    // ------------------------------------------------------------------ settings

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(true);

    private void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowSettings(false);

    public void ShowSettings(bool show)
    {
        SettingsPage.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MainPage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        CopyDone.Visibility = Visibility.Collapsed;
    }

    public void ShowAddPanel(bool show) => AddPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = Log.Snapshot();
        var text = $"AirWave {_app.Version} on {Environment.OSVersion}\r\n" + string.Join("\r\n", lines.TakeLast(400));
        try
        {
            Clipboard.SetText(text);
            CopyDone.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Folder}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void DisconnectReceiver_Click(object sender, RoutedEventArgs e) => _app.DisconnectReceiver();

    private void ReceiverName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            ReceiverNameBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    private async void MirrorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ScreenViewModel vm) await _app.ToggleMirrorAsync(vm);
    }

    private void PromptOk_Click(object sender, RoutedEventArgs e)
    {
        var value = _app.PromptIsPassword ? PromptPassword.Password : PromptText.Text;
        PromptText.Text = ""; PromptPassword.Password = "";
        _app.SubmitPrompt(value);
    }

    private void PromptCancel_Click(object sender, RoutedEventArgs e)
    {
        PromptText.Text = ""; PromptPassword.Password = "";
        _app.CancelPrompt();
    }

    private void Prompt_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) PromptOk_Click(sender, e);
        if (e.Key == System.Windows.Input.Key.Escape) PromptCancel_Click(sender, e);
    }

    private void ForgetTvs_Click(object sender, RoutedEventArgs e)
    {
        _app.ForgetTvPairings();
        ForgetDone.Visibility = Visibility.Visible;
    }

    internal void ScrollSpeakersToEnd() => SpeakerScroll.ScrollToEnd();

    private void PromptPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => { if (_app.PromptIsPassword) PromptPassword.Focus(); else PromptText.Focus(); },
                System.Windows.Threading.DispatcherPriority.Input);
    }
}
