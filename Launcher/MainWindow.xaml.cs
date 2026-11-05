using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Launcher;

public partial class MainWindow : Window
{
    readonly LauncherViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // The client box is what a player downloads, so it is just "BattleBit Revival";
        // the full box is the host-and-play tool and says so.
        Title = App.ClientOnly ? "BattleBit Revival" : "BattleBit Revival Launcher";
        Subtitle.Text = App.ClientOnly
            ? "Join a cluster and play"
            : "Host a cluster, or join one";

        _vm.LogAppended += () => LogView.ScrollToEnd();
    }

    /// Windows paints the title bar from the app's theme preference, not from the
    /// window's own background, so a dark window keeps a light caption unless it asks.
    /// Failure here is cosmetic, which is why nothing checks the result.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
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
        if (_vm.SendCommandCommand.CanExecute(null)) _vm.SendCommandCommand.Execute(null);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _vm.StopAll();
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
