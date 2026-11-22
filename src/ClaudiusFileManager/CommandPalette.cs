using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudiusFileManager;

/// <summary>One entry of the command palette.</summary>
public sealed record PaletteCommand(string Id, string Label, string Shortcut, string Glyph)
{
    /// <summary>Text the search looks in: the translated name, the English name and the command id.</summary>
    public string Haystack { get; init; } = "";
}

/// <summary>
/// Command palette (Ctrl+Shift+P): type part of a command's name, press Enter to run it.
/// Every command of the app is listed with its shortcut.
/// </summary>
public sealed class CommandPalette : Window
{
    private readonly TextBox _box;
    private readonly ListBox _list;
    private readonly List<PaletteCommand> _all;
    public string? Chosen { get; private set; }
    private bool _closing;

    private const string RowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid Height="32">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="30"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Glyph}" FontFamily="{StaticResource IconFont}" FontSize="15"
                           VerticalAlignment="Center" Foreground="{DynamicResource AccentBrush}"/>
                <TextBlock Grid.Column="1" Text="{Binding Label}" FontSize="13.5" VerticalAlignment="Center"
                           TextTrimming="CharacterEllipsis"/>
                <Border Grid.Column="2" Background="{DynamicResource FillBrush}" CornerRadius="5" Padding="7,1"
                        VerticalAlignment="Center" Margin="8,0,4,0">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Shortcut}" Value="">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Border.Style>
                    <TextBlock Text="{Binding Shortcut}" FontSize="11.5" Foreground="{DynamicResource SecondaryLabelBrush}"/>
                </Border>
            </Grid>
        </DataTemplate>
        """;

    private CommandPalette(Window owner, List<PaletteCommand> commands)
    {
        _all = commands;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 600;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = (FontFamily)Application.Current.FindResource("AppFont");
        SetResourceReference(ForegroundProperty, "LabelBrush");

        _box = new TextBox
        {
            Style = (Style)Application.Current.FindResource("IosTextBox"),
            FontSize = 15,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(12, 12, 12, 8)
        };
        _box.SetResourceReference(TagProperty, "L.PaletteHint");
        _box.TextChanged += (_, _) => Filter();
        _box.PreviewKeyDown += Box_PreviewKeyDown;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 420,
            Margin = new Thickness(0, 0, 0, 10),
            ItemContainerStyle = (Style)Application.Current.FindResource("FileRow"),
            ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate),
            Focusable = false
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(_list, d) is ListBoxItem) Run();
        };

        var stack = new StackPanel();
        stack.Children.Add(_box);
        stack.Children.Add(_list);

        var shadow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.3 }
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        var card = new Border { CornerRadius = new CornerRadius(14), Child = stack, BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SeparatorBrush");
        var root = new Grid { Margin = new Thickness(30) };
        root.Children.Add(shadow);
        root.Children.Add(card);
        Content = root;

        Filter();
        Loaded += (_, _) =>
        {
            // Top centre of the main window, like a search field dropping down.
            var ownerLeft = owner.WindowState == WindowState.Maximized ? 0 : owner.Left;
            var ownerTop = owner.WindowState == WindowState.Maximized ? 0 : owner.Top;
            Left = ownerLeft + (owner.ActualWidth - ActualWidth) / 2;
            Top = ownerTop + 60;
            _box.Focus();
        };
        Closing += (_, _) => _closing = true;
        // Clicking elsewhere dismisses the palette.
        Deactivated += (_, _) => { if (!_closing) Close(); };
    }

    /// <summary>Shows the palette and returns the id of the chosen command (or null).</summary>
    public static string? Pick(Window owner, List<PaletteCommand> commands)
    {
        var w = new CommandPalette(owner, commands);
        w.ShowDialog();
        return w.Chosen;
    }

    private void Filter()
    {
        var words = _box.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<PaletteCommand> hits = _all;
        if (words.Length > 0)
        {
            hits = _all.Where(c => words.All(w => c.Haystack.Contains(w, StringComparison.OrdinalIgnoreCase)))
                       .OrderBy(c => c.Label.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0
                                   : c.Label.Contains(" " + words[0], StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        }
        var list = hits.ToList();
        _list.ItemsSource = list;
        if (list.Count > 0)
        {
            _list.SelectedIndex = 0;
            _list.ScrollIntoView(list[0]);
        }
    }

    private void Box_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int n = _list.Items.Count;
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter: Run(); e.Handled = true; break;
            case Key.Down when n > 0: Move(1); e.Handled = true; break;
            case Key.Up when n > 0: Move(-1); e.Handled = true; break;
            case Key.PageDown when n > 0: Move(8); e.Handled = true; break;
            case Key.PageUp when n > 0: Move(-8); e.Handled = true; break;
        }
    }

    private void Move(int step)
    {
        int n = _list.Items.Count;
        int i = Math.Clamp((_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex) + step, 0, n - 1);
        _list.SelectedIndex = i;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Run()
    {
        if (_closing || _list.SelectedItem is not PaletteCommand c) return;
        Chosen = c.Id;
        Close();
    }
}
