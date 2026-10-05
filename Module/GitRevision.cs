namespace GenHTTP.Modules.Git;

/// <summary>
/// A commit received with a push, along with its files.
/// </summary>
/// <param name="Commit">The commit as created by the client</param>
/// <param name="Tree">The files of the commit</param>
public sealed record GitRevision(GitCommit Commit, GitTree Tree);
