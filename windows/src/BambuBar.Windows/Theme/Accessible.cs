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
