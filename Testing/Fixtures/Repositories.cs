namespace GenHTTP.Modules.Git.Tests.Fixtures;

public static class Repositories
{

    public static readonly DateTimeOffset Start = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Creates a repository with a linear history on main, one day per
    /// commit, and a tag for every commit ("v1", "v2", ...).
    /// </summary>
    public static async Task<InMemoryGitRepository> WithHistoryAsync(int commits, bool tags = true)
    {
        var repository = new InMemoryGitRepository();

        for (var i = 1; i <= commits; i++)
        {
            var tree = GitTree.Create()
                              .Add("README.md", $"# Version {i}\n")
                              .Add("src/Shared.cs", "// unchanged\n")
                              .Add($"src/Version{i}.cs", $"// added in {i}\n")
                              .Build();

            var author = new GitSignature("Test", "test@example.com", Start.AddDays(i));

            var commit = await repository.CommitAsync("main", tree, $"Version {i}", author);

            if (tags)
            {
                repository.SetReference($"refs/tags/v{i}", commit.Id);
            }
        }

        return repository;
    }

}

/// <summary>
/// Wraps a repository, counting how often it is asked for something.
/// </summary>
public sealed class CountingRepository(IGitRepository inner) : IGitRepository
{

    public int TreeRequests { get; private set; }

    public int CommitRequests { get; private set; }

    public ValueTask<GitReferences> GetReferencesAsync() => inner.GetReferencesAsync();

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
    {
        CommitRequests++;
        return inner.GetCommitAsync(id);
    }

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit)
    {
        TreeRequests++;
        return inner.GetTreeAsync(commit);
    }

}

/// <summary>
/// Serves only the newest commits of another repository, simulating
/// a repository that deleted older parts of its history.
/// </summary>
public sealed class TruncatedRepository(InMemoryGitRepository inner, int keep) : IGitRepository
{

    public ValueTask<GitReferences> GetReferencesAsync()
    {
        var result = new GitReferences().Head(inner.Head);

        var visible = GetVisible();

        foreach (var (name, target) in inner.References)
        {
            if (visible.Contains(target))
            {
                result.Add(name, target);
            }
        }

        return new(result);
    }

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => new(GetVisible().Contains(id) ? inner.GetRevision(id)?.Commit : null);

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => inner.GetTreeAsync(commit);

    private HashSet<GitObjectId> GetVisible()
    {
        var result = new HashSet<GitObjectId>();

        var current = inner.GetRevision(inner.References[inner.Head])?.Commit;

        while (current != null && result.Count < keep)
        {
            result.Add(current.Id);

            current = current.Parents.Count > 0 ? inner.GetRevision(current.Parents[0])?.Commit : null;
        }

        return result;
    }

}
