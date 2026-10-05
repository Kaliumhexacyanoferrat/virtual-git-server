namespace GenHTTP.Modules.Git;

/// <summary>
/// Whether the repository accepted an update sent by a client.
/// </summary>
public enum GitUpdateStatus
{

    /// <summary>
    /// The repository did not decide yet.
    /// </summary>
    Pending,

    /// <summary>
    /// The update has been accepted and applied.
    /// </summary>
    Accepted,

    /// <summary>
    /// The update has been rejected.
    /// </summary>
    Rejected

}
