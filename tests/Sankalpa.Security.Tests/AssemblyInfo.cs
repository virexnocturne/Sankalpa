using Xunit;

// Disable parallel test execution within this assembly because security tests manipulate
// process-wide static state (e.g. SecurityHardener.IsServiceLockedDown, system mutexes).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
