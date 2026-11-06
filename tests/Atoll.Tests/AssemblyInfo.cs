// WPF state is process-wide: one Application per process, and several tests replace its resources.
// Parallel test classes raced on that state and hung CI (v1.0.1). The suite runs in seconds serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
