using Irie.Dialects.Pseudo;
using Irie.Mir;
using Irie.Passes.Analyses;
using Irie.Target;

namespace Irie.Passes;

// =============================================================================
// GraphColouringAllocator — the iterated-register-coalescing core.
// =============================================================================
//
// This implements the algorithm of Appel, *Modern Compiler Implementation*
// ch. 11 ("Register Allocation for Trees" → "Graph Coloring by Simplification"
// → "Coalescing" → "Graph Coloring Implementation"). The pseudocode in §11.4
// is the direct model for the Build / MakeWorklist / Simplify / Coalesce /
// Freeze / SelectSpill / AssignColors structure below.
//
// One run colours one MirFunction. The result is a vreg → physreg map that the
// surrounding RegisterAllocatorPass applies to the IR.
//
// ----------------------------------------------------------------------------
// Vocabulary recap (full definitions in the plan's "Reader's primer")
// ----------------------------------------------------------------------------
//   node              a vreg, or a precoloured physreg that appears in the IR.
//   interference edge two nodes whose live ranges overlap → cannot share a reg.
//   move edge         a pseudo.copy `a = b` relating a and b: if they end up the
//                     same colour, the copy is free and disappears.
//   degree            number of interference neighbours.
//   K                 number of colours available to a node. Here K is
//                     PER-NODE, not a single function-wide constant, because a
//                     node's colours are exactly its class-intersection's
//                     allocatable register set (AllowedColors). "Significant
//                     degree" for a node N means degree(N) >= K(N).
//   simplify          remove a low-degree (degree < K) non-move-related node and
//                     push it on the select stack; this is always colourable.
//   coalesce          merge the two ends of a move when a conservative test
//                     (Briggs / George) proves it stays colourable; the copy
//                     then vanishes because both ends share one register.
//   freeze            give up coalescing a move whose ends are stuck, so its
//                     (now non-move-related) nodes can be simplified.
//   optimistic spill  (Briggs) when no node is trivially simplifiable, push the
//                     CHEAPEST-to-spill node anyway and hope a colour is free at
//                     select. If a colour IS free at select (the common case on
//                     this target — the zero-page file absorbs most pressure) the
//                     node is coloured normally and nothing spills. Only a true
//                     select failure (a popped node with no free colour) makes the
//                     node an ACTUAL spill, reported back to RegisterAllocatorPass
//                     which then rewrites it to memory traffic and re-runs the
//                     whole colouring (Phase 4; Appel §11.4 "the spilling loop").
//
// ----------------------------------------------------------------------------
// Determinism
// ----------------------------------------------------------------------------
// Every worklist is processed in a deterministic order and every tie is broken
// by ascending node id (vreg ids are dense and stable; physreg ids are fixed).
// Colour choice prefers, in order: (1) a coalesced/copy-hinted colour, then
// (2) for a SHORT-lived value, the scarce architectural GPRs ($x/$y/$a) — the
// cost-driven Phase-5 preference, see ColourPreferenceOrder — then (3) the
// node's class default (zp-first) allocatable order. Every tier iterates in a
// fixed order, so the characterization lit tests stay stable (plan §8).
internal sealed class GraphColouringAllocator
{
    private readonly MirFunction _function;
    private readonly TargetRegisterInfo _registerInfo;
    private readonly LiveIntervals _intervals;

    // Vregs that must NOT be chosen as spill candidates (spill cost = ∞). These
    // are the tiny reload/spill temporaries a previous spill round already
    // introduced: re-spilling them would loop forever (Appel §11.4 marks the
    // freshly-created reload temps as "do not spill again"). The pass passes this
    // set in on each re-run.
    private readonly IReadOnlySet<int> _unspillable;

    // The vregs whose optimistic-spill bet FAILED at select time — there was no
    // free colour for them. These are the ACTUAL spills. RegisterAllocatorPass
    // reads this (via SpilledVregs) and, if it is non-empty, rewrites each to
    // memory traffic (remat or store/reload) and re-runs the allocator. An empty
    // set means colouring succeeded and Assignment is final.
    private readonly List<int> _actualSpills = [];

    public IReadOnlyList<int> SpilledVregs => _actualSpills;

    // ---- Node identity --------------------------------------------------
    // Nodes are addressed by a single integer "node id". Vregs keep their own
    // id (always >= 0, dense). Precoloured physreg nodes are offset by
    // PhysNodeBase so the two id spaces never collide. The helpers below convert
    // between the two views.
    private const int PhysNodeBase = 1 << 20;
    private static int PhysNode(int physReg) => PhysNodeBase + physReg;
    private static bool IsPhysNode(int node) => node >= PhysNodeBase;
    private static int PhysRegOfNode(int node) => node - PhysNodeBase;

    // The precolour assigned to a physreg node (identity) or, after select, the
    // colour assigned to a vreg node.
    private readonly Dictionary<int, int> _colour = [];

    // Interference adjacency. _adjSet is the membership set (fast edge test);
    // _adjList is the iterable neighbour list (only meaningful for non-removed
    // nodes, à la Appel). Edges are symmetric.
    private readonly HashSet<(int, int)> _adjSet = [];
    private readonly Dictionary<int, HashSet<int>> _adjList = [];
    private readonly Dictionary<int, int> _degree = [];

    // Move (pseudo.copy) edges. Each move is a (def-node, use-node) pair plus the
    // instruction it came from. _moveList maps a node to the moves it takes part
    // in. Move state is tracked by membership in the worklist sets below.
    private readonly List<MoveEdge> _moves = [];
    private readonly Dictionary<MoveEdge, int> _copyDefSlot = []; // move → copy's def slot
    private readonly Dictionary<int, List<int>> _moveList = []; // node → move indices

    // Move worklists (Appel): a move is in exactly one of these at a time, or in
    // none once it has been processed.
    private readonly HashSet<int> _worklistMoves = [];   // moves eligible for coalescing
    private readonly HashSet<int> _activeMoves = [];     // moves not yet ready, may revive
    private readonly HashSet<int> _frozenMoves = [];     // moves given up on
    private readonly HashSet<int> _coalescedMoves = [];  // moves successfully coalesced
    private readonly HashSet<int> _constrainedMoves = []; // moves whose ends interfere

    // Node worklists (Appel). A vreg node lives in exactly one of these.
    private readonly HashSet<int> _simplifyWorklist = []; // low-degree, non-move-related
    private readonly HashSet<int> _freezeWorklist = [];   // low-degree, move-related
    private readonly HashSet<int> _spillWorklist = [];    // high-degree
    private readonly HashSet<int> _coalescedNodes = [];   // merged away into an alias
    private readonly Stack<int> _selectStack = [];
    private readonly HashSet<int> _onSelectStack = [];

    // Coalescing alias: when v is coalesced into u, _alias[v] = u. GetAlias
    // chases the chain to the representative node.
    private readonly Dictionary<int, int> _alias = [];

    // Per-vreg allowed-register set (class intersection) and its size K.
    private readonly Dictionary<int, int[]> _allowedColours = [];

    // The vregs that actually appear in the IR (have at least one operand
    // occurrence) — the only ones that need colouring. An ORPHANED vreg (a dead
    // merge result the legalizer DCE'd but whose annotation lingers, e.g. an
    // unused i16 parameter) is never referenced and is skipped entirely: it has
    // no live interval, no node, and no colour. The old pass skipped these the
    // same way (linear scan ignored empty-interval vregs).
    private List<int> _vregNodes = [];

    public GraphColouringAllocator(
        MirFunction function, TargetRegisterInfo registerInfo, LiveIntervals intervals,
        IReadOnlySet<int>? unspillable = null)
    {
        _function = function;
        _registerInfo = registerInfo;
        _intervals = intervals;
        _unspillable = unspillable ?? new HashSet<int>();
    }

    // A move edge between two nodes (the two operands of a pseudo.copy). Stored
    // with the source instruction so we can debug; equality is by endpoints.
    private readonly record struct MoveEdge(int A, int B, MirInstruction Instr);

    // Returns the final vreg→physreg colouring, or null if colouring failed and
    // produced actual spills (read them from SpilledVregs and re-run after the
    // pass has rewritten them).
    public Dictionary<int, int>? Run()
    {
        _vregNodes = CollectReferencedVregs();
        ComputeAllowedColours();
        Build();
        MakeWorklist();

        // Iterated register coalescing main loop (Appel §11.4, "Main"). Each
        // iteration does exactly ONE of the four actions, preferring cheap ones:
        // simplify a trivially colourable node, coalesce a safe move, freeze a
        // stuck move, or optimistically push a spill candidate. The loop ends
        // when every worklist is empty.
        while (_simplifyWorklist.Count > 0 || _worklistMoves.Count > 0
               || _freezeWorklist.Count > 0 || _spillWorklist.Count > 0)
        {
            if (_simplifyWorklist.Count > 0) Simplify();
            else if (_worklistMoves.Count > 0) Coalesce();
            else if (_freezeWorklist.Count > 0) Freeze();
            else if (_spillWorklist.Count > 0) SelectSpill();
        }

        AssignColours();

        // If any node's optimistic-spill bet failed at select, we have ACTUAL
        // spills. Return null: RegisterAllocatorPass will rewrite the spilled
        // vregs to memory traffic (remat or store/reload) and re-run a fresh
        // allocator. A partial assignment is meaningless when we are about to
        // change the IR, so we do not build one.
        if (_actualSpills.Count > 0)
            return null;

        return BuildAssignment();
    }

    // =========================================================================
    // Class intersection (replaces the deleted widen passes — plan §3.2 interim)
    // =========================================================================
    //
    // A vreg's allocatable registers are the intersection of the register
    // classes implied by all of its appearances:
    //   * its ClassedVReg annotation (the class isel/legalizer committed it to);
    //   * the operand-class constraint of every instruction operand it fills
    //     (DialectInstructionInfo.OperandClasses), when that class is non-zero.
    //
    // Intersecting these means a value that is `ac` at one operand but otherwise
    // free is constrained to $a (correct), while a value annotated `ac` only
    // because isel pinned it — but never actually required on $a by an operand
    // class — keeps the broad set its annotation's class allows. In practice the
    // annotation class already is the operand class for our dialects, so the
    // intersection mostly just reproduces the annotation's allocatable set; the
    // machinery is here so a future broad-class isel (plan §3.2 preferred path)
    // needs no RA change. The crucial behaviour difference from the old pass is
    // that we no longer WIDEN single-physreg `ac`/`xc` annotations to `any8`:
    // coalescing, not widening, is what frees those values to live elsewhere.
    private void ComputeAllowedColours()
    {
        // Operand-class constraints gathered from every use/def site, per vreg.
        // CRUCIAL: a pseudo.copy imposes NO operand-class constraint (it has no
        // OperandClasses), so an operand-class constraint is a HARD requirement.
        var operandClassConstraints = new Dictionary<int, HashSet<int>>();

        // "Copy-only" vregs — those that appear ONLY as pseudo.copy operands.
        // Such a value has no hard physreg requirement of its own: every real
        // (target-dialect) operand that would pin it to a specific physreg
        // reaches it through a copy, so the value itself is free to live
        // anywhere flexible. This is the SAME test the deleted
        // WidenUnconstrainedToFlexibleClass used, and it is load-bearing:
        // a target op like `lda.imm.symhi` produces its result in $a and is
        // annotated `ac`, but carries NO declared OperandClasses, so we must
        // NOT widen its result to `any8` (doing so let the result land in $x,
        // which an LDA cannot target — a real miscompile). Only a value touched
        // exclusively by copies is safe to widen; everything else keeps its
        // selected single-physreg class as a hard constraint. (Plan §3.2: the
        // intersection-in-RA interim path; the "preferred" path of moving these
        // constraints into isel would make the declared OperandClasses complete
        // and retire this scan, but that is deferred.)
        var touchedByNonCopy = new HashSet<int>();

        foreach (var block in _function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                var isCopy = instr.Opcode.Dialect == PseudoDialect.Id
                    && (PseudoOp)instr.Opcode.Code == PseudoOp.Copy;
                if (!isCopy)
                    foreach (var op in instr.Operands)
                        if (op is VirtualReg v)
                            touchedByNonCopy.Add(v.Id);

                var info = DialectRegistry.ById(instr.Opcode.Dialect)
                    .GetInstructionInfo(instr.Opcode.Code);
                var classes = info.OperandClasses;
                if (classes == null) continue;

                var operands = instr.Operands;
                for (var i = 0; i < operands.Length && i < classes.Length; i++)
                {
                    if (classes[i] == 0) continue;
                    if (operands[i] is not VirtualReg v) continue;
                    if (!operandClassConstraints.TryGetValue(v.Id, out var set))
                        operandClassConstraints[v.Id] = set = [];
                    set.Add(classes[i]);
                }
            }
        }

        var flexibleId = _registerInfo.FlexibleI8ClassId;
        var flexibleRegs = flexibleId != 0
            ? _registerInfo.GetAllocatableRegisters(flexibleId).ToArray()
            : [];

        foreach (var vreg in _vregNodes)
        {
            int classId;
            string className;
            switch (_function.GetVRegAnnotation(vreg))
            {
                case ClassedVReg classed:
                    classId = classed.ClassId;
                    className = classed.Name;
                    break;

                // A vreg still carrying a TypedVReg annotation at RA is an 8-bit
                // value the selector never pinned to a class — it flows only
                // through pseudo.copy plumbing (the canonical source is an i8
                // block parameter the legalizer narrows, surviving PhiElimination
                // as a typed pseudo.copy def). Such a value is free to live
                // anywhere 8-bit, so it takes the target's flexible class. A
                // still-typed WIDER vreg would be an isel/legalizer gap and is
                // rejected loudly. This is the one genuinely-needed remnant of
                // the deleted WidenLeftoverTypedToFlexibleClass: it assigns a
                // class to an unclassed value, it does NOT widen an already
                // single-physreg class (that job is now the coalescer's).
                case TypedVReg typed when _registerInfo.FlexibleI8ClassId != 0
                                          && typed.Type.SizeInBits <= 8:
                    classId = _registerInfo.FlexibleI8ClassId;
                    className = _registerInfo.GetRegisterClassName(classId) ?? $"class{classId}";
                    break;

                case TypedVReg typed:
                    throw new InvalidOperationException(
                        $"RegisterAllocator: vreg %{vreg} still has wide/unclassable type " +
                        $"{typed.Type.DisplayName} at RA — legalization/isel did not narrow it.");

                default:
                    throw new InvalidOperationException(
                        $"RegisterAllocator: vreg %{vreg} has no register class.");
            }

            // Re-annotate so downstream code (and the move/coalesce machinery)
            // sees a uniform ClassedVReg view.
            _function.ReclassifyVirtualRegister(vreg, classId, className);

            // ---- Base set: the broadest registers this value could use -------
            // For a COPY-ONLY value whose annotation class is a single-physreg
            // subset of the flexible 8-bit class (e.g. `ac` ⊂ any8 — the shape
            // isel produces for a copy-only livein, a tied result relocated
            // through a copy, or a copied-around address byte), the annotation
            // class is only a preference: the value is really free to live
            // anywhere flexible. The base set is then the flexible set, ordered
            // with the annotation register FIRST so colouring still prefers it
            // (an `ac` value prefers $a, then falls back to the zp pool). This is
            // the principled replacement for the widen pre-passes.
            //
            // For a value TOUCHED BY A NON-COPY op, the annotation class is a
            // HARD constraint and is NOT widened — target ops carry implicit
            // physreg requirements not all declared in OperandClasses (e.g.
            // `lda.imm.symhi` must produce its `ac` result in $a; widening it to
            // any8 would let it land in $x, which LDA cannot target). See the
            // touchedByNonCopy comment above.
            var annotationRegs = _registerInfo.GetAllocatableRegisters(classId).ToArray();
            int[] allowed;
            if (flexibleId != 0 && classId != flexibleId
                && !touchedByNonCopy.Contains(vreg)
                && annotationRegs.All(r => Contains(flexibleRegs, r)))
            {
                // annotation regs first (preference), then the rest of flexible.
                allowed = annotationRegs
                    .Concat(flexibleRegs.Where(r => !Contains(annotationRegs, r)))
                    .ToArray();
            }
            else
            {
                allowed = annotationRegs;
            }

            // ---- Hard constraints: intersect real operand-class register sets.
            if (operandClassConstraints.TryGetValue(vreg, out var constraintClasses))
            {
                foreach (var constraintClass in constraintClasses)
                {
                    var constraintRegs = _registerInfo.GetAllocatableRegisters(constraintClass).ToArray();
                    allowed = allowed.Where(r => Contains(constraintRegs, r)).ToArray();
                }
            }

            if (allowed.Length == 0)
                throw new InvalidOperationException(
                    $"RegisterAllocator: vreg %{vreg} in class {className} has an empty " +
                    "allocatable set after intersecting operand-class constraints.");

            _allowedColours[vreg] = allowed;
        }
    }

    // The colours a node may take, in preference order. For a precoloured
    // physreg node that is the single physreg; for a vreg node it is the
    // class-intersection computed above.
    private ReadOnlySpan<int> AllowedColoursOf(int node) =>
        IsPhysNode(node) ? new[] { PhysRegOfNode(node) } : _allowedColours[node];

    // K for a node = the number of colours it could take. "Significant degree"
    // (the Briggs threshold) is degree >= K.
    private int KOf(int node) => AllowedColoursOf(node).Length;

    // =========================================================================
    // Build the interference + move graph
    // =========================================================================
    private void Build()
    {
        var vregs = _vregNodes;

        // Precoloured physreg nodes: every physreg that appears anywhere in the
        // IR (explicit or implicit operand) or in a block live-in. Each is a
        // node already assigned its own colour.
        var physRegs = CollectPhysRegs();
        foreach (var p in physRegs)
        {
            var node = PhysNode(p);
            _colour[node] = p;
            _degree[node] = int.MaxValue / 2; // precoloured: effectively infinite
            EnsureAdj(node);
        }

        foreach (var v in vregs)
        {
            _degree[v] = 0;
            EnsureAdj(v);
        }

        // ---- Move edges ----------------------------------------------------
        // Every pseudo.copy `dst = src` between two register operands is a move.
        // These are exactly the copies TwoAddress (tied operands), PhiElim (phi
        // copies), AbiLowering (livein / call-relocation copies) and isel
        // (constraint copies into pinned physregs) emit. Coalescing folds them
        // all through one mechanism (plan §3.3).
        //
        // Built BEFORE interference so the move-self-interference suppression
        // below can consult the move set.
        BuildMoves();
        ComputeSuppressedMoveSelfEdges();

        // ---- Interference edges --------------------------------------------
        // vreg ↔ vreg: from LiveIntervals.Interfere.
        for (var i = 0; i < vregs.Count; i++)
            for (var j = i + 1; j < vregs.Count; j++)
                if (_intervals.Interfere(vregs[i], vregs[j])
                    && !IsSuppressedMoveSelfEdge(vregs[i], vregs[j]))
                    AddEdge(vregs[i], vregs[j]);

        // vreg ↔ precoloured physreg: from LiveIntervals.Overlaps. A vreg whose
        // live range overlaps a physreg's busy window (call clobber, flag def,
        // CC live-in, or another value parked on that physreg) cannot take it.
        foreach (var v in vregs)
            foreach (var p in physRegs)
                if (_intervals.Overlaps(v, p)
                    && !IsSuppressedMoveSelfEdge(v, PhysNode(p)))
                    AddEdge(v, PhysNode(p));
    }

    // ----------------------------------------------------------------------
    // Move-self-interference suppression (Appel §11.4 "Build": a copy
    // `a <- b` must NOT make a and b interfere just because b is live across
    // the copy — that overlap is exactly the copy itself, and coalescing the
    // two ends into one register turns the copy into a no-op).
    //
    // Our interference comes from LiveIntervals, which has no notion of moves:
    // it reports `a` and `b` as overlapping wherever their live ranges touch,
    // INCLUDING the def slot of the copy. The canonical case this breaks is a
    // DEAD copy `%dead <- $p` where $p is still live afterwards (an ignored
    // calling-convention argument byte): %dead's one-slot live range sits
    // inside $p's range, so the raw graph says they interfere, so the coalescer
    // refuses to merge them, so %dead is given a FRESH register — and the copy
    // `$fresh <- $p` then clobbers whatever live value sat in $fresh. (Exactly
    // the WriteLineInt32 miscompile.)
    //
    // The fix is precise, not a blanket "moves never interfere" (which would be
    // unsound: `a <- b; b = 5; use a; use b` makes a and b genuinely interfere
    // *outside* the copy). We suppress the a↔b edge ONLY when their overlap is
    // attributable solely to the copy — i.e. removing the copy's def slot from
    // the def endpoint's interval eliminates the overlap. A genuinely-live-out
    // def still overlaps the source elsewhere, so its edge is kept and the
    // conservative coalescing test decides its fate. This is the trivial-dead-
    // copy case the deleted TryGetDeadCopySourcePhysReg handled, now folded
    // into coalescing as the plan intended.
    // ----------------------------------------------------------------------
    private readonly HashSet<(int, int)> _suppressedEdges = [];

    private void ComputeSuppressedMoveSelfEdges()
    {
        foreach (var move in _moves)
        {
            // The copy instruction's def slot: the def endpoint's interval is
            // attributed to the copy there. move.A is always the copy's def
            // operand, move.B its source (see BuildMoves: [def, use] order).
            if (!_copyDefSlot.TryGetValue(move, out var defSlot)) continue;

            if (!OverlapsExcludingSlot(move.A, move.B, defSlot))
            {
                _suppressedEdges.Add((move.A, move.B));
                _suppressedEdges.Add((move.B, move.A));
            }
        }
    }

    private bool IsSuppressedMoveSelfEdge(int a, int b) => _suppressedEdges.Contains((a, b));

    // Do the live ranges of two nodes overlap anywhere OTHER than the half-open
    // unit window [slot, slot+1)? (slot is the copy's def point.) Returns false
    // when the only overlap is that one slot — the signal that the edge is the
    // copy's own and may be suppressed.
    private bool OverlapsExcludingSlot(int a, int b, int slot)
    {
        var ia = IntervalSegmentsOf(a);
        var ib = IntervalSegmentsOf(b);
        foreach (var sa in ia)
            foreach (var sb in ib)
            {
                // Intersection of the two segments.
                var lo = Math.Max(sa.Start, sb.Start);
                var hi = Math.Min(sa.End, sb.End);
                if (lo >= hi) continue; // disjoint
                // Overlap exists. Is it confined to exactly [slot, slot+1)?
                if (lo == slot && hi == slot + 1) continue; // just the copy slot
                return true;
            }
        return false;
    }

    private IReadOnlyList<LiveSegment> IntervalSegmentsOf(int node) =>
        IsPhysNode(node)
            ? _intervals.PhysIntervalOf(PhysRegOfNode(node)).Segments
            : _intervals.IntervalOf(node).Segments;

    // Every vreg that appears as an operand anywhere (def or use, including
    // nested in BlockTarget.Args), in ascending id order for determinism.
    private List<int> CollectReferencedVregs()
    {
        var set = new SortedSet<int>();
        foreach (var block in _function.Blocks)
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    CollectVregRefs(op, set);
        return set.ToList();
    }

    private static void CollectVregRefs(MirOperand op, SortedSet<int> into)
    {
        switch (op)
        {
            case VirtualReg v:
                into.Add(v.Id);
                break;
            case BlockTarget bt:
                foreach (var arg in bt.Args)
                    CollectVregRefs(arg, into);
                break;
        }
    }

    private List<int> CollectPhysRegs()
    {
        var set = new SortedSet<int>();
        foreach (var block in _function.Blocks)
        {
            foreach (var p in block.LiveIns)
                set.Add(p);
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    if (op is PhysicalReg p)
                        set.Add(p.Id);
        }
        return set.ToList();
    }

    private void BuildMoves()
    {
        foreach (var block in _function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.Opcode.Dialect != PseudoDialect.Id) continue;
                if ((PseudoOp)instr.Opcode.Code != PseudoOp.Copy) continue;
                if (instr.Operands.Length != 2) continue;

                // pseudo.copy operand shape: [def, use].
                var dstNode = NodeOfOperand(instr.Operands[0]);
                var srcNode = NodeOfOperand(instr.Operands[1]);
                if (dstNode is null || srcNode is null) continue;     // copy from immediate/symbol: not a move
                if (dstNode == srcNode) continue;                     // already identity

                var moveIdx = _moves.Count;
                var move = new MoveEdge(dstNode.Value, srcNode.Value, instr);
                _moves.Add(move);
                _copyDefSlot[move] = LiveIntervals.DefSlot(_intervals.BaseSlotOf[instr]);
                AddToMoveList(dstNode.Value, moveIdx);
                AddToMoveList(srcNode.Value, moveIdx);
                _worklistMoves.Add(moveIdx);
            }
        }
    }

    // Map a register operand to its graph node, or null for a non-register
    // operand (immediate / symbol / block target — those make a copy non-move).
    private int? NodeOfOperand(MirOperand op) => op switch
    {
        VirtualReg v => v.Id,
        PhysicalReg p => PhysNode(p.Id),
        _ => null,
    };

    private void AddToMoveList(int node, int moveIdx)
    {
        if (!_moveList.TryGetValue(node, out var list))
            _moveList[node] = list = [];
        list.Add(moveIdx);
    }

    // ---- Graph helpers ------------------------------------------------------
    private void EnsureAdj(int node)
    {
        if (!_adjList.ContainsKey(node)) _adjList[node] = [];
        _degree.TryAdd(node, 0);
    }

    private void AddEdge(int a, int b)
    {
        if (a == b) return;
        if (_adjSet.Contains((a, b))) return;
        _adjSet.Add((a, b));
        _adjSet.Add((b, a));

        // Precoloured nodes carry no adjacency list and infinite degree (Appel:
        // "we do not bother to keep an adjacency list for precoloured nodes").
        if (!IsPhysNode(a)) { _adjList[a].Add(b); _degree[a]++; }
        if (!IsPhysNode(b)) { _adjList[b].Add(a); _degree[b]++; }
    }

    private bool Interferes(int a, int b) => a == b || _adjSet.Contains((a, b));

    // =========================================================================
    // MakeWorklist — bucket every vreg node into simplify / freeze / spill.
    // =========================================================================
    private void MakeWorklist()
    {
        foreach (var node in _vregNodes)
        {
            if (_degree[node] >= KOf(node))
                _spillWorklist.Add(node);            // high degree → spill candidate
            else if (IsMoveRelated(node))
                _freezeWorklist.Add(node);           // low degree but in a move
            else
                _simplifyWorklist.Add(node);         // trivially colourable
        }
    }

    // The moves a node is still part of (not yet frozen/constrained/coalesced):
    // those on the active or worklist sets. Appel's NodeMoves.
    private IEnumerable<int> NodeMoves(int node)
    {
        if (!_moveList.TryGetValue(node, out var list)) yield break;
        foreach (var m in list)
            if (_activeMoves.Contains(m) || _worklistMoves.Contains(m))
                yield return m;
    }

    private bool IsMoveRelated(int node) => NodeMoves(node).Any();

    // =========================================================================
    // Simplify — remove a low-degree non-move-related node (Appel §11.4).
    // =========================================================================
    private void Simplify()
    {
        // Deterministic: lowest node id first.
        var node = Min(_simplifyWorklist);
        _simplifyWorklist.Remove(node);
        PushSelect(node);

        // Removing the node lowers each neighbour's degree, which may make a
        // neighbour newly trivially colourable (DecrementDegree handles the
        // worklist move).
        foreach (var adj in Adjacent(node).ToList())
            DecrementDegree(adj);
    }

    private void PushSelect(int node)
    {
        _selectStack.Push(node);
        _onSelectStack.Add(node);
    }

    // Neighbours of a node that are still "in the graph": not yet pushed on the
    // select stack and not coalesced away. (Appel's adjacent().)
    private IEnumerable<int> Adjacent(int node)
    {
        if (!_adjList.TryGetValue(node, out var list)) yield break;
        foreach (var n in list)
            if (!_onSelectStack.Contains(n) && !_coalescedNodes.Contains(n))
                yield return n;
    }

    // Lower a node's degree; when it drops below K, the node becomes colourable
    // and migrates from the spill worklist to freeze/simplify. Precoloured nodes
    // are skipped (their degree is conceptually infinite).
    private void DecrementDegree(int node)
    {
        if (IsPhysNode(node)) return;
        var d = _degree[node];
        _degree[node] = d - 1;

        if (d == KOf(node))
        {
            // Just dropped from significant to insignificant degree. Its moves
            // (and its own) may now be ready to revisit.
            EnableMoves(node);
            foreach (var adj in Adjacent(node))
                EnableMoves(adj);

            _spillWorklist.Remove(node);
            if (IsMoveRelated(node)) _freezeWorklist.Add(node);
            else _simplifyWorklist.Add(node);
        }
    }

    // Move any active moves of these nodes back onto the worklist so coalescing
    // reconsiders them now the graph changed (Appel's EnableMoves).
    private void EnableMoves(int node)
    {
        foreach (var m in NodeMoves(node).ToList())
        {
            if (_activeMoves.Remove(m))
                _worklistMoves.Add(m);
        }
    }

    // =========================================================================
    // Coalesce — try to merge the two ends of one move (Appel §11.4).
    // =========================================================================
    private void Coalesce()
    {
        var moveIdx = Min(_worklistMoves);
        var move = _moves[moveIdx];
        _worklistMoves.Remove(moveIdx);

        var x = GetAlias(move.A);
        var y = GetAlias(move.B);

        // Canonicalise so that if either end is precoloured it is `u`. George's
        // test needs the precoloured node on a fixed side.
        var (u, v) = IsPhysNode(y) ? (y, x) : (x, y);

        if (u == v)
        {
            // Already the same node (a prior coalesce merged them). The copy is
            // an identity; record it as coalesced and re-examine u.
            _coalescedMoves.Add(moveIdx);
            AddWorklist(u);
        }
        else if (IsPhysNode(v) || Interferes(u, v))
        {
            // Both precoloured (different regs) or the ends interfere → the move
            // can never be coalesced; it stays a real copy.
            _constrainedMoves.Add(moveIdx);
            AddWorklist(u);
            AddWorklist(v);
        }
        else if (CoalesceIsSafe(u, v))
        {
            _coalescedMoves.Add(moveIdx);
            Combine(u, v);
            AddWorklist(u);
        }
        else
        {
            // Not provably safe yet; keep it active so a later graph change can
            // re-enable it.
            _activeMoves.Add(moveIdx);
        }
    }

    // Conservative coalescing safety. Two tests, both guaranteeing the merged
    // node stays colourable (so coalescing never *introduces* a spill):
    //
    //   * George's test (used when u is precoloured): for every neighbour t of
    //     the non-precoloured node v, t is either of insignificant degree, or
    //     precoloured, or already interferes with u. Intuitively: merging adds
    //     nothing that could make u harder to colour.
    //   * Briggs' test (both ordinary): the combined node has fewer than K
    //     neighbours of significant degree. A node with < K significant
    //     neighbours is guaranteed colourable by the simplify argument.
    private bool CoalesceIsSafe(int u, int v)
    {
        if (IsPhysNode(u))
        {
            // George — but also honour the class-intersection: a vreg can only
            // be coalesced onto a physreg it is actually allowed to take.
            if (!VregMayTakeColour(v, PhysRegOfNode(u))) return false;
            foreach (var t in Adjacent(v))
                if (!GeorgeOk(t, u)) return false;
            return true;
        }

        // The merged node must satisfy BOTH ends' class constraints, so its
        // colours are the INTERSECTION of the two allowed sets. If that is
        // empty the merge is illegal (e.g. an `ac`-pinned lda.indy def coalesced
        // with a value that may not live in $a) — never coalesce it.
        var mergedAllowed = AllowedIntersectionCount(u, v);
        if (mergedAllowed == 0) return false;

        // Briggs — the merged node's neighbours of significant degree must
        // number < the merged node's colour count (the intersection size, which
        // is ≤ min(K(u), K(v))). A node with fewer significant neighbours than
        // available colours is guaranteed colourable by the simplify argument.
        var significant = new HashSet<int>();
        foreach (var t in Adjacent(u)) if (IsSignificant(t)) significant.Add(t);
        foreach (var t in Adjacent(v)) if (IsSignificant(t)) significant.Add(t);
        return significant.Count < mergedAllowed;
    }

    private bool GeorgeOk(int t, int u) =>
        !IsSignificant(t) || IsPhysNode(t) || Interferes(t, u);

    private bool IsSignificant(int node) =>
        IsPhysNode(node) || _degree[node] >= KOf(node);

    // Can the vreg legally take this colour given its class intersection?
    private bool VregMayTakeColour(int vreg, int colour) =>
        Contains(_allowedColours[vreg], colour);

    // Size of the intersection of two ordinary nodes' allowed-colour sets — the
    // colour count the node would have if u and v were coalesced. Both nodes are
    // ordinary vregs here (the precoloured case is George's, handled separately).
    private int AllowedIntersectionCount(int u, int v)
    {
        var au = _allowedColours[u];
        var av = _allowedColours[v];
        var count = 0;
        foreach (var r in au) if (Contains(av, r)) count++;
        return count;
    }

    // Merge v into u: u absorbs v's edges and moves. After this v is gone.
    private void Combine(int u, int v)
    {
        if (!_freezeWorklist.Remove(v))
            _spillWorklist.Remove(v);
        _coalescedNodes.Add(v);
        _alias[v] = u;

        // The merged value must satisfy both ends' class constraints. Restrict
        // u's allowed colours to the intersection with v's, preserving u's
        // preference order. Without this, a broad-class u (e.g. any8) coalesced
        // with a single-physreg-pinned v (e.g. an `ac` lda.indy def) would keep
        // u's broad set and could colour to a register v's op cannot target —
        // a miscompile (CoalesceIsSafe already guaranteed the intersection is
        // non-empty for ordinary-ordinary merges).
        if (!IsPhysNode(u)
            && _allowedColours.TryGetValue(u, out var au)
            && _allowedColours.TryGetValue(v, out var av))
        {
            _allowedColours[u] = au.Where(r => Contains(av, r)).ToArray();
        }

        // u inherits v's moves so it stays move-related where v was.
        if (_moveList.TryGetValue(v, out var vMoves))
        {
            if (!_moveList.TryGetValue(u, out var uMoves))
                _moveList[u] = uMoves = [];
            uMoves.AddRange(vMoves);
        }

        EnableMoves(v);

        // Every neighbour t of v now interferes with u (the merged value is live
        // wherever either end was). AddEdge bumps u's degree; DecrementDegree
        // then undoes the bump from v's perspective so the graph stays correct.
        foreach (var t in Adjacent(v).ToList())
        {
            AddEdge(t, u);
            DecrementDegree(t);
        }

        // u may have become high-degree by absorbing v.
        if (!IsPhysNode(u) && _degree[u] >= KOf(u) && _freezeWorklist.Remove(u))
            _spillWorklist.Add(u);
    }

    // Chase the alias chain to a node's representative.
    private int GetAlias(int node)
    {
        while (_coalescedNodes.Contains(node))
            node = _alias[node];
        return node;
    }

    // A node whose moves are now all settled and which is low-degree drops from
    // the freeze worklist to the simplify worklist (Appel's AddWorkList).
    private void AddWorklist(int node)
    {
        if (!IsPhysNode(node) && !IsMoveRelated(node) && _degree[node] < KOf(node))
        {
            if (_freezeWorklist.Remove(node))
                _simplifyWorklist.Add(node);
        }
    }

    // =========================================================================
    // Freeze — give up coalescing the moves of a low-degree node (Appel §11.4).
    // =========================================================================
    private void Freeze()
    {
        var node = Min(_freezeWorklist);
        _freezeWorklist.Remove(node);
        _simplifyWorklist.Add(node);
        FreezeMoves(node);
    }

    // Freeze every move `node` is in: drop it to the frozen set, and if the move
    // partner thereby becomes non-move-related and low-degree, move it to
    // simplify too.
    private void FreezeMoves(int node)
    {
        foreach (var m in NodeMoves(node).ToList())
        {
            var move = _moves[m];
            if (!_activeMoves.Remove(m))
                _worklistMoves.Remove(m);
            _frozenMoves.Add(m);

            var v = GetAlias(move.A) == GetAlias(node) ? GetAlias(move.B) : GetAlias(move.A);
            if (!IsPhysNode(v) && !IsMoveRelated(v) && _degree[v] < KOf(v))
            {
                if (_freezeWorklist.Remove(v))
                    _simplifyWorklist.Add(v);
            }
        }
    }

    // =========================================================================
    // SelectSpill — Briggs optimistic spill with spill-cost heuristic (Appel
    // §11.4 "SelectSpill").
    // =========================================================================
    // No node is trivially colourable, so we must pick one to push onto the
    // select stack optimistically. The classic choice is the CHEAPEST node to
    // spill, NOT the highest-degree one: if the optimistic bet later fails, this
    // is the node we will actually move to memory, so we want it to be the one
    // whose spill costs the program the least.
    //
    // Spill cost (Appel §11.4, Chaitin's metric): a node's cost is the number of
    // its definitions and uses, each weighted by loop nesting depth (a use inside
    // a loop is executed far more often, so spilling it is far more expensive).
    // The allocator's "priority" for spilling is cost / degree — spill the node
    // that relieves the most interference per unit of runtime cost. We use that
    // ratio; lower ratio = better spill candidate.
    //
    // Some nodes are effectively unspillable (cost = ∞) and must never be chosen:
    //   * reload/spill temporaries from a previous spill round (in _unspillable)
    //     — re-spilling them loops forever;
    //   * a node whose live range is a single tiny segment (a def with its uses
    //     all at one program point) — there is nothing to gain by spilling it and
    //     the store/reload would be longer than the value's whole life.
    // If EVERY spill-worklist node is unspillable we still have to push one (the
    // optimistic bet may yet succeed at select); we pick the lowest id so the
    // choice stays deterministic and a genuine select failure surfaces clearly.
    private void SelectSpill()
    {
        var chosen = -1;
        var bestPriority = double.PositiveInfinity;
        foreach (var node in _spillWorklist)
        {
            var priority = SpillPriority(node);
            // Strictly-less wins; ties broken by ascending id (determinism).
            if (priority < bestPriority || (priority == bestPriority && (chosen == -1 || node < chosen)))
            {
                bestPriority = priority;
                chosen = node;
            }
        }

        _spillWorklist.Remove(chosen);
        _simplifyWorklist.Add(chosen);
        FreezeMoves(chosen);
    }

    // Spill priority = weighted spill cost / current degree. Lower is a better
    // (cheaper, higher-relief) spill candidate. Unspillable nodes return +∞ so
    // they sort last and are never preferred. (Appel §11.4.)
    private double SpillPriority(int node)
    {
        if (IsUnspillable(node)) return double.PositiveInfinity;
        var degree = _degree[node];
        if (degree <= 0) return double.PositiveInfinity;
        return (double)SpillCost(node) / degree;
    }

    // True if this node must not be spilled: a previously-introduced reload/spill
    // temporary, or a value whose entire live range is a single sub-slot window
    // (too short to be worth spilling — nothing else can be packed into its hole).
    private bool IsUnspillable(int node)
    {
        if (IsPhysNode(node)) return true;
        if (_unspillable.Contains(node)) return true;

        var segs = _intervals.IntervalOf(node).Segments;
        // A single, unit-width segment is a dead/immediately-consumed def: there
        // is no live range to break up, so spilling cannot help (and would only
        // add traffic). Treat it as unspillable.
        if (segs.Count == 1 && segs[0].End - segs[0].Start <= 1) return true;
        return false;
    }

    // Weighted spill cost: count of def + use occurrences, each weighted by loop
    // nesting depth (Chaitin). We do not have a loop-nesting analysis yet, so the
    // weight is currently 1 per occurrence — i.e. raw def+use count. The hook is
    // here (LoopDepthWeight) so Phase 5 can plug a real loop-depth analysis in
    // without touching the selection logic. Cost is at least 1.
    private long SpillCost(int node)
    {
        long cost = 0;
        foreach (var block in _function.Blocks)
        {
            var weight = LoopDepthWeight(block);
            foreach (var instr in block.Instructions)
                foreach (var op in instr.Operands)
                    cost += CountVregOccurrences(op, node) * weight;
        }
        return Math.Max(cost, 1);
    }

    // Loop-depth weight for a block. Placeholder (always 1) until a loop-nesting
    // analysis lands (Phase 5); kept as a named seam so the spill-cost formula
    // already reads "weighted by loop depth" as the plan specifies (§3.4).
    private static long LoopDepthWeight(MirBlock block) => 1;

    private static long CountVregOccurrences(MirOperand op, int vreg)
    {
        switch (op)
        {
            case VirtualReg v when v.Id == vreg:
                return 1;
            case BlockTarget bt:
                long n = 0;
                foreach (var arg in bt.Args)
                    n += CountVregOccurrences(arg, vreg);
                return n;
            default:
                return 0;
        }
    }

    // =========================================================================
    // AssignColours — pop the select stack and colour each node (Appel §11.4).
    // =========================================================================
    private void AssignColours()
    {
        while (_selectStack.Count > 0)
        {
            var node = _selectStack.Pop();
            _onSelectStack.Remove(node);

            // Colours used by already-coloured neighbours are unavailable.
            var unavailable = new HashSet<int>();
            foreach (var w in _adjList[node])
            {
                var rep = GetAlias(w);
                if (_colour.TryGetValue(rep, out var c) && !_coalescedNodes.Contains(rep))
                    unavailable.Add(c);
                else if (IsPhysNode(rep))
                    unavailable.Add(PhysRegOfNode(rep));
            }

            // Prefer a colour biased by coalescing/hints, then the class's
            // allocatable order. The biased colour collapses surviving copies.
            int? picked = null;
            foreach (var candidate in ColourPreferenceOrder(node))
            {
                if (!unavailable.Contains(candidate))
                {
                    picked = candidate;
                    break;
                }
            }

            if (picked is null)
            {
                // Optimistic select failed: no free colour for this node. It
                // becomes an ACTUAL spill (Appel §11.4: "actualSpills"). We do
                // NOT colour it; RegisterAllocatorPass will rewrite it to memory
                // traffic and re-run a fresh allocator. We keep popping the rest
                // of the stack so we collect every spill in one round (fewer
                // re-runs), but the returned assignment is discarded by Run when
                // _actualSpills is non-empty.
                _actualSpills.Add(node);
                continue;
            }

            _colour[node] = picked.Value;
        }

        // Coalesced nodes take their representative's colour (only meaningful
        // when there were no actual spills; otherwise the assignment is discarded).
        if (_actualSpills.Count == 0)
            foreach (var v in _coalescedNodes)
                _colour[v] = _colour[GetAlias(v)];
    }

    // The order in which to try colours for a node (plan §3.5 — register
    // preference / cost). Three tiers, tried in order:
    //
    //   1. COPY/COALESCE HINTS DOMINATE. For each move this node is related to,
    //      if the OTHER end is already coloured with an allowed colour, prefer it
    //      so the copy collapses to an identity and vanishes. This is the
    //      biased-colouring win Phase 3 built; it stays on top because a removed
    //      copy is worth more than any preference below it.
    //
    //   2. COST-DRIVEN GPR-vs-MEMORY ORDERING (the new Phase-5 policy). Absent a
    //      hint, we no longer fall straight through the class's fixed (zp-first)
    //      allocatable order. Instead we classify the value's live range:
    //        * SHORT range  → try the scarce architectural GPRs ($x/$y/$a) FIRST,
    //          then the zero-page pool. A brief cross-instruction value belongs in
    //          a cheap real register; interference excludes whichever GPRs are
    //          busy, so the value lands in the free one (this is what moves
    //          add-i16's relocated low result byte onto $y — the references' own
    //          choice — instead of $zp2).
    //        * LONG range   → keep the class's default zp-first order, so a value
    //          carried across many arithmetic ops (a loop-carried phi, a long
    //          chain temporary) sits in the abundant zero-page file and leaves all
    //          three GPRs free for the ADC/SBC/CMP chain that NEEDS them (plan §2
    //          lever #4: "keep $a free for arithmetic, prefer the zp pool for
    //          long-lived values").
    //      Cheaper-but-overflowing GPR demand falls back to memory naturally: the
    //      first short value to be coloured grabs a GPR, the next short value whose
    //      range overlaps it interferes and drops to zp — exactly how the
    //      references park add-i32's low byte in $y and its middle bytes in zp.
    //
    //   3. Whatever allowed colours tiers 1–2 did not already emit, in the class's
    //      default order (a safety net so every legal colour is still reachable).
    //
    // All tiers are filtered to the node's allowed set, and every iteration order
    // is deterministic (moves by index, GPR list and allowed list in fixed order)
    // so the characterization lit tests stay stable (plan §8).
    private IEnumerable<int> ColourPreferenceOrder(int node)
    {
        var allowed = _allowedColours[node];
        var emitted = new HashSet<int>();

        // ---- Tier 1: copy/coalesce hints (dominant). -----------------------
        if (_moveList.TryGetValue(node, out var moves))
        {
            foreach (var m in moves.OrderBy(x => x))
            {
                var move = _moves[m];
                var other = GetAlias(move.A) == node ? move.B : move.A;
                other = GetAlias(other);
                if (other == node) continue;

                int? hinted = null;
                if (_colour.TryGetValue(other, out var c) && Contains(allowed, c))
                    hinted = c;
                else if (IsPhysNode(other) && Contains(allowed, PhysRegOfNode(other)))
                    hinted = PhysRegOfNode(other);

                if (hinted is int h && emitted.Add(h))
                    yield return h;
            }
        }

        // ---- Tier 2: cost-driven GPR-first ordering for SHORT ranges. ------
        // Physreg nodes never reach here (they have a single fixed colour); only
        // vreg nodes are cost-classified.
        if (!IsPhysNode(node) && IsShortLived(node))
        {
            // Copy to an array: a ReadOnlySpan cannot survive a yield boundary.
            var gprPreference = _registerInfo.GetShortRangeGprPreference().ToArray();

            // Greedy colouring has no lookahead, so before grabbing a GPR we
            // deprioritize any that an UNCOLOURED interfering neighbour is hinted
            // toward (its own copy to a fixed physreg). Without this, two values
            // each destined for a different fixed register via a copy — but whose
            // own hint is momentarily unavailable — can both grab the same GPR
            // here, evicting the neighbour from the register it actually needs and
            // forcing a shuffle (global-rw: result_low wants $a, can't have it
            // across the 2nd ADC, and would steal $x from result_high which is
            // returned in $x). Trying the un-contended GPRs first lets the
            // neighbour keep its hinted register. The contended ones are still
            // emitted (just later), so this only reorders — it never removes a
            // legal colour.
            var neighbourHints = NeighbourHintColours(node);
            foreach (var gpr in gprPreference)
                if (Contains(allowed, gpr) && !neighbourHints.Contains(gpr) && emitted.Add(gpr))
                    yield return gpr;
            foreach (var gpr in gprPreference)
                if (Contains(allowed, gpr) && emitted.Add(gpr))
                    yield return gpr;
        }

        // ---- Tier 3: the class's default (zp-first) allocatable order. -----
        foreach (var c in allowed)
            if (emitted.Add(c))
                yield return c;
    }

    // The physregs that UNCOLOURED interfering neighbours of `node` are hinted
    // toward through a copy (a move whose other end is a physreg, or an
    // already-coloured node). Coloured neighbours are excluded — their colour is
    // already in AssignColours' `unavailable` set, so they need no avoidance here;
    // this only protects neighbours not yet coloured, which greedy colouring would
    // otherwise trample. Used by ColourPreferenceOrder Tier 2 to defer contended
    // GPRs.
    private HashSet<int> NeighbourHintColours(int node)
    {
        var hints = new HashSet<int>();
        foreach (var w in _adjList[node])
        {
            var rep = GetAlias(w);
            if (rep == node || IsPhysNode(rep) || _colour.ContainsKey(rep)) continue;
            if (!_moveList.TryGetValue(rep, out var moves)) continue;
            foreach (var m in moves)
            {
                var move = _moves[m];
                var other = GetAlias(GetAlias(move.A) == rep ? move.B : move.A);
                if (other == rep) continue;
                if (IsPhysNode(other))
                    hints.Add(PhysRegOfNode(other));
                else if (_colour.TryGetValue(other, out var c))
                    hints.Add(c);
            }
        }
        return hints;
    }

    // The maximum number of GPR-pressuring (see IsArithmeticOp) instructions a
    // value's live range may span and still count as SHORT — i.e. cheap enough to
    // favour a scarce architectural register over the zero-page pool (plan §3.5).
    //
    // Why "GPR-chain ops spanned" and not raw slot length: on this target the
    // pressure on the GPRs comes from the ADC/SBC/CMP/EOR chain, each link of
    // which pins an operand to $a (and ferries multi-byte data through $x/$y). A
    // value whose life straddles only a couple of those links can comfortably ride
    // a GPR (the references put add-i16 and add-i32's low result byte in $y,
    // spanning one and three chain links respectively). A value that spans MANY of
    // them — a loop-carried induction value, a long-chain accumulator, a constant
    // byte threaded through a multi-byte compare — would, if parked in a GPR,
    // block that GPR for the whole chain (and starve the post-RA copy-scavenger of
    // scratch); such values belong in the abundant zp file.
    //
    // The threshold is 3, tuned against the corpus (plan §6):
    //   * add-i32's low result byte spans exactly 3 ADC links and the reference
    //     keeps it in $y — so the threshold must admit a 3-span as short.
    //   * an i32 (4-byte) compare's high operand bytes each span 4 CMP links; at
    //     threshold 3 those classify as LONG and stay in zp, which keeps a GPR
    //     free for the immediate→zp constant copies the same compare emits (the
    //     post-RA copy-scavenger needs a dead GPR there — without this the
    //     CompareLessThanInt32 case had all of $a/$x/$y busy and scavenging
    //     threw). 3 is the largest value that separates these two cases, so it
    //     maximises legitimate GPR use while respecting the scavenger's need.
    // (When Phase 4's emergency save/restore lands, the scavenger constraint
    // relaxes and this threshold can be revisited upward; it is a tuning knob, not
    // a correctness boundary.)
    private const int ShortRangeArithSpanThreshold = 3;

    // Is this vreg's live range short enough to prefer a scarce GPR? "Short" =
    // its live range spans at most ShortRangeArithSpanThreshold GPR-pressuring
    // instructions (IsArithmeticOp). An op is "spanned" when its slot lies
    // strictly INSIDE one of the value's live segments — i.e. the value is live
    // ACROSS that op (computed before it, still needed after). The op that DEFINES
    // the value (at the segment's start) and an op that merely consumes it at the
    // segment's end do not count: those are the value's own endpoints, not chain
    // links it has to survive.
    private bool IsShortLived(int vreg)
    {
        var segments = _intervals.IntervalOf(vreg).Segments;
        if (segments.Count == 0) return true; // no range at all → trivially short.

        var spanned = 0;
        foreach (var block in _function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (!IsArithmeticOp(instr)) continue;
                if (!_intervals.BaseSlotOf.TryGetValue(instr, out var baseSlot)) continue;

                // The op is "spanned" iff the value is live both just before and
                // just after it: its use point and def point both fall strictly
                // within a single live segment (not merely touching an endpoint).
                var usePoint = LiveIntervals.UseSlot(baseSlot);
                var defPoint = LiveIntervals.DefSlot(baseSlot);
                foreach (var seg in segments)
                {
                    if (seg.Start < usePoint && defPoint < seg.End)
                    {
                        spanned++;
                        break;
                    }
                }

                if (spanned > ShortRangeArithSpanThreshold) return false;
            }
        }
        return true;
    }

    // An "arithmetic op" for the cost model = an instruction that creates demand
    // on a scarce GPR by pinning one of its operands to a single-GPR register
    // class. On the MOS6502 these are the chip ops that the carry/compare chain is
    // built from — adc/sbc (result tied to $a), cmp/eor (an operand pinned to $a)
    // — every one of which needs $a (and, for the multi-byte forms, ferries bytes
    // through $x/$y). A value whose live range straddles MANY of these is exactly
    // the value we must keep OUT of the GPRs (parking it in zp leaves the GPRs
    // free for the chain *and* for the post-RA copy-scavenger). A value that
    // straddles only a few can safely ride a GPR.
    //
    // We detect this GENERICALLY via the declared operand classes: an op counts
    // if any of its operand-class constraints is a single-register class drawn
    // from the target's short-range GPR set (GprPressureClasses, computed once
    // below). This is broader — and more correct — than keying on tied operands
    // alone: a `mos6502.cmp` pins its accumulator operand to `ac` but has NO tied
    // operand, yet it is unmistakably $a-chain pressure. The earlier tied-only
    // test missed cmp/eor and so mis-classified constants feeding a multi-byte
    // compare as "short", parking them in $x/$y — which left no GPR free for the
    // immediate→zp copies the same compare emits, and the post-RA scavenger then
    // could not find scratch (the CompareLessThanInt32 regression). Counting any
    // GPR-pinned operand fixes that at the policy level rather than papering over
    // it downstream.
    private bool IsArithmeticOp(MirInstruction instr)
    {
        var info = DialectRegistry.ById(instr.Opcode.Dialect)
            .GetInstructionInfo(instr.Opcode.Code);
        var classes = info.OperandClasses;
        if (classes == null) return false;
        foreach (var c in classes)
            if (c != 0 && GprPressureClasses.Contains(c))
                return true;
        return false;
    }

    // The single-register register classes that correspond to the scarce GPRs the
    // short-range preference draws on (e.g. on the MOS6502: `ac` for $a, `xc` for
    // $x, `yc` for $y). An operand pinned to one of these classes is GPR-chain
    // pressure. Computed once from the target: a class qualifies if its allocatable
    // set is exactly one register and that register is in the short-range GPR list.
    private HashSet<int>? _gprPressureClasses;
    private HashSet<int> GprPressureClasses =>
        _gprPressureClasses ??= ComputeGprPressureClasses();

    private HashSet<int> ComputeGprPressureClasses()
    {
        var gprs = _registerInfo.GetShortRangeGprPreference().ToArray();
        var result = new HashSet<int>();
        // Walk every operand-class that appears in the function and keep those that
        // are a single-register class over a short-range GPR. (We can derive the
        // class of each GPR directly from the target.)
        foreach (var gpr in gprs)
        {
            var classId = _registerInfo.ClassOfPhysicalRegister(gpr);
            if (classId == 0) continue;
            var regs = _registerInfo.GetAllocatableRegisters(classId);
            if (regs.Length == 1 && regs[0] == gpr)
                result.Add(classId);
        }
        return result;
    }

    // =========================================================================
    // Build the final vreg → physreg map.
    // =========================================================================
    private Dictionary<int, int> BuildAssignment()
    {
        var assignment = new Dictionary<int, int>();
        foreach (var vreg in _vregNodes)
        {
            var rep = GetAlias(vreg);
            if (!_colour.TryGetValue(rep, out var colour))
                throw new InvalidOperationException(
                    $"RegisterAllocator: vreg %{vreg} was never coloured.");
            assignment[vreg] = colour;
        }
        return assignment;
    }

    // ---- small utilities ----------------------------------------------------
    private static int Min(HashSet<int> set)
    {
        var best = int.MaxValue;
        foreach (var x in set) if (x < best) best = x;
        return best;
    }

    private static bool Contains(ReadOnlySpan<int> values, int target)
    {
        foreach (var v in values)
            if (v == target) return true;
        return false;
    }
}
