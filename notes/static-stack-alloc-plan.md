# Static Stack Allocation — Implementation Notes

Background research conducted 2026-05-03. Implementation deferred until after the
register allocator (phases 2–8 of `register-allocation-plan.md`) is complete.

## What this optimisation does

On MOS6502, calling-convention spills and local-variable stack slots normally
require push/pop sequences through the hardware stack (S register), which is
slow and limited to 256 bytes. For functions that are provably non-reentrant
(no recursion, not callable from interrupt handlers), those slots can instead
be allocated as **static globals in zero page**, making every access a cheap
absolute or zero-page load/store.

This is what `llvm-mos` does in `MOSStaticStackAlloc.cpp` /
`MOSNonReentrant.cpp`. The analysis and the transformation are separate passes.

## LLVM-MOS pipeline reference

| Stage | Pass | Notes |
|---|---|---|
| IR (pre-codegen) | `MOSNonReentrantPass` | Marks functions `"nonreentrant"` attribute via call-graph SCC analysis |
| Pre-RA | `MOSFrameLowering::processFunctionBeforeFrameFinalized` | Tags all stack objects in non-reentrant functions with `TargetStackID::MosStatic` |
| Post-RA | `MOSStaticStackAllocPass` | Rewrites MosStatic slots → offsets into a single `static_stack` global |

RA itself does not change; only the *destination* of spill slots differs.

## Source files for reference

```
/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSNonReentrant.cpp
/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSNonReentrant.h
/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSStaticStackAlloc.cpp
/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSStaticStackAlloc.h
/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSFrameLowering.cpp  (processFunctionBeforeFrameFinalized)
/Users/timjones/Code/llvm-mos/llvm/lib/Target/MOS/MOSTargetMachine.cpp  (addOptimizedRegAlloc, addPreSched2)
```

## Irie design

### Default: reentrant

Every `MachineFunction` is **reentrant by default**. This is the conservative
choice: spill slots on reentrant functions must use the hardware stack, which
is always correct. Non-reentrant status is opt-in, detected by a separate
analysis pass.

### Attribute on `MachineFunction`

Add a `bool IsNonReentrant { get; set; }` (or an enum `Reentrancy`) to
`MachineFunction`. Default: `false` (reentrant).

### NonReentrantDetectionPass (new, target-independent)

An interprocedural pass that analyses the call graph of a `MachineModule`:

1. Build the call graph from `ExternalSymbolOperand` targets in call
   instructions and known `GenericReturn`-bearing callees.
2. Find SCCs using Tarjan's algorithm (same approach as LLVM-MOS).
3. Mark a function non-reentrant iff:
   - Its SCC is a singleton (no mutual recursion).
   - It does not directly recurse.
   - It is not reachable from any function marked as an interrupt handler
     (we'll need a `[interrupt]` attribute on `MachineFunction` or derived
     from the source IR).
4. Set `function.IsNonReentrant = true` for qualifying functions.

Run this pass **before** register allocation, so that RA-driven spilling can
choose the right slot kind immediately.

### SpillSlot destination policy

When the register allocator needs to spill a vreg:

| Function reentrancy | Spill destination |
|---|---|
| Reentrant (default) | Hardware stack slot (push/pop via S register) |
| Non-reentrant | Static zero-page slot (an `Imag8` register or a named global in the zero-page static area) |

For non-reentrant functions, the preferred spill target for an `Ac` vreg is a
free `Imag8` slot (RC2–RC31), since it avoids any memory traffic entirely. If
all `Imag8` slots are occupied, fall back to a named static in the zero-page
static area (analogous to LLVM-MOS's `static_stack` global).

### StaticStackAllocPass (new, MOS6502-specific)

Runs **post-RA**. Converts static spill/frame slots into concrete addresses:

1. Collect all functions in the module that have `IsNonReentrant = true`.
2. Perform call-graph-aware layout: callee slots are placed before caller
   slots (so the static area can be reused across non-overlapping calls),
   matching LLVM-MOS's SCC-ordering approach.
3. Assign a concrete zero-page address (or static-data address) to each slot
   and rewrite the MIR operands that reference them.

Interrupt-handler callees are deferred and placed last in the layout (as in
LLVM-MOS) to avoid conflicts with non-interrupt call chains.

## Interaction with RA (why this doesn't change the algorithm)

The linear-scan register allocator works entirely with *slot kinds* (hardware
stack vs. static), not concrete addresses. The `SpillSlot` allocator on
`MachineFunction` just needs to record which kind to use (determined by
`IsNonReentrant`). The slot-to-address mapping happens in `StaticStackAllocPass`
post-RA, keeping the allocator target-independent.

## What to implement later

Suggested order (all after RA phases 2–8 are done):

1. Add `IsNonReentrant` flag to `MachineFunction`.
2. Add `SpillSlot` support to `MachineFunction` (needed by RA spilling anyway).
3. Implement `NonReentrantDetectionPass`.
4. Implement `StaticStackAllocPass` (MOS6502-specific).
5. Lit test: a simple non-recursive function whose spill goes to a static slot,
   not a push/pop pair. Compare with the reentrant version of the same function.
