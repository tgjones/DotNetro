using Irie.Mir;

namespace Irie.Passes.Analyses;

// Interprocedural analysis over a MirModule's call graph that determines, for
// each function, whether it is *non-reentrant* — i.e. it can never be active on
// the call stack more than once at a time.
//
// A non-reentrant function may place its locals in static memory (fixed
// zero-page addresses) rather than on a real call stack, because there is never
// a second live activation whose locals would alias the first's. The MOS6502
// static-frame allocator relies on this.
//
// Algorithm:
//   1. Build a directed call graph. An edge A → B exists if A contains a call
//      instruction whose Symbol operand names B (covers both the pre-isel
//      `call.func @B` form and the lowered `mos6502.jsr.abs @B` form). A single
//      conservative "indirect" pseudo-edge models call.indirect / any other
//      indirect dispatch: any function that performs an indirect call may reach
//      any address-taken function. For the current corpus indirect calls go
//      through the runtime trampoline; we treat every function as a potential
//      indirect target (maximally conservative).
//   2. Tarjan SCCs. A function is non-reentrant iff:
//        * its SCC is a singleton (no mutual recursion), AND
//        * it does not call itself directly (no self-recursion), AND
//        * it is not reachable from an interrupt handler.
//      An interrupt handler can preempt and re-enter any function reachable
//      from it, which would alias static locals. No `[interrupt]` attribute
//      exists yet (see IsInterruptHandler below); when one is added, mark such
//      functions and the reachability filter here begins excluding their
//      transitive callees.
//
// The DotNetro corpus is entirely non-recursive, so every function qualifies
// today; the analysis exists so a future recursive program is rejected (left
// reentrant) rather than silently miscompiled.
public sealed class ReentrancyAnalysis
{
    // Runs the analysis over the module and writes the result onto each
    // MirFunction's IsNonReentrant flag. Returns the same module for chaining.
    public static void Run(MirModule module)
    {
        var functions = module.Functions;
        var indexByName = new Dictionary<string, int>(functions.Count);
        for (var i = 0; i < functions.Count; i++)
            indexByName[functions[i].Name] = i;

        // Adjacency: callees[i] = set of function indices i calls directly.
        var callees = new HashSet<int>[functions.Count];
        var selfRecursive = new bool[functions.Count];
        var hasIndirectCall = new bool[functions.Count];

        for (var i = 0; i < functions.Count; i++)
        {
            callees[i] = [];
            CollectCallEdges(functions[i], indexByName, callees[i], ref selfRecursive[i], ref hasIndirectCall[i]);
        }

        // The single conservative indirect-call edge: any function that makes an
        // indirect call may reach any function (address-taken or not — we don't
        // track address-taken-ness yet, so we are maximally conservative). Model
        // this as an edge from each indirect caller to EVERY function, which both
        // pulls indirect callers + their targets into a shared SCC where mutual
        // recursion is possible and prevents either from being marked
        // non-reentrant if a cycle results.
        for (var i = 0; i < functions.Count; i++)
        {
            if (!hasIndirectCall[i]) continue;
            for (var j = 0; j < functions.Count; j++)
                callees[i].Add(j);
        }

        var sccId = TarjanSccs(callees, out var sccSize);

        // Interrupt reachability: functions reachable from an interrupt handler
        // can be preempted/re-entered. No interrupt attribute exists yet, so this
        // set is currently empty. Extension point: seed `interruptRoots` from
        // functions flagged with the future [interrupt] attribute and BFS the
        // call graph.
        var reachableFromInterrupt = ComputeInterruptReachability(functions, callees);

        for (var i = 0; i < functions.Count; i++)
        {
            var nonReentrant =
                sccSize[sccId[i]] == 1 &&
                !selfRecursive[i] &&
                !reachableFromInterrupt[i];
            functions[i].IsNonReentrant = nonReentrant;
        }
    }

    private static void CollectCallEdges(
        MirFunction function,
        Dictionary<string, int> indexByName,
        HashSet<int> calleeSet,
        ref bool selfRecursive,
        ref bool hasIndirectCall)
    {
        foreach (var block in function.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (!IsCallInstruction(instr))
                    continue;

                var calleeName = FindCalleeSymbol(instr);
                if (calleeName is null)
                {
                    // A call with no resolvable Symbol operand (e.g. a numeric
                    // jsr.abs to a ROM vector, or an indirect dispatch) — treat
                    // as an indirect call edge. Numeric jsr.abs targets are
                    // outside the module (OS ROM) and never recurse back in, but
                    // being conservative here costs nothing for the corpus.
                    hasIndirectCall = true;
                    continue;
                }

                if (!indexByName.TryGetValue(calleeName, out var calleeIndex))
                    continue; // external symbol not in this module

                calleeSet.Add(calleeIndex);
                if (calleeName == function.Name)
                    selfRecursive = true;
            }
        }
    }

    // A call instruction is either a generic call.func (pre-isel) / call.indirect
    // or a lowered target jsr.abs. We detect calls structurally: the call dialect
    // ops, plus any instruction carrying a Symbol operand whose opcode is not
    // side-effect-free (covers mos6502.jsr.abs @name). mem.symbol is side-effect-
    // free, so it is excluded.
    private static bool IsCallInstruction(MirInstruction instr)
    {
        var dialect = DialectRegistry.ById(instr.Opcode.Dialect);
        if (dialect.Prefix == "call")
            return true;
        // A target jsr carries a Symbol operand and is not side-effect-free.
        return !dialect.IsSideEffectFree(instr.Opcode.Code) && HasSymbolOperand(instr);
    }

    private static bool HasSymbolOperand(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is Symbol)
                return true;
        return false;
    }

    private static string? FindCalleeSymbol(MirInstruction instr)
    {
        foreach (var op in instr.Operands)
            if (op is Symbol s)
                return s.Name;
        return null;
    }

    // Extension point for interrupt handlers. No [interrupt] attribute exists in
    // the IR yet, so no function is currently an interrupt root and the returned
    // array is all-false. When the attribute lands, seed the roots here and BFS.
    private static bool[] ComputeInterruptReachability(
        List<MirFunction> functions, HashSet<int>[] callees)
    {
        var reachable = new bool[functions.Count];

        var roots = new List<int>();
        for (var i = 0; i < functions.Count; i++)
            if (IsInterruptHandler(functions[i]))
                roots.Add(i);

        var queue = new Queue<int>(roots);
        foreach (var r in roots)
            reachable[r] = true;

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            foreach (var c in callees[i])
            {
                if (reachable[c]) continue;
                reachable[c] = true;
                queue.Enqueue(c);
            }
        }

        return reachable;
    }

    // Placeholder: no interrupt attribute exists in the MIR yet. Always false.
    // When an [interrupt] attribute is added to MirFunction, test it here.
    private static bool IsInterruptHandler(MirFunction function) => false;

    // Tarjan's strongly-connected-components algorithm. Returns an array mapping
    // each function index to its SCC id, and (out) an array of SCC sizes indexed
    // by SCC id. Iterative to avoid deep recursion on large modules.
    private static int[] TarjanSccs(HashSet<int>[] adjacency, out int[] sccSize)
    {
        var n = adjacency.Length;
        var index = new int[n];
        var lowlink = new int[n];
        var onStack = new bool[n];
        var visited = new bool[n];
        var sccId = new int[n];
        Array.Fill(sccId, -1);

        var stack = new Stack<int>();
        var nextIndex = 0;
        var nextScc = 0;
        var sizes = new List<int>();

        // Iterative DFS frame: the node and an enumerator position over its
        // successors.
        for (var start = 0; start < n; start++)
        {
            if (visited[start]) continue;

            var work = new Stack<(int node, List<int> succ, int pos)>();
            visited[start] = true;
            index[start] = lowlink[start] = nextIndex++;
            stack.Push(start);
            onStack[start] = true;
            work.Push((start, [.. adjacency[start]], 0));

            while (work.Count > 0)
            {
                var (node, succ, pos) = work.Pop();

                var recursed = false;
                while (pos < succ.Count)
                {
                    var w = succ[pos];
                    pos++;
                    if (!visited[w])
                    {
                        visited[w] = true;
                        index[w] = lowlink[w] = nextIndex++;
                        stack.Push(w);
                        onStack[w] = true;
                        // Re-push current frame at its advanced position, then
                        // descend into w.
                        work.Push((node, succ, pos));
                        work.Push((w, [.. adjacency[w]], 0));
                        recursed = true;
                        break;
                    }
                    if (onStack[w])
                        lowlink[node] = Math.Min(lowlink[node], index[w]);
                }

                if (recursed) continue;

                // All successors processed: if node is an SCC root, pop the SCC.
                if (lowlink[node] == index[node])
                {
                    var size = 0;
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        sccId[member] = nextScc;
                        size++;
                    } while (member != node);
                    sizes.Add(size);
                    nextScc++;
                }

                // Propagate lowlink to the parent frame (now on top of work).
                if (work.Count > 0)
                {
                    var parent = work.Peek();
                    lowlink[parent.node] = Math.Min(lowlink[parent.node], lowlink[node]);
                }
            }
        }

        sccSize = [.. sizes];
        return sccId;
    }
}
