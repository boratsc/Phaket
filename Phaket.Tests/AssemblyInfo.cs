using Xunit;

// Headless UI tests share global state (LocalizationService singleton, dispatcher);
// running classes in parallel made language assertions race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
