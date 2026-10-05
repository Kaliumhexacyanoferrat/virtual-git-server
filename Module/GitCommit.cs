using System.Text;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// A git commit, i.e. a snapshot of the files of a repository along
/// with the information who created it, when and why.
/// </summary>
/// <remarks>
/// <para>
/// The id of a commit is the hash of its <see cref="Data" />, which
/// includes the ids of its tree and of its parents. Changing a single
/// byte (e.g. a time stamp or a line break in the message) results in
/// a different commit and rewrites the history for every client that
/// cloned the repository.
/// </para>
/// <para>
/// Providers should therefore create the commit for a change once
/// (see <see cref="Create" />), store <see cref="Data" /> and pass
/// the stored bytes to <see cref="Parse" /> whenever the commit is
/// requested - instead of creating it again from the same information.
/// Commits received with a push must be stored the same way, as the
/// client already uses their ids.
/// </para>
/// </remarks>
public sealed class GitCommit
{

    #region Get-/Setters

    /// <summary>
    /// The id of this commit.
    /// </summary>
    public GitObjectId Id { get; }

    /// <summary>
    /// The raw content of the commit object, which is what providers persist.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// The id of the tree holding the files of this commit.
    /// </summary>
    public GitObjectId Tree { get; }

    /// <summary>
    /// The ids of the commits this commit is based on. Empty for the
    /// first commit of a history, more than one for merge commits.
    /// </summary>
    public IReadOnlyList<GitObjectId> Parents { get; }

    /// <summary>
    /// Who wrote the change and when.
    /// </summary>
    public GitSignature Author { get; }

    /// <summary>
    /// Who created the commit and when.
    /// </summary>
    public GitSignature Committer { get; }

    /// <summary>
    /// The message describing the change.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// The first line of the message.
    /// </summary>
    public string Subject
    {
        get
        {
            var index = Message.IndexOf('\n');
            return (index < 0 ? Message : Message[..index]).TrimEnd('\r');
        }
    }

    #endregion

    #region Initialization

    private GitCommit(GitObjectId id, ReadOnlyMemory<byte> data, GitObjectId tree, IReadOnlyList<GitObjectId> parents, GitSignature author, GitSignature committer, string message)
    {
        Id = id;
        Data = data;
        Tree = tree;
        Parents = parents;
        Author = author;
        Committer = committer;
        Message = message;
    }

    /// <summary>
    /// Starts creating a new commit.
    /// </summary>
    public static GitCommitBuilder Create() => new();

    /// <summary>
    /// Restores a commit from its raw content as stored by the provider.
    /// </summary>
    /// <param name="data">The content of the commit object (see <see cref="Data" />)</param>
    /// <exception cref="FormatException">Thrown if the given data is not a valid commit</exception>
    public static GitCommit Parse(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;

        var end = span.IndexOf("\n\n"u8);

        var header = end < 0 ? span : span[..(end + 1)];

        var message = end < 0 ? string.Empty : Encoding.UTF8.GetString(span[(end + 2)..]);

        GitObjectId? tree = null;

        var parents = new List<GitObjectId>();

        GitSignature? author = null, committer = null;

        var position = 0;

        while (!header.IsEmpty)
        {
            var newline = header.IndexOf((byte)'\n');

            var line = newline < 0 ? header : header[..newline];

            header = newline < 0 ? [] : header[(newline + 1)..];

            if (line.IsEmpty || line[0] == ' ')
            {
                // continuation of a multi-line header (e.g. a signature)
                continue;
            }

            var space = line.IndexOf((byte)' ');

            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? [] : line[(space + 1)..];

            if (key.SequenceEqual("tree"u8))
            {
                if (position != 0 || !GitObjectId.TryParse(value, out var id))
                {
                    throw new FormatException("Malformed commit: invalid or misplaced tree");
                }

                tree = id;
                position = 1;
            }
            else if (key.SequenceEqual("parent"u8))
            {
                if (position != 1 || !GitObjectId.TryParse(value, out var id))
                {
                    throw new FormatException("Malformed commit: invalid or misplaced parent");
                }

                parents.Add(id);
            }
            else if (key.SequenceEqual("author"u8))
            {
                if (position != 1)
                {
                    throw new FormatException("Malformed commit: misplaced author");
                }

                author = GitSignature.Parse(value);
                position = 2;
            }
            else if (key.SequenceEqual("committer"u8))
            {
                if (position != 2)
                {
                    throw new FormatException("Malformed commit: misplaced committer");
                }

                committer = GitSignature.Parse(value);
                position = 3;
            }
            else if (position < 3)
            {
                throw new FormatException($"Malformed commit: unexpected header '{Encoding.UTF8.GetString(key)}'");
            }
        }

        if (tree == null || author == null || committer == null)
        {
            throw new FormatException("Malformed commit: missing tree, author or committer");
        }

        var commitId = ObjectHasher.Hash(GitObjectType.Commit, span);

        return new GitCommit(commitId, data, tree.Value, parents, author, committer, message);
    }

    #endregion

    #region Functionality

    public override string ToString() => $"{Id} {Subject}";

    #endregion

}
