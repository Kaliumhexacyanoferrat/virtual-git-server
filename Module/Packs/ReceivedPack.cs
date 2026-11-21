using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// An object received with a pack.
/// </summary>
internal sealed class ReceivedObject
{
    internal const int OffsetDelta = 6;

    internal const int ReferenceDelta = 7;

    #region Get-/Setters

    /// <summary>
    /// The position of the object within the pack.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// The position of the compressed data within the buffer holding the pack.
    /// </summary>
    public long DataOffset { get; }

    public int CompressedLength { get; set; }

    /// <summary>
    /// The type of the entry within the pack (an object type or a kind of delta).
    /// </summary>
    public int Kind { get; }

    /// <summary>
    /// The size of the data stored in the pack (the object or the delta).
    /// </summary>
    public long Size { get; }

    public long BaseOffset { get; }

    public GitObjectId BaseId { get; }

    public bool IsDelta => Kind is OffsetDelta or ReferenceDelta;

    /// <summary>
    /// The object a delta needs to be applied to.
    /// </summary>
    public ReceivedObject? Base { get; set; }

    /// <summary>
    /// Whether other objects are stored as deltas against this one.
    /// </summary>
    public bool IsBase { get; set; }

    public GitObjectType Type { get; set; }

    public GitObjectId Id { get; set; }

    public int Depth { get; set; }

    public bool Resolved { get; set; }

    /// <summary>
    /// The content of the object, kept in memory for commits and trees.
    /// </summary>
    public byte[]? Data { get; set; }

    #endregion

    #region Initialization

    public ReceivedObject(long offset, long dataOffset, int kind, long size, long baseOffset, GitObjectId baseId)
    {
        Offset = offset;
        DataOffset = dataOffset;
        Kind = kind;
        Size = size;
        BaseOffset = baseOffset;
        BaseId = baseId;
    }

    #endregion

}

/// <summary>
/// The objects received with a push, reading their content from the
/// buffer holding the pack when requested.
/// </summary>
/// <remarks>
/// The content of objects can only be read until the pack is disposed,
/// which happens after the push has been processed.
/// </remarks>
internal sealed class ReceivedPack : IDisposable
{
    private readonly SpooledBuffer? _buffer;

    private readonly long _maximumObjectSize;

    private readonly Dictionary<GitObjectId, ReceivedObject> _objects = new();

    private readonly BaseCache _cache;

    private readonly ScratchBuffer _compressed = new();

    private readonly ScratchBuffer _instructions = new();

    private readonly Lock _sync = new();

    private bool _disposed;

    #region Get-/Setters

    public static ReceivedPack Empty => new(null, 0, 0);

    public int Count => _objects.Count;

    public IReadOnlyCollection<GitObjectId> Ids => _objects.Keys;

    #endregion

    #region Initialization

    internal ReceivedPack(SpooledBuffer? buffer, long maximumObjectSize, long cacheSize)
    {
        _buffer = buffer;
        _maximumObjectSize = maximumObjectSize;
        _cache = new BaseCache(cacheSize);
    }

    internal void Add(ReceivedObject obj) => _objects.TryAdd(obj.Id, obj);

    #endregion

    #region Functionality

    public ReceivedObject? Get(GitObjectId id) => _objects.GetValueOrDefault(id);

    /// <summary>
    /// Reads the content of the given object.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the push has already been processed</exception>
    public byte[] Read(ReceivedObject obj)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new InvalidOperationException("The content of files received with a push can only be read while the push is processed, so repositories need to read it within IWritableGitRepository.PushAsync()");
            }

            return Load(obj);
        }
    }

    /// <summary>
    /// Reconstructs the given object, applying the chain of deltas leading to it.
    /// </summary>
    internal byte[] Load(ReceivedObject target)
    {
        lock (_sync)
        {
            var chain = new List<ReceivedObject>();

            var current = target;

            byte[] data;

            while (true)
            {
                if (current.Data != null)
                {
                    data = current.Data;
                    break;
                }

                if (_cache.TryGet(current, out var cached))
                {
                    data = cached;
                    break;
                }

                if (!current.IsDelta)
                {
                    data = new byte[current.Size];

                    Inflate(current, data);
                    Remember(current, data);

                    break;
                }

                chain.Add(current);

                current = current.Base ?? throw new InvalidOperationException($"The base of the delta at offset {current.Offset} has not been resolved");
            }

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var delta = chain[i];

                var instructions = _instructions.Get(delta.Size).AsSpan(0, (int)delta.Size);

                Inflate(delta, instructions);

                data = DeltaApplier.Apply(data, instructions, _maximumObjectSize);

                Remember(delta, data);
            }

            return data;
        }
    }

    /// <summary>
    /// Keeps the content of the given object in memory if other objects are
    /// stored as deltas against it.
    /// </summary>
    internal void Remember(ReceivedObject obj, byte[] data)
    {
        if (obj.IsBase && obj.Data == null)
        {
            _cache.Add(obj, data);
        }
    }

    private void Inflate(ReceivedObject obj, Span<byte> destination)
    {
        var buffer = _buffer ?? throw new InvalidOperationException("The pack does not hold any data");

        try
        {
            var source = _compressed.Get(obj.CompressedLength).AsSpan(0, obj.CompressedLength);

            if (buffer.Read(obj.DataOffset, source) != source.Length)
            {
                throw new InvalidDataException("Invalid pack: unexpected end of data");
            }

            Zlib.Inflate(source, destination);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException("The content of files received with a push can only be read while the push is processed, so repositories need to read it within IWritableGitRepository.PushAsync()");
        }
    }

    /// <summary>
    /// Releases the memory held by this pack. Afterwards, the content of
    /// objects can no longer be read.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;

            _cache.Clear();

            _compressed.Dispose();
            _instructions.Dispose();

            foreach (var obj in _objects.Values)
            {
                obj.Data = null;
            }
        }
    }

    #endregion

    #region Cache

    /// <summary>
    /// Keeps the most recently used delta bases in memory.
    /// </summary>
    private sealed class BaseCache(long capacity)
    {
        private readonly LinkedList<(ReceivedObject Key, byte[] Data)> _order = new();

        private readonly Dictionary<ReceivedObject, LinkedListNode<(ReceivedObject Key, byte[] Data)>> _index = new();

        private long _size;

        public bool TryGet(ReceivedObject key, out byte[] data)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);

                data = node.Value.Data;
                return true;
            }

            data = [];
            return false;
        }

        public void Add(ReceivedObject key, byte[] data)
        {
            if (data.Length > capacity || _index.ContainsKey(key))
            {
                return;
            }

            _index.Add(key, _order.AddFirst((key, data)));

            _size += data.Length;

            while (_size > capacity && _order.Last != null)
            {
                var last = _order.Last.Value;

                _order.RemoveLast();
                _index.Remove(last.Key);

                _size -= last.Data.Length;
            }
        }

        public void Clear()
        {
            _order.Clear();
            _index.Clear();

            _size = 0;
        }

    }

    #endregion

}
