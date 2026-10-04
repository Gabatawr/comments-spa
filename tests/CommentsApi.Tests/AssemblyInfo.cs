using Xunit;

// Each test creates its own PostgreSQL database via process-wide environment variables
// (see Infrastructure/TestAppFactory.cs), so collections must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
