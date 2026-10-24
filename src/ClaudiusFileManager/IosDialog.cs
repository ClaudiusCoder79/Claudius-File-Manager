using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudiusFileManager;

/// <summary>An iOS-style alert (UIAlertController look-alike), optionally with a text field.</summary>
public sealed class IosDialog : Window
{
    private readonly TextBox? _input;

    private IosDialog(Window? owner, string title, string message, string? initial, string okText,
                      bool destructive, bool showCancel, int selectLength, bool wide)
    {
        if (owner is { IsLoaded: true }) Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)Application.Current.FindResource("AppFont");
        SetResourceReference(ForegroundProperty, "LabelBrush");

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 20, 18, 6)
        });
        if (!string.IsNullOrEmpty(message))
        {
            var msg = new TextBlock
            {
                Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                TextAlignment = wide ? TextAlignment.Left : TextAlignment.Center,
                LineHeight = wide ? 20 : double.NaN,
                Margin = new Thickness(wide ? 24 : 18, 0, wide ? 24 : 18, 0)
            };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "DialogTextBrush");
            stack.Children.Add(msg);
        }

        if (initial != null)
        {
            _input = new TextBox
            {
                Text = initial,
                Style = (Style)Application.Current.FindResource("IosTextBox"),
                Margin = new Thickness(16, 12, 16, 0)
            };
            _input.SetResourceReference(BackgroundProperty, "FieldFocusBrush");
            stack.Children.Add(_input);
            Loaded += (_, _) =>
            {
                _input.Focus();
                if (selectLength >= 0) _input.Select(0, selectLength); else _input.SelectAll();
            };
        }

        var sep = new Border { Height = 1, Margin = new Thickness(0, 18, 0, 0) };
        sep.SetResourceReference(Border.BackgroundProperty, "DialogSeparatorBrush");
        stack.Children.Add(sep);

        var buttons = new Grid();
        var ok = MakeButton(okText, destructive ? "RedBrush" : "AccentBrush", bold: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => DialogResult = true;
        if (showCancel)
        {
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            var cancel = MakeButton(Loc.T("Cancel"), "AccentBrush", bold: false);
            cancel.IsCancel = true;
            buttons.Children.Add(cancel);
            var vsep = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right };
            vsep.SetResourceReference(Border.BackgroundProperty, "DialogSeparatorBrush");
            buttons.Children.Add(vsep);
            Grid.SetColumn(ok, 1);
        }
        else
        {
            ok.IsCancel = true;
        }
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        var shadow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.3 }
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        var card = new Border { CornerRadius = new CornerRadius(14), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "DialogBrush");

        var root = new Grid { Margin = new Thickness(30), Width = wide ? 460 : initial != null ? 330 : 300 };
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { /* ignore */ } }
        };
    }

    private static Button MakeButton(string text, string brushKey, bool bold)
    {
        var b = new Button
        {
            Content = text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Style = (Style)Application.Current.FindResource("DialogButton")
        };
        b.SetResourceReference(ForegroundProperty, brushKey);
        return b;
    }

    public static bool Confirm(Window? owner, string title, string message, string okText, bool destructive = false) =>
        new IosDialog(owner, title, message, null, okText, destructive, true, -1, false).ShowDialog() == true;

    public static string? Prompt(Window? owner, string title, string message, string initial, string okText,
                                 int selectLength = -1)
    {
        var d = new IosDialog(owner, title, message, initial, okText, false, true, selectLength, false);
        return d.ShowDialog() == true ? d._input!.Text : null;
    }

    public static void Alert(Window? owner, string title, string message) =>
        new IosDialog(owner, title, message, null, Loc.T("OK"), false, false, -1, false).ShowDialog();

    /// <summary>Wider alert with left-aligned text, for longer content (shortcuts, licence).</summary>
    public static void Info(Window? owner, string title, string message) =>
        new IosDialog(owner, title, message, null, Loc.T("OK"), false, false, -1, true).ShowDialog();
}
