using System.Text;

using GenHTTP.Modules.Git.Objects;
using GenHTTP.Modules.Git.Packs;

using Microsoft.Extensions.Logging;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Answers the requests of clients pushing to a repository.
/// </summary>
/// <remarks>
/// Pushes are always handled with protocol v0, as git does not
/// implement pushing for protocol v2.
/// </remarks>
internal static class ReceivePack
{

    private sealed record Command(GitObjectId OldId, GitObjectId NewId, string Name)
    {

        public string? Error { get; set; }

        public GitReferenceUpdate? Update { get; set; }

    }

    public static Func<Stream, ValueTask> Handle(RepositoryContext context, IWritableGitRepository repository, GitServerOptions options, ReadOnlyMemory<byte> body, ILogger logger)
    {
        var reader = new PktLineReader(body);

        var commands = new List<Command>();

        var capabilities = new HashSet<string>(StringComparer.Ordinal);

        PktLine line;

        while ((line = reader.Read()).IsData)
        {
            var payload = line.Line;

            var nul = payload.IndexOf((byte)0);

            if (nul >= 0)
            {
                foreach (var capability in Encoding.UTF8.GetString(payload[(nul + 1)..]).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    capabilities.Add(capability);
                }

                payload = payload[..nul];
            }

            var text = Encoding.UTF8.GetString(payload);

            if (text.StartsWith("shallow ", StringComparison.Ordinal))
            {
                // sent by shallow clients - we verify the pushed history on our own
                continue;
            }

            var parts = text.Split(' ');

            if (parts.Length != 3 || !GitObjectId.TryParse(parts[0], out var oldId) || !GitObjectId.TryParse(parts[1], out var newId))
            {
                throw new ProtocolException($"invalid command '{text}'");
            }

            commands.Add(new Command(oldId, newId, parts[2]));
        }

        if (commands.Count == 0)
        {
            // probing request sent before large pushes
            return _ => ValueTask.CompletedTask;
        }

        if (line.Kind != PktLineKind.Flush)
        {
            throw new ProtocolException("expected a flush packet after the commands");
        }

        var pushOptions = new List<string>();

        if (capabilities.Contains("push-options"))
        {
            while ((line = reader.Read()).IsData)
            {
                pushOptions.Add(line.Text);
            }
        }

        var pack = reader.Remaining;

        var sideband = capabilities.Contains("side-band-64k");
        var quiet = capabilities.Contains("quiet");
        var report = capabilities.Contains("report-status");

        return async stream =>
        {
            async ValueTask SendMessageAsync(string message)
            {
                if (!sideband || quiet)
                {
                    return;
                }

                if (!message.EndsWith('\n'))
                {
                    message += '\n';
                }

                var writer = new PktLineWriter();

                writer.Band(2, Encoding.UTF8.GetBytes(message));

                await writer.CopyToAsync(stream);
                await stream.FlushAsync();
            }

            string? unpackError = null;

            try
            {
                await ProcessAsync(context, repository, options, commands, pushOptions, pack, SendMessageAsync, logger);
            }
            catch (InvalidDataException e)
            {
                unpackError = PktLineWriter.Sanitize(e.Message);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to process a push to a git repository");

                foreach (var command in commands)
                {
                    command.Error ??= "internal server error";
                }
            }

            var result = new PktLineWriter();

            if (report)
            {
                result.Line(unpackError != null ? $"unpack {unpackError}" : "unpack ok");

                foreach (var command in commands)
                {
                    if (unpackError != null)
                    {
                        result.Line($"ng {command.Name} unpacker error");
                    }
                    else if (command.Error != null)
                    {
                        result.Line($"ng {command.Name} {PktLineWriter.Sanitize(command.Error)}");
                    }
                    else
                    {
                        result.Line($"ok {command.Name}");
                    }
                }

                result.Flush();
            }

            if (sideband)
            {
                var wrapper = new PktLineWriter();

                if (result.Length > 0)
                {
                    wrapper.Band(1, result.ToArray());
                }

                wrapper.Flush();

                await wrapper.CopyToAsync(stream);
            }
            else
            {
                await result.CopyToAsync(stream);
            }
        };
    }

    private static async ValueTask ProcessAsync(RepositoryContext context, IWritableGitRepository repository, GitServerOptions options, List<Command> commands, List<string> pushOptions,
                                                ReadOnlyMemory<byte> pack, Func<string, ValueTask> messenger, ILogger logger)
    {
        var received = pack.IsEmpty ? new Dictionary<GitObjectId, ReceivedObject>() : PackParser.Parse(pack.Span, PackLimits.FromPushSize(options.MaximumPushSize));

        if (pack.IsEmpty && commands.Any(c => !c.NewId.IsZero))
        {
            throw new InvalidDataException("missing pack");
        }

        var objects = new PushedObjects(context, received);

        var references = await context.GetReferencesAsync();

        var names = new HashSet<string>(StringComparer.Ordinal);

        var updates = new List<GitReferenceUpdate>();

        foreach (var command in commands)
        {
            try
            {
                if (!names.Add(command.Name))
                {
                    throw new UpdateRejectedException("duplicate update of the same reference");
                }

                command.Update = await ValidateAsync(context, objects, references, command);

                updates.Add(command.Update);
            }
            catch (UpdateRejectedException e)
            {
                command.Error = e.Message;
            }
        }

        if (updates.Count > 0)
        {
            try
            {
                await repository.PushAsync(new GitPush(updates, pushOptions, messenger));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to apply a push to a git repository");

                foreach (var update in updates)
                {
                    if (update.Status == GitUpdateStatus.Pending)
                    {
                        update.Reject("internal server error");
                    }
                }
            }
        }

        foreach (var command in commands)
        {
            if (command.Update != null && command.Error == null)
            {
                command.Error = command.Update.Status switch
                {
                    GitUpdateStatus.Accepted => null,
                    GitUpdateStatus.Rejected => command.Update.Reason ?? "rejected",
                    _ => "not accepted by the repository"
                };
            }
        }
    }

    private static async ValueTask<GitReferenceUpdate> ValidateAsync(RepositoryContext context, PushedObjects objects, GitReferences references, Command command)
    {
        if (!ReferenceNames.IsValid(command.Name, out var reason))
        {
            throw new UpdateRejectedException($"invalid reference name: {reason}");
        }

        var current = references.Get(command.Name)?.Target ?? GitObjectId.Zero;

        if (command.OldId != current)
        {
            throw new UpdateRejectedException(current.IsZero ? "the reference does not exist (anymore)" : "the reference has been changed in the meantime, fetch first");
        }

        if (command.NewId.IsZero)
        {
            if (command.OldId.IsZero)
            {
                throw new UpdateRejectedException("the reference does not exist");
            }

            return new GitReferenceUpdate(command.Name, command.OldId, command.NewId, false, null, []);
        }

        var type = objects.GetReceivedType(command.NewId);

        if (type != null && type != GitObjectType.Commit)
        {
            throw new UpdateRejectedException(type == GitObjectType.Tag ? "annotated tags are not supported" : "references can only point to commits");
        }

        // collect the commits the repository does not know yet
        var created = new Dictionary<GitObjectId, GitCommit>();

        var existing = new HashSet<GitObjectId>();

        var pending = new Stack<GitObjectId>();

        pending.Push(command.NewId);

        while (pending.TryPop(out var id))
        {
            if (created.ContainsKey(id) || existing.Contains(id))
            {
                continue;
            }

            if (await context.GetCommitAsync(id) != null)
            {
                existing.Add(id);
                continue;
            }

            var commit = objects.GetReceivedCommit(id) ?? throw new UpdateRejectedException($"missing commit {id}");

            created.Add(id, commit);

            foreach (var parent in commit.Parents)
            {
                pending.Push(parent);
            }
        }

        var tip = created.GetValueOrDefault(command.NewId) ?? (await context.GetCommitAsync(command.NewId))!;

        var ordered = SortTopologically(command.NewId, created);

        var revisions = new List<GitRevision>(ordered.Count);

        foreach (var commit in ordered)
        {
            var hints = commit.Parents.Where(existing.Contains);

            revisions.Add(new GitRevision(commit, await objects.GetTreeAsync(commit, hints)));
        }

        var fastForward = command.OldId.IsZero || await IsAncestorAsync(context, created, command.OldId, command.NewId);

        return new GitReferenceUpdate(command.Name, command.OldId, command.NewId, fastForward, tip, revisions);
    }

    /// <summary>
    /// Orders the given commits so that parents come before their children.
    /// </summary>
    private static List<GitCommit> SortTopologically(GitObjectId tip, Dictionary<GitObjectId, GitCommit> commits)
    {
        var result = new List<GitCommit>(commits.Count);

        var visited = new HashSet<GitObjectId>();

        var stack = new Stack<(GitObjectId Id, bool Expanded)>();

        stack.Push((tip, false));

        while (stack.TryPop(out var current))
        {
            if (!commits.TryGetValue(current.Id, out var commit))
            {
                continue;
            }

            if (current.Expanded)
            {
                result.Add(commit);
                continue;
            }

            if (!visited.Add(current.Id))
            {
                continue;
            }

            stack.Push((current.Id, true));

            for (var i = commit.Parents.Count - 1; i >= 0; i--)
            {
                if (!visited.Contains(commit.Parents[i]))
                {
                    stack.Push((commit.Parents[i], false));
                }
            }
        }

        return result;
    }

    private static async ValueTask<bool> IsAncestorAsync(RepositoryContext context, Dictionary<GitObjectId, GitCommit> created, GitObjectId ancestor, GitObjectId descendant)
    {
        var visited = new HashSet<GitObjectId>();

        var pending = new Stack<GitObjectId>();

        pending.Push(descendant);

        while (pending.TryPop(out var id))
        {
            if (id == ancestor)
            {
                return true;
            }

            if (!visited.Add(id))
            {
                continue;
            }

            var commit = created.GetValueOrDefault(id) ?? await context.GetCommitAsync(id);

            if (commit != null)
            {
                foreach (var parent in commit.Parents)
                {
                    pending.Push(parent);
                }
            }
        }

        return false;
    }

}
