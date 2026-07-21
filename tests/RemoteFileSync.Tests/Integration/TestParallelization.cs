using Xunit;

// Serialise the whole RemoteFileSync.Tests assembly.
//
// Eleven test classes drive real loopback sockets, and every one allocates its port with the
// same bind-read-close-rebind GetFreePort helper. That is a TOCTOU: the OS can hand the freed
// ephemeral port to a second GetFreePort call between the close and the server's rebind, after
// which a client connects to the wrong server and fails at the handshake read
// (EndOfStreamException from ReadExactAsync). Under xUnit's default per-class parallelism the two
// racers can live in different test collections, so a [Collection] that only groups the five
// socket classes under Integration/ — the files this phase owns — cannot cover the other six
// under Network/, and the cross-folder race would survive.
//
// Disabling parallelization for the assembly closes the race by construction: no two socket tests
// ever run at once, so no concurrent binder can steal a just-freed port. The unit tests run
// serially too, which costs some wall-clock time, but a deterministically green suite is the
// deliverable and a probably-green one is not. Only this assembly is affected; ExecRFS.Tests is a
// separate assembly and keeps its own defaults.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
