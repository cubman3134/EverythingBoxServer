using Xunit;

// Structural backstop for the hazard documented on AddonServerCollection: any test class
// that touches the process-wide EBS_* environment variables must not run concurrently with
// another one. [Collection(AddonServerCollection.Name)] + DisableParallelization on that one
// collection only serialises that collection's own members — a different collection (or a
// class with no [Collection] attribute at all) would still run concurrently against it. This
// assembly-level attribute disables parallelization for the WHOLE suite, so a test class that
// forgets to join AddonServerCollection still cannot race the env-var writes.
//
// The suite is small and fast, so serialising all of it costs very little against the
// correctness this buys.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
