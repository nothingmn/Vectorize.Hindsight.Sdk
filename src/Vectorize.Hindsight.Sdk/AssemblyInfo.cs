using System.Runtime.CompilerServices;

// The unit suite exercises internals deliberately: the retry timing policy, the
// jitter and time sources, and the pipeline's error mapping are all internal by
// design (they are not part of the public contract), but they are exactly the
// things that must be asserted against observed behaviour rather than left to a
// test that can only see the public surface.
[assembly: InternalsVisibleTo("Vectorize.Hindsight.Sdk.Tests")]
