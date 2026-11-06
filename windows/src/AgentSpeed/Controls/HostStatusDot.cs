using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using AgentSpeed.Presentation.Remote;

namespace AgentSpeed.Controls;

/// <summary>
/// A host's 8 DIP status dot (DESIGN 13): green connected, a breathing gray dot while connecting (resting half lit under
/// reduced motion), orange retrying, red when it needs the user, a ring while off. Decorative: the row's text always says
/// what it means. The look lives in Styles.xaml.
/// </summary>
internal sealed class HostStatusDot : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(RemoteDotKind), typeof(HostStatusDot), new PropertyMetadata(RemoteDotKind.Connected));

    public static readonly DependencyProperty ReduceMotionProperty = DependencyProperty.Register(
        nameof(ReduceMotion), typeof(bool), typeof(HostStatusDot), new PropertyMetadata(false));

    static HostStatusDot()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HostStatusDot), new FrameworkPropertyMetadata(typeof(HostStatusDot)));
        FocusableProperty.OverrideMetadata(typeof(HostStatusDot), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(HostStatusDot), new FrameworkPropertyMetadata(false));
    }

    public RemoteDotKind Kind
    {
        get => (RemoteDotKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool ReduceMotion
    {
        get => (bool)GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(HostStatusDot owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
