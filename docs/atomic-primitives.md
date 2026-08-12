# Atomic primitives

The optimistic primitives extend the BCL `Interlocked` and `Volatile` APIs with operations used repeatedly by the rest of the repository. Their central invariant is that a state transition is committed only if the value observed by the caller is still current.

## Compare-and-swap update model

Most compound operations in `Atomic` use the same CAS loop:

```text
read current value
       |
       v
derive replacement
       |
       v
compare-exchange ---- succeeds ----> return original value
       |
       +---- another writer won ----> retry from a fresh read
```

The transform may be arithmetic, a bitwise operation, or a caller-supplied delegate. Because the replacement is recomputed after a failed CAS, it is always based on the value that the current iteration actually observed.

This model is appropriate only when the complete consistency boundary fits in the target value. Operations that need to coordinate multiple independent locations use a higher-level lock elsewhere in the library.

## `AtomicBoolean`

`AtomicBoolean` is an explicitly laid out 32-bit value type designed to be used directly with `Interlocked` without boxing or a reference wrapper.

Its canonical encodings are:

- false: all zero bits;
- true: all one bits.

The all-ones true representation is useful for branchless mask expansion in low-level code. Conversions to `bool` treat any non-zero internal value as true, while normal construction from `bool` produces one of the canonical encodings.

`Atomic.Exchange`, `Atomic.CompareExchange`, and `Atomic.VolatileRead` provide the corresponding atomic operations over this type. `AsyncLock` uses them to make disposal a one-winner state transition.

## Generic enum operations

`EnumHelpers` performs `And`, `Or`, `Xor`, `Not`, and equality directly on the unmanaged backing representation of an enum. `Atomic` builds on the same representation strategy for volatile reads, exchange/compare-exchange, atomic bit operations, and conditional transforms.

The implementation specializes by `Unsafe.SizeOf<TEnum>()` and supports the normal 1-, 2-, 4-, and 8-byte enum backing sizes. Values are bit-cast rather than converted through `object`, so the operations preserve the exact bit representation and avoid boxing.

The enum operations do not require `[Flags]`; that attribute affects enum semantics and formatting, not the underlying bitwise mechanics. Callers are responsible for using bit operations meaningfully for the enum in question.

## Operation families

`Atomic` groups several recurring state-transition patterns:

- exchange, compare-exchange, and volatile reads for `AtomicBoolean` and unmanaged enums;
- modulo increment operations;
- write-if-greater (`WriteMax`);
- increment-with-maximum and decrement-with-minimum clamping;
- conditional exchange when all or any requested flags are set;
- predicate/flag-gated transformations;
- generic atomic `And`, `Or`, `Xor`, and transform operations for enums.

Most methods return the original value rather than the replacement. This mirrors `Interlocked` and lets the caller infer whether its desired condition held without an additional non-atomic read. `Try*` conditional methods instead return whether the guarded replacement was committed.

## Fast arithmetic variants

The signed `int` fast variants use the branchless `Math.FastMin`/`Math.FastMax` helpers from `Fho.Core`. Those helpers deliberately trade a wider input domain for lower-level arithmetic and carry explicit subtraction-range preconditions.

The precondition is part of the API contract. Use the regular `WriteMax`, `IncrementClampMax`, or `DecrementClampMin` variants when the caller cannot establish the documented range constraint.

## Delegate-based transforms

`Transform`, `TryTestAnyFlagsTransform`, and `TryCompareTransform` may execute their supplied delegate more than once under contention because a failed CAS restarts the operation from a new observed value.

Transform and predicate delegates should therefore be deterministic with respect to their input and free of externally visible side effects. Side effects inside a retryable transform would occur even on iterations whose computed replacement is never committed.

## Memory and consistency boundary

The atomic primitives provide atomicity for the referenced storage location. They do not make surrounding fields part of the same transaction.

When a state machine requires several values to change together, either pack the required state into one atomic representation or introduce a synchronization boundary that protects the multi-location invariant. The collection implementations demonstrate both approaches: bitmap segment value plus guard token are packed into one 64-bit word, while summary metadata and tree restructuring use additional coordination.
