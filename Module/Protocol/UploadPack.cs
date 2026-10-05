using System.Globalization;

using GenHTTP.Modules.Git.Packs;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Answers the requests of clients fetching from a repository
/// (<c>git clone</c>, <c>git fetch</c>, <c>git pull</c>, <c>git ls-remote</c>).
/// </summary>
/// <remarks>
/// Each request is answered completely from the repository, as HTTP
/// is stateless: in protocol v0 every request repeats the wants and
/// all haves the server acknowledged so far, in protocol v2 the client
/// sends the full state anyway.
/// </remarks>
internal static class UploadPack
{

    #region Protocol v2

    public static async ValueTask<Func<Stream, ValueTask>> HandleV2Async(RepositoryContext context, GitServerOptions options, ReadOnlyMemory<byte> body)
    {
        var reader = new PktLineReader(body);

        var first = reader.Read();

        if (!first.IsData)
        {
            // an empty request, telling the server that the client is done
            return _ => ValueTask.CompletedTask;
        }

        var text = first.Text;

        if (!text.StartsWith("command=", StringComparison.Ordinal))
        {
            throw new ProtocolException($"expected a command, got '{text}'");
        }

        var command = text["command=".Length..];

        PktLine line;

        while ((line = reader.Read()).IsData)
        {
            var capability = line.Text;

            if (capability.StartsWith("object-format=", StringComparison.Ordinal) && capability != "object-format=sha1")
            {
                throw new ProtocolException($"unsupported object format '{capability["object-format=".Length..]}'");
            }
        }

        var arguments = new List<string>();

        if (line.Kind == PktLineKind.Delimiter)
        {
            while ((line = reader.Read()).IsData)
            {
                arguments.Add(line.Text);
            }
        }

        if (line.Kind != PktLineKind.Flush)
        {
            throw new ProtocolException("expected a flush packet at the end of the request");
        }

        return command switch
        {
            "ls-refs" => await ListReferencesAsync(context, arguments),
            "fetch" => await FetchAsync(context, options, arguments),
            _ => throw new ProtocolException($"unknown command '{command}'")
        };
    }

    private static async ValueTask<Func<Stream, ValueTask>> ListReferencesAsync(RepositoryContext context, List<string> arguments)
    {
        bool symrefs = false, unborn = false;

        var prefixes = new List<string>();

        foreach (var argument in arguments)
        {
            if (argument == "symrefs")
            {
                symrefs = true;
            }
            else if (argument == "unborn")
            {
                unborn = true;
            }
            else if (argument.StartsWith("ref-prefix ", StringComparison.Ordinal))
            {
                prefixes.Add(argument["ref-prefix ".Length..]);
            }
            else if (argument != "peel")
            {
                throw new ProtocolException($"unexpected ls-refs argument '{argument}'");
            }
        }

        // as git does, ignore prefixes if there are too many of them to be checked efficiently
        if (prefixes.Count > 65)
        {
            prefixes.Clear();
        }

        bool Matches(string name) => prefixes.Count == 0 || prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

        var references = await context.GetReferencesAsync();

        var writer = new PktLineWriter();

        if (Matches("HEAD"))
        {
            var head = references.GetHead();

            var target = symrefs ? $" symref-target:{references.HeadTarget}" : string.Empty;

            if (head != null)
            {
                writer.Line($"{head.Target} HEAD{target}");
            }
            else if (unborn)
            {
                writer.Line($"unborn HEAD{target}");
            }
        }

        foreach (var reference in references)
        {
            if (Matches(reference.Name))
            {
                writer.Line($"{reference.Target} {reference.Name}");
            }
        }

        writer.Flush();

        return writer.CopyToAsync;
    }

    private static async ValueTask<Func<Stream, ValueTask>> FetchAsync(RepositoryContext context, GitServerOptions options, List<string> arguments)
    {
        var request = new FetchRequest();

        foreach (var argument in arguments)
        {
            ParseArgument(request, argument, true);
        }

        var writer = new PktLineWriter();

        var common = await FetchPlanner.FindCommonAsync(context, request.Haves);

        if (!request.Done)
        {
            writer.Line("acknowledgments");

            if (common.Count == 0)
            {
                // the client needs to tell us more about its history
                writer.Line("NAK").Flush();
                return writer.CopyToAsync;
            }

            foreach (var id in common)
            {
                writer.Line($"ACK {id}");
            }

            if (!await FetchPlanner.IsReadyAsync(context, request, common))
            {
                // some of the wanted commits are not connected to what the client has yet
                writer.Flush();
                return writer.CopyToAsync;
            }

            writer.Line("ready").Delimiter();
        }

        var plan = await FetchPlanner.PlanAsync(context, request, common);

        if (request.IsDeepening || plan.Shallow.Count > 0 || plan.Unshallow.Count > 0)
        {
            writer.Line("shallow-info");

            WriteShallowUpdate(writer, plan.Shallow, plan.Unshallow);

            writer.Delimiter();
        }

        writer.Line("packfile");

        return async stream =>
        {
            await writer.CopyToAsync(stream);

            await WritePackAsync(stream, plan, options, PktLineWriter.MaximumPacketSize);

            await stream.WriteAsync("0000"u8.ToArray());
        };
    }

    #endregion

    #region Protocol v0 / v1

    public static async ValueTask<Func<Stream, ValueTask>> HandleV0Async(RepositoryContext context, GitServerOptions options, ReadOnlyMemory<byte> body)
    {
        var reader = new PktLineReader(body);

        var request = new FetchRequest();

        var capabilities = new HashSet<string>(StringComparer.Ordinal);

        PktLine line;

        while ((line = reader.Read()).IsData)
        {
            var text = line.Text;

            if (text.StartsWith("want ", StringComparison.Ordinal) && request.Wants.Count == 0)
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 2)
                {
                    throw new ProtocolException($"invalid line '{text}'");
                }

                foreach (var capability in parts.Skip(2))
                {
                    capabilities.Add(capability);
                }

                text = $"want {parts[1]}";
            }

            ParseArgument(request, text, false);
        }

        if (line.Kind == PktLineKind.End && request.Wants.Count == 0)
        {
            // probing request or a client that is up to date
            return _ => ValueTask.CompletedTask;
        }

        if (line.Kind != PktLineKind.Flush)
        {
            throw new ProtocolException("expected a flush packet after the wants");
        }

        request.DeepenRelative = capabilities.Contains("deepen-relative");
        request.NoProgress = capabilities.Contains("no-progress");

        var detailed = capabilities.Contains("multi_ack_detailed");

        var maximumPacketSize = capabilities.Contains("side-band-64k") ? PktLineWriter.MaximumPacketSize : capabilities.Contains("side-band") ? 1000 : 0;

        while ((line = reader.Read()).Kind != PktLineKind.End)
        {
            if (!line.IsData)
            {
                continue;
            }

            var text = line.Text;

            if (text == "done")
            {
                request.Done = true;
                break;
            }

            if (!text.StartsWith("have ", StringComparison.Ordinal))
            {
                throw new ProtocolException($"unexpected line '{text}'");
            }

            ParseArgument(request, text, false);
        }

        var writer = new PktLineWriter();

        if (request.IsDeepening)
        {
            // stateless clients expect the shallow update with every response
            var update = await FetchPlanner.GetShallowUpdateAsync(context, request);

            WriteShallowUpdate(writer, update.Shallow, update.Unshallow);

            writer.Flush();
        }

        if (request.Haves.Count == 0 && !request.Done)
        {
            return writer.CopyToAsync;
        }

        var common = await FetchPlanner.FindCommonAsync(context, request.Haves);

        if (detailed)
        {
            foreach (var id in common)
            {
                writer.Line($"ACK {id} common");
            }

            if (!request.Done)
            {
                if (await FetchPlanner.IsReadyAsync(context, request, common))
                {
                    writer.Line($"ACK {common[^1]} ready");
                }

                writer.Line("NAK");
                return writer.CopyToAsync;
            }

            writer.Line(common.Count > 0 ? $"ACK {common[^1]}" : "NAK");
        }
        else
        {
            if (common.Count > 0)
            {
                writer.Line($"ACK {common[0]}");
            }
            else
            {
                writer.Line("NAK");
            }

            if (!request.Done)
            {
                return writer.CopyToAsync;
            }
        }

        var plan = await FetchPlanner.PlanAsync(context, request, common);

        return async stream =>
        {
            await writer.CopyToAsync(stream);

            await WritePackAsync(stream, plan, options, maximumPacketSize);

            if (maximumPacketSize > 0)
            {
                await stream.WriteAsync("0000"u8.ToArray());
            }
        };
    }

    #endregion

    #region Helpers

    private static void ParseArgument(FetchRequest request, string argument, bool v2)
    {
        var space = argument.IndexOf(' ');

        var key = space < 0 ? argument : argument[..space];
        var value = space < 0 ? string.Empty : argument[(space + 1)..];

        switch (key)
        {
            case "want":
                request.Wants.Add(ParseId(value));
                break;
            case "have":
                request.Haves.Add(ParseId(value));

                if (request.Haves.Count > FetchRequest.MaximumObjects)
                {
                    throw new ProtocolException("too many haves");
                }

                break;
            case "shallow":
                request.ClientShallows.Add(ParseId(value));

                if (request.ClientShallows.Count > FetchRequest.MaximumObjects)
                {
                    throw new ProtocolException("too many shallow commits");
                }

                break;
            case "deepen":
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth) || depth <= 0)
                    {
                        throw new ProtocolException($"invalid depth '{value}'");
                    }

                    request.Depth = depth;
                    break;
                }
            case "deepen-since":
                {
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var since))
                    {
                        throw new ProtocolException($"invalid timestamp '{value}'");
                    }

                    request.DeepenSince = since;
                    break;
                }
            case "deepen-not":
                request.DeepenNot.Add(value);
                break;
            case "deepen-relative" when v2:
                request.DeepenRelative = true;
                break;
            case "done" when v2:
                request.Done = true;
                break;
            case "no-progress" when v2:
                request.NoProgress = true;
                break;
            case "thin-pack" or "ofs-delta" or "include-tag" when v2:
                // we never send deltas and there are no annotated tags
                break;
            default:
                throw new ProtocolException($"unexpected argument '{argument}'");
        }
    }

    private static GitObjectId ParseId(string value)
    {
        if (!GitObjectId.TryParse(value, out var id))
        {
            throw new ProtocolException($"invalid object id '{value}'");
        }

        return id;
    }

    private static void WriteShallowUpdate(PktLineWriter writer, List<GitObjectId> shallow, List<GitObjectId> unshallow)
    {
        foreach (var id in shallow)
        {
            writer.Line($"shallow {id}");
        }

        foreach (var id in unshallow)
        {
            writer.Line($"unshallow {id}");
        }
    }

    private static async ValueTask WritePackAsync(Stream stream, FetchPlan plan, GitServerOptions options, int maximumPacketSize)
    {
        if (maximumPacketSize > 0)
        {
            await using var sideband = new SidebandStream(stream, maximumPacketSize);

            await PackWriter.WriteAsync(sideband, plan.Objects, options.Compression);

            await sideband.FlushAsync();
        }
        else
        {
            await PackWriter.WriteAsync(stream, plan.Objects, options.Compression);
        }
    }

    #endregion

}
