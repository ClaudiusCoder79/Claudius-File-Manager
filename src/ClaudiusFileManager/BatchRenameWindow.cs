using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudiusFileManager;

/// <summary>
/// Batch rename (Ctrl+M), like Total Commander's Multi-Rename tool: a name pattern with placeholders,
/// find &amp; replace, upper/lower case and a counter, with a live before → after list.
/// </summary>
public sealed class BatchRenameWindow : Window
{
    public sealed record Row(string Old, string New, string Folder, bool Changed, string Problem)
    {
        public bool HasProblem => Problem.Length > 0;
        public string Arrow => "→";
    }

    private readonly List<FileItem> _items;
    private readonly TextBox _name, _ext, _find, _replace, _start, _step, _digits;
    private readonly CheckBox _regex;
    private readonly ComboBox _case;
    private readonly ListBox _preview;
    private readonly TextBlock _summary;
    private readonly Button _ok;
    private List<Row> _rows = new();

    /// <summary>How many items were renamed (0 if cancelled).</summary>
    public int Renamed { get; private set; }

    private const string RowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Grid Height="26" ToolTip="{Binding Folder}">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="28"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Old}" FontSize="12.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"
                           Foreground="{DynamicResource SecondaryLabelBrush}"/>
                <TextBlock Grid.Column="1" Text="{Binding Arrow}" HorizontalAlignment="Center" VerticalAlignment="Center"
                           Foreground="{DynamicResource SecondaryLabelBrush}"/>
                <TextBlock Grid.Column="2" FontSize="12.5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Text" Value="{Binding New}"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Changed}" Value="True">
                                    <Setter Property="FontWeight" Value="SemiBold"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding HasProblem}" Value="True">
                                    <Setter Property="Foreground" Value="{DynamicResource RedBrush}"/>
                                    <Setter Property="ToolTip" Value="{Binding Problem}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Grid>
        </DataTemplate>
        """;

    private BatchRenameWindow(Window owner, List<FileItem> items)
    {
        _items = items;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Width = 820;
        Height = 640;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = (FontFamily)Application.Current.FindResource("AppFont");
        SetResourceReference(ForegroundProperty, "LabelBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // fields
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // preview
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // summary
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        var title = new TextBlock
        {
            Text = Loc.F("BatchRenameTitle", items.Count), FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(22, 18, 22, 10)
        };
        root.Children.Add(title);

        // ── fields ──
        var fields = new Grid { Margin = new Thickness(22, 0, 22, 8) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(fields, 1);
        root.Children.Add(fields);

        TextBox Field(string text, int row, int col)
        {
            var tb = new TextBox
            {
                Text = text, Style = (Style)Application.Current.FindResource("IosTextBox"),
                Margin = new Thickness(0, 4, 0, 4)
            };
            tb.TextChanged += (_, _) => Update();
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            fields.Children.Add(tb);
            return tb;
        }
        void Label(string key, int row, int col)
        {
            var l = new TextBlock { Text = Loc.T(key), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "DialogTextBrush");
            Grid.SetRow(l, row);
            Grid.SetColumn(l, col);
            fields.Children.Add(l);
        }

        Label("BrName", 0, 0);
        _name = Field("[N]", 0, 1);
        Label("BrExt", 0, 3);
        _ext = Field("[E]", 0, 4);

        // Buttons that insert placeholders into the name pattern.
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (code, key) in new[] { ("[N]", "BrPhName"), ("[C]", "BrPhCounter"), ("[D]", "BrPhDate"),
                                            ("[T]", "BrPhTime"), ("[P]", "BrPhFolder"), ("[E]", "BrPhExt") })
        {
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("DriveChip"),
                Content = new TextBlock { Text = $"{code}  {Loc.T(key)}", FontSize = 11.5 },
                ToolTip = Loc.T("BrInsertTip")
            };
            b.Click += (_, _) =>
            {
                int at = _name.CaretIndex;
                _name.Text = _name.Text.Insert(at, code);
                _name.Focus();
                _name.CaretIndex = at + code.Length;
            };
            chips.Children.Add(b);
        }
        Grid.SetRow(chips, 1);
        Grid.SetColumn(chips, 1);
        Grid.SetColumnSpan(chips, 4);
        fields.Children.Add(chips);

        Label("BrFind", 2, 0);
        _find = Field("", 2, 1);
        Label("BrReplace", 2, 3);
        _replace = Field("", 2, 4);

        // Case, regex and counter settings on one line.
        var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        _case = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in new[] { "BrCaseKeep", "BrCaseLower", "BrCaseUpper", "BrCaseTitle" }) _case.Items.Add(Loc.T(key));
        _case.SelectedIndex = 0;
        _case.SelectionChanged += (_, _) => Update();
        _regex = new CheckBox { Content = Loc.T("BrRegex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22, 0) };
        _regex.SetResourceReference(ForegroundProperty, "LabelBrush");
        _regex.Checked += (_, _) => Update();
        _regex.Unchecked += (_, _) => Update();

        TextBox Small(string text)
        {
            var tb = new TextBox { Text = text, Width = 52, Style = (Style)Application.Current.FindResource("IosTextBox"), Margin = new Thickness(6, 0, 14, 0) };
            tb.TextChanged += (_, _) => Update();
            return tb;
        }
        TextBlock Caption(string key)
        {
            var t = new TextBlock { Text = Loc.T(key), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "DialogTextBrush");
            return t;
        }
        line.Children.Add(_case);
        line.Children.Add(_regex);
        line.Children.Add(Caption("BrCounterStart"));
        line.Children.Add(_start = Small("1"));
        line.Children.Add(Caption("BrCounterStep"));
        line.Children.Add(_step = Small("1"));
        line.Children.Add(Caption("BrCounterDigits"));
        line.Children.Add(_digits = Small("1"));
        Grid.SetRow(line, 3);
        Grid.SetColumn(line, 0);
        Grid.SetColumnSpan(line, 5);
        fields.Children.Add(line);

        // ── preview ──
        var listCard = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(22, 6, 22, 6), Padding = new Thickness(0, 6, 0, 6) };
        listCard.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        _preview = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)Application.Current.FindResource("FileRow"),
            ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate),
            Focusable = false
        };
        VirtualizingPanel.SetIsVirtualizing(_preview, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_preview, ScrollBarVisibility.Disabled);
        listCard.Child = _preview;
        Grid.SetRow(listCard, 2);
        root.Children.Add(listCard);

        _summary = new TextBlock { FontSize = 12.5, Margin = new Thickness(24, 2, 24, 10), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_summary, 3);
        root.Children.Add(_summary);

        // ── buttons ──
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var sep = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
        sep.SetResourceReference(Border.BackgroundProperty, "DialogSeparatorBrush");
        Grid.SetColumnSpan(sep, 2);
        buttons.Children.Add(sep);
        var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        cancel.SetResourceReference(ForegroundProperty, "AccentBrush");
        _ok = new Button { Content = Loc.T("Rename"), Style = (Style)Application.Current.FindResource("DialogButton"), IsDefault = true, FontWeight = FontWeights.SemiBold };
        _ok.SetResourceReference(ForegroundProperty, "AccentBrush");
        _ok.Click += (_, _) => Apply();
        Grid.SetColumn(_ok, 1);
        var vsep = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Right };
        vsep.SetResourceReference(Border.BackgroundProperty, "DialogSeparatorBrush");
        buttons.Children.Add(cancel);
        buttons.Children.Add(vsep);
        buttons.Children.Add(_ok);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        var shadow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 270, Opacity = 0.3 }
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        var card = new Border { CornerRadius = new CornerRadius(14), Child = root };
        card.SetResourceReference(Border.BackgroundProperty, "DialogBrush");
        var outer = new Grid { Margin = new Thickness(24) };
        outer.Children.Add(shadow);
        outer.Children.Add(card);
        Content = outer;

        // Drag the dialog by its title.
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { /* ignore */ } };

        Update();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    /// <summary>Shows the dialog; returns how many items were renamed.</summary>
    public static int Run(Window owner, List<FileItem> items)
    {
        var w = new BatchRenameWindow(owner, items);
        w.ShowDialog();
        return w.Renamed;
    }

    // ───────────────────────── naming ─────────────────────────

    private static readonly Regex Placeholder = new(@"\[(N|E|C|D|T|P)(?:(\d+)(?:-(\d*))?)?\]", RegexOptions.IgnoreCase);

    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out var v) ? v : fallback;

    private string NewName(FileItem it, int index, Regex? findRx)
    {
        string baseName = it.IsDir ? it.Name : Path.GetFileNameWithoutExtension(it.Name);
        string ext = it.IsDir ? "" : Path.GetExtension(it.Name).TrimStart('.');
        long counter = ParseInt(_start.Text, 1) + (long)index * ParseInt(_step.Text, 1);
        int digits = Math.Clamp(ParseInt(_digits.Text, 1), 1, 12);
        string folder = Path.GetFileName(Path.GetDirectoryName(it.FullPath) ?? "") ?? "";

        string Expand(string pattern) => Placeholder.Replace(pattern, m =>
        {
            string value = char.ToUpperInvariant(m.Groups[1].Value[0]) switch
            {
                'N' => baseName,
                'E' => ext,
                'C' => counter < 0 ? "-" + (-counter).ToString().PadLeft(digits, '0') : counter.ToString().PadLeft(digits, '0'),
                'D' => it.Modified.ToString("yyyy-MM-dd"),
                'T' => it.Modified.ToString("HHmmss"),
                'P' => folder,
                _ => m.Value
            };
            // [N2-5] = characters 2 to 5, [N3-] = from character 3, [N4] = just character 4.
            if (m.Groups[2].Success && "NEP".Contains(char.ToUpperInvariant(m.Groups[1].Value[0])))
            {
                int from = Math.Max(1, int.Parse(m.Groups[2].Value));
                int to = !m.Groups[3].Success ? from : m.Groups[3].Value.Length == 0 ? value.Length : int.Parse(m.Groups[3].Value);
                if (from > value.Length) return "";
                to = Math.Min(to, value.Length);
                value = to < from ? "" : value.Substring(from - 1, to - from + 1);
            }
            return value;
        });

        string name = Expand(_name.Text);
        string newExt = it.IsDir ? "" : Expand(_ext.Text);
        string full = newExt.Length > 0 ? name + "." + newExt : name;

        if (_find.Text.Length > 0)
        {
            full = findRx != null
                ? findRx.Replace(full, _replace.Text)
                : full.Replace(_find.Text, _replace.Text, StringComparison.OrdinalIgnoreCase);
        }

        full = _case.SelectedIndex switch
        {
            1 => full.ToLowerInvariant(),
            2 => full.ToUpperInvariant(),
            3 => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(full.ToLowerInvariant()),
            _ => full
        };
        return full.Trim();
    }

    private void Update()
    {
        if (_preview == null || _ok == null || _digits == null) return;   // still building the window
        Regex? rx = null;
        string rxError = "";
        if (_regex.IsChecked == true && _find.Text.Length > 0)
        {
            try { rx = new Regex(_find.Text, RegexOptions.IgnoreCase); }
            catch (ArgumentException ex) { rxError = ex.Message; }
        }

        var invalid = Path.GetInvalidFileNameChars();
        var rows = new List<Row>();
        // Names that will exist in each folder afterwards, to catch duplicates.
        var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var renaming = _items.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var dir = Path.GetDirectoryName(it.FullPath) ?? "";
            string newName = rxError.Length > 0 ? it.Name : NewName(it, i, rx);
            string problem = "";
            if (rxError.Length > 0) problem = rxError;
            else if (newName.Length == 0 || newName is "." or "..") problem = Loc.T("BrErrEmpty");
            else if (newName.IndexOfAny(invalid) >= 0) problem = Loc.T("BrErrChars");
            else
            {
                if (!taken.TryGetValue(dir, out var set)) taken[dir] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!set.Add(newName)) problem = Loc.T("BrErrDuplicate");
                else if (!string.Equals(newName, it.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var target = Path.Combine(dir, newName);
                    if ((File.Exists(target) || Directory.Exists(target)) && !renaming.Contains(target))
                        problem = Loc.T("BrErrExists");
                }
            }
            rows.Add(new Row(it.Name, newName, dir, !string.Equals(newName, it.Name, StringComparison.Ordinal), problem));
        }
        _rows = rows;
        _preview.ItemsSource = rows;

        int changed = rows.Count(r => r.Changed && !r.HasProblem);
        int problems = rows.Count(r => r.HasProblem);
        _summary.Text = problems > 0 ? Loc.F("BrSummaryProblems", problems) : Loc.F("BrSummary", changed, rows.Count);
        _summary.SetResourceReference(TextBlock.ForegroundProperty, problems > 0 ? "RedBrush" : "DialogTextBrush");
        _ok.IsEnabled = problems == 0 && changed > 0;
        _ok.Opacity = _ok.IsEnabled ? 1 : 0.4;
    }

    // ───────────────────────── renaming ─────────────────────────

    /// <summary>
    /// Renames in two steps (everything to a temporary name first, then to the final name),
    /// so swaps like a→b, b→a and case-only changes work. On an error, finished renames are kept
    /// and the rest are put back.
    /// </summary>
    private void Apply()
    {
        Update();
        if (!_ok.IsEnabled) return;
        var jobs = _items.Zip(_rows).Where(p => p.Second.Changed).Select(p => (Item: p.First, NewName: p.Second.New)).ToList();
        var temps = new List<(string Temp, string Original, string Final, bool IsDir)>();
        string tag = ".~cfm" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            foreach (var (item, newName) in jobs)
            {
                var dir = Path.GetDirectoryName(item.FullPath)!;
                var temp = item.FullPath + tag;
                MoveItem(item.FullPath, temp, item.IsDir);
                temps.Add((temp, item.FullPath, Path.Combine(dir, newName), item.IsDir));
            }
            for (int i = 0; i < temps.Count; i++)
            {
                MoveItem(temps[i].Temp, temps[i].Final, temps[i].IsDir);
                Renamed++;
                temps[i] = temps[i] with { Temp = "" };
            }
            DialogResult = true;
        }
        catch (Exception ex)
        {
            // Put back anything still waiting under a temporary name.
            foreach (var t in temps.Where(t => t.Temp.Length > 0))
                try { MoveItem(t.Temp, t.Original, t.IsDir); } catch { /* leave it; the name shows what happened */ }
            IosDialog.Alert(this, Loc.T("ErrOpFailed"), ex.Message);
            if (Renamed > 0) DialogResult = true;
        }
    }

    private static void MoveItem(string from, string to, bool isDir)
    {
        if (isDir) Directory.Move(from, to); else File.Move(from, to);
    }
}
