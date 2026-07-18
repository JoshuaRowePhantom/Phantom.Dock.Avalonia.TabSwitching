using Xunit;

// Serialize headless Avalonia tests (mirrors the product-wide policy from issue #1101): the stock
// Avalonia.Headless.XUnit harness dispatches every test on a single dispatch thread and Avalonia
// does not support concurrent execution against a shared application.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, DisableTestParallelization = true, MaxParallelThreads = 1)]
