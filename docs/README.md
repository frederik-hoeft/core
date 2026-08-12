# Fho.Core Documentation

This directory documents the architecture and concurrency contracts of Fho.Core. The root [README](../README.md) remains the user-facing project summary; the documents here are intended for contributors who need to understand how the major pieces fit together and which invariants matter when changing them.

## Documentation map

- [Architecture overview](architecture.md) describes the assembly boundaries, major subsystems, dependency direction, and shared design principles.
- [Synchronization](synchronization.md) covers `AsyncLock`, `AlphaBetaLockSlim`, scoped `ReaderWriterLockSlim` ownership, disposal, cancellation, and ownership semantics.
- [Atomic primitives](atomic-primitives.md) covers `Atomic`, `AtomicBoolean`, and generic enum operations, including the compare-and-swap update model.
- [Concurrent collections](concurrent-collections.md) covers `ConcurrentHashSet<T>`, the 56-bit guarded bitmap primitive, and the hierarchical `ConcurrentBitmap` built on top of it.

For local coding conventions, see [code-style.md](../code-style.md).
