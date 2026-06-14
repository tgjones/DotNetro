using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes;
using Irie.Passes.Analyses;

namespace Irie.Target.MOS6502;

// Target-private, module-level post-RA pass that places the FrameSlots of
// non-reentrant functions at fixed zero-page addresses and rewrites their
// accesses from the slow indirect-Y path into direct zero-page LDA/STA.
//
// === Why this is safe (static stack discipline) ===
//
// A non-reentrant function (ReentrancyAnalysis / MirFunction.IsNonReentrant) is
// never active on the call stack more than once, so its locals can live in fixed
// static memory rather than on a real stack. To let a value stored in a frame
// slot survive a call, a function's frame must be DISJOINT from every transitive
// callee's frame. We lay frames out like a static stack, bottom-up:
//
//     base(f) = max over direct callees c of (base(c) + size(c))   (leaves: 0)
//
// so f's frame sits ABOVE all its callees' frames. Independent subtrees of the
// call graph reuse the same zero page (their bases are computed independently),
// so total zero-page use = the largest frame footprint along any single call
// path, not the sum over all functions.
//
// === The zero-page window ===
//
// Frame slots are placed in the CC_MOS callee-saved cross-call window
// RC20..RC29 (zero page $14..$1D, 10 bytes). RC30/RC31 ($1E/$1F) are reserved
// for the indirect-call trampoline and are NOT used. RC0/RC1 ($00/$01) are the
// soft-stack-pointer reservation; the instruction selector borrows them as the
// indirect-Y pointer scratch pair — rewriting a slot access to direct zp frees
// that borrow for the access, but we conservatively do NOT extend the frame
// window into RC0/RC1 (they could be live as a pointer pair for a DIFFERENT,
// non-slot access in the same function).
//
// The window deliberately overlaps the register allocator's callee-saved pool
// (RA may park a cross-call vreg in RC20..RC29). The pass therefore checks, per
// function, which RC registers RA actually assigned and refuses to place a slot
// on any RC the function already uses — that function's slots then stay on the
// .bss path (the existing, always-correct indirect-Y lowering). The same .bss
// fallback covers window overflow (a call path needing more than 10 frame
// bytes) and any reentrant function. The pass is therefore always correctness-
// safe: it only ever turns a correct-but-slow access into a correct-and-fast one.
public sealed class MOS6502StaticFrameAllocPass : Pass
{
    public override string Name => "MOS6502StaticFrameAlloc";

    // RC20..RC29 = zero page $14..$1D. RC30/RC31 reserved for the trampoline.
    private const int WindowFirstRc = 20;
    private const int WindowSizeBytes = 10;

    private const int PointerZpLo = 12; // MOS6502Registers.RC(0)
    private const int PointerZpHi = 13; // MOS6502Registers.RC(1)

    public override void Run(CompilationContext context)
    {
        var module = context.Module;

        // 2a: make sure reentrancy is computed (idempotent; cheap).
        ReentrancyAnalysis.Run(module);

        var functions = module.Functions;
        var indexByName = new Dictionary<string, int>(functions.Count);
        for (var i = 0; i < functions.Count; i++)
            indexByName[functions[i].Name] = i;

        // Direct-call adjacency (post-RA: callees are mos6502.jsr.abs @name).
        var callees = BuildCallGraph(functions, indexByName);

        // size(f) = total bytes of f's frame slots.
        var size = new int[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            foreach (var slot in functions[i].FrameSlots)
                size[i] += slot.Type.SizeInBits / 8;

        // Eligibility independent of the zp layout: a function may be relocated
        // only if it is non-reentrant AND none of its frame-slot ADDRESSES escape
        // (the address is materialised then passed/stored/computed rather than
        // used purely as the base of an in-function indirect-Y access this pass
        // rewrites). An escaped address would still point at the .bss global,
        // which a callee reads/writes — so relocating the in-function accesses to
        // zero page would split the slot across two locations. See
        // SlotAddressesAreContained.
        var eligible = new bool[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            eligible[i] =
                functions[i].FrameSlots.Count > 0 &&
                functions[i].IsNonReentrant &&
                SlotAddressesAreContained(functions[i]);

        // The zero-page footprint a function contributes to a caller's layout:
        // its frame size if it WILL be relocated, else 0 (a .bss function uses no
        // window). Relocation also depends on the base fitting the window, so
        // resolve footprint + base together bottom-up.
        var footprint = new int[functions.Count];
        var baseOf = new int[functions.Count];
        var resolved = new bool[functions.Count];
        var onStack = new bool[functions.Count];
        for (var i = 0; i < functions.Count; i++)
            Resolve(i, callees, size, eligible, footprint, baseOf, resolved, onStack);

        // Colour each function that ended up with a non-zero footprint (i.e. it
        // is eligible AND its frame fits the window at its computed base).
        for (var i = 0; i < functions.Count; i++)
        {
            if (footprint[i] == 0) continue; // .bss (ineligible / overflow)

            var function = functions[i];

            // RC registers RA already assigned anywhere in this function. A slot
            // placed on one of these would alias a live allocated value.
            var usedRc = CollectUsedRcRegisters(function);

            // Assign each slot a zero-page byte. Bail to .bss for the whole
            // function if any byte collides with an RA-used RC.
            var assignment = TryAssignSlots(function, baseOf[i], usedRc);
            if (assignment is null) continue; // collision → .bss

            RewriteSlotAccesses(function, assignment);
        }
    }

    // slot symbol name → zero-page byte (RC index) of its first byte.
    private static Dictionary<string, int>? TryAssignSlots(
        MirFunction function, int frameBase, HashSet<int> usedRc)
    {
        var assignment = new Dictionary<string, int>();
        var offset = 0;
        foreach (var slot in function.FrameSlots)
        {
            var bytes = slot.Type.SizeInBits / 8;
            var firstRc = WindowFirstRc + frameBase + offset;
            for (var b = 0; b < bytes; b++)
            {
                if (usedRc.Contains(firstRc + b))
                    return null; // collides with an RA-assigned register
            }
            // The zero-page address byte equals the RC index: RC(n)'s zp byte is n.
            assignment[slot.SymbolName] = firstRc;
            offset += bytes;
        }
        return assignment;
    }

    // Bottom-up resolution of base + zp footprint. A callee that is kept on .bss
    // (footprint 0) reserves no window, so it doesn't push its callers' frames
    // up. base(f) = max over callees of (base(c) + footprint(c)); footprint(f) =
    // size(f) if f is eligible AND base(f)+size(f) fits the window, else 0.
    private static void Resolve(
        int i, HashSet<int>[] callees, int[] size, bool[] eligible,
        int[] footprint, int[] baseOf, bool[] resolved, bool[] onStack)
    {
        if (resolved[i]) return;
        if (onStack[i]) return; // cycle guard: treat as base 0 (over-reserves only)
        onStack[i] = true;

        var maxBelow = 0;
        foreach (var c in callees[i])
        {
            if (c == i) continue;
            Resolve(c, callees, size, eligible, footprint, baseOf, resolved, onStack);
            maxBelow = Math.Max(maxBelow, baseOf[c] + footprint[c]);
        }

        baseOf[i] = maxBelow;
        footprint[i] = eligible[i] && maxBelow + size[i] <= WindowSizeBytes ? size[i] : 0;
        resolved[i] = true;
        onStack[i] = false;
    }

    // True iff none of the function's frame-slot addresses escape: every
    // materialisation of a slot symbol (`$a = lda.imm.symlo/.symhi @slot`) feeds
    // directly into the RC0/RC1 indirect-Y pointer pair (the EmitSymbolPointerBytes
    // shape this pass rewrites) and nowhere else. If a slot address is taken and
    // passed to a callee, stored, or used in pointer arithmetic, the callee sees
    // the .bss global address — so the slot must stay on .bss.
    private static bool SlotAddressesAreContained(MirFunction function)
    {
        var slotSymbols = new HashSet<string>();
        foreach (var slot in function.FrameSlots)
            slotSymbols.Add(slot.SymbolName);

        foreach (var block in function.Blocks)
        {
            var instrs = block.Instructions;
            for (var i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.Opcode.Dialect != MOS6502Dialect.Id) continue;
                var op = (MOS6502Op)instr.Opcode.Code;
                if (op != MOS6502Op.LdaImmSymLo && op != MOS6502Op.LdaImmSymHi) continue;

                var sym = FindSymbol(instr);
                if (sym is null || !slotSymbols.Contains(sym)) continue;

                // The symlo/symhi result ($a) must be consumed by the very next
                // instruction as `pseudo.copy $rc0/$rc1 ← $a`. Anything else means
                // the address flows elsewhere (escapes).
                var wantPtr = op == MOS6502Op.LdaImmSymLo ? PointerZpLo : PointerZpHi;
                if (i + 1 >= instrs.Count || !IsCopyToPointer(instrs[i + 1], wantPtr))
                    return false;
            }
        }
        return true;
    }

    private static bool IsCopyToPointer(MirInstruction instr, int pointerReg) =>
        instr.Opcode.Dialect == PseudoDialect.Id
        && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy
        && instr.Operands is [PhysicalReg dst, PhysicalReg src]
        && dst is { IsDefinition: true, Id: var d } && d == pointerReg
        && src is { IsDefinition: false, Id: var s } && s == MOS6502Registers.A;

    private static HashSet<int>[] BuildCallGraph(
        List<MirFunction> functions, Dictionary<string, int> indexByName)
    {
        var callees = new HashSet<int>[functions.Count];
        for (var i = 0; i < functions.Count; i++)
        {
            callees[i] = [];
            foreach (var block in functions[i].Blocks)
                foreach (var instr in block.Instructions)
                {
                    // Only jsr.abs @callee is a real call edge. Other Symbol
                    // operands (lda.imm.symlo/.symhi @global, mem.symbol @data)
                    // reference data, not callees, and must not create edges —
                    // a spurious edge would only over-reserve the window, but
                    // keeping it precise avoids surprising layouts.
                    if (instr.Opcode.Dialect != MOS6502Dialect.Id
                        || (MOS6502Op)instr.Opcode.Code != MOS6502Op.JsrAbs)
                        continue;
                    foreach (var op in instr.Operands)
                        if (op is Symbol s && indexByName.TryGetValue(s.Name, out var c))
                            callees[i].Add(c);
                }
        }
        return callees;
    }

    // Every RC (imaginary zero-page) register that appears as an operand anywhere
    // in the function — covers RA-assigned data, the pointer scratch pair, and
    // jsr clobber lists. The frame window must avoid all of these.
    private static HashSet<int> CollectUsedRcRegisters(MirFunction function)
    {
        var used = new HashSet<int>();
        foreach (var block in function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg p && p.Id >= MOS6502Registers.RC(0))
                        used.Add(RcIndexOf(p.Id));
        return used;
    }

    // The zero-page byte index of an RC physical register (RC(n) → n).
    private static int RcIndexOf(int physReg) => physReg - MOS6502Registers.RC(0);

    // Rewrites each frame-slot indirect-Y access whose symbol is zp-assigned into
    // a direct zero-page LDA/STA, then removes the now-dead pointer-setup and
    // ldy.imm instructions via a block-local backward dead-register sweep.
    private static void RewriteSlotAccesses(MirFunction function, Dictionary<string, int> assignment)
    {
        foreach (var block in function.Blocks)
        {
            // Track the symbol whose low/high bytes are currently materialised in
            // the RC0/RC1 pointer pair, and the last ldy.imm offset, so an indy
            // access can be resolved to <slotBase + offset>.
            string? pointerSymbol = null;
            long currentOffset = 0;

            var instrs = block.Instructions;
            for (var i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr.Opcode.Dialect != MOS6502Dialect.Id) continue;
                var op = (MOS6502Op)instr.Opcode.Code;

                switch (op)
                {
                    case MOS6502Op.LdaImmSymLo:
                    case MOS6502Op.LdaImmSymHi:
                        // `$a = lda.imm.symX @sym` — record the symbol the pointer
                        // pair is about to hold. symlo precedes symhi for the same
                        // symbol; either updates the tracked pointer symbol.
                        if (FindSymbol(instr) is { } sym)
                            pointerSymbol = sym;
                        break;

                    case MOS6502Op.LdyImm:
                        if (instr.Operands is [_, Immediate imm])
                            currentOffset = imm.Value;
                        break;

                    case MOS6502Op.LdaIndY:
                        // def[0]=$a, use[0]=$zp0 (pointer low).
                        if (pointerSymbol is not null
                            && assignment.TryGetValue(pointerSymbol, out var loadBase)
                            && instr.Operands is [PhysicalReg load0, PhysicalReg loadPtr, ..]
                            && load0.Id == MOS6502Registers.A
                            && loadPtr.Id == PointerZpLo)
                        {
                            var addr = MOS6502Registers.RC(loadBase + (int)currentOffset);
                            instrs[i] = new MirInstruction(
                                MOS6502Dialect.OpRef(MOS6502Op.LdaZp),
                                [
                                    new PhysicalReg(MOS6502Registers.A, IsDefinition: true),
                                    new PhysicalReg(addr, IsDefinition: false),
                                ])
                            { Parent = block };
                        }
                        break;

                    case MOS6502Op.StaIndY:
                        // use[0]=$zp0 (pointer low), use[1]=$a (source).
                        if (pointerSymbol is not null
                            && assignment.TryGetValue(pointerSymbol, out var storeBase)
                            && instr.Operands is [PhysicalReg storePtr, PhysicalReg storeSrc, ..]
                            && storePtr.Id == PointerZpLo
                            && storeSrc.Id == MOS6502Registers.A)
                        {
                            var addr = MOS6502Registers.RC(storeBase + (int)currentOffset);
                            instrs[i] = new MirInstruction(
                                MOS6502Dialect.OpRef(MOS6502Op.StaZp),
                                [
                                    new PhysicalReg(addr, IsDefinition: true),
                                    new PhysicalReg(MOS6502Registers.A, IsDefinition: false),
                                ])
                            { Parent = block };
                        }
                        break;
                }
            }
        }

        // Remove the pointer-setup / ldy.imm instructions that are now dead.
        DeadRegisterSweep(function);
    }

    private static string? FindSymbol(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is Symbol s)
                return s.Name;
        return null;
    }

    // Block-local backward dead-code elimination over physical registers. An
    // instruction is removed iff: it is side-effect-free OR a pseudo.copy / a
    // register-load (lda.imm.symX, ldy.imm) — i.e. it only writes registers, has
    // no memory/control side effect — AND every physical register it defines is
    // dead at that point (not read later in the block before being redefined, and
    // not in the block's live-out set).
    //
    // This is exactly enough to delete the orphaned pointer-pair setup and the
    // ldy.imm offsets left behind once an indy access is rewritten to direct zp,
    // without disturbing any live value. Conservative: instructions with memory
    // or control side effects (stores, jsr, branches, return) are never removed.
    private static void DeadRegisterSweep(MirFunction function)
    {
        // Block live-outs: a register is live-out of B if it is live-in to some
        // successor. We approximate with a global fixpoint over physreg liveness.
        var liveOut = ComputeBlockLiveOut(function);

        foreach (var block in function.Blocks)
        {
            var live = new HashSet<int>(liveOut[block]);
            var instrs = block.Instructions;
            for (var i = instrs.Count - 1; i >= 0; i--)
            {
                var instr = instrs[i];
                var defs = PhysDefs(instr);
                var uses = PhysUses(instr);

                var removable = IsPurelyRegisterDefining(instr)
                    && defs.Count > 0
                    && defs.All(d => !live.Contains(d));

                if (removable)
                {
                    instrs.RemoveAt(i);
                    continue;
                }

                // Update liveness: kill defs, then gen uses.
                foreach (var d in defs) live.Remove(d);
                foreach (var u in uses) live.Add(u);
            }
        }
    }

    private static Dictionary<MirBlock, HashSet<int>> ComputeBlockLiveOut(MirFunction function)
    {
        function.RebuildCfg();
        var liveIn = new Dictionary<MirBlock, HashSet<int>>();
        var liveOut = new Dictionary<MirBlock, HashSet<int>>();
        foreach (var block in function.Blocks)
        {
            liveIn[block] = [];
            liveOut[block] = [];
        }

        bool changed;
        do
        {
            changed = false;
            for (var bi = function.Blocks.Count - 1; bi >= 0; bi--)
            {
                var block = function.Blocks[bi];

                var outSet = new HashSet<int>();
                foreach (var succ in block.Successors)
                    outSet.UnionWith(liveIn[succ]);

                var inSet = new HashSet<int>(outSet);
                var instrs = block.Instructions;
                for (var i = instrs.Count - 1; i >= 0; i--)
                {
                    foreach (var d in PhysDefs(instrs[i])) inSet.Remove(d);
                    foreach (var u in PhysUses(instrs[i])) inSet.Add(u);
                }

                if (!liveOut[block].SetEquals(outSet)) { liveOut[block] = outSet; changed = true; }
                if (!liveIn[block].SetEquals(inSet))   { liveIn[block] = inSet;   changed = true; }
            }
        } while (changed);

        return liveOut;
    }

    private static List<int> PhysDefs(MirInstruction instr)
    {
        var result = new List<int>();
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: true } p)
                result.Add(p.Id);
        return result;
    }

    private static List<int> PhysUses(MirInstruction instr)
    {
        var result = new List<int>();
        foreach (var op in instr.Operands)
            if (op is PhysicalReg { IsDefinition: false } p)
                result.Add(p.Id);
        return result;
    }

    // True for instructions that only write registers and have no memory or
    // control side effect — safe to delete when their defs are dead.
    private static bool IsPurelyRegisterDefining(MirInstruction instr)
    {
        if (instr.Opcode.Dialect == PseudoDialect.Id
            && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy)
            return true;

        if (instr.Opcode.Dialect != MOS6502Dialect.Id)
            return false;

        return (MOS6502Op)instr.Opcode.Code switch
        {
            MOS6502Op.LdaImmSymLo => true,
            MOS6502Op.LdaImmSymHi => true,
            MOS6502Op.LdaImm      => true,
            MOS6502Op.LdyImm      => true,
            MOS6502Op.LdxImm      => true,
            _ => false,
        };
    }
}
