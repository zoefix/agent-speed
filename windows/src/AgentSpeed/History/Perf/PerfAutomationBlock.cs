using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AgentSpeed.History.Perf;

/// <summary>
/// A block a screen reader reads as one text named by <c>AutomationProperties.Name</c>, which says everything its texts and
/// drawn meters show (DESIGN 12, <i>Accessibility</i>: meters are hidden, <c>–</c> speaks its tooltip).
/// </summary>
internal sealed class PerfAutomationBlock : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(PerfAutomationBlock owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(PerfAutomationBlock);

        protected override List<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
