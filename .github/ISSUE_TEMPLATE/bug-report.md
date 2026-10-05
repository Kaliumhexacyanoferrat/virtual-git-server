---
name: Bug Report
about: Report a situation where the git server does not behave as expected.
title: ''
labels: bug
assignees: ''

---

When [steps to reproduce], [expected result]. Instead [actual result].

**Example**

```csharp
// add an expressive example to reproduce this issue (e.g. a test method)
var repository = new InMemoryGitRepository();

var server = GitServer.Create()
                      .Repository(repository);
```

**Client output**

Run the failing git command with `GIT_TRACE_PACKET=1 GIT_CURL_VERBOSE=1` and add the relevant output (remove any credentials).

**Environment**

- Version of GenHTTP.Modules.Git:
- Version of git (`git --version`):
- Protocol version (`git config protocol.version`):
