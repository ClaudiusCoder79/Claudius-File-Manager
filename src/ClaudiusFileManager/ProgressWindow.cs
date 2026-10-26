using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ClaudiusFileManager;

/// <summary>
/// iOS-style progress card for long jobs (packing, unpacking). The job runs on a background thread;
/// the card only appears if it takes longer than a moment, and has a Cancel button.
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _detail;
    private readonly CancellationTokenSource _cts = new();
    private double _fraction;
    private bool _finished;
    private string _name = "";
    private readonly DispatcherTimer _ui;

    private ProgressWindow(Window owner, string title)
    {
        if (owner is { IsLoaded: true }) Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        Opacity = 0;      // shown only if the job is not finished almost at once
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)Application.Current.FindResource("AppFont");
        SetResourceReference(ForegroundProperty, "LabelBrush");

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 20, 18, 10)
        });
        _bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(22, 4, 22, 8), BorderThickness = new Thickness(0) };
        _bar.SetResourceReference(ForegroundProperty, "AccentBrush");
        _bar.SetResourceReference(BackgroundProperty, "DialogSeparatorBrush");
        stack.Children.Add(_bar);
        _detail = new TextBlock
        {
            FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(18, 0, 18, 0)
        };
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "DialogTextBrush");
        stack.Children.Add(_detail);

        var sep = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 0) };
        sep.SetResourceReference(Border.BackgroundProperty, "DialogSeparatorBrush");
        stack.Children.Add(sep);
        var cancel = new Button
        {
            Content = Loc.T("Cancel"),
            Style = (Style)Application.Current.FindResource("DialogButton")
        };
        cancel.SetResourceReference(ForegroundProperty, "AccentBrush");
        cancel.Click += (_, _) => { _cts.Cancel(); cancel.IsEnabled = false; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _cts.Cancel(); cancel.IsEnabled = false; e.Handled = true; } };
        // The card closes itself when the job has stopped; Alt+F4 only asks the job to stop.
        Closing += (_, e) => { if (!_finished) { e.Cancel = true; _cts.Cancel(); cancel.IsEnabled = false; } };
        stack.Children.Add(cancel);

        var shadow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.3 }
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        var card = new Border { CornerRadius = new CornerRadius(14), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        var root = new Grid { Margin = new Thickness(30), Width = 340 };
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { /* ignore */ } }
        };

        _ui = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ui.Tick += (_, _) =>
        {
            _bar.Value = _fraction;
            _detail.Text = _name;
        };
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a background thread while showing progress.
    /// Returns false if the user cancelled; rethrows any error from the job.
    /// </summary>
    public static bool Run(Window owner, string title, Action<ZipOps.Progress, CancellationToken> work)
    {
        var w = new ProgressWindow(owner, title);
        Exception? error = null;
        bool cancelled = false;
        w.Loaded += async (_, _) =>
        {
            w._ui.Start();
            var reveal = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            reveal.Tick += (_, _) => { reveal.Stop(); w.Opacity = 1; };
            reveal.Start();
            try
            {
                await Task.Run(() => work((f, n) => { w._fraction = f; if (n.Length > 0) w._name = n; }, w._cts.Token));
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { error = ex; }
            reveal.Stop();
            w._ui.Stop();
            w._finished = true;
            w.DialogResult = true;
        };
        Mouse.OverrideCursor = Cursors.AppStarting;
        try { w.ShowDialog(); }
        finally { Mouse.OverrideCursor = null; }
        if (error != null) throw error;
        return !cancelled;
    }
}
