# Virtual Git Server

`GenHTTP.Modules.Git` serves **virtual git repositories** from a [GenHTTP](https://genhttp.org/) handler. Every
clone, fetch and push is answered from your own data model over the smart HTTP protocol, with no repository on disk
and no `git` binary.

```csharp
var git = GitServer.Create()
                   .Repository(new DocumentRepository());

await Host.Create()
          .Handler(git)
          .RunAsync();
```

```sh
git clone http://localhost:8080/ documents
```

## Why?

Git is the interface developers, CI pipelines and coding agents already know. If your application manages versioned
content (documents, configurations, small apps, snippets), offering it via git removes the need for custom sync
tools: users clone it, edit it with their favorite tools and push their changes back.

Hosting real repositories for this means keeping two sources of truth in sync. A virtual repository has a single
source of truth (your data model) and derives everything git needs from it on the fly:

- **Branches and tags** are whatever your model provides, e.g. one tag per version.
- **Commits** are created once from your data and stored as a few hundred bytes.
- **Files** are loaded from your storage only when a client actually needs them.
- **Pushes** are parsed, verified and handed to you as a list of commits with their files, so you decide what to
  accept.

## Features

| Area      | Support                                                                                                     |
|-----------|-------------------------------------------------------------------------------------------------------------|
| Transport | Smart HTTP, protocol v2 (`ls-refs`, `fetch`) and v0/v1 for fetching, v0 for pushing                           |
| Fetching  | Clones, incremental fetches, shallow clones (`--depth`, `--shallow-since`, `--shallow-exclude`, `--deepen`) |
| Pushing   | Creating, updating and deleting branches and tags, deltas, push options, `remote:` messages                 |
| History   | Truncated histories (e.g. deleted old versions) are served as shallow repositories                          |
| Files     | Regular files, executables and symbolic links, loaded on demand                                             |
| Hosting   | Single or multiple repositories, any GenHTTP engine, concerns such as authentication                        |

## Where to go next

<div class="grid cards" markdown>

- **[Getting Started](getting-started.md)**: serve your first repository in a few lines of code.
- **[How It Works](concepts.md)**: references, commits, trees and why commit ids must never change.
- **[Serving Content](repositories.md)**: implement `IGitRepository` for your data model.
- **[Accepting Pushes](pushing.md)**: implement `IWritableGitRepository` and validate what clients send.
- **[Versioned Content](examples/versioned-content.md)**: a complete example with versions, drafts and retention.
- **[Protocol Support](protocol.md)**: what git features are supported and what is not.

</div>
