using System.Diagnostics;
using System.Text;

namespace GenHTTP.Modules.Git.Tests.Infrastructure;

/// <summary>
/// The result of running a git command.
/// </summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{

    public bool Success => ExitCode == 0;

    public override string ToString() => $"exit code {ExitCode}\n--- stdout ---\n{Output}\n--- stderr ---\n{Error}";

}

/// <summary>
/// Runs the git command line client in an isolated environment, so
/// tests neither depend on nor change the configuration of the machine.
/// </summary>
public sealed class GitClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    #region Get-/Setters

    /// <summary>
    /// The directory all repositories of this client are created in.
    /// </summary>
    public string Root { get; }

    private string Home { get; }

    #endregion

    #region Initialization

    /// <param name="protocol">The protocol version the client uses for fetching</param>
    public GitClient(int protocol = 2)
    {
        Root = Path.Combine(Path.GetTempPath(), "genhttp-git-tests", Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, ".home");

        Directory.CreateDirectory(Home);

        File.WriteAllText(Path.Combine(Home, ".gitconfig"), $"""
            [user]
                name = Test User
                email = test@example.com
            [init]
                defaultBranch = main
            [protocol]
                version = {protocol}
            [transfer]
                fsckObjects = true
            [core]
                autocrlf = false
            [advice]
                detachedHead = false
            [commit]
                gpgsign = false
            [gc]
                auto = 0
            """);
    }

    #endregion

    #region Functionality

    /// <summary>
    /// The path of a directory below the root of this client.
    /// </summary>
    public string PathOf(string directory) => Path.Combine(Root, directory);

    /// <summary>
    /// Runs git and fails if the command does not succeed.
    /// </summary>
    public async Task<string> RunAsync(string? directory, params string[] arguments)
    {
        var result = await TryRunAsync(directory, arguments);

        if (!result.Success)
        {
            Assert.Fail($"git {string.Join(' ', arguments)} failed with {result}");
        }

        return result.Output;
    }

    /// <summary>
    /// Runs git, returning the result regardless of whether the command succeeded.
    /// </summary>
    public async Task<GitResult> TryRunAsync(string? directory, params string[] arguments) => await TryRunAsync(directory, null, arguments);

    public async Task<GitResult> TryRunAsync(string? directory, IDictionary<string, string>? environment, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory != null ? PathOf(directory) : Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["HOME"] = Home;
        info.Environment["XDG_CONFIG_HOME"] = Home;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_ASKPASS"] = "";
        info.Environment["LANG"] = "C";
        info.Environment["LC_ALL"] = "C";

        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_TRACE", StringComparison.Ordinal)).ToList())
        {
            info.Environment.Remove(key);
        }

        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start git");

        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var cancellation = new CancellationTokenSource(Timeout);

        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            Assert.Fail($"git {string.Join(' ', arguments)} did not complete within {Timeout}");
        }

        return new GitResult(process.ExitCode, await output, await error);
    }

    /// <summary>
    /// Writes a file within a working copy, creating directories as needed.
    /// </summary>
    public void Write(string directory, string path, string content)
    {
        var file = Path.Combine(PathOf(directory), path);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        File.WriteAllText(file, content);
    }

    public void Write(string directory, string path, byte[] content)
    {
        var file = Path.Combine(PathOf(directory), path);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        File.WriteAllBytes(file, content);
    }

    public string Read(string directory, string path) => File.ReadAllText(Path.Combine(PathOf(directory), path));

    public bool Exists(string directory, string path) => File.Exists(Path.Combine(PathOf(directory), path));

    /// <summary>
    /// Commits all changes of a working copy.
    /// </summary>
    public async Task CommitAllAsync(string directory, string message)
    {
        await RunAsync(directory, "add", "-A");
        await RunAsync(directory, "commit", "-q", "-m", message);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    #endregion

}
