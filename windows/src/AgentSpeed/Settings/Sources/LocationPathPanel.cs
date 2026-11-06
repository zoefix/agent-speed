using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AgentSpeed.Settings.Sources;

/// <summary>
/// The path column of a location row: one accessibility element named by <c>AutomationProperties.Name</c> (the folder, its paths,
/// 預設, the missing state; DESIGN 5), whose drawn parts stay out of the automation tree.
/// </summary>
internal sealed class LocationPathPanel : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(LocationPathPanel owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(LocationPathPanel);

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
