using Xunit;

// Each test creates its own temp SQLite DB via process-wide environment variables
// (see Infrastructure/TestAppFactory.cs), so collections must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
