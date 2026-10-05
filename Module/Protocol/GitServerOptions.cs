using System.IO.Compression;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Settings applied to all protocol operations of a server.
/// </summary>
internal sealed record GitServerOptions(string Agent, long MaximumPushSize, int MaximumFilesPerCommit, long MaximumRequestSize, long ContentCacheSize, CompressionLevel Compression);
