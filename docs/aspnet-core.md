# ASP.NET Core

The module is a regular GenHTTP handler, so it does not require a GenHTTP server. With the
[GenHTTP.Adapters.AspNetCore](https://www.nuget.org/packages/GenHTTP.Adapters.AspNetCore/) package, it can be
mapped to a path of an existing ASP.NET Core application and runs next to your controllers, Razor pages and
minimal APIs.

## Installation

```sh
dotnet add package GenHTTP.Modules.Git
dotnet add package GenHTTP.Adapters.AspNetCore
```

## Mapping a repository

The adapter adds a `Map` overload to `WebApplication` that accepts GenHTTP handlers:

```csharp
using GenHTTP.Adapters.AspNetCore;

using GenHTTP.Modules.Git;

var repository = new InMemoryGitRepository();

await repository.CommitAsync("main", GitTree.Create()
                                            .Add("README.md", "# Hello World")
                                            .Build(), "Initial commit");

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 128 * 1024 * 1024);

var app = builder.Build();

app.Map("/repo", GitServer.Create().Repository(repository));

app.MapGet("/", () => "Hello from ASP.NET Core");

await app.RunAsync();
```

```sh
# use the address your application listens on
git clone http://localhost:5000/repo hello
```

Requests below the mapped path are handled by the git server, all other requests continue through the ASP.NET Core
pipeline.

The builder returned by `GitServer.Create()` is configured exactly as described in [Hosting](hosting.md), so limits,
resolvers and concerns work the same way.

## Request size

Kestrel rejects request bodies larger than 30 MB by default, while the module accepts pushes up to 128 MB
(`MaximumPushSize`). Larger pushes then fail with HTTP 413 before they reach the module, so raise the Kestrel limit
to at least the configured push size, as shown above. If the application runs behind IIS or a reverse proxy, their
limits apply as well.

## Multiple repositories

Resolvers work the same way as with a GenHTTP server. The path segment below the mapped path selects the
repository:

```csharp
app.Map("/git", GitServer.Create()
                         .Repositories(async (request, name) => await store.FindRepositoryAsync(name)));

// git clone http://localhost:5000/git/my-project
// git clone http://localhost:5000/git/my-project.git
```

## Authentication

GenHTTP concerns can be added to the builder, so the [authentication](hosting.md#authentication) described for
GenHTTP servers also works within ASP.NET Core:

```sh
dotnet add package GenHTTP.Modules.Authentication
```

```csharp
using GenHTTP.Modules.Authentication;

app.Map("/repo", GitServer.Create()
                          .Repository(repository)
                          .Add(BasicAuthentication.Create().Add("jane", "secret")));
```

The handler is invoked by the adapter outside of the ASP.NET Core routing, so endpoint metadata such as
`RequireAuthorization()` does not apply to it. Use a GenHTTP concern or a middleware registered before the mapping
to protect the repository.
