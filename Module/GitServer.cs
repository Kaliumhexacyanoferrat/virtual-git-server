using GenHTTP.Modules.Git.Handler;

namespace GenHTTP.Modules.Git;

/// <summary>
/// Entry point to serve virtual git repositories.
/// </summary>
/// <example>
/// <code>
/// var git = GitServer.Create()
///                    .Repository(new MyRepository());
///
/// await Host.Create()
///           .Handler(git)
///           .RunAsync();
///
/// // git clone http://localhost:8080/
/// </code>
/// </example>
public static class GitServer
{

    /// <summary>
    /// Creates a handler serving git repositories, answering
    /// clones, fetches and pushes from the configured repository.
    /// </summary>
    public static GitServerBuilder Create() => new();

}
