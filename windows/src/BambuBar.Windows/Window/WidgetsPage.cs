using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Imaging;
using BambuBar.Core.Model;
using BambuBar.Core.Widgets;
using BambuBar.Theme;
using BambuBar.TrayPanel;
using BambuBar.Widgets;

namespace BambuBar.AppWindow;

/// <summary>
/// The desktop widgets (the Mac's WidgetsPage, P3.8): a showcase that plays a print through every state in each size,
/// drawn from the same <see cref="WidgetState"/> and printer art as the real widgets (dressed as the current printer),
/// what each size carries, then Add Widget for a size and printer, and the widgets already on the desktop with their
/// printer, size and Remove.
/// </summary>
internal sealed class WidgetsPage(WindowContext ctx) : Page(ctx)
{
    private WidgetHost? Host => Ctx.Services.Widgets;

    public override FrameworkElement Build()
    {
        _showcase = new Showcase(Ctx);
        var page = new Stack
        {
            Spacing = 22,
            Children =
            {
                W.PageHeader(Loc.T("Widgets"), Loc.T("Your printers on the desktop, in three sizes.")),
                _showcase.Root,
                AddSection(),
            },
        };
        if (Host is { } host) host.Changed += OnWidgetsChanged;
        ReadPlaced();
        return page;
    }

    public override void Refresh()
    {
        _showcase?.Refresh();
        var printers = Store.Sessions.Select(s => (s.Id, s.Config.Name)).ToList();
        var printersKey = string.Join("#", printers);
        if (printersKey != _printersKey)
        {
            _printersKey = printersKey;
            FillAddControls();
        }
        _placedRegion.Show(string.Join("#", _placed.Select(w => $"{w.Config}|{w.Size}|{w.Serial}")) + "|" + printersKey + "|" + (Host is null),
            PlacedList);
        _steps.Show(Store.HasMultiplePrinters.ToString(), Steps);
    }

    public override void Dispose()
    {
        if (Host is { } host) host.Changed -= OnWidgetsChanged;
        _showcase?.Dispose();
    }

    private Showcase? _showcase;
    private List<WidgetHost.Widget> _placed = [];
    private string _printersKey = "-";
    private readonly Region _placedRegion = new(), _steps = new();
    private WidgetHost.Size _addSize = WidgetHost.Size.Medium;
    private string? _addSerial;
    private Dropdown _sizePick = null!, _printerPick = null!;
    private ChromeButton _add = null!;

    private void OnWidgetsChanged()
    {
        ReadPlaced();
        Refresh();
    }

    private void ReadPlaced()
    {
        try { _placed = Host?.Widgets.ToList() ?? []; }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { _placed = []; }
    }

    private string PrinterTitle(string? serial) =>
        serial is null ? Loc.T("Printer in the tray") : Store.Session(serial)?.Config.Name ?? serial;

    // MARK: Add a widget

    private FrameworkElement AddSection()
    {
        var group = W.Group(Loc.T("Add a widget"), AppPage.Widgets.Glyph(), out var body);
        _sizePick = new Dropdown(Loc.T("Size"), Ctx.Theme, () => Enum.GetValues<WidgetHost.Size>()
            .Select(size => new MenuEntry(WidgetMenu.Title(size), () => { _addSize = size; FillAddControls(); }, size == _addSize)).ToList(), 150);
        _printerPick = new Dropdown(Loc.T("Printer"), Ctx.Theme, () => PrinterEntries(_addSerial, serial => { _addSerial = serial; FillAddControls(); }), 240);
        _add = W.Button(Loc.T("Add Widget"), AddWidget, accent: true, glyph: "");
        _add.IsEnabled = Host is not null;
        if (Host is null) Ui.Tip(_add, Loc.T("Widgets aren't available in this run of Bambu Bar."));
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var part in new FrameworkElement[] { Labeled(Loc.T("Size"), _sizePick), Labeled(Loc.T("Printer"), _printerPick), _add })
        {
            part.Margin = new Thickness(0, 0, 16, 6);
            part.VerticalAlignment = VerticalAlignment.Bottom;
            row.Children.Add(part);
        }
        body.Children.Add(row);
        body.Children.Add(_steps.Host);
        body.Children.Add(W.Divider());
        body.Children.Add(Ui.SectionLabel(Loc.T("On your desktop"), null));
        body.Children.Add(_placedRegion.Host);
        FillAddControls();
        return group;
    }

    private static Stack Labeled(string title, FrameworkElement control) =>
        new() { Spacing = 6, Children = { Ui.Text(title, WRamp.Note, null, ThemeKeys.TextSecondary), control } };

    private void FillAddControls()
    {
        if (_addSerial is not null && Store.Session(_addSerial) is null) _addSerial = null;
        _sizePick.Text = WidgetMenu.Title(_addSize);
        _printerPick.Text = PrinterTitle(_addSerial);
    }

    private List<MenuEntry> PrinterEntries(string? current, Action<string?> pick)
    {
        var entries = new List<MenuEntry> { new(Loc.T("Printer in the tray"), () => pick(null), current is null) };
        if (Store.Sessions.Count > 0) entries.Add(MenuEntry.Separator);
        entries.AddRange(Store.Sessions.Select(s => new MenuEntry(s.Config.Name, () => pick(s.Id), s.Id == current)));
        return entries;
    }

    private void AddWidget()
    {
        if (Host is not { } host) return;
        try
        {
            host.Add(_addSize, _addSerial);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Store.ShowToast(e.Message, error: true);
        }
    }

    /// <summary>The Windows way to get one onto the desktop and change it later.</summary>
    private FrameworkElement Steps()
    {
        var list = new Stack { Spacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        list.Children.Add(Step(1, "", Loc.T("Choose a size and a printer, then click **Add Widget**. It appears at the top right of your screen.")));
        list.Children.Add(Step(2, "", Loc.T("Drag it wherever you like. It stays there, and comes back each time Bambu Bar starts.")));
        list.Children.Add(Step(3, "", Loc.T("Right-click a widget to change its printer or size, keep it above other windows, or remove it.")));
        list.Children.Add(Step(4, Icons.Printer, Store.HasMultiplePrinters
            ? Loc.T("A widget set to **Printer in the tray** follows the printer the tray icon shows. Add one widget per printer to watch them side by side.")
            : Loc.T("Add more printers later and each widget can show a different one: right-click it and choose **Printer**.")));
        return list;
    }

    private static FrameworkElement Step(int n, string glyph, string text)
    {
        var grid = Ui.Columns("Auto", "Auto", "*");
        var number = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Palette.Brush(Palette.WithAlpha(Palette.Accent, 0.15)),
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        var digit = Ui.Text(n.ToString(System.Globalization.CultureInfo.CurrentCulture), 12, FontWeights.Bold);
        digit.HorizontalAlignment = HorizontalAlignment.Center;
        digit.Foreground = Palette.Brush(Palette.AccentDeep);
        number.Child = digit;
        grid.Children.Add(Ui.At(number, 0));
        var icon = Icons.Glyph(glyph, 14, ThemeKeys.TextSecondary);
        icon.Width = 18;
        icon.Margin = new Thickness(0, 3, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(Ui.At(icon, 1));
        var label = W.Note(text, ThemeKeys.TextPrimary, WRamp.Detail);
        label.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(Ui.At(label, 2));
        return Ui.ReadAsOne(grid, $"{n}. {text.Replace("**", "", StringComparison.Ordinal)}");
    }

    /// <summary>The widgets on the desktop: size and printer, each changeable, and Remove.</summary>
    private UIElement PlacedList()
    {
        if (Host is not { } host || _placed.Count == 0)
            return W.Note(Loc.T("No widgets on your desktop yet."), ThemeKeys.TextSecondary, WRamp.Detail);
        var list = new Stack { Spacing = 8 };
        foreach (var widget in _placed)
        {
            var grid = Ui.Columns("Auto", "*", "Auto", "Auto", "Auto");
            var icon = Icons.Glyph(AppPage.Widgets.Glyph(), 16, ThemeKeys.TextSecondary);
            icon.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(Ui.At(icon, 0));
            var name = $"{WidgetMenu.Title(widget.Size)} · {PrinterTitle(widget.Serial)}";
            grid.Children.Add(Ui.At(Ui.Text(name, WRamp.Body), 1));
            var printer = new Dropdown(Loc.T("Printer"), Ctx.Theme, () => PrinterEntries(widget.Serial, serial => host.Change(widget, widget.Size, serial)), 200)
            {
                Text = PrinterTitle(widget.Serial), Margin = new Thickness(12, 0, 0, 0),
            };
            var size = new Dropdown(Loc.T("Size"), Ctx.Theme, () => Enum.GetValues<WidgetHost.Size>()
                .Select(s => new MenuEntry(WidgetMenu.Title(s), () => host.Change(widget, s, widget.Serial), s == widget.Size)).ToList(), 120)
            {
                Text = WidgetMenu.Title(widget.Size), Margin = new Thickness(8, 0, 0, 0),
            };
            var remove = W.Button(Loc.T("Remove"), () => host.Remove(widget), small: true);
            remove.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetHelpText(remove, name);
            grid.Children.Add(Ui.At(printer, 2));
            grid.Children.Add(Ui.At(size, 3));
            grid.Children.Add(Ui.At(remove, 4));
            // Each row a group named after its widget, so its Printer, Size and Remove say which widget they change.
            list.Children.Add(W.Card(Ui.Group(grid, name), 10));
        }
        return list;
    }
}

/// <summary>The showcase (the Mac's WidgetShowcase): one widget on a desktop backdrop that changes size while a
/// simulated print plays through every state, the states as a bar under it (the highlight slides to the current one,
/// a click jumps there), and beside it what the size carries. The tour advances every 3.6 s, all states in one size
/// and then the next, while the page is on screen and Windows' animations are on ("reduce motion" off), and holds
/// while the keyboard focus is on it.</summary>
internal sealed class Showcase : IDisposable
{
    public const double SceneSeconds = 3.6;
    private const double StageMargin = 26, ScenesHeight = 44, ScenesWidth = 470;

    private readonly WindowContext _ctx;
    private readonly Grid _layout = new();
    private readonly Border _stage;
    private readonly Border _widgetSlot = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _scenesRow = new();
    private readonly Border _highlight;
    private readonly StackPanel _sceneButtons = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<ShowcaseScene, (ChromeButton Button, FrameworkElement Icon, TextBlock Label)> _scenes = [];
    private readonly Dictionary<WidgetHost.Size, ChromeToggle> _sizes = [];
    private readonly Stack _details;
    private readonly TextBlock _title, _summary;
    private readonly WrapPanel _features = new();
    private readonly DispatcherTimer? _timer;
    private WidgetCard _card = null!;
    private WidgetHost.Size _size = WidgetHost.Size.Small;
    private ShowcaseScene _scene = ShowcaseScene.Printing;
    private DateTime _sceneStart = DateTime.UtcNow;

    public Showcase(WindowContext ctx)
    {
        _ctx = ctx;
        _highlight = new Border
        {
            CornerRadius = new CornerRadius(999), HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.Brush(Color.FromArgb(0x2E, 255, 255, 255)),
            BorderThickness = new Thickness(0.75), RenderTransform = new TranslateTransform(), IsHitTestVisible = false,
        };
        // Capsules: a 999 radius draws ellipses in WPF.
        Ui.Capsule(_highlight);
        _stage = Stage();
        _title = Ui.Text("", 17, FontWeights.SemiBold);
        _summary = Ui.Paragraph("", WRamp.Detail);
        _details = new Stack { Spacing = 14, Children = { SizePicker(), Features() } };

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.Children.Add(_stage);
        _layout.Children.Add(_details);
        _layout.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        Arrange(800);

        ShowSize(animate: false);
        ShowScene(animate: false);
        if (ctx.Animate)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += (_, _) => Tick();
            _stage.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Restart(); else _timer.Stop(); };
            _stage.Unloaded += (_, _) => _timer.Stop();
        }
    }

    public FrameworkElement Root => _layout;

    /// <summary>The store changed: the widget is dressed as the current printer, so draw it again.</summary>
    public void Refresh() => Draw();

    public void Dispose() => _timer?.Stop();

    /// <summary>Side by side when there's room for the stage and 240 px of details, else stacked (ViewThatFits).</summary>
    private void Arrange(double width)
    {
        var wide = width >= ScenesWidth + 20 + 240;
        Grid.SetColumn(_details, wide ? 1 : 0);
        Grid.SetRow(_details, wide ? 0 : 1);
        _details.Margin = wide ? new Thickness(20, 0, 0, 0) : new Thickness(0, 20, 0, 0);
        Grid.SetColumnSpan(_details, wide ? 1 : 2);
    }

    // MARK: Stage

    private Border Stage()
    {
        // A Windows 11 "bloom" wallpaper: deep blue, a bright blue light top left and violet bottom right.
        var backdrop = new Grid();
        backdrop.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromRgb(0x24, 0x3E, 0x7A), Color.FromRgb(0x0B, 0x14, 0x2E), 45) });
        backdrop.Children.Add(new Border { Background = Bloom(Color.FromArgb(0x99, 0x5B, 0x9B, 0xFF), new Point(0.18, 0.18), 0.7) });
        backdrop.Children.Add(new Border { Background = Bloom(Color.FromArgb(0x73, 0xB0, 0x5C, 0xE6), new Point(0.9, 0.95), 0.75) });

        _widgetSlot.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45 };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(Ui.At(_widgetSlot, 0, 0));
        content.Children.Add(Ui.At(ScenesBar(), 0, 1));

        // A group named "Widgets": the widget and the states the tour walks through.
        var stage = new AccessibleGroup
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, MinWidth = ScenesWidth, ClipToBounds = true,
            Child = new Grid { Children = { backdrop, content } },
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = 0.18 },
        };
        stage.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.CardStroke);
        backdrop.SizeChanged += (_, e) => backdrop.Clip = new RectangleGeometry(new Rect(e.NewSize), 15, 15);
        AutomationProperties.SetName(stage, Loc.T("Widgets"));
        return stage;
    }

    private static RadialGradientBrush Bloom(Color color, Point center, double radius)
    {
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B))
        {
            Center = center, GradientOrigin = center, RadiusX = radius, RadiusY = radius,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>The states the tour walks through, on a frosted bar under the widget.</summary>
    private FrameworkElement ScenesBar()
    {
        foreach (var scene in Enum.GetValues<ShowcaseScene>())
        {
            var icon = scene.Icon(11);
            icon.Margin = new Thickness(0, 0, 6, 0);
            var label = Ui.Text(scene.Title(), 12);
            var button = new ChromeButton
            {
                Look = new ButtonLook(null, null, null), Padding = new Thickness(11, 6, 11, 7), CornerRadius = new CornerRadius(999),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
                Role = AutomationControlType.RadioButton, Selected = false,
            };
            AutomationProperties.SetName(button, scene.Title());
            Ui.Tip(button, scene.Help());
            button.Click += (_, _) =>
            {
                _scene = scene;
                Restart();
                ShowScene(animate: _ctx.Animate);
            };
            Ui.Capsule(button);
            _scenes[scene] = (button, icon, label);
            _sceneButtons.Children.Add(button);
        }
        _scenesRow.Children.Add(_highlight);
        _scenesRow.Children.Add(_sceneButtons);
        _sceneButtons.SizeChanged += (_, _) => MoveHighlight(animate: false);
        var bar = new Border
        {
            Child = _scenesRow, CornerRadius = new CornerRadius(999), Padding = new Thickness(3), Margin = new Thickness(14, 0, 14, 14),
            Background = Palette.Brush(Color.FromArgb(0x66, 0x08, 0x0C, 0x18)), BorderBrush = Palette.Brush(Color.FromArgb(0x1F, 255, 255, 255)),
            BorderThickness = new Thickness(0.75), HorizontalAlignment = HorizontalAlignment.Center,
        };
        return Ui.Capsule(bar);
    }

    private void MoveHighlight(bool animate)
    {
        if (!_scenes.TryGetValue(_scene, out var current) || current.Button.ActualWidth <= 0) return;
        var x = current.Button.TranslatePoint(new Point(0, 0), _sceneButtons).X;
        var shift = (TranslateTransform)_highlight.RenderTransform;
        _highlight.Height = current.Button.ActualHeight;
        _highlight.BorderBrush = Palette.Brush(Palette.WithAlpha(_scene.Color(), 0.55));
        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease });
            _highlight.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(current.Button.ActualWidth, TimeSpan.FromMilliseconds(400)) { EasingFunction = ease });
        }
        else
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            _highlight.BeginAnimation(FrameworkElement.WidthProperty, null);
            shift.X = x;
            _highlight.Width = current.Button.ActualWidth;
        }
    }

    // MARK: Details

    /// <summary>Small, Medium, Large: a segmented control.</summary>
    private Border SizePicker()
    {
        var row = new UniformGrid3();
        foreach (var size in Enum.GetValues<WidgetHost.Size>())
        {
            var toggle = new ChromeToggle
            {
                Content = Ui.Text(WidgetMenu.Title(size), WRamp.Body), Height = 30, HorizontalContentAlignment = HorizontalAlignment.Center,
                CheckedLook = ButtonLook.Standard, CornerRadius = new CornerRadius(4),
            };
            AutomationProperties.SetName(toggle, WidgetMenu.Title(size));
            toggle.Click += (_, _) =>
            {
                _size = size;
                Restart();
                ShowSize(animate: _ctx.Animate);
            };
            _sizes[size] = toggle;
            row.Children.Add(toggle);
        }
        var box = new AccessibleGroup { Child = row, CornerRadius = Metrics.Control, Padding = new Thickness(2), BorderThickness = new Thickness(1), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        box.SetResourceReference(Border.BackgroundProperty, WindowKeys.OffFill);
        box.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.ControlStroke);
        AutomationProperties.SetName(box, Loc.T("Size"));
        return box;
    }

    private Border Features()
    {
        var stack = new Stack { Spacing = 10, Children = { _title, _summary, _features } };
        return W.Card(stack, 14);
    }

    private void FillDetails()
    {
        foreach (var (size, toggle) in _sizes) toggle.IsChecked = size == _size;
        _title.Text = WidgetMenu.Title(_size);
        _summary.Text = _size switch
        {
            WidgetHost.Size.Small => Loc.T("The essentials at a glance: a progress ring with the percentage, time left and when it'll be done. Paused, it says why; finished, how long ago; idle, the temperatures and loaded spools."),
            WidgetHost.Size.Medium => Loc.T("The printer as it is: the part growing in the filament's colour, the job and its progress, finish time and layers, and nozzle, bed and chamber temperatures."),
            _ => Loc.T("Everything: progress and timing, temperature tiles, every AMS spool with what's left, the newest error in plain words, and how the last print went."),
        };
        _features.Children.Clear();
        foreach (var feature in Enum.GetValues<WidgetFeature>())
        {
            var on = feature.In(_size);
            var color = on ? _ctx.Ink(Palette.Accent) : _ctx.Brush(ThemeKeys.TextTertiary);
            var icon = feature.Icon(11, color);
            icon.Margin = new Thickness(0, 0, 6, 0);
            var label = Ui.Text(feature.Title(), 12, FontWeights.Medium);
            label.Foreground = color;
            var chip = new AccessibleCard
            {
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
                CornerRadius = Metrics.Pill, Padding = new Thickness(9, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6),
            };
            Ui.Capsule(chip);
            if (on) chip.Background = Palette.Brush(Palette.WithAlpha(Palette.Accent, 0.13));
            else chip.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SubtleHover);
            AutomationProperties.SetName(chip, feature.Title());
            AutomationProperties.SetItemStatus(chip, on ? Loc.T("Showing") : "");
            _features.Children.Add(chip);
        }
    }

    // MARK: Tour

    private void Restart()
    {
        _sceneStart = DateTime.UtcNow;
        if (_timer is not null && _stage.IsVisible) _timer.Start();
    }

    private void Tick()
    {
        // "Show animations" turned off while the page is open: the tour stops where it is.
        if (!AppTheme.AnimationsEnabled)
        {
            _timer?.Stop();
            return;
        }
        // While the keyboard is on the showcase's buttons, the tour holds the scene, so it doesn't change under
        // someone reading or picking one (it moves on again once the focus leaves).
        if (_layout.IsKeyboardFocusWithin && (DateTime.UtcNow - _sceneStart).TotalSeconds >= SceneSeconds)
        {
            _sceneStart = DateTime.UtcNow.AddSeconds(-SceneSeconds / 2);
            return;
        }
        if ((DateTime.UtcNow - _sceneStart).TotalSeconds >= SceneSeconds)
        {
            // Every scene in one size, then the next size, on a loop.
            var scenes = Enum.GetValues<ShowcaseScene>();
            var next = ((int)_scene + 1) % scenes.Length;
            _scene = scenes[next];
            _sceneStart = DateTime.UtcNow;
            if (next == 0)
            {
                var sizes = Enum.GetValues<WidgetHost.Size>();
                _size = sizes[((int)_size + 1) % sizes.Length];
                ShowSize(animate: true);
            }
            ShowScene(animate: true);
            return;
        }
        Draw();
    }

    private void ShowScene(bool animate)
    {
        foreach (var (scene, (button, icon, label)) in _scenes)
        {
            var on = scene == _scene;
            button.Selected = on;
            label.Foreground = Palette.Brush(Color.FromArgb((byte)(on ? 255 : 204), 255, 255, 255));
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium;
            ShowcaseScenes.Tint(icon, on ? scene.Color() : Color.FromArgb(0xBF, 255, 255, 255));
        }
        MoveHighlight(animate);
        Draw(fade: animate);
    }

    /// <summary>A new card for the size; the stage grows or shrinks to it and the card fades in.</summary>
    private void ShowSize(bool animate)
    {
        _card = new WidgetCard(_size);
        var scale = _size switch { WidgetHost.Size.Small => 1.3, WidgetHost.Size.Medium => 1.1, _ => 1.0 };
        var card = WidgetCard.CardSize(_size);
        _widgetSlot.Child = _card;
        _widgetSlot.LayoutTransform = new ScaleTransform(scale, scale);
        var height = card.Height * scale + StageMargin * 2 + ScenesHeight;
        _stage.MinWidth = Math.Max(card.Width * scale + StageMargin * 2, ScenesWidth);
        if (animate)
        {
            var from = _stage.ActualHeight > 0 ? _stage.ActualHeight : height;
            _stage.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(from, height, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            _stage.BeginAnimation(FrameworkElement.HeightProperty, null);
            _stage.Height = height;
        }
        FillDetails();
        Draw(fade: animate);
    }

    private void Draw(bool fade = false)
    {
        var now = DateTimeOffset.Now;
        var t = _timer is null ? SceneSeconds / 2 : Math.Min(SceneSeconds, (DateTime.UtcNow - _sceneStart).TotalSeconds);
        var (state, art) = ShowcaseScenes.Build(_scene, t, now, ShowcasePrinter.For(_ctx.Store.Current));
        _card.SpinAngle = t * 360 / 1.1;
        _card.Show(state, art, now);
        if (!fade) return;
        _card.RenderTransformOrigin = new Point(0.5, 0.5);
        var grow = new ScaleTransform(0.96, 0.96);
        _card.RenderTransform = grow;
        var duration = TimeSpan.FromMilliseconds(350);
        _card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, duration));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    /// <summary>Three equal columns (WPF's UniformGrid lives in Primitives with a name that clashes here).</summary>
    private sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid3() => Columns = 3;
    }
}

/// <summary>The simulated print the showcase plays (the Mac's ShowcaseScene).</summary>
internal enum ShowcaseScene { Printing, Preparing, Paused, Finished, Idle }

/// <summary>What each size carries, for the feature chips (the Mac's WidgetFeature).</summary>
internal enum WidgetFeature { Progress, TimeLeft, FinishTime, Layers, Stage, Temperatures, Spools, Printer, Errors, LastPrint }

internal static class ShowcaseScenes
{
    public static string Title(this ShowcaseScene scene) => scene switch
    {
        ShowcaseScene.Printing => Loc.T("Printing", "Printer state"),
        ShowcaseScene.Preparing => Loc.T("Heating", "Printer state"),
        ShowcaseScene.Paused => Loc.T("Paused", "Printer state"),
        ShowcaseScene.Finished => Loc.T("Finished", "Printer state"),
        _ => Loc.T("Ready", "Printer state"),
    };

    public static string Help(this ShowcaseScene scene) => scene switch
    {
        ShowcaseScene.Printing => Loc.T("Show the widget while printing"),
        ShowcaseScene.Preparing => Loc.T("Show the widget while heating"),
        ShowcaseScene.Paused => Loc.T("Show the widget while paused"),
        ShowcaseScene.Finished => Loc.T("Show the widget once finished"),
        _ => Loc.T("Show the widget while ready"),
    };

    public static Color Color(this ShowcaseScene scene) => scene switch
    {
        ShowcaseScene.Printing => Palette.Accent,
        ShowcaseScene.Preparing => Palette.Preparing,
        ShowcaseScene.Paused => Palette.Paused,
        ShowcaseScene.Finished => Palette.Finished,
        _ => Palette.Idle,
    };

    /// <summary>printer.fill, flame.fill, pause.fill, checkmark.circle.fill, moon.zzz.fill.</summary>
    public static FrameworkElement Icon(this ShowcaseScene scene, double size) => scene == ShowcaseScene.Preparing
        ? new Border { Child = Icons.Draw(Icons.Vector.Flame, size, Palette.Brush(Colors.White)), Width = size, Height = size }
        : Icons.Glyph(scene switch
        {
            ShowcaseScene.Printing => Icons.Printer,
            ShowcaseScene.Paused => Icons.Pause,
            ShowcaseScene.Finished => Icons.Done,
            _ => "",
        }, size, null, Palette.Brush(Colors.White));

    /// <summary>Colours an icon made by <see cref="Icon(ShowcaseScene, double)"/>.</summary>
    public static void Tint(FrameworkElement icon, Color color)
    {
        if (icon is TextBlock glyph) glyph.Foreground = Palette.Brush(color);
        else if (icon is Border box) box.Child = Icons.Draw(Icons.Vector.Flame, box.Width, Palette.Brush(color));
    }

    public static string Title(this WidgetFeature feature) => feature switch
    {
        WidgetFeature.Progress => Loc.T("Progress", "Widget feature"),
        WidgetFeature.TimeLeft => Loc.T("Time left", "Widget feature"),
        WidgetFeature.FinishTime => Loc.T("Finish time", "Widget feature"),
        WidgetFeature.Layers => Loc.T("Layers", "Widget feature"),
        WidgetFeature.Stage => Loc.T("Stage & pause reason", "Widget feature"),
        WidgetFeature.Temperatures => Loc.T("Temperatures", "Widget feature"),
        WidgetFeature.Spools => Loc.T("AMS spools", "Widget feature"),
        WidgetFeature.Printer => Loc.T("Live drawing", "Widget feature: the printer drawing that shows the part growing"),
        WidgetFeature.Errors => Loc.T("Errors", "Widget feature"),
        _ => Loc.T("Last print", "Widget feature"),
    };

    public static FrameworkElement Icon(this WidgetFeature feature, double size, Brush brush) => feature switch
    {
        WidgetFeature.Temperatures => Icons.Draw(Icons.Vector.Thermometer, size + 1, brush),
        _ => Icons.Glyph(feature switch
        {
            WidgetFeature.Progress => "",
            WidgetFeature.TimeLeft => "",
            WidgetFeature.FinishTime => "",
            WidgetFeature.Layers => "",
            WidgetFeature.Stage => Icons.Pause,
            WidgetFeature.Spools => AppPage.Filament.Glyph(),
            WidgetFeature.Printer => Icons.Printer,
            WidgetFeature.Errors => Icons.Warning,
            _ => Icons.Done,
        }, size, null, brush),
    };

    public static bool In(this WidgetFeature feature, WidgetHost.Size size) => size switch
    {
        WidgetHost.Size.Small => feature is not (WidgetFeature.Printer or WidgetFeature.Errors),
        WidgetHost.Size.Medium => feature is not (WidgetFeature.Errors or WidgetFeature.LastPrint),
        _ => true,
    };

    /// <summary>The widget's state <paramref name="t"/> seconds into the scene, so numbers move while it plays, and the
    /// printer art for it. The printer's own name, model and spools are used when there is one, so the tour previews
    /// the person's setup rather than made-up filament.</summary>
    public static (WidgetState State, PrinterArt.Scene Art) Build(ShowcaseScene scene, double t, DateTimeOffset now, ShowcasePrinter printer)
    {
        var s = DemoDriver.Sample();
        s.JobName = "Articulated Dragon";
        s.UpdatedAt = now;
        if (printer.Ams.Count > 0 || printer.ExternalSpool is not null)
        {
            s.Ams = printer.Ams.Select(u => u.Clone()).ToList();
            s.ExternalSpool = printer.ExternalSpool?.Clone();
            s.ActiveSpoolId = printer.ActiveSpoolId;
        }
        s.FitTo(PrinterModel.Named(printer.Model));
        var filament = s.ActiveSpool;
        JobRecord? lastJob = null;
        const double total = Showcase.SceneSeconds;
        switch (scene)
        {
            case ShowcaseScene.Printing:
                // Races from 18 % to 96 % so the ring, bar, layers and time left all move.
                var p = Math.Min(0.96, 0.18 + t / total * 0.78);
                s.Progress = (int)(p * 100);
                s.TotalLayers = 284;
                s.Layer = (int)(s.TotalLayers * p);
                s.RemainingMinutes = Math.Max(1, (int)(148 * (1 - p)));
                s.Nozzle = new Temperature(220 + (t % 2 < 1 ? 1 : 0), 220);
                break;
            case ShowcaseScene.Preparing:
                s.State = JobState.Preparing;
                s.StageId = t < total * 0.55 ? 7 : 1;
                s.Progress = 0;
                s.Layer = 0;
                s.RemainingMinutes = 152;
                s.Nozzle = new Temperature(Math.Min(220, 95 + t * 40), 220);
                s.Bed = new Temperature(Math.Min(55, 30 + t * 9), 55);
                break;
            case ShowcaseScene.Paused:
                s.State = JobState.Paused;
                s.StageId = 6;
                s.Progress = 64;
                s.Layer = 182;
                s.TotalLayers = 284;
                s.RemainingMinutes = 54;
                s.Issues =
                [
                    new PrinterIssue
                    {
                        Id = "HMS_0700_2300_0002_0001", Severity = IssueSeverity.Serious,
                        Message = HmsCatalog.Shared.HmsText("0700230000020001") ?? "AMS A slot 4 filament has run out. Insert a new spool to continue.",
                    },
                ];
                break;
            case ShowcaseScene.Finished:
                s.State = JobState.Finished;
                s.StageId = 255;
                s.Progress = 100;
                s.Layer = 284;
                s.TotalLayers = 284;
                s.RemainingMinutes = 0;
                s.ActiveSpoolId = null;
                s.Nozzle = new Temperature(Math.Max(38, 120 - t * 14), 0);
                s.Bed = new Temperature(Math.Max(30, 52 - t * 3), 0);
                lastJob = Job(s.JobName, now.AddHours(-3), now.AddMinutes(-12), filament);
                break;
            default:
                s.State = JobState.Idle;
                s.StageId = 255;
                s.JobName = "";
                s.Progress = 0;
                s.Layer = 0;
                s.TotalLayers = 0;
                s.RemainingMinutes = 0;
                s.ActiveSpoolId = null;
                s.ChamberLightOn = false;
                s.Nozzle = new Temperature(26, 0);
                s.Bed = new Temperature(24, 0);
                s.Chamber = 23;
                lastJob = Job("Articulated Dragon", now.AddHours(-5), now.AddHours(-2), filament);
                break;
        }
        var snapshot = new PrinterSnapshot
        {
            Serial = "showcase", PrinterName = printer.Name, Model = printer.Model, Link = PrinterSnapshot.LinkState.Online, Status = s,
            LastJob = lastJob, SavedAt = now,
        };
        var state = WidgetStateWriter.Build(snapshot, now, artFolder: null, motion: false);
        var art = ArtImage.Scene(s, printer.Model, s.ChamberLightOn ?? false, dimmed: false, lastFilament: lastJob?.FilamentColor);
        // Art in 5 % steps: the part grows visibly without a new render every frame.
        return (state, art with { Progress = Math.Round(art.Progress * 20) / 20 });
    }

    private static JobRecord Job(string name, DateTimeOffset start, DateTimeOffset end, Spool? filament) => new()
    {
        Name = name, StartedAt = start, EndedAt = end, Outcome = JobOutcome.Finished, TotalLayers = 284,
        FilamentColor = filament?.Color ?? "E2763AFF", FilamentMaterial = filament?.Material ?? "PLA",
    };
}

/// <summary>Who the showcase dresses up as: the current printer, or a sample P2S before one is set up.</summary>
internal sealed record ShowcasePrinter(string Name, string Model, List<AmsUnit> Ams, Spool? ExternalSpool, int? ActiveSpoolId)
{
    public static ShowcasePrinter For(PrinterSession? session)
    {
        var status = session?.Status;
        var loaded = status?.AllSpools.Where(s => s.IsLoaded).ToList() ?? [];
        // The spool in use if it's printing, else the first loaded one that isn't white, so the part has a real colour.
        var active = status?.ActiveSpool?.Id ?? loaded.FirstOrDefault(s => Palette.Luminance(s.Color) < 0.85)?.Id ?? loaded.FirstOrDefault()?.Id;
        return new ShowcasePrinter(session?.Config.Name ?? "P2S Workshop", session?.Config.ModelName ?? "P2S", status?.Ams ?? [],
            status?.ExternalSpool is { IsLoaded: true } external ? external : null, active);
    }
}
