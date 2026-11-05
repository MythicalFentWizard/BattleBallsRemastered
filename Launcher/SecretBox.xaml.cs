using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Launcher;

public partial class SecretBox : UserControl
{
    /// Guards the two-way sync. Writing Password from inside a control's own change
    /// handler would otherwise bounce straight back and fight the caret.
    bool _syncing;

    public SecretBox()
    {
        InitializeComponent();

        Hidden.PasswordChanged += (_, _) => Push(Hidden.Password);
        Shown.TextChanged += (_, _) => Push(Shown.Text);

        Reveal.Checked += (_, _) => Swap(toPlain: true);
        Reveal.Unchecked += (_, _) => Swap(toPlain: false);

        // The border owns the focus cue, because the inner controls are borderless and
        // swap places - a focus style on either one would blink out on reveal.
        foreach (var c in new Control[] { Hidden, Shown })
        {
            c.GotFocus += (_, _) => Shell.BorderBrush = Brush("Accent");
            c.LostFocus += (_, _) => Shell.BorderBrush = Brush("Border");
        }
        MouseEnter += (_, _) => { if (!HasAnyFocus) Shell.BorderBrush = Brush("BorderLit"); };
        MouseLeave += (_, _) => { if (!HasAnyFocus) Shell.BorderBrush = Brush("Border"); };

        IsEnabledChanged += (_, _) => Shell.Opacity = IsEnabled ? 1.0 : 0.55;
    }

    bool HasAnyFocus => Hidden.IsKeyboardFocusWithin || Shown.IsKeyboardFocusWithin;

    static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(nameof(Password), typeof(string), typeof(SecretBox),
            new FrameworkPropertyMetadata("",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SecretBox)d;
        if (box._syncing) return;
        var text = (string)(e.NewValue ?? "");
        box._syncing = true;
        if (box.Hidden.Password != text) box.Hidden.Password = text;
        if (box.Shown.Text != text) box.Shown.Text = text;
        box._syncing = false;
    }

    void Push(string value)
    {
        if (_syncing) return;
        _syncing = true;
        if (Hidden.Password != value) Hidden.Password = value;
        if (Shown.Text != value) Shown.Text = value;
        Password = value;
        _syncing = false;
    }

    /// Moves focus and the caret across with the swap, so revealing mid-typing does not
    /// dump the user back at the start of the field or somewhere else entirely.
    void Swap(bool toPlain)
    {
        var focused = HasAnyFocus;
        Shown.Visibility = toPlain ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = toPlain ? Visibility.Collapsed : Visibility.Visible;
        Reveal.ToolTip = toPlain ? "Hide password" : "Show password";
        if (!focused) return;
        if (toPlain) { Shown.Focus(); Shown.CaretIndex = Shown.Text.Length; }
        else Hidden.Focus();
    }
}
