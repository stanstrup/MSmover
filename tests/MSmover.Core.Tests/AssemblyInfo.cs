using Xunit;

// TempWorkspace redirects AppPaths.Root, which is process-wide static state. With xUnit's default
// per-class parallelism two workspaces race and some tests end up writing to the real
// %APPDATA%\MSmover. Running the assembly serially keeps each test's state fully isolated.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
