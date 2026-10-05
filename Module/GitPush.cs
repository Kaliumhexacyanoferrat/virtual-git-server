namespace GenHTTP.Modules.Git;

/// <summary>
/// The changes sent by a client with a single <c>git push</c>.
/// </summary>
public sealed class GitPush
{
    private readonly Func<string, ValueTask> _messenger;

    #region Get-/Setters

    /// <summary>
    /// The references the client would like to create, move or delete.
    /// </summary>
    public IReadOnlyList<GitReferenceUpdate> Updates { get; }

    /// <summary>
    /// The options passed by the client (<c>git push -o deploy</c>).
    /// </summary>
    public IReadOnlyList<string> Options { get; }

    #endregion

    #region Initialization

    internal GitPush(IReadOnlyList<GitReferenceUpdate> updates, IReadOnlyList<string> options, Func<string, ValueTask> messenger)
    {
        Updates = updates;
        Options = options;

        _messenger = messenger;
    }

    #endregion

    #region Functionality

    /// <summary>
    /// Sends a message to the client, which will be shown as
    /// "remote: ..." in the output of <c>git push</c>.
    /// </summary>
    /// <remarks>
    /// Messages are sent immediately, so they can be used to report
    /// progress of long-running operations (e.g. a build triggered by
    /// the push). Clients without support for side bands will not show them.
    /// </remarks>
    /// <param name="message">The message to be sent (may span multiple lines)</param>
    public ValueTask MessageAsync(string message) => _messenger(message);

    #endregion

}
