using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BambuBar.AppWindow;
using BambuBar.Core;
using BambuBar.Core.App;
using BambuBar.Core.Model;
using BambuBar.Model3D;
using BambuBar.Theme;
using BambuBar.TrayPanel;
using BambuBar.Widgets;

namespace BambuBar;

/// <summary>
/// <c>BambuBar.exe --a11y-check &lt;dir&gt;</c> (P3.12): opens the panel and the window over the demo fleet
/// (<c>--demo printing --fleet</c>) in real windows and checks what a screen reader and a keyboard get, scene by scene:
/// the panel (one printer, all printers), its printer and speed menus, the stop confirmation, every page of the window
/// and setup. It fails (exit 1) on
/// <list type="bullet">
/// <item>a control (anything that can be invoked, toggled, picked, expanded or typed in) without a name;</item>
/// <item>a meaningless name: a glyph or symbol character, nothing but punctuation, or the control's type
/// ("Button");</item>
/// <item>two controls of the same type with the same name in the same named container (Narrator couldn't tell
/// them apart);</item>
/// <item>a picture with no name (one that only decorates should be hidden);</item>
/// <item>a control Tab never reaches (an item of a radio group, list or menu is fine when Tab reaches one of its
/// set: arrows move between them), a Tab stop screen readers can't see or that has no name, one with no focus
/// rectangle, and Tab losing the focus or leaving the window;</item>
/// <item>Enter or Space not opening a menu button's menu, Esc not closing a menu (and giving the focus back), the
/// stop confirmation or the panel.</item>
/// </list>
/// The rules read WPF's automation peers, which are what UI Automation serves; each window is then also read through
/// the UI Automation client (<see cref="AutomationElement"/>, from another thread, as Narrator would), and an unnamed
/// control there fails too. Keys go through WPF's input pipeline (<see cref="InputManager.ProcessInput"/>), so Tab moves
/// the focus exactly as the keyboard does. Writes <c>a11y-report.txt</c> (per scene: problems, the Tab order, the tree
/// with names, roles and states, and the UI Automation client's tree) and <c>a11y-problems.txt</c>.
/// </summary>
public static class A11yCheck
{
    public static string? Folder(IReadOnlyList<string> args)
    {
        var i = args.ToList().IndexOf("--a11y-check");
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        Renderer3D.LogPath = null;
        Strings.Load().Use("en");
        var check = new Checker(folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            check.Problem("app", $"unhandled exception: {e.Exception}");
            e.Handled = true;
        };
        // A hang (a dialog that never closes, a UI Automation call that never returns) still ends the run, with a report.
        using var watchdog = new Timer(_ =>
        {
            check.Problem("app", "the check didn't finish within 5 minutes");
            check.Write();
            Environment.Exit(3);
        }, null, TimeSpan.FromMinutes(5), Timeout.InfiniteTimeSpan);
        var code = 2;
        app.Startup += async (_, _) =>
        {
            try
            {
                await check.RunAsync();
            }
            catch (Exception e)
            {
                check.Problem("app", $"the check stopped: {e}");
            }
            code = check.Write();
            app.Shutdown(code);
        };
        app.Run();
        return code;
    }

    private sealed class Checker(string folder)
    {
        private static readonly string[] DemoArgs = ["--demo", "printing", "--fleet"];
        private readonly List<(string Scene, string Text)> _problems = [];
        private readonly StringBuilder _report = new();
        private readonly Lock _lock = new();
        private PanelWindow? _panel;
        private readonly List<(string Scene, string Text)> _notes = [];
        private readonly HashSet<string> _movedByHand = [];
        private bool _clientStuck;

        /// <summary>Something worth knowing about a scene that isn't a failure.</summary>
        private void Note(string scene, string text)
        {
            lock (_lock) _notes.Add((scene, text));
        }

        public void Problem(string scene, string text)
        {
            lock (_lock) _problems.Add((scene, text));
        }

        /// <summary>Writes the report and the problems; 0 when there are none.</summary>
        public int Write()
        {
            lock (_lock)
            {
                var problems = _problems.Select(p => $"[{p.Scene}] {p.Text}").ToList();
                File.WriteAllText(Path.Combine(folder, "a11y-problems.txt"),
                    problems.Count == 0 ? "No problems.\n" : string.Join("\n", problems) + "\n");
                File.WriteAllText(Path.Combine(folder, "a11y-report.txt"),
                    $"Bambu Bar accessibility check ({DateTime.Now:yyyy-MM-dd HH:mm}), {problems.Count} problem(s)\n\n" + _report);
                return problems.Count == 0 ? 0 : 1;
            }
        }

        public async Task RunAsync()
        {
            var settings = new AppSettings(null);
            using var store = new PrinterStore(new PrinterStore.Services
            {
                Settings = settings, History = new HistoryStore(null), Secrets = new MemorySecretStore(),
                Confirmer = new StopConfirmer(() => _panel), Arguments = DemoArgs,
            });
            store.Start();
            await Until(() => store.Sessions.Count > 1 && store.Sessions.All(s => s.Status is not null), TimeSpan.FromSeconds(10));
            if (store.Sessions.Count < 2) Problem("app", "the demo fleet didn't start");

            await PanelAsync(store, settings);
            await WindowAsync(store, settings);
            store.Stop();
        }

        // MARK: Panel

        private async Task PanelAsync(PrinterStore store, AppSettings settings)
        {
            var view = new PanelView(store, settings, AppTheme.Current, animate: false);
            var panel = new PanelWindow(view);
            _panel = panel;
            panel.ShowPanel();
            using (panel.HoldOpen())
            {
                // On a printer that's printing, so the panel has everything: the job, controls, temperatures, filament.
                if (store.Sessions.FirstOrDefault(s => s.Status?.State == JobState.Printing) is { } printing) store.Select(printing.Id);
                await Settle();
                await SceneAsync("panel", panel, view);

                // The printer menu by Enter, the speed menu by Space: each opens, is checked, and Esc closes it.
                await MenuAsync("panel-printer-menu", panel, view, store.PrinterName, Key.Enter);
                await MenuAsync("panel-speed-menu", panel, view, Loc.T("Speed"), Key.Space);
                await StopConfirmationAsync(panel);

                // Esc closes the panel.
                if (!InScope(panel, Keyboard.FocusedElement)) panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                await PressAsync(Key.Escape, panel);
                if (panel.IsVisible)
                {
                    Problem("panel", "Esc didn't close the panel");
                    panel.HidePanel();
                }

                view.ShowsAllOverride = true;
                panel.ShowPanel();
                await Settle();
                await SceneAsync("panel-all-printers", panel, view);
                panel.HidePanel();
            }
            panel.Quit();
            _panel = null;
        }

        private async Task MenuAsync(string scene, PanelWindow panel, PanelView view, string buttonName, Key key)
        {
            var button = Find<ChromeButton>(panel, b => AutomationProperties.GetName(b) == buttonName && b.IsVisible);
            if (button is null)
            {
                Problem(scene, $"no \"{buttonName}\" button in the panel to open the menu with");
                return;
            }
            Keyboard.Focus(button);
            var active = panel.IsActive;
            await PressAsync(key, panel);
            await Settle();
            if (MenuPopup.Open is not { IsOpen: true, Child: FrameworkElement card } popup)
            {
                Problem(scene, $"{key} on \"{buttonName}\" didn't open its menu");
                return;
            }
            // The menu takes the keyboard focus without taking activation, which would light-dismiss the panel.
            if (active && !panel.IsActive) Problem(scene, "opening the menu took activation from the panel (outside this check, the panel would close)");
            if (!InScope(card, Keyboard.FocusedElement)) Problem(scene, $"the open menu doesn't have the keyboard focus (it's on {Describe(Keyboard.FocusedElement)})");
            if (button.Expanded != true) Problem(scene, $"\"{buttonName}\" doesn't say its menu is expanded");
            await SceneAsync(scene, card, view, popup);
            await PressAsync(Key.Escape, card);
            await Settle();
            if (popup.IsOpen)
            {
                Problem(scene, "Esc didn't close the menu");
                popup.IsOpen = false;
            }
            else if (Keyboard.FocusedElement != button) Problem(scene, $"after Esc the focus is on {Describe(Keyboard.FocusedElement)}, not \"{buttonName}\"");
            if (button.Expanded == true) Problem(scene, $"\"{buttonName}\" still says expanded after its menu closed");
        }

        /// <summary>Stop (invoked through UI Automation's Invoke pattern) asks first; Esc keeps printing.</summary>
        private async Task StopConfirmationAsync(PanelWindow panel)
        {
            const string scene = "stop-confirmation";
            var stop = Find<ChromeButton>(panel, b => AutomationProperties.GetName(b) == Loc.T("Stop") && b.IsVisible);
            if (stop is null || UIElementAutomationPeer.CreatePeerForElement(stop)?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            {
                Problem(scene, "no Stop button with the Invoke pattern in the panel");
                return;
            }
            // Invoke posts the click; the dialog then runs its own loop, in which this continues.
            invoke.Invoke();
            await Until(() => Dialog() is not null, TimeSpan.FromSeconds(5));
            if (Dialog() is not { } dialog)
            {
                Problem(scene, "Stop didn't ask for confirmation");
                return;
            }
            await Settle();
            await SceneAsync(scene, dialog, null);
            if (!InScope(dialog, Keyboard.FocusedElement)) dialog.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            await PressAsync(Key.Escape, dialog);
            await Until(() => !dialog.IsVisible, TimeSpan.FromSeconds(2));
            if (dialog.IsVisible)
            {
                Problem(scene, "Esc didn't close the stop confirmation");
                dialog.DialogResult = false;
            }
            await Settle();

            static ConfirmDialog? Dialog() => Application.Current.Windows.OfType<ConfirmDialog>().FirstOrDefault(w => w.IsVisible);
        }

        // MARK: Window

        private async Task WindowAsync(PrinterStore store, AppSettings settings)
        {
            // Two placed widgets (in a folder of their own, no Rainmeter), so the Widgets page lists rows.
            var temp = Path.Combine(Path.GetTempPath(), "BambuBarA11y-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var widgets = new WidgetHost(temp, temp);
            widgets.Add(WidgetHost.Size.Medium, store.Sessions.FirstOrDefault()?.Id, 1500, 16);
            widgets.Add(WidgetHost.Size.Small, null, 1500, 240);
            var window = new MainWindow(store, settings, new WindowServices { Widgets = widgets });
            try
            {
                window.Present();
                foreach (var page in AppPages.Order)
                {
                    window.Show(page);
                    await Settle();
                    await SceneAsync($"window-{page.ToString().ToLowerInvariant()}", window, window.View);
                }
                window.AddPrinter(null);
                await Settle();
                await SceneAsync("window-add-printer", window, window.View);
                // The manual address folds open with Enter, and its fields have names.
                if (Find<ChromeButton>(window, b => AutomationProperties.GetName(b) == Loc.T("Enter address manually") && b.IsVisible) is { } manual)
                {
                    Keyboard.Focus(manual);
                    await PressAsync(Key.Enter, window);
                    await Settle();
                    if (manual.Expanded != true) Problem("window-add-printer-manual", "Enter on \"Enter address manually\" didn't open it");
                    await SceneAsync("window-add-printer-manual", window, window.View);
                }
                else Problem("window-add-printer", "no \"Enter address manually\" button");
            }
            finally
            {
                window.Close();
                widgets.Dispose();
                try { Directory.Delete(temp, recursive: true); }
                catch (IOException) { /* Left in %TEMP%. */ }
            }
        }

        // MARK: A scene

        /// <summary>Checks what's on screen now: the peer tree's names and roles, the Tab order through
        /// <paramref name="scope"/>, and the UI Automation client's view of its window. <paramref name="live"/> (the panel
        /// or window view) is paused meanwhile, so a report doesn't rebuild what's being walked.</summary>
        private async Task SceneAsync(string scene, FrameworkElement scope, FrameworkElement? live, Popup? popup = null)
        {
            SetLive(live, false);
            try
            {
                var nodes = new List<Node>();
                Walk(scene, scope, null, 0, nodes);
                var tabs = await TabWalkAsync(scene, scope);
                Rules(scene, nodes, tabs);
                var hwnd = popup?.Child is { } child ? (PresentationSource.FromVisual(child) as HwndSource)?.Handle ?? 0
                    : PresentationSource.FromVisual(scope) is HwndSource source ? source.Handle : 0;
                var uia = await ClientAsync(scene, hwnd);

                var text = new StringBuilder();
                text.Append(CultureInfo.InvariantCulture, $"===== {scene} =====\n");
                lock (_lock)
                {
                    var mine = _problems.Where(p => p.Scene == scene).ToList();
                    text.Append(mine.Count == 0 ? "Problems: none\n" : "Problems:\n" + string.Concat(mine.Select(p => $"  - {p.Text}\n")));
                    foreach (var note in _notes.Where(n => n.Scene == scene)) text.Append(CultureInfo.InvariantCulture, $"Note: {note.Text}\n");
                }
                text.Append("\nTab order:\n");
                for (var i = 0; i < tabs.Count; i++) text.Append(CultureInfo.InvariantCulture, $"  {i + 1,3}. {Describe(tabs[i])}\n");
                text.Append("\nAutomation tree (WPF peers):\n");
                foreach (var node in nodes) text.Append(node.Line(tabs)).Append('\n');
                text.Append("\nUI Automation client (control view):\n").Append(uia).Append("\n\n");
                lock (_lock) _report.Append(text);
            }
            finally
            {
                SetLive(live, true);
            }
        }

        private static void SetLive(FrameworkElement? view, bool on)
        {
            if (view is PanelView panel) panel.IsLive = on;
            else if (view is WindowView window) window.IsLive = on;
        }

        // MARK: The peer tree

        private sealed class Node
        {
            public required AutomationPeer Peer { get; init; }
            public Node? Parent { get; init; }
            public int Depth { get; init; }
            public UIElement? Owner { get; init; }
            public AutomationControlType Type { get; init; }
            public string Name { get; init; } = "";
            public string Class { get; init; } = "";
            public string Help { get; init; } = "";
            public string Status { get; init; } = "";
            public string Key { get; init; } = "";
            public bool Enabled { get; init; }
            public bool Visible { get; init; }
            public bool Offscreen { get; init; }
            public bool Invoke { get; init; }
            public string? Toggle { get; init; }
            public bool? Selected { get; init; }
            public string? Expand { get; init; }
            public string? Value { get; init; }
            public bool ValueReadOnly { get; init; } = true;
            public bool Range { get; init; }
            public bool RangeReadOnly { get; init; } = true;
            public AutomationHeadingLevel Heading { get; init; }
            public AutomationLiveSetting LiveSetting { get; init; }
            /// <summary>Under a scroll bar (Windows' own parts, not ours to name).</summary>
            public bool Chrome { get; init; }

            /// <summary>Something a person operates: invoke, toggle, pick, expand, type or set.</summary>
            public bool Interactive => !Chrome && (Invoke || Toggle is not null || Selected is not null || Expand is not null
                || (Value is not null && !ValueReadOnly) || (Range && !RangeReadOnly));

            public string Line(List<UIElement> tabs)
            {
                var flags = new List<string>();
                if (!Visible) flags.Add("hidden");
                else if (Offscreen) flags.Add("scrolled away");
                if (!Enabled) flags.Add("disabled");
                if (Owner is not null && tabs.IndexOf(Owner) is >= 0 and var i) flags.Add($"tab {i + 1}");
                if (Invoke) flags.Add("invoke");
                if (Toggle is not null) flags.Add($"toggle {Toggle}");
                if (Selected is { } s) flags.Add(s ? "selected" : "not selected");
                if (Expand is not null) flags.Add(Expand.ToLowerInvariant());
                if (Value is not null) flags.Add($"value \"{Value}\"{(ValueReadOnly ? " read-only" : "")}");
                if (Range) flags.Add("range");
                if (Heading != AutomationHeadingLevel.None) flags.Add(Heading.ToString().ToLowerInvariant());
                if (LiveSetting != AutomationLiveSetting.Off) flags.Add($"live {LiveSetting.ToString().ToLowerInvariant()}");
                if (Help.Length > 0) flags.Add($"help \"{Help}\"");
                if (Status.Length > 0) flags.Add($"status \"{Status}\"");
                if (Key.Length > 0) flags.Add($"key {Key}");
                return $"{new string(' ', 2 + Depth * 2)}{Type} \"{Name}\" [{Class}]{(flags.Count == 0 ? "" : " · " + string.Join(", ", flags))}";
            }
        }

        /// <summary>The peers under <paramref name="element"/>, as UI Automation gets them: an element without a peer
        /// passes its children up, as WPF's own peers do.</summary>
        private void Walk(string scene, UIElement element, Node? parent, int depth, List<Node> nodes)
        {
            if (UIElementAutomationPeer.CreatePeerForElement(element) is { } peer) Add(scene, peer, parent, depth, nodes);
            else
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                    if (VisualTreeHelper.GetChild(element, i) is UIElement child) Walk(scene, child, parent, depth, nodes);
            }
        }

        private void Add(string scene, AutomationPeer peer, Node? parent, int depth, List<Node> nodes)
        {
            if (nodes.Count > 5000 || depth > 80) return;
            Node node;
            try
            {
                node = Describe(peer, parent, depth);
            }
            catch (Exception e)
            {
                Problem(scene, $"a {peer.GetType().Name} failed to describe itself: {e.Message}");
                return;
            }
            nodes.Add(node);
            List<AutomationPeer>? children;
            try { children = peer.GetChildren(); }
            catch (Exception e)
            {
                Problem(scene, $"\"{node.Name}\" ({node.Class}) failed to list its children: {e.Message}");
                return;
            }
            foreach (var child in children ?? []) Add(scene, child, node, depth + 1, nodes);
        }

        private static Node Describe(AutomationPeer peer, Node? parent, int depth)
        {
            var owner = (peer as UIElementAutomationPeer)?.Owner;
            var type = peer.GetAutomationControlType();
            var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider;
            var selection = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
            var expand = peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
            var value = peer.GetPattern(PatternInterface.Value) as IValueProvider;
            var range = peer.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
            var offscreen = peer.IsOffscreen();
            return new Node
            {
                Peer = peer, Parent = parent, Depth = depth, Owner = owner, Type = type,
                Name = peer.GetName() ?? "", Class = peer.GetClassName() ?? "", Help = peer.GetHelpText() ?? "",
                Status = peer.GetItemStatus() ?? "", Key = peer.GetAcceleratorKey() ?? "", Enabled = peer.IsEnabled(),
                Visible = owner?.IsVisible ?? !offscreen, Offscreen = offscreen,
                Invoke = peer.GetPattern(PatternInterface.Invoke) is not null,
                Toggle = toggle?.ToggleState.ToString(), Selected = selection?.IsSelected, Expand = expand?.ExpandCollapseState.ToString(),
                Value = value is null ? null : owner is PasswordBox ? "(hidden)" : Shorten(value.Value), ValueReadOnly = value?.IsReadOnly ?? true,
                Range = range is not null, RangeReadOnly = range?.IsReadOnly ?? true,
                Heading = peer.GetHeadingLevel(), LiveSetting = peer.GetLiveSetting(),
                Chrome = parent is not null && (parent.Chrome || parent.Type is AutomationControlType.ScrollBar) || type is AutomationControlType.ScrollBar,
            };
        }

        private static string Shorten(string? text) => text is null ? "" : text.Length > 60 ? text[..60] + "…" : text;

        // MARK: Rules

        /// <summary>Containers whose name tells two same-named controls apart.</summary>
        private static readonly AutomationControlType[] Contexts =
        [
            AutomationControlType.Group, AutomationControlType.List, AutomationControlType.Menu, AutomationControlType.Window,
            AutomationControlType.Pane, AutomationControlType.Tab, AutomationControlType.DataItem, AutomationControlType.Tree,
        ];

        private static readonly AutomationControlType[] ArrowSets =
        [
            AutomationControlType.RadioButton, AutomationControlType.ListItem, AutomationControlType.MenuItem,
            AutomationControlType.TabItem, AutomationControlType.TreeItem,
        ];

        private void Rules(string scene, List<Node> nodes, List<UIElement> tabs)
        {
            var shown = nodes.Where(n => n.Visible && !n.Chrome).ToList();
            foreach (var n in shown)
            {
                var label = $"{n.Type} [{n.Class}]";
                if (n.Interactive && n.Name.Trim().Length == 0) Problem(scene, $"{label} has no name{Where(n)}");
                else if (n.Name.Any(IsGlyph)) Problem(scene, $"{label} \"{n.Name}\" has a glyph character in its name{Where(n)}");
                else if (n.Interactive && Meaningless(n)) Problem(scene, $"{label} \"{n.Name}\" has a meaningless name{Where(n)}");
                if (n.Type == AutomationControlType.Image && n.Name.Trim().Length == 0)
                    Problem(scene, $"an image [{n.Class}] has no name (hide it if it only decorates){Where(n)}");
            }

            // The same name for the same kind of control in the same named container.
            foreach (var same in shown.Where(n => n.Interactive && n.Name.Trim().Length > 0)
                .GroupBy(n => (n.Type, n.Name.Trim(), Context(n)), n => n).Where(g => g.Count() > 1))
            {
                var context = same.Key.Item3 is { } c ? $" in {c.Type} \"{c.Name}\"" : "";
                Problem(scene, $"{same.Count()} {same.Key.Type} controls are all called \"{same.Key.Item2}\"{context}");
            }

            // Tab reaches every enabled control (or one of its radio, list or menu set), and every stop is named and shows
            // where the focus is.
            if (tabs.Count == 0) return;
            var reached = tabs.ToHashSet();
            if (tabs.Count == 1 && shown.Count(n => n.Interactive && n.Enabled) > 1)
                Problem(scene, $"Tab doesn't move the focus from {Describe(tabs[0])}");
            foreach (var n in shown.Where(n => n.Interactive && n.Enabled && n.Owner is not null))
            {
                if (reached.Contains(n.Owner!)) continue;
                if (ArrowSets.Contains(n.Type) && n.Parent is { } p
                    && shown.Any(o => o.Parent == p && o.Type == n.Type && o.Owner is not null && reached.Contains(o.Owner))) continue;
                Problem(scene, $"Tab never reaches {n.Type} \"{n.Name}\" [{n.Class}]{Where(n)}");
            }
            foreach (var element in tabs)
            {
                var node = nodes.FirstOrDefault(n => n.Owner == element);
                if (node is null || !node.Visible) Problem(scene, $"Tab stops on {Describe(element)}, which screen readers don't see");
                else if (node.Name.Trim().Length == 0) Problem(scene, $"Tab stops on {node.Type} [{node.Class}], which has no name{Where(node)}");
                if (element is FrameworkElement { FocusVisualStyle: null } fe && fe is not (TextBoxBase or PasswordBox))
                    Problem(scene, $"{Describe(element)} draws no focus rectangle when Tab reaches it");
            }
        }

        private static Node? Context(Node n)
        {
            for (var p = n.Parent; p is not null; p = p.Parent)
                if (Contexts.Contains(p.Type) && p.Name.Trim().Length > 0) return p;
            return null;
        }

        private static string Where(Node n)
        {
            var path = new List<string>();
            for (var p = n.Parent; p is not null && path.Count < 3; p = p.Parent)
                if (p.Name.Trim().Length > 0) path.Add($"\"{Shorten(p.Name)}\"");
            return path.Count == 0 ? "" : " (in " + string.Join(" < ", path) + ")";
        }

        /// <summary>Private-use characters: icon font glyphs (Segoe Fluent Icons) read out as nonsense.</summary>
        private static bool IsGlyph(char c) => c is >= '' and <= '';

        private static readonly string[] TypeWords = ["button", "toggle", "togglebutton", "radiobutton", "checkbox", "item", "menuitem", "listitem", "edit", "text", "image", "control"];

        private static bool Meaningless(Node n)
        {
            var name = n.Name.Trim();
            if (!name.Any(char.IsLetterOrDigit)) return true;
            var word = name.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            return TypeWords.Contains(word) || word == n.Class.ToLowerInvariant() || word == n.Type.ToString().ToLowerInvariant()
                || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("BambuBar.", StringComparison.Ordinal);
        }

        // MARK: Keyboard

        /// <summary>Tab from the first stop until the focus comes back to it: every element it stops on, in order.</summary>
        private async Task<List<UIElement>> TabWalkAsync(string scene, FrameworkElement scope)
        {
            var stops = new List<UIElement>();
            if (!InScope(scope, Keyboard.FocusedElement))
            {
                scope.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                await Dispatcher.Yield(DispatcherPriority.Input);
            }
            if (Keyboard.FocusedElement is not UIElement first || !InScope(scope, first))
            {
                Problem(scene, $"the keyboard focus can't be put in {Describe(scope)} (it's on {Describe(Keyboard.FocusedElement)})");
                return stops;
            }
            var start = first;
            if (start != scope) stops.Add(start);
            for (var i = 0; i < 400; i++)
            {
                var before = Keyboard.FocusedElement;
                await PressAsync(Key.Tab, scope);
                if (Keyboard.FocusedElement == before && before is UIElement from && from.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                {
                    // The same navigation Tab does, asked for directly: the order is still WPF's, but say the key didn't.
                    await Dispatcher.Yield(DispatcherPriority.Input);
                    if (!_movedByHand.Contains(scene))
                    {
                        _movedByHand.Add(scene);
                        Note(scene, $"a synthesized Tab didn't move the focus from {Describe(from)}; the walk uses MoveFocus(Next) where it doesn't");
                    }
                }
                var now = Keyboard.FocusedElement as UIElement;
                if (now is null)
                {
                    Problem(scene, $"Tab from {Describe(before)} lost the keyboard focus");
                    break;
                }
                if (!InScope(scope, now))
                {
                    Problem(scene, $"Tab from {Describe(before)} left for {Describe(now)}");
                    break;
                }
                if (now == scope) continue;
                if (now == start || (stops.Count > 0 && now == stops[0])) break;
                if (stops.Contains(now))
                {
                    Problem(scene, $"Tab goes round in circles from {Describe(now)} without coming back to the first stop");
                    break;
                }
                if (now == before)
                {
                    if (stops.Count > 1) Problem(scene, $"Tab is stuck on {Describe(now)}");
                    break;
                }
                stops.Add(now);
            }
            return stops;
        }

        private static bool InScope(FrameworkElement scope, IInputElement? element) =>
            element is DependencyObject d && (d == scope || (d is Visual v && scope.IsAncestorOf(v)));

        /// <summary>A key press through WPF's input pipeline, to the focused element (as the keyboard's would be):
        /// PreviewKeyDown, which WPF follows with KeyDown (where Tab moves the focus and buttons click), then the
        /// release. Should WPF not follow up a synthesized preview, the KeyDown or KeyUp is sent too.</summary>
        private async Task PressAsync(Key key, FrameworkElement scope)
        {
            Watch();
            var time = Environment.TickCount;
            Send(key, scope, time, Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent);
            await Dispatcher.Yield(DispatcherPriority.Input);
            Send(key, scope, time + 30, Keyboard.PreviewKeyUpEvent, Keyboard.KeyUpEvent);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }

        private RoutedEvent? _seen;
        private bool _watching;

        /// <summary>Notes which key events WPF's pipeline processes (to tell whether a preview was followed up).</summary>
        private void Watch()
        {
            if (_watching) return;
            _watching = true;
            InputManager.Current.PreProcessInput += (_, e) =>
            {
                if (e.StagingItem.Input is KeyEventArgs k && (k.RoutedEvent == Keyboard.KeyDownEvent || k.RoutedEvent == Keyboard.KeyUpEvent)) _seen = k.RoutedEvent;
            };
        }

        private void Send(Key key, FrameworkElement scope, int time, RoutedEvent preview, RoutedEvent main)
        {
            var source = PresentationSource.FromVisual(Keyboard.FocusedElement as Visual ?? scope) ?? PresentationSource.FromVisual(scope);
            if (source is null) return;
            _seen = null;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, time, key) { RoutedEvent = preview };
            InputManager.Current.ProcessInput(args);
            if (_seen != main && !args.Handled)
                InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, time, key) { RoutedEvent = main });
        }

        private static string Describe(object? element)
        {
            if (element is null) return "nothing";
            var name = element is DependencyObject d ? AutomationProperties.GetName(d) : "";
            if (name.Length == 0 && element is TextBlock t) name = t.Text;
            if (name.Length == 0 && element is ContentControl { Content: string s }) name = s;
            return $"{element.GetType().Name}{(name.Length > 0 ? $" \"{Shorten(name)}\"" : "")}";
        }

        private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t && match(t)) return t;
                if (Find(child, match) is { } found) return found;
            }
            return null;
        }

        // MARK: The UI Automation client

        /// <summary>The window as a UI Automation client sees it (from another thread, as Narrator does): every element of
        /// the control view; a control with no name, or a glyph for one, fails.</summary>
        private async Task<string> ClientAsync(string scene, nint hwnd)
        {
            if (hwnd == 0)
            {
                Problem(scene, "no window handle to read through UI Automation");
                return "  (no window)";
            }
            if (_clientStuck) return "  (skipped: UI Automation didn't answer earlier)";
            var read = Task.Run(() => ReadClient(hwnd));
            if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(60))) != read)
            {
                _clientStuck = true;
                Problem(scene, "UI Automation didn't answer within 60 s");
                return "  (timed out)";
            }
            var (text, problems, count) = await read;
            foreach (var p in problems) Problem(scene, "UI Automation: " + p);
            if (count < 3) Problem(scene, $"UI Automation sees {count} element(s) only");
            return text;
        }

        private static (string Text, List<string> Problems, int Count) ReadClient(nint hwnd)
        {
            var text = new StringBuilder();
            var problems = new List<string>();
            var count = 0;
            AutomationElement root;
            try { root = AutomationElement.FromHandle(hwnd); }
            catch (Exception e) when (e is ElementNotAvailableException or ArgumentException or InvalidOperationException)
            {
                problems.Add($"the window isn't readable: {e.Message}");
                return ("", problems, 0);
            }
            var walker = TreeWalker.ControlViewWalker;
            void Visit(AutomationElement element, int depth)
            {
                if (count > 5000 || depth > 80) return;
                count++;
                string type, name, patterns;
                bool offscreen, interactive;
                try
                {
                    var current = element.Current;
                    type = current.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal);
                    name = current.Name ?? "";
                    offscreen = current.IsOffscreen;
                    var supported = element.GetSupportedPatterns();
                    patterns = string.Join(", ", supported.Select(p => p.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "", StringComparison.Ordinal)));
                    var editable = supported.Contains(ValuePattern.Pattern)
                        && element.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern { Current.IsReadOnly: false };
                    interactive = editable || supported.Any(p => p == InvokePattern.Pattern || p == TogglePattern.Pattern
                        || p == SelectionItemPattern.Pattern || p == ExpandCollapsePattern.Pattern);
                    // Windows' own frame (title bar, caption buttons) and scroll bars aren't ours to name.
                    if (current.ControlType == ControlType.TitleBar || current.ControlType == ControlType.ScrollBar)
                    {
                        text.Append(' ', 2 + depth * 2).Append(type).Append(" (Windows' own)\n");
                        return;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    return;
                }
                text.Append(' ', 2 + depth * 2).Append(type).Append(" \"").Append(name).Append('"');
                if (offscreen) text.Append(" · offscreen");
                if (patterns.Length > 0) text.Append(" · ").Append(patterns);
                text.Append('\n');
                if (interactive && !offscreen && name.Trim().Length == 0) problems.Add($"a {type} has no name");
                if (name.Any(IsGlyph)) problems.Add($"a {type} is called \"{name}\", a glyph character");
                for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child)) Visit(child, depth + 1);
            }
            Visit(root, 0);
            return (text.ToString(), problems, count);
        }

        // MARK: Timing

        private static async Task Settle()
        {
            await Task.Delay(400);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        private static async Task Until(Func<bool> done, TimeSpan limit)
        {
            var end = DateTime.UtcNow + limit;
            while (!done() && DateTime.UtcNow < end) await Task.Delay(100);
        }
    }
}
