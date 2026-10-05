using GenHTTP.Engine.Internal;

using GenHTTP.Modules.Git;
using GenHTTP.Modules.Practices;

// a repository that only exists in memory - implement IGitRepository
// or IWritableGitRepository to serve your own data instead
var repository = new InMemoryGitRepository();

var files = GitTree.Create()
                   .Add("README.md", "# Hello from GenHTTP\n\nThis repository is served from memory, there is no git repository on disk.\n")
                   .Add("src/Program.cs", "Console.WriteLine(\"Hello World\");\n")
                   .Build();

await repository.CommitAsync("main", files, "Initial commit");

var git = GitServer.Create()
                   .Repository(repository);

Console.WriteLine("Serving a virtual git repository, try:");
Console.WriteLine();
Console.WriteLine("  git clone http://localhost:8080/ playground");
Console.WriteLine();

await Host.Create()
          .Handler(git)
          .Defaults()
          .Development()
          .RunAsync();
