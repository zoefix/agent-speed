using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AgentSpeed.Controls;

/// <summary>A symbol-font glyph next to text that already says what it means: hidden from UI Automation.</summary>
internal sealed class SymbolGlyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(SymbolGlyph owner) : TextBlockAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
