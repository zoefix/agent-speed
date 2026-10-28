namespace AgentSpeed.Presentation.Updates;

/// <summary>
/// <c>idle → checking → upToDate | available | failed</c>, and for install
/// <c>downloading → verifying → installing → relaunching | failed</c> (UPDATES section 4).
/// </summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Verifying,
    Installing,
    Relaunching,
    CheckFailed,
    InstallFailed,
}

/// <param name="Release">The release being offered or installed.</param>
/// <param name="Progress">Download progress 0..1 while <see cref="UpdatePhase.Downloading"/>.</param>
public sealed record UpdateStatus(UpdatePhase Phase, ReleaseInfo? Release = null, double Progress = 0, UpdateFailure? Failure = null)
{
    public static UpdateStatus Idle { get; } = new(UpdatePhase.Idle);

    public bool IsInstalling => Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Installing or UpdatePhase.Relaunching;

    public bool IsBusy => Phase == UpdatePhase.Checking || IsInstalling;

    public bool IsFailed => Phase is UpdatePhase.CheckFailed or UpdatePhase.InstallFailed;
}
