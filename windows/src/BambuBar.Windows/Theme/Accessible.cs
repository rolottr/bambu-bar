using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BambuBar.Theme;

/// <summary>
/// A card that screen readers read as one item with one name, ignoring the pieces inside (SwiftUI's
/// <c>.accessibilityElement(children: .ignore)</c> with a label): temperature tiles, fan readings, spools.
/// Set the text with <see cref="AutomationProperties.NameProperty"/>.
/// </summary>
public class AccessibleCard : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(AccessibleCard);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>A stack read as one item (a spool column, a fan), like <see cref="AccessibleCard"/>.</summary>
public sealed class AccessibleStack : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(AccessibleStack);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// A named container whose controls stay reachable inside it (SwiftUI's <c>.accessibilityElement(children: .contain)</c>
/// with a label): an issue banner with its buttons, a settings group, one placed widget's row. Screen readers say the
/// name on the way in, so two "Remove" buttons in two rows aren't the same button. Panels and borders have no peer of
/// their own in WPF, so a name set on one is otherwise lost.
/// </summary>
public sealed class GroupPeer(FrameworkElement owner, AutomationControlType type = AutomationControlType.Group) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => type;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
}

/// <summary>A <see cref="Border"/> read as a named group (<see cref="GroupPeer"/>).</summary>
public class AccessibleGroup : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

/// <summary>A picture that only decorates (the app's mark, the camera's frame inside its named player): screen readers
/// skip it, as the Mac's <c>.accessibilityHidden(true)</c>.</summary>
public sealed class DecorativeImage : Image
{
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}

/// <summary>Screen reader announcements.</summary>
public static class Announce
{
    /// <summary>A message that appears with the element (an inline error, a banner): read out when it's shown, without
    /// moving the focus (a polite live region, raised once it's loaded).</summary>
    public static T WhenShown<T>(T element) where T : FrameworkElement
    {
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        element.Loaded += (_, _) => Changed(element);
        return element;
    }

    /// <summary>The live region's text changed: read it again.</summary>
    public static void Changed(UIElement element) =>
        UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
}
