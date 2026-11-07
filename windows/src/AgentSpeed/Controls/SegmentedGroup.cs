using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AgentSpeed.Controls;

/// <summary>
/// The border of a segmented control that screen readers see as one group (its <c>AutomationProperties.Name</c>) holding
/// the segments' radio buttons (DESIGN 14, Accessibility).
/// </summary>
internal sealed class SegmentedGroup : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);

    private sealed class GroupPeer(SegmentedGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(SegmentedGroup);
    }
}
