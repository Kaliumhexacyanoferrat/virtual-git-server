# Protocol Support

The server implements the [smart HTTP protocol](https://git-scm.com/docs/gitprotocol-http) of git from scratch,
covering what clients need to clone, fetch and push. This page lists what is supported and the deliberate
limitations.

## Transport

| Feature                    | Support                                                                                |
|----------------------------|----------------------------------------------------------------------------------------|
| Smart HTTP                 | Supported                                                                              |
| Protocol v2                | Supported for fetching (`ls-refs`, `fetch`), the default since git 2.26                |
| Protocol v0 / v1           | Supported for fetching and pushing (git implements pushing for v0 only)                |
| Dumb HTTP                  | Not supported, requests are answered with HTTP 403                                     |
| Compressed requests        | Supported (git compresses larger fetch requests with gzip)                             |
| Chunked requests           | Supported (used by git for pushes larger than 1 MB)                                   |
| Object format              | SHA-1 only                                                                             |

## Fetching

| Feature                                         | Support                                                       |
|-------------------------------------------------|---------------------------------------------------------------|
| Clone, fetch, pull, ls-remote                   | Supported                                                     |
| Negotiation                                     | `multi_ack_detailed` (v0), acknowledgments and `ready` (v2)   |
| Side bands                                      | `side-band`, `side-band-64k` (v0), always used in v2          |
| Shallow clones (`--depth`)                      | Supported                                                     |
| `--shallow-since`, `--shallow-exclude`          | Supported                                                     |
| `--deepen`, `--unshallow`                       | Supported                                                     |
| Unborn HEAD                                     | Supported in v2, clients learn the default branch of empty repositories |
| Requesting commits by id                        | Supported for all commits the repository resolves             |
| Partial clones (`--filter`)                     | Not supported                                                 |
| Annotated tags (`include-tag`, peeled refs)     | Not supported                                                 |
| `wait-for-done`, `packfile-uris`, `bundle-uri`  | Not supported (clients do not depend on them)                 |

Packs sent to clients contain all objects in full, without deltas. Deltas are an optimization: clients store the
pack as received and compress their repository on their own (`git gc`). For the repositories this library is meant
for, computing deltas on every request would cost more than the bandwidth it saves.

## Pushing

| Feature                                 | Support                                                                |
|-----------------------------------------|------------------------------------------------------------------------|
| Create, update and delete branches      | Supported (`delete-refs`)                                              |
| Lightweight tags                        | Supported                                                              |
| Deltas within the pack                  | Supported (`ofs-delta` and reference deltas)                           |
| Thin packs                              | Disabled via `no-thin`, clients send self-contained packs              |
| Status reports                          | `report-status`, with messages for rejected updates                    |
| Messages                                | Sent via side band and shown as `remote: ...`                          |
| Push options (`git push -o`)            | Supported (`push-options`)                                             |
| Pushing from shallow clones             | Supported                                                              |
| Atomic pushes (`--atomic`)              | Not supported                                                          |
| Signed pushes (`--signed`)              | Not supported                                                          |
| Annotated tags                          | Rejected                                                               |
| Submodules                              | Rejected                                                               |

## Content

| Feature                      | Support                                                                    |
|------------------------------|----------------------------------------------------------------------------|
| Regular files                | Supported (mode `100644`)                                                  |
| Executable files             | Supported (mode `100755`)                                                  |
| Symbolic links               | Supported (mode `120000`)                                                  |
| Empty directories            | Not representable in git                                                   |
| Submodules                   | Not supported (mode `160000`)                                              |
| File names                   | Valid UTF-8, no `..`, `.git` (in any spelling), backslashes or NUL characters |
| Signed commits               | Supported, signatures are kept as part of the commit                       |

## Compatibility

The test suite runs every scenario against the installed git client with `transfer.fsckObjects` enabled and verifies
clones with `git fsck --strict`, using protocol v0 and v2. Other clients based on the same protocol (e.g. libgit2 or
JGit based tools) are expected to work as well, as only standard capabilities are announced.
