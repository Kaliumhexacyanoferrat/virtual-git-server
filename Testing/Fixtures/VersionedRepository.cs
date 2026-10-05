namespace GenHTTP.Modules.Git.Tests.Fixtures;

/// <summary>
/// A content store modelled after GenHTTP Lambda: content is saved as
/// immutable, numbered versions, and changes are prepared in features
/// (drafts) based on a version before they are merged into a new version.
/// </summary>
/// <remarks>
/// <para>
/// Exposed via git as follows:
/// </para>
/// <list type="bullet">
/// <item>every version is a commit on "main" and tagged as "v{number}"</item>
/// <item>every feature is a branch based on the version it was created from</item>
/// <item>pushing commits to main creates a version per commit</item>
/// <item>pushing a branch creates or updates the feature with that name, deleting it removes the feature</item>
/// </list>
/// <para>
/// Commits are created once and stored as raw bytes, so that the history
/// never changes for clients, no matter whether a change was made via git
/// or via the platform itself.
/// </para>
/// </remarks>
public sealed class VersionedRepository : IWritableGitRepository
{
    private static readonly GitSignature Platform = new("Platform", "noreply@example.com", DateTimeOffset.UnixEpoch);

    private readonly Lock _lock = new();

    #region Domain model

    /// <summary>
    /// A commit as persisted by the platform: its raw bytes and the files it consists of.
    /// </summary>
    public sealed record StoredCommit(GitObjectId Id, byte[] Data, IReadOnlyDictionary<string, byte[]> Files);

    public sealed record Version(int Number, StoredCommit Commit, string Note);

    public sealed class Feature(string name, int baseVersion)
    {

        public string Name { get; } = name;

        public int BaseVersion { get; set; } = baseVersion;

        /// <summary>
        /// The commits of this feature since its base version, oldest first.
        /// </summary>
        public List<StoredCommit> Commits { get; } = [];

        public StoredCommit? Tip => Commits.Count > 0 ? Commits[^1] : null;

    }

    public List<Version> Versions { get; } = [];

    public Dictionary<string, Feature> Features { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How many versions are kept, older ones are deleted.
    /// </summary>
    public int Retention { get; set; } = int.MaxValue;

    public DateTimeOffset Now { get; set; } = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    #endregion

    #region Platform operations

    /// <summary>
    /// Saves a new version, e.g. via an editor.
    /// </summary>
    public async Task<Version> SaveVersionAsync(IReadOnlyDictionary<string, byte[]> files, string note)
    {
        var parent = Versions.LastOrDefault()?.Commit.Id;

        var commit = await CreateCommitAsync(files, note, parent);

        lock (_lock)
        {
            var version = new Version((Versions.LastOrDefault()?.Number ?? 0) + 1, commit, note);

            Versions.Add(version);
            ApplyRetention();

            return version;
        }
    }

    /// <summary>
    /// Creates a feature based on the given version.
    /// </summary>
    public Feature CreateFeature(string name, int baseVersion)
    {
        lock (_lock)
        {
            var feature = new Feature(name, baseVersion);

            Features.Add(name, feature);

            return feature;
        }
    }

    /// <summary>
    /// Changes the files of a feature, e.g. via an editor.
    /// </summary>
    public async Task UpdateFeatureAsync(string name, IReadOnlyDictionary<string, byte[]> files, string note)
    {
        var feature = Features[name];

        var parent = feature.Tip?.Id ?? GetVersion(feature.BaseVersion).Commit.Id;

        var commit = await CreateCommitAsync(files, note, parent);

        lock (_lock)
        {
            feature.Commits.Add(commit);
        }
    }

    /// <summary>
    /// Converts a feature into a new version (as a squash merge) and removes it.
    /// </summary>
    public async Task<Version> MergeFeatureAsync(string name, string note)
    {
        var feature = Features[name];

        if (feature.BaseVersion != Versions[^1].Number)
        {
            throw new InvalidOperationException("Only features based on the newest version can be merged");
        }

        var version = await SaveVersionAsync(feature.Tip?.Files ?? GetVersion(feature.BaseVersion).Commit.Files, note);

        lock (_lock)
        {
            Features.Remove(name);
        }

        return version;
    }

    public Version GetVersion(int number) => Versions.Single(v => v.Number == number);

    private async Task<StoredCommit> CreateCommitAsync(IReadOnlyDictionary<string, byte[]> files, string message, GitObjectId? parent)
    {
        var tree = ToTree(files);

        var builder = GitCommit.Create()
                               .Tree(await tree.ComputeIdAsync())
                               .Author(new GitSignature(Platform.Name, Platform.Email, Now))
                               .Message(message);

        if (parent != null)
        {
            builder.Parent(parent.Value);
        }

        Now = Now.AddMinutes(1);

        var commit = builder.Build();

        return new StoredCommit(commit.Id, commit.Data.ToArray(), files);
    }

    private void ApplyRetention()
    {
        while (Versions.Count > Retention)
        {
            Versions.RemoveAt(0);
        }
    }

    #endregion

    #region Git

    public ValueTask<GitReferences> GetReferencesAsync()
    {
        lock (_lock)
        {
            var references = new GitReferences().Head("main");

            if (Versions.Count > 0)
            {
                references.Branch("main", Versions[^1].Commit.Id);
            }

            foreach (var version in Versions)
            {
                references.Tag($"v{version.Number}", version.Commit.Id);
            }

            foreach (var feature in Features.Values)
            {
                var tip = feature.Tip?.Id ?? Versions.FirstOrDefault(v => v.Number == feature.BaseVersion)?.Commit.Id;

                if (tip != null)
                {
                    references.Branch(feature.Name, tip.Value);
                }
            }

            return new(references);
        }
    }

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
    {
        var stored = Find(id);

        return new(stored != null ? GitCommit.Parse(stored.Data) : null);
    }

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => new(ToTree(Find(commit.Id)!.Files));

    public async ValueTask PushAsync(GitPush push)
    {
        foreach (var update in push.Updates)
        {
            if (update.IsTag)
            {
                update.Reject("versions are tagged by the platform");
            }
            else if (update.Name == "refs/heads/main")
            {
                await PushToMainAsync(push, update);
            }
            else if (update.IsBranch)
            {
                await PushToFeatureAsync(update);
            }
            else
            {
                update.Reject("only branches can be pushed");
            }
        }
    }

    private async Task PushToMainAsync(GitPush push, GitReferenceUpdate update)
    {
        if (update.IsDelete)
        {
            update.Reject("main cannot be deleted");
            return;
        }

        if (!update.IsFastForward)
        {
            update.Reject("versions cannot be changed, pull and push again without --force");
            return;
        }

        if (update.Revisions.Any(r => r.Commit.Parents.Count > 1))
        {
            update.Reject("merge commits are not supported, please rebase");
            return;
        }

        var commits = new List<StoredCommit>();

        foreach (var revision in update.Revisions)
        {
            var files = await ReadFilesAsync(revision.Tree);

            if (files == null)
            {
                update.Reject("only regular files are supported");
                return;
            }

            commits.Add(new StoredCommit(revision.Commit.Id, revision.Commit.Data.ToArray(), files));
        }

        lock (_lock)
        {
            // someone else might have saved a version in the meantime
            if (Versions.Count > 0 && Versions[^1].Commit.Id != update.OldId)
            {
                update.Reject("a new version has been saved in the meantime, fetch first");
                return;
            }

            foreach (var commit in commits)
            {
                var number = (Versions.LastOrDefault()?.Number ?? 0) + 1;

                Versions.Add(new Version(number, commit, GitCommit.Parse(commit.Data).Subject));
            }

            ApplyRetention();
        }

        foreach (var version in Versions.TakeLast(commits.Count))
        {
            await push.MessageAsync($"Created version {version.Number}");
        }

        update.Accept();
    }

    private async Task PushToFeatureAsync(GitReferenceUpdate update)
    {
        var name = update.ShortName;

        if (update.IsDelete)
        {
            lock (_lock)
            {
                Features.Remove(name);
            }

            update.Accept();
            return;
        }

        // find the version the feature is based on by following the first parents
        var chain = new List<StoredCommit>();

        var revisions = update.Revisions.ToDictionary(r => r.Commit.Id);

        var current = update.NewId;

        Version? baseVersion = null;

        while (baseVersion == null)
        {
            baseVersion = Versions.FirstOrDefault(v => v.Commit.Id == current);

            if (baseVersion != null)
            {
                break;
            }

            GitCommit commit;
            IReadOnlyDictionary<string, byte[]>? files;

            if (revisions.TryGetValue(current, out var revision))
            {
                commit = revision.Commit;
                files = await ReadFilesAsync(revision.Tree);

                if (files == null)
                {
                    update.Reject("only regular files are supported");
                    return;
                }
            }
            else
            {
                var stored = Find(current);

                if (stored == null)
                {
                    update.Reject("features need to be based on a version");
                    return;
                }

                commit = GitCommit.Parse(stored.Data);
                files = stored.Files;
            }

            if (commit.Parents.Count != 1)
            {
                update.Reject("features need to be based on a version and must not contain merges");
                return;
            }

            chain.Insert(0, new StoredCommit(commit.Id, commit.Data.ToArray(), files));

            current = commit.Parents[0];
        }

        lock (_lock)
        {
            if (!Features.TryGetValue(name, out var feature))
            {
                feature = new Feature(name, baseVersion.Number);
                Features.Add(name, feature);
            }

            feature.BaseVersion = baseVersion.Number;

            feature.Commits.Clear();
            feature.Commits.AddRange(chain);
        }

        update.Accept();
    }

    private StoredCommit? Find(GitObjectId id)
    {
        lock (_lock)
        {
            return Versions.FirstOrDefault(v => v.Commit.Id == id)?.Commit
                ?? Features.Values.SelectMany(f => f.Commits).FirstOrDefault(c => c.Id == id);
        }
    }

    private static GitTree ToTree(IReadOnlyDictionary<string, byte[]> files)
    {
        var builder = GitTree.Create();

        foreach (var (path, content) in files)
        {
            builder.Add(path, content);
        }

        return builder.Build();
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>?> ReadFilesAsync(GitTree tree)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in tree)
        {
            if (file.Mode != GitFileMode.Regular)
            {
                return null;
            }

            result.Add(file.Path, (await file.ReadAsync()).ToArray());
        }

        return result;
    }

    #endregion

}
