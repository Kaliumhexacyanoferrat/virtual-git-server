using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// A reference a client would like to create, move or delete.
/// </summary>
public sealed class GitReferenceUpdate
{

    #region Get-/Setters

    /// <summary>
    /// The full name of the reference (e.g. "refs/heads/main").
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Whether the reference is a branch (located below "refs/heads/").
    /// </summary>
    public bool IsBranch => Name.StartsWith(ReferenceNames.Branches, StringComparison.Ordinal);

    /// <summary>
    /// Whether the reference is a tag (located below "refs/tags/").
    /// </summary>
    public bool IsTag => Name.StartsWith(ReferenceNames.Tags, StringComparison.Ordinal);

    /// <summary>
    /// The name of the reference without its prefix (e.g. "main" for "refs/heads/main").
    /// </summary>
    public string ShortName => IsBranch ? Name[ReferenceNames.Branches.Length..] : IsTag ? Name[ReferenceNames.Tags.Length..] : Name;

    /// <summary>
    /// The commit the reference pointed to when the client started
    /// the push, or <see cref="GitObjectId.Zero" /> if it is created.
    /// </summary>
    public GitObjectId OldId { get; }

    /// <summary>
    /// The commit the reference should point to, or <see cref="GitObjectId.Zero" />
    /// if it should be deleted.
    /// </summary>
    public GitObjectId NewId { get; }

    /// <summary>
    /// Whether the reference does not exist yet.
    /// </summary>
    public bool IsCreate => OldId.IsZero;

    /// <summary>
    /// Whether the reference should be deleted.
    /// </summary>
    public bool IsDelete => NewId.IsZero;

    /// <summary>
    /// Whether the reference is created or moved forward, i.e. the old
    /// commit is part of the history of the new one. False for deletions
    /// and for updates rewriting the history (<c>git push --force</c>).
    /// </summary>
    public bool IsFastForward { get; }

    /// <summary>
    /// The commit the reference should point to, or null if it should be deleted.
    /// </summary>
    /// <remarks>
    /// Either one of <see cref="Revisions" /> or a commit already known
    /// to the repository (e.g. when a branch is created for an existing commit).
    /// </remarks>
    public GitCommit? Commit { get; }

    /// <summary>
    /// The commits introduced by this update, i.e. the commits not yet
    /// known to the repository, ordered so that parents are listed before
    /// their children (for a linear history, the oldest commit comes first).
    /// </summary>
    public IReadOnlyList<GitRevision> Revisions { get; }

    /// <summary>
    /// Whether the repository accepted the update.
    /// </summary>
    public GitUpdateStatus Status { get; private set; }

    /// <summary>
    /// Why the update has been rejected, if so.
    /// </summary>
    public string? Reason { get; private set; }

    #endregion

    #region Initialization

    internal GitReferenceUpdate(string name, GitObjectId oldId, GitObjectId newId, bool isFastForward, GitCommit? commit, IReadOnlyList<GitRevision> revisions)
    {
        Name = name;
        OldId = oldId;
        NewId = newId;
        IsFastForward = isFastForward;
        Commit = commit;
        Revisions = revisions;
    }

    #endregion

    #region Functionality

    /// <summary>
    /// Marks the update as accepted, reporting success to the client.
    /// </summary>
    public void Accept()
    {
        Status = GitUpdateStatus.Accepted;
        Reason = null;
    }

    /// <summary>
    /// Marks the update as rejected, which will be reported to the client
    /// as "! [remote rejected] main -> main (reason)".
    /// </summary>
    /// <param name="reason">A short explanation, which will be reduced to a single line</param>
    public void Reject(string reason)
    {
        Status = GitUpdateStatus.Rejected;
        Reason = reason;
    }

    public override string ToString() => $"{OldId} -> {NewId} {Name} ({Status})";

    #endregion

}
