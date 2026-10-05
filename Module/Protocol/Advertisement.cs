namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Generates the initial response of the server, listing its
/// references (protocol v0 and v1) or its capabilities (protocol v2).
/// </summary>
internal static class Advertisement
{
    private static readonly string ZeroId = GitObjectId.Zero.ToString();

    public static async ValueTask<PktLineWriter> UploadPackAsync(RepositoryContext context, GitServerOptions options, int version)
    {
        var writer = new PktLineWriter();

        if (version == 2)
        {
            writer.Line("version 2")
                  .Line($"agent={options.Agent}")
                  .Line("ls-refs=unborn")
                  .Line("fetch=shallow")
                  .Line("object-format=sha1")
                  .Flush();

            return writer;
        }

        writer.Line("# service=git-upload-pack").Flush();

        if (version == 1)
        {
            writer.Line("version 1");
        }

        var references = await context.GetReferencesAsync();

        var head = references.GetHead();

        var capabilities = "multi_ack_detailed side-band side-band-64k shallow deepen-since deepen-not deepen-relative no-progress allow-tip-sha1-in-want allow-reachable-sha1-in-want object-format=sha1";

        if (head != null)
        {
            capabilities += $" symref=HEAD:{head.Name}";
        }

        capabilities += $" agent={options.Agent}";

        var lines = new List<(GitObjectId Id, string Name)>();

        if (head != null)
        {
            lines.Add((head.Target, "HEAD"));
        }

        foreach (var reference in references)
        {
            lines.Add((reference.Target, reference.Name));
        }

        WriteReferences(writer, lines, capabilities);

        // the shallow boundaries are only announced here in v0, so
        // the client knows that the history ends at these commits
        foreach (var shallow in await context.FindShallowBoundariesAsync(lines.Select(l => l.Id)))
        {
            writer.Line($"shallow {shallow}");
        }

        writer.Flush();

        return writer;
    }

    public static async ValueTask<PktLineWriter> ReceivePackAsync(RepositoryContext context, GitServerOptions options, int version)
    {
        var writer = new PktLineWriter();

        writer.Line("# service=git-receive-pack").Flush();

        if (version == 1)
        {
            writer.Line("version 1");
        }

        var references = await context.GetReferencesAsync();

        var capabilities = $"report-status delete-refs side-band-64k quiet no-thin ofs-delta push-options object-format=sha1 agent={options.Agent}";

        WriteReferences(writer, references.Select(r => (r.Target, r.Name)).ToList(), capabilities);

        writer.Flush();

        return writer;
    }

    private static void WriteReferences(PktLineWriter writer, List<(GitObjectId Id, string Name)> references, string capabilities)
    {
        if (references.Count == 0)
        {
            writer.Line($"{ZeroId} capabilities^{{}}\0{capabilities}");
            return;
        }

        for (var i = 0; i < references.Count; i++)
        {
            var (id, name) = references[i];

            writer.Line(i == 0 ? $"{id} {name}\0{capabilities}" : $"{id} {name}");
        }
    }

}
