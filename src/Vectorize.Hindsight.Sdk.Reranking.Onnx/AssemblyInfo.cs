using System.Runtime.CompilerServices;

// The parity suite asserts the tokenizer against the reference implementation's own ids, and the
// diagnostics below (vocabulary size, unmapped-piece count, model-call count) are how it proves the
// mapping is exact and batching is actually happening. Both are internal by design — they are not
// part of the public contract — but they are exactly what must be asserted against observed
// behaviour rather than left to a test that can only see the public surface.
[assembly: InternalsVisibleTo("Vectorize.Hindsight.Sdk.Reranking.Tests")]
