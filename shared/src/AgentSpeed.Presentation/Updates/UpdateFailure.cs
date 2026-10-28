namespace AgentSpeed.Presentation.Updates;

/// <summary>Why a check or an install failed; each maps to one user-facing reason (UPDATES section 4).</summary>
public enum UpdateFailure
{
    Network,
    RateLimited,
    Checksum,
    Permission,
    Invalid,
}

public sealed class UpdateException : Exception
{
    public UpdateException()
        : this(UpdateFailure.Invalid, "The update failed.")
    {
    }

    public UpdateException(string message)
        : this(UpdateFailure.Invalid, message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : this(UpdateFailure.Invalid, message, innerException)
    {
    }

    public UpdateException(UpdateFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public UpdateFailure Failure { get; }
}
