namespace GenHTTP.Modules.Git;

/// <summary>
/// A virtual repository that clients can push to.
/// </summary>
public interface IWritableGitRepository : IGitRepository
{

    /// <summary>
    /// Invoked when a client pushes changes to the repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server already parsed and verified the received objects, and
    /// checked that the updates are well-formed and based on the current
    /// state of the references. Implementations need to decide for every
    /// update whether it is accepted (<see cref="GitReferenceUpdate.Accept" />)
    /// or rejected (<see cref="GitReferenceUpdate.Reject(string)" />).
    /// Updates that are neither accepted nor rejected are reported as
    /// rejected to the client.
    /// </para>
    /// <para>
    /// After an update has been accepted, the repository must return
    /// the new commits and their trees exactly as received, i.e. persist
    /// <see cref="GitCommit.Data" /> and the files of the tree with their
    /// content and mode. As references might have been changed by someone
    /// else in the meantime, implementations should verify
    /// <see cref="GitReferenceUpdate.OldId" /> while holding a lock.
    /// </para>
    /// </remarks>
    /// <param name="push">The updates sent by the client</param>
    ValueTask PushAsync(GitPush push);

}
