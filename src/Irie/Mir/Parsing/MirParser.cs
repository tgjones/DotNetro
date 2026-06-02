using System.Text;

namespace Irie.Mir.Parsing;

internal sealed class MirParser
{
    private MirLexer _lexer = null!;
    private MirToken _current = null!;

    public static MirModule Parse(TextReader reader)
    {
        MirBootstrap.EnsureRegistered();
        var parser = new MirParser
        {
            _lexer = new MirLexer(reader),
        };
        parser._current = parser._lexer.NextToken();
        return parser.ParseModule();
    }

    // -------------------------------------------------------------------------
    // Module / function header  (streaming — no forward refs at this level)
    // -------------------------------------------------------------------------

    private MirModule ParseModule()
    {
        var module = new MirModule();
        while (_current.Kind != MirTokenKind.Eof)
            module.Functions.Add(ParseFunction());
        return module;
    }

    private MirFunction ParseFunction()
    {
        Expect(MirTokenKind.Func);
        Expect(MirTokenKind.At);
        var name = Expect(MirTokenKind.Identifier).Text!;
        Expect(MirTokenKind.Colon);
        var paramTypes = ParseSignature();
        Expect(MirTokenKind.Arrow);
        var returnType = ParseType();
        Expect(MirTokenKind.LBrace);

        // Collect every token in the function body so the inner two-pass
        // block resolution can do its own scan without re-lexing.
        var body = CollectBodyTokens();

        var function = new MirFunction(name, paramTypes, returnType);

        // Pass 1: identify block headers (BlockLabel LParen ... RParen Colon).
        // A bare `bbN` or `bbN(args)` without a trailing colon is a block-target
        // use operand, not a header.
        var headerPositions = ScanBlockHeaderPositions(body);
        var blockMap = new Dictionary<long, MirBlock>();
        foreach (var idx in headerPositions.OrderBy(x => x))
        {
            var blockId = body[idx].IntValue!.Value;
            blockMap[blockId] = function.CreateBlock();
        }

        // Pass 2: parse each block's parameters / liveins / instructions.
        var tokens = new TokenReader(body);
        while (!tokens.IsAtEnd)
            ParseBlock(tokens, function, blockMap, headerPositions);

        return function;
    }

    // -------------------------------------------------------------------------
    // Signature / type
    // -------------------------------------------------------------------------

    private IRType[] ParseSignature()
    {
        Expect(MirTokenKind.LParen);
        if (_current.Kind == MirTokenKind.RParen)
        {
            Advance();
            return [];
        }
        var types = new List<IRType> { ParseType() };
        while (_current.Kind == MirTokenKind.Comma)
        {
            Advance();
            types.Add(ParseType());
        }
        Expect(MirTokenKind.RParen);
        return types.ToArray();
    }

    private IRType ParseType()
    {
        var token = Expect(MirTokenKind.Identifier);
        return ParseTypeName(token);
    }

    private static IRType ParseTypeName(MirToken token) => token.Text switch
    {
        "void" => IRType.Void,
        "i1"   => IRType.I1,
        "i8"   => IRType.I8,
        "i16"  => IRType.I16,
        "i32"  => IRType.I32,
        _ => throw Fail(token, $"Unknown type '{token.Text}'"),
    };

    private static bool IsTypeName(string? name) =>
        name is "void" or "i1" or "i8" or "i16" or "i32";

    // -------------------------------------------------------------------------
    // Block-header scan (pass 1)
    // -------------------------------------------------------------------------

    private static HashSet<int> ScanBlockHeaderPositions(List<MirToken> body)
    {
        var headers = new HashSet<int>();
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Kind != MirTokenKind.BlockLabel) continue;
            if (i + 1 >= body.Count || body[i + 1].Kind != MirTokenKind.LParen) continue;

            var depth = 0;
            var j = i + 1;
            while (j < body.Count)
            {
                if (body[j].Kind == MirTokenKind.LParen)
                {
                    depth++;
                }
                else if (body[j].Kind == MirTokenKind.RParen)
                {
                    depth--;
                    if (depth == 0) { j++; break; }
                }
                j++;
            }
            if (j < body.Count && body[j].Kind == MirTokenKind.Colon)
                headers.Add(i);
        }
        return headers;
    }

    // -------------------------------------------------------------------------
    // Block / instruction  (token-reader based — supports forward block refs)
    // -------------------------------------------------------------------------

    private void ParseBlock(
        TokenReader tokens,
        MirFunction function,
        Dictionary<long, MirBlock> blockMap,
        HashSet<int> headerPositions)
    {
        var labelToken = tokens.Expect(MirTokenKind.BlockLabel);
        var block = blockMap[labelToken.IntValue!.Value];

        tokens.Expect(MirTokenKind.LParen);

        if (tokens.Current.Kind == MirTokenKind.ValueRef)
        {
            ParseBlockParameter(tokens, function, block);
            while (tokens.Current.Kind == MirTokenKind.Comma)
            {
                tokens.Advance();
                ParseBlockParameter(tokens, function, block);
            }
        }

        tokens.Expect(MirTokenKind.RParen);
        tokens.Expect(MirTokenKind.Colon);

        // Optional `[liveins: $a, $b, ...]` on a line by itself.
        if (tokens.Current.Kind == MirTokenKind.LBracket)
        {
            tokens.Advance(); // consume '['
            var keyword = tokens.Expect(MirTokenKind.Identifier);
            if (keyword.Text != "liveins")
                throw Fail(keyword, $"Expected 'liveins', got '{keyword.Text}'");
            tokens.Expect(MirTokenKind.Colon);
            while (tokens.Current.Kind == MirTokenKind.PhysRegRef)
            {
                block.LiveIns.Add(ResolvePhysReg(tokens.Advance()));
                if (tokens.Current.Kind == MirTokenKind.Comma)
                    tokens.Advance();
                else
                    break;
            }
            tokens.Expect(MirTokenKind.RBracket);
        }

        while (!tokens.IsAtEnd && !headerPositions.Contains(tokens.Position))
            ParseInstruction(tokens, function, block, blockMap, headerPositions);
    }

    private void ParseBlockParameter(
        TokenReader tokens,
        MirFunction function,
        MirBlock block)
    {
        var vregToken = tokens.Expect(MirTokenKind.ValueRef);
        tokens.Expect(MirTokenKind.Colon);
        var annotationToken = tokens.Expect(MirTokenKind.Identifier);

        var id = (int)vregToken.IntValue!.Value;
        function.RegisterVirtualRegister(id, ParseAnnotation(annotationToken));
        block.Parameters.Add(id);
    }

    private static VRegAnnotation ParseAnnotation(MirToken token) =>
        IsTypeName(token.Text)
            ? new TypedVReg(ParseTypeName(token))
            // ClassId is unknown without a target — store -1 as a placeholder.
            // Round-tripping preserves the textual class name.
            : new ClassedVReg(ClassId: -1, Name: token.Text!);

    private void ParseInstruction(
        TokenReader tokens,
        MirFunction function,
        MirBlock block,
        Dictionary<long, MirBlock> blockMap,
        HashSet<int> headerPositions)
    {
        var defOperands = new List<MirOperand>();

        // Vreg defs: %N : annotation, %M : annotation = opcode ...
        // (A bare %N without a Colon is a use operand.)
        while (tokens.Current.Kind == MirTokenKind.ValueRef
               && tokens.Peek.Kind == MirTokenKind.Colon)
        {
            var vregToken = tokens.Advance();
            tokens.Advance(); // consume ':'
            var annotationToken = tokens.Expect(MirTokenKind.Identifier);

            var vreg = (int)vregToken.IntValue!.Value;
            function.RegisterVirtualRegister(vreg, ParseAnnotation(annotationToken));
            defOperands.Add(new VirtualReg(vreg, IsDefinition: true));

            // Consume comma only if it precedes another `%N :` def, not a use.
            if (tokens.Current.Kind == MirTokenKind.Comma
                && tokens.Peek.Kind == MirTokenKind.ValueRef
                && tokens.Peek2.Kind == MirTokenKind.Colon)
            {
                tokens.Advance();
            }
            else
            {
                break;
            }
        }

        // Physreg defs (only valid when there are no vreg defs): $a = ...
        // or $a, $c = ...
        if (defOperands.Count == 0 && tokens.Current.Kind == MirTokenKind.PhysRegRef)
        {
            defOperands.Add(new PhysicalReg(ResolvePhysReg(tokens.Advance()), IsDefinition: true));
            while (tokens.Current.Kind == MirTokenKind.Comma
                   && tokens.Peek.Kind == MirTokenKind.PhysRegRef)
            {
                tokens.Advance(); // consume ','
                defOperands.Add(new PhysicalReg(ResolvePhysReg(tokens.Advance()), IsDefinition: true));
            }
        }

        if (defOperands.Count > 0)
            tokens.Expect(MirTokenKind.Equals);

        var opcodeRef = ParseOpcode(tokens);

        // Use operands: comma-separated; loop terminates when we hit the start of
        // the next instruction, block header, or end-of-body.
        var useOperands = new List<MirOperand>();
        while (IsUseOperandStart(tokens, headerPositions))
        {
            useOperands.Add(ParseUseOperand(tokens, blockMap, headerPositions, opcodeRef, useOperands.Count));
            if (tokens.Current.Kind == MirTokenKind.Comma)
                tokens.Advance();
            else
                break;
        }

        block.AddInstruction(opcodeRef, [.. defOperands, .. useOperands]);
    }

    private static OpcodeRef ParseOpcode(TokenReader tokens)
    {
        var prefixToken = tokens.Expect(MirTokenKind.Identifier);
        tokens.Expect(MirTokenKind.Dot);
        var firstOpPart = tokens.Expect(MirTokenKind.Identifier);

        var opName = new StringBuilder(firstOpPart.Text);
        while (tokens.Current.Kind == MirTokenKind.Dot
               && tokens.Peek.Kind == MirTokenKind.Identifier)
        {
            tokens.Advance(); // consume '.'
            opName.Append('.').Append(tokens.Advance().Text);
        }

        if (!DialectRegistry.TryByPrefix(prefixToken.Text!, out var dialect))
            throw Fail(prefixToken, $"Unknown dialect '{prefixToken.Text}'");
        if (!dialect.TryParseOp(opName.ToString(), out var code))
            throw Fail(firstOpPart, $"Unknown opcode '{prefixToken.Text}.{opName}'");

        return new OpcodeRef(dialect.Id, code);
    }

    private static bool IsUseOperandStart(TokenReader tokens, HashSet<int> headerPositions) => tokens.Current.Kind switch
    {
        MirTokenKind.PhysRegRef => true,
        MirTokenKind.Integer    => true,
        MirTokenKind.At         => true,
        // A BlockLabel inside an instruction is always a use operand (a header
        // would have terminated the enclosing ParseInstruction loop).
        MirTokenKind.BlockLabel when !headerPositions.Contains(tokens.Position) => true,
        // %N not followed by `:` is unambiguously a use; the def form is `%N : annotation`.
        MirTokenKind.ValueRef when tokens.Peek.Kind != MirTokenKind.Colon => true,
        // An Identifier followed by `.` would be the next instruction's opcode,
        // so it terminates the use loop. Otherwise it's either `implicit` /
        // `implicit-def`, or a dialect-specific symbolic immediate (e.g. cmpi's
        // predicate name `slt`).
        MirTokenKind.Identifier when tokens.Peek.Kind != MirTokenKind.Dot => true,
        _ => false,
    };

    private MirOperand ParseUseOperand(
        TokenReader tokens,
        Dictionary<long, MirBlock> blockMap,
        HashSet<int> headerPositions,
        OpcodeRef opcode,
        int useIndex)
    {
        switch (tokens.Current.Kind)
        {
            case MirTokenKind.ValueRef:
            {
                var vreg = (int)tokens.Advance().IntValue!.Value;
                // Optional `(tied-def N)` annotation; the value is derivable
                // from DialectInstructionInfo.TiedOperands so we discard it.
                if (tokens.Current.Kind == MirTokenKind.LParen)
                {
                    tokens.Advance(); // consume '('
                    var keyword = tokens.Expect(MirTokenKind.Identifier);
                    if (keyword.Text != "tied-def")
                        throw Fail(keyword, $"Expected 'tied-def', got '{keyword.Text}'");
                    tokens.Expect(MirTokenKind.Integer); // def index
                    tokens.Expect(MirTokenKind.RParen);
                }
                return new VirtualReg(vreg, IsDefinition: false);
            }

            case MirTokenKind.PhysRegRef:
                return new PhysicalReg(ResolvePhysReg(tokens.Advance()), IsDefinition: false);

            case MirTokenKind.Integer:
                return new Immediate(tokens.Advance().IntValue!.Value);

            case MirTokenKind.BlockLabel:
            {
                var labelToken = tokens.Advance();
                var target = blockMap[labelToken.IntValue!.Value];

                MirOperand[] args;
                if (tokens.Current.Kind == MirTokenKind.LParen)
                {
                    tokens.Advance(); // consume '('
                    if (tokens.Current.Kind == MirTokenKind.RParen)
                    {
                        args = [];
                    }
                    else
                    {
                        // Block-target args are not dialect-aware (no opcode
                        // context here); use a sentinel index of -1.
                        var argList = new List<MirOperand> { ParseUseOperand(tokens, blockMap, headerPositions, opcode, -1) };
                        while (tokens.Current.Kind == MirTokenKind.Comma)
                        {
                            tokens.Advance();
                            argList.Add(ParseUseOperand(tokens, blockMap, headerPositions, opcode, -1));
                        }
                        args = argList.ToArray();
                    }
                    tokens.Expect(MirTokenKind.RParen);
                }
                else
                {
                    args = [];
                }
                return new BlockTarget(target, args);
            }

            case MirTokenKind.At:
            {
                tokens.Advance(); // consume '@'
                var nameToken = tokens.Expect(MirTokenKind.Identifier);
                return new Symbol(nameToken.Text!);
            }

            case MirTokenKind.Identifier when tokens.Current.Text is "implicit" or "implicit-def":
            {
                var isDef = tokens.Advance().Text == "implicit-def";
                var physToken = tokens.Expect(MirTokenKind.PhysRegRef);
                return new PhysicalReg(ResolvePhysReg(physToken), IsDefinition: isDef, IsImplicit: true);
            }

            case MirTokenKind.Identifier:
            {
                // Dialect-specific symbolic immediate (e.g. arith.cmpi's `slt`).
                var identToken = tokens.Advance();
                var dialect = DialectRegistry.ById(opcode.Dialect);
                if (useIndex >= 0
                    && dialect.TryParseImmediateUse(opcode.Code, useIndex, identToken.Text!, out var value))
                    return new Immediate(value);
                throw Fail(identToken,
                    $"Unexpected identifier '{identToken.Text}' in operand position {useIndex} of opcode {dialect.Prefix}.{dialect.GetOpName(opcode.Code)}");
            }

            default:
                throw Fail(tokens.Current, $"Expected operand, got '{tokens.Current.Kind}'");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int ResolvePhysReg(MirToken token)
    {
        if (token.IntValue.HasValue)
            return (int)token.IntValue.Value;

        // Name resolution requires a target's RegisterInfo. When a target is
        // wired into the parser (later steps), this branch will look the name
        // up; for now, target-less parsing only accepts numeric '$N'.
        throw Fail(token, $"Symbolic physical register '${token.Text}' requires a target — only numeric '$N' is supported.");
    }

    private List<MirToken> CollectBodyTokens()
    {
        var tokens = new List<MirToken>();
        while (_current.Kind != MirTokenKind.RBrace && _current.Kind != MirTokenKind.Eof)
        {
            tokens.Add(_current);
            Advance();
        }
        Expect(MirTokenKind.RBrace);
        return tokens;
    }

    private MirToken Expect(MirTokenKind kind)
    {
        if (_current.Kind != kind)
            throw Fail(_current, $"Expected {kind}, got '{_current.Kind}'");
        return Advance();
    }

    private MirToken Advance()
    {
        var token = _current;
        _current = _lexer.NextToken();
        return token;
    }

    private static ParseException Fail(MirToken token, string message) =>
        new(new Diagnostic(token.Line, token.Column, message));

    // -------------------------------------------------------------------------
    // TokenReader — wraps a pre-collected token list for two-pass block parsing
    // -------------------------------------------------------------------------

    private sealed class TokenReader(List<MirToken> tokens)
    {
        private int _position;

        public int Position => _position;
        public MirToken Current => At(_position);
        public MirToken Peek    => At(_position + 1);
        public MirToken Peek2   => At(_position + 2);
        public bool IsAtEnd     => _position >= tokens.Count;

        private MirToken At(int pos) =>
            pos < tokens.Count
                ? tokens[pos]
                : new MirToken(MirTokenKind.Eof, 0, 0);

        public MirToken Advance()
        {
            var token = Current;
            if (_position < tokens.Count) _position++;
            return token;
        }

        public MirToken Expect(MirTokenKind kind)
        {
            if (Current.Kind != kind)
                throw Fail(Current, $"Expected {kind}, got '{Current.Kind}'");
            return Advance();
        }

        private static ParseException Fail(MirToken token, string message) =>
            new(new Diagnostic(token.Line, token.Column, message));
    }
}
