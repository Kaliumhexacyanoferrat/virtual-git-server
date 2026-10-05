using GenHTTP.Modules.Git.Objects;
using GenHTTP.Modules.Git.Packs;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// What a client asked for in a fetch request, independent of the protocol version.
/// </summary>
internal sealed class FetchRequest
{

    /// <summary>
    /// The maximum number of haves or shallow commits accepted per request, as each
    /// of them may cause the repository to be queried.
    /// </summary>
    public const int MaximumObjects = 100_000;

    public List<GitObjectId> Wants { get; } = [];

    public List<GitObjectId> Haves { get; } = [];

    public HashSet<GitObjectId> ClientShallows { get; } = [];

    public int? Depth { get; set; }

    public bool DeepenRelative { get; set; }

    public long? DeepenSince { get; set; }

    public List<string> DeepenNot { get; } = [];

    public bool Done { get; set; }

    public bool NoProgress { get; set; }

    public bool IsDeepening => Depth != null || DeepenSince != null || DeepenNot.Count > 0;

}

/// <summary>
/// The result of planning a fetch: what to tell the client about its
/// shallow boundaries, and which objects to send.
/// </summary>
internal sealed class FetchPlan(List<GitObjectId> shallow, List<GitObjectId> unshallow, List<PackEntry> objects)
{

    /// <summary>
    /// Commits whose parents are not sent.
    /// </summary>
    public List<GitObjectId> Shallow { get; } = shallow;

    /// <summary>
    /// Commits the client marked as shallow whose parents are sent now.
    /// </summary>
    public List<GitObjectId> Unshallow { get; } = unshallow;

    public List<PackEntry> Objects { get; } = objects;

}

/// <summary>
/// Changes to the shallow boundaries of a client.
/// </summary>
/// <param name="Shallow">Commits whose parents will not be sent</param>
/// <param name="Unshallow">Commits the client marked as shallow whose parents will be sent</param>
/// <param name="Boundaries">All commits whose parents will not be sent, including the ones already shallow on the client</param>
internal sealed record ShallowUpdate(List<GitObjectId> Shallow, List<GitObjectId> Unshallow, HashSet<GitObjectId> Boundaries);

/// <summary>
/// Decides which objects need to be sent to a client.
/// </summary>
/// <remarks>
/// Starting from the commits the client wants, the history is traversed
/// until reaching commits the client already has. The tree objects and
/// blobs of the commits to be sent are included unless they are already
/// part of the commits the new ones are based on.
/// </remarks>
internal static class FetchPlanner
{

    /// <summary>
    /// Determines which of the commits the client has are known to the repository.
    /// </summary>
    public static async ValueTask<List<GitObjectId>> FindCommonAsync(RepositoryContext context, IEnumerable<GitObjectId> haves)
    {
        var result = new List<GitObjectId>();

        var seen = new HashSet<GitObjectId>();

        foreach (var have in haves)
        {
            if (!seen.Add(have))
            {
                continue;
            }

            // also the commits the history no longer has, which a client that
            // cloned before it was truncated still has - acknowledged, it
            // keeps saying so for the rest of the negotiation
            if (await context.GetCommitAsync(have) != null || (await context.GetTruncatedAsync()).Contains(have))
            {
                result.Add(have);
            }
        }

        return result;
    }

    public static async ValueTask<FetchPlan> PlanAsync(RepositoryContext context, FetchRequest request, IReadOnlyList<GitObjectId> common)
    {
        if (request.Wants.Count == 0)
        {
            throw new ProtocolException("no wants given");
        }

        foreach (var want in request.Wants)
        {
            if (await context.GetCommitAsync(want) == null)
            {
                throw new ProtocolException($"upload-pack: not our ref {want}");
            }
        }

        var owned = await GetOwnedAsync(context, request, common);

        var haves = request.Haves.ToHashSet();

        var update = await GetShallowUpdateAsync(context, request);

        var shallow = new List<GitObjectId>(update.Shallow);

        var boundaries = update.Boundaries;

        var starts = new List<GitObjectId>(request.Wants);

        var commits = new List<GitCommit>();
        var edges = new HashSet<GitObjectId>();

        foreach (var commit in update.Unshallow)
        {
            // the client has the commit (and its files), but not its parents
            starts.AddRange((await context.GetCommitAsync(commit))!.Parents);
            edges.Add(commit);
        }

        if (request.IsDeepening)
        {
            // the commits sent may not be connected to the ones the client has
            // (e.g. "fetch --depth 1"), but most of their files will be the same
            foreach (var commit in common.Take(8))
            {
                edges.Add(commit);
            }
        }

        var visited = new HashSet<GitObjectId>();

        var pending = new Queue<GitObjectId>(starts);

        while (pending.TryDequeue(out var id))
        {
            if (owned.Contains(id) || !visited.Add(id))
            {
                continue;
            }

            var commit = await context.GetCommitAsync(id);

            if (commit == null)
            {
                continue;
            }

            commits.Add(commit);

            if (boundaries.Contains(id))
            {
                continue;
            }

            var parents = await context.GetParentsAsync(commit);

            if (parents == null)
            {
                // the history of the repository ends here - unless the client
                // still has what came before (e.g. a clone made before old
                // versions were deleted), which it would refuse to cut off
                if (!request.ClientShallows.Contains(id) && !shallow.Contains(id) && !HasHistory(commit, haves, owned))
                {
                    shallow.Add(id);
                }

                continue;
            }

            foreach (var parent in parents)
            {
                if (owned.Contains(parent.Id))
                {
                    edges.Add(parent.Id);
                }
                else
                {
                    pending.Enqueue(parent.Id);
                }
            }
        }

        var excluded = new HashSet<GitObjectId>();

        foreach (var edge in edges)
        {
            var tree = await context.GetTreeAsync((await context.GetCommitAsync(edge))!);

            foreach (var treeObject in tree.Trees)
            {
                excluded.Add(treeObject.Id);
            }

            foreach (var blob in tree.Blobs)
            {
                excluded.Add(blob.Id);
            }
        }

        var objects = new List<PackEntry>();

        foreach (var commit in commits)
        {
            objects.Add(new PackEntry(GitObjectType.Commit, commit.Data));
        }

        var blobs = new List<PackEntry>();

        foreach (var commit in commits)
        {
            var tree = await context.GetTreeAsync(commit);

            foreach (var treeObject in tree.Trees)
            {
                if (excluded.Add(treeObject.Id))
                {
                    objects.Add(new PackEntry(GitObjectType.Tree, treeObject.Data));
                }
            }

            foreach (var blob in tree.Blobs)
            {
                if (excluded.Add(blob.Id))
                {
                    blobs.Add(new PackEntry(blob));
                }
            }
        }

        objects.AddRange(blobs);

        return new FetchPlan(shallow, update.Unshallow, objects);
    }

    /// <summary>
    /// Whether the client has the history before a commit whose parents the
    /// repository does not have (anymore): it said it has every one of them,
    /// or they are part of what it has with the common commits.
    /// </summary>
    /// <remarks>
    /// Announcing such a commit as shallow would cut off history the client
    /// already has, which git refuses ("shallow roots are not allowed to be
    /// updated") - so a clone made before old commits were deleted could not
    /// fetch anything newer.
    /// </remarks>
    private static bool HasHistory(GitCommit commit, HashSet<GitObjectId> haves, HashSet<GitObjectId> owned)
        => commit.Parents.Count > 0 && commit.Parents.All(p => haves.Contains(p) || owned.Contains(p));

    /// <summary>
    /// Collects the commits the client has: everything reachable from the
    /// common commits, cut at the shallow boundaries of the client.
    /// </summary>
    private static async ValueTask<HashSet<GitObjectId>> GetOwnedAsync(RepositoryContext context, FetchRequest request, IReadOnlyList<GitObjectId> common)
    {
        var owned = await context.GetAncestryAsync(common, request.ClientShallows);

        foreach (var shallow in request.ClientShallows)
        {
            if (await context.GetCommitAsync(shallow) != null)
            {
                owned.Add(shallow);
            }
        }

        // what the history no longer has but the client does: the commits
        // based on it are connected to what the client has
        var truncated = await context.GetTruncatedAsync();

        foreach (var commit in common)
        {
            if (truncated.Contains(commit))
            {
                owned.Add(commit);
            }
        }

        return owned;
    }

    /// <summary>
    /// Checks whether every commit the client wants is based on a commit the
    /// client has, which allows to stop the negotiation and to send a pack.
    /// </summary>
    public static async ValueTask<bool> IsReadyAsync(RepositoryContext context, FetchRequest request, IReadOnlyList<GitObjectId> common)
    {
        if (common.Count == 0)
        {
            return false;
        }

        var owned = await GetOwnedAsync(context, request, common);

        var connected = new HashSet<GitObjectId>();

        foreach (var want in request.Wants)
        {
            var found = false;

            var visited = new HashSet<GitObjectId>();

            var pending = new Stack<GitObjectId>();

            pending.Push(want);

            while (!found && pending.TryPop(out var id))
            {
                if (owned.Contains(id) || connected.Contains(id))
                {
                    found = true;
                    break;
                }

                if (!visited.Add(id))
                {
                    continue;
                }

                var commit = await context.GetCommitAsync(id);

                if (commit != null)
                {
                    foreach (var parent in commit.Parents)
                    {
                        pending.Push(parent);
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            // allows wants based on other wants to stop early
            connected.Add(want);
        }

        return true;
    }

    /// <summary>
    /// Computes the changes to the shallow boundaries of the client
    /// caused by a request to deepen (or shorten) its history.
    /// </summary>
    public static async ValueTask<ShallowUpdate> GetShallowUpdateAsync(RepositoryContext context, FetchRequest request)
    {
        var shallow = new List<GitObjectId>();
        var unshallow = new List<GitObjectId>();

        if (!request.IsDeepening)
        {
            return new ShallowUpdate(shallow, unshallow, []);
        }

        var (region, boundaries) = await GetRegionAsync(context, request);

        foreach (var commit in boundaries)
        {
            if (!request.ClientShallows.Contains(commit))
            {
                shallow.Add(commit);
            }
        }

        // "fetch --unshallow" asks for everything, including the history
        // behind shallow commits not reachable from the wanted commits
        var infinite = request.Depth == int.MaxValue && !request.DeepenRelative && request.DeepenSince == null && request.DeepenNot.Count == 0;

        foreach (var commit in request.ClientShallows)
        {
            if (region.Contains(commit) && !boundaries.Contains(commit))
            {
                unshallow.Add(commit);
            }
            else if (infinite && !region.Contains(commit))
            {
                var known = await context.GetCommitAsync(commit);

                if (known != null && known.Parents.Count > 0 && await context.GetParentsAsync(known) != null)
                {
                    unshallow.Add(commit);
                }
            }
        }

        return new ShallowUpdate(shallow, unshallow, boundaries);
    }

    /// <summary>
    /// Computes the commits within the requested depth, time range or
    /// exclusions, and the commits at the border of this region, whose
    /// parents will not be sent.
    /// </summary>
    private static async ValueTask<(HashSet<GitObjectId> Region, HashSet<GitObjectId> Boundaries)> GetRegionAsync(RepositoryContext context, FetchRequest request)
    {
        HashSet<GitObjectId>? excluded = null;

        if (request.DeepenNot.Count > 0)
        {
            var revisions = new List<GitObjectId>();

            foreach (var revision in request.DeepenNot)
            {
                revisions.Add(await ResolveRevisionAsync(context, revision));
            }

            excluded = await context.GetAncestryAsync(revisions);
        }

        bool IsIncluded(GitCommit commit) => (request.DeepenSince == null || commit.Committer.When.ToUnixTimeSeconds() >= request.DeepenSince)
                                             && (excluded == null || !excluded.Contains(commit.Id));

        List<GitObjectId> starts;

        int? limit = request.Depth;

        if (request.DeepenRelative)
        {
            if (limit == null)
            {
                throw new ProtocolException("deepen-relative requires a depth");
            }

            // deepen relative to the current shallow boundary of the client
            starts = [];

            foreach (var commit in request.ClientShallows)
            {
                if (await context.GetCommitAsync(commit) != null)
                {
                    starts.Add(commit);
                }
            }

            limit = limit == int.MaxValue ? limit : limit + 1;
        }
        else
        {
            starts = request.Wants;
        }

        var region = new HashSet<GitObjectId>();
        var boundaries = new HashSet<GitObjectId>();

        var pending = new Queue<(GitObjectId Id, int Depth)>();

        foreach (var start in starts)
        {
            pending.Enqueue((start, 1));
        }

        while (pending.TryDequeue(out var current))
        {
            if (region.Contains(current.Id))
            {
                continue;
            }

            var commit = await context.GetCommitAsync(current.Id);

            if (commit == null || !IsIncluded(commit))
            {
                continue;
            }

            region.Add(commit.Id);

            if (commit.Parents.Count == 0)
            {
                continue;
            }

            if (limit != null && current.Depth >= limit)
            {
                boundaries.Add(commit.Id);
                continue;
            }

            var parents = await context.GetParentsAsync(commit);

            if (parents == null || parents.Any(p => !IsIncluded(p)))
            {
                boundaries.Add(commit.Id);
                continue;
            }

            foreach (var parent in parents)
            {
                pending.Enqueue((parent.Id, current.Depth + 1));
            }
        }

        if (region.Count == 0)
        {
            throw new ProtocolException("no commits selected for shallow requests");
        }

        return (region, boundaries);
    }

    private static async ValueTask<GitObjectId> ResolveRevisionAsync(RepositoryContext context, string revision)
    {
        if (GitObjectId.TryParse(revision, out var id))
        {
            return id;
        }

        var references = await context.GetReferencesAsync();

        var reference = references.Get(revision) ?? references.Get(ReferenceNames.Branches + revision) ?? references.Get(ReferenceNames.Tags + revision);

        return reference?.Target ?? throw new ProtocolException($"unknown revision '{revision}' given for deepen-not");
    }

}
