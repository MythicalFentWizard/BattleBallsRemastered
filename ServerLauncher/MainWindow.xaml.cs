using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ServerLauncher;

public partial class MainWindow : Window
{
    readonly ServerViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Appended += () => LogView.ScrollToEnd();
    }

    /// Windows paints the title bar from the app's theme preference rather than the
    /// window background, so a dark window keeps a light caption unless it asks.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                                  DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        }
        catch { }
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window must not orphan the game server the child process started,
        // so the server is shut down the same way the Stop button does it.
        if (_vm.IsRunning)
        {
            var answer = MessageBox.Show(
                "The server is running. Stop it and close?",
                "BattleBit Revival Server", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) { e.Cancel = true; return; }
            _vm.Stop();
        }
        base.OnClosing(e);
    }
}

/// Maps a palette key from the view model onto the brush of that name, so the view
/// model can say "Ok" or "Warn" without holding a reference to a Brush.
public sealed class ResourceKeyToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
