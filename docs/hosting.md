# Hosting

`GitServer.Create()` returns a builder that follows the conventions of all GenHTTP modules: configure it, add it to
your handler tree and optionally add concerns.

## A single repository

```csharp
var git = GitServer.Create()
                   .Repository(repository);
```

The repository is served at the path the handler is mounted at. Added to a layout as `repo`, clients clone it from
`https://example.com/repo`. The handler only answers the requests sent by git (`info/refs`, `git-upload-pack` and
`git-receive-pack`) and returns nothing for all other requests, so other handlers can serve content at the same path.

## Multiple repositories

```csharp
var git = GitServer.Create()
                   .Repositories(async (request, name) => await _store.FindRepositoryAsync(name));

var app = Layout.Create()
                .Add("git", git);

// git clone https://example.com/git/my-project.git
// git clone https://example.com/git/my-project
```

The function receives the first segment of the path below the handler, without a `.git` suffix. Return `null` for
unknown repositories, which results in a "not found" response.

## Resolving repositories per request

To decide based on the request (e.g. a key routed by another handler, or the authenticated user), pass a function
receiving the request:

```csharp
var git = GitServer.Create()
                   .Repository(request => ResolveAsync(request));
```

Return `null` to respond with "not found". To deny access, throw a `ProviderException` with the appropriate status
code. The function is invoked for every request, so a client performing a clone causes multiple invocations (one
for the references, one or more for the data).

## Authentication

Git sends credentials via HTTP basic authentication, so the authentication concerns of GenHTTP can be used:

```csharp
var git = GitServer.Create()
                   .Repository(repository)
                   .Add(BasicAuthentication.Create().Add("jane", "secret"));

// or with a custom check, returning the authenticated user (or null)
var git = GitServer.Create()
                   .Repository(repository)
                   .Add(BasicAuthentication.Create((user, password) => CheckAsync(user, password)));
```

Clients are challenged when they connect without credentials and ask the user or their credential helper for them.
The authenticated user is available to resolvers via `request.GetUser<IUser>()`. To
grant different permissions to different users (e.g. read-only access for some), resolve the repository per request
and return an instance that only implements `IGitRepository` for read-only users.

!!! tip "Secrets in URLs"
    Instead of credentials, a repository can also be addressed by a secret part of its URL (as with a private link).
    Keep in mind that git stores the URL in `.git/config` and that URLs show up in logs and shell histories.

## Limits

| Method                     | Default    | Description                                                                         |
|----------------------------|------------|-------------------------------------------------------------------------------------|
| `MaximumPushSize(bytes)`   | 128 MB     | The maximum size of a push. Pushes are processed in memory.                         |
| `MaximumRequestSize(bytes)`| 16 MB      | The maximum size of a fetch request, which lists commits the client wants and has.  |
| `ContentCache(bytes)`      | 64 MB      | How much file content is kept in memory per request to avoid reading files twice.   |

Requests exceeding a limit are answered with HTTP 413.

## Other settings

| Method                     | Default              | Description                                                         |
|----------------------------|----------------------|---------------------------------------------------------------------|
| `Agent(agent)`             | `genhttp-git/x.y.z`  | The agent announced to clients, for statistics and debugging only.  |
| `Compression(level)`       | `Fastest`            | How strongly objects sent to clients are compressed.                 |

## Engines and concerns

The handler works with all GenHTTP engines. Concerns such as compression or client caching added via `Defaults()` do
not interfere with git: responses are marked as not cacheable, and request bodies compressed by git are detected
regardless of whether a decompression concern already handled them.

## Logging

Errors are logged via the logging infrastructure of the server (`IServer.Logging`). Clients only receive generic
messages for unexpected errors, so check the log if a client reports "internal server error" or "the repository is
inconsistent".
