using Irie.IR;
using Irie.IR.Parsing;

namespace Irie.CodeGen.Parsing;

internal sealed class MachineParser(Target target)
{
    private readonly TargetRegisterInfo _registerInfo = target.CreateRegisterInfo();
    private readonly TargetInstructionInfo _instrInfo = target.CreateInstructionInfo();
    private MachineLexer _lexer = null!;
    private MachineToken _current = null!;

    public static MachineModule Parse(TextReader reader, Target target)
    {
        var parser = new MachineParser(target);
        parser._lexer = new MachineLexer(reader);
        parser._current = parser._lexer.NextToken();
        return parser.ParseModule();
    }

    // -------------------------------------------------------------------------
    // Module / function header  (streaming — no forward refs at this level)
    // -------------------------------------------------------------------------

    private MachineModule ParseModule()
    {
        var module = new MachineModule(target);
        while (_current.Kind != MachineTokenKind.Eof)
            module.Functions.Add(ParseFunction());
        return module;
    }

    private MachineFunction ParseFunction()
    {
        Expect(MachineTokenKind.Func);
        Expect(MachineTokenKind.At);
        var name = Expect(MachineTokenKind.Identifier).Text!;
        Expect(MachineTokenKind.LBrace);

        // Collect all tokens in the function body so we can do two passes
        // for forward block-reference resolution.
        var body = CollectBodyTokens();
        var tokens = new TokenReader(body);

        var function = new MachineFunction(name);

        // Pass 1: pre-create one MachineBasicBlock per block header found, in order.
        // A block header is a BlockLabel immediately followed by '(' — this distinguishes
        // header occurrences from operand references in branch instructions.
        var blockMap = new Dictionary<long, MachineBasicBlock>();
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Kind == MachineTokenKind.BlockLabel
                && i + 1 < body.Count
                && body[i + 1].Kind == MachineTokenKind.LParen)
            {
                var idx = body[i].IntValue!.Value;
                var block = new MachineBasicBlock();
                block.Parent = function;
                blockMap[idx] = block;
                function.Blocks.Add(block);
            }
        }

        // Pass 2: parse each block's parameters and instructions.
        while (!tokens.IsAtEnd)
            ParseBlock(tokens, function, blockMap);

        return function;
    }

    // -------------------------------------------------------------------------
    // Block / instruction  (token-reader based — supports forward block refs)
    // -------------------------------------------------------------------------

    private void ParseBlock(
        TokenReader tokens,
        MachineFunction function,
        Dictionary<long, MachineBasicBlock> blockMap)
    {
        var labelToken = tokens.Expect(MachineTokenKind.BlockLabel);
        var block = blockMap[labelToken.IntValue!.Value];

        tokens.Expect(MachineTokenKind.LParen);

        if (tokens.Current.Kind == MachineTokenKind.ValueRef)
        {
            ParseBlockParameter(tokens, function, block);
            while (tokens.Current.Kind == MachineTokenKind.Comma)
            {
                tokens.Advance();
                ParseBlockParameter(tokens, function, block);
            }
        }

        tokens.Expect(MachineTokenKind.RParen);
        tokens.Expect(MachineTokenKind.Colon);

        if (tokens.Current.Kind == MachineTokenKind.Identifier
            && tokens.Current.Text == "liveins"
            && tokens.Peek.Kind == MachineTokenKind.Colon)
        {
            tokens.Advance(); // consume "liveins"
            tokens.Advance(); // consume ":"
            while (tokens.Current.Kind == MachineTokenKind.PhysRegRef)
            {
                block.LiveIns.Add(ResolvePhysReg(tokens.Advance()));
                if (tokens.Current.Kind == MachineTokenKind.Comma)
                    tokens.Advance();
                else
                    break;
            }
        }

        while (!tokens.IsAtEnd && tokens.Current.Kind != MachineTokenKind.BlockLabel)
            ParseInstruction(tokens, function, block, blockMap);
    }

    private void ParseBlockParameter(
        TokenReader tokens,
        MachineFunction function,
        MachineBasicBlock block)
    {
        var vregToken = tokens.Expect(MachineTokenKind.ValueRef);
        tokens.Expect(MachineTokenKind.Colon);
        var annotationToken = tokens.Expect(MachineTokenKind.Identifier);

        var id = (int)vregToken.IntValue!.Value;

        if (TryParseType(annotationToken, out var type))
            function.RegisterVirtualRegister(id, type);
        else if (TryParseClass(annotationToken, out var classId))
            function.RegisterVirtualRegisterWithClass(id, classId);
        else
            throw Fail(annotationToken, $"Unknown type or register class '{annotationToken.Text}'");

        block.Parameters.Add(id);
    }

    private void ParseInstruction(
        TokenReader tokens,
        MachineFunction function,
        MachineBasicBlock block,
        Dictionary<long, MachineBasicBlock> blockMap)
    {
        var defOperands = new List<MachineOperand>();

        // Parse zero or more virtual-register definitions: %N:type_or_class, ...
        // A def is identified by ValueRef immediately followed by Colon.
        // Multiple defs are separated by commas: %a:type, %b:type = opcode
        while (tokens.Current.Kind == MachineTokenKind.ValueRef
               && tokens.Peek.Kind == MachineTokenKind.Colon)
        {
            var vregToken = tokens.Advance(); // consume %N
            tokens.Advance();                 // consume :
            var annotationToken = tokens.Expect(MachineTokenKind.Identifier);

            var vreg = (int)vregToken.IntValue!.Value;

            if (TryParseType(annotationToken, out var type))
                function.RegisterVirtualRegister(vreg, type);
            else if (TryParseClass(annotationToken, out var classId))
                function.RegisterVirtualRegisterWithClass(vreg, classId);
            else
                throw Fail(annotationToken, $"Unknown type or register class '{annotationToken.Text}'");

            defOperands.Add(new VirtualRegisterOperand(vreg, IsDefinition: true));

            // Check if a comma is followed by another %N:type (more defs) or a use.
            // We need three tokens of lookahead: comma, ValueRef, Colon.
            if (tokens.Current.Kind == MachineTokenKind.Comma
                && tokens.Peek.Kind == MachineTokenKind.ValueRef
                && tokens.Peek2.Kind == MachineTokenKind.Colon)
            {
                tokens.Advance(); // consume comma; loop continues
            }
            else
            {
                break;
            }
        }

        // Physical register definition(s): $A = ... or $A, $C = ...
        if (defOperands.Count == 0 && tokens.Current.Kind == MachineTokenKind.PhysRegRef)
        {
            defOperands.Add(new PhysicalRegisterOperand(ResolvePhysReg(tokens.Advance()), IsDefinition: true));
            while (tokens.Current.Kind == MachineTokenKind.Comma
                   && tokens.Peek.Kind == MachineTokenKind.PhysRegRef)
            {
                tokens.Advance(); // consume comma
                defOperands.Add(new PhysicalRegisterOperand(ResolvePhysReg(tokens.Advance()), IsDefinition: true));
            }
        }

        if (defOperands.Count > 0)
            tokens.Expect(MachineTokenKind.Equals);

        // Opcode — try generic first, then target-specific via target.ParseOpcode
        var opcodeToken = tokens.Expect(MachineTokenKind.Identifier);
        int opcode;
        if (GenericOpcode.TryParse(opcodeToken.Text!, out opcode))
        {
            // generic opcode — ok
        }
        else if (_instrInfo.ParseDisplayName(opcodeToken.Text!) is { } targetOpcode)
        {
            opcode = targetOpcode;
        }
        else
        {
            throw Fail(opcodeToken, $"Unknown opcode '{opcodeToken.Text}'");
        }

        // Use operands (comma-separated; terminated by start of next def or block label)
        var useOperands = new List<MachineOperand>();
        while (IsUseOperandStart(tokens))
        {
            useOperands.Add(ParseUseOperand(tokens, function, blockMap));
            if (tokens.Current.Kind == MachineTokenKind.Comma)
                tokens.Advance();
            else
                break;
        }

        block.AddInstruction(opcode, [.. defOperands, .. useOperands]);
    }

    private static bool IsUseOperandStart(TokenReader tokens) => tokens.Current.Kind switch
    {
        MachineTokenKind.PhysRegRef => true,
        MachineTokenKind.Integer    => true,
        // A BlockLabel followed by '(' is a block header (bbN(...):), not a use operand.
        MachineTokenKind.BlockLabel when tokens.Peek.Kind != MachineTokenKind.LParen => true,
        MachineTokenKind.At         => true,
        // %N without ':' is unambiguously a use.
        MachineTokenKind.ValueRef when tokens.Peek.Kind != MachineTokenKind.Colon => true,
        // %N:ClassName is a use operand (post-isel class annotation) when the token
        // after the class name is NOT '=', which would indicate a new def start.
        // Note: %N:Class, %M:Class = NextOpcode (multi-def next instruction) can
        // only be disambiguated if the ',' is followed by '=' eventually; with
        // bounded lookahead we check Peek3 ≠ Equals, which is safe for all output
        // our passes produce (no zero-use instruction precedes a multi-def instruction).
        MachineTokenKind.ValueRef when tokens.Peek.Kind == MachineTokenKind.Colon
                                   && tokens.Peek2.Kind == MachineTokenKind.Identifier
                                   && tokens.Peek3.Kind != MachineTokenKind.Equals => true,
        // 'implicit' / 'implicit-def' prefixing a physical register operand.
        MachineTokenKind.Identifier when tokens.Current.Text is "implicit" or "implicit-def"
                                      && tokens.Peek.Kind == MachineTokenKind.PhysRegRef => true,
        _ => false,
    };

    private MachineOperand ParseUseOperand(
        TokenReader tokens,
        MachineFunction function,
        Dictionary<long, MachineBasicBlock> blockMap)
    {
        switch (tokens.Current.Kind)
        {
            case MachineTokenKind.ValueRef:
            {
                var vreg = (int)tokens.Advance().IntValue!.Value;

                // Consume optional :ClassName(tied-def M) or bare :ClassName suffix.
                if (tokens.Current.Kind == MachineTokenKind.Colon
                    && tokens.Peek.Kind == MachineTokenKind.Identifier)
                {
                    tokens.Advance(); // consume ':'
                    var classToken = tokens.Advance(); // consume class name

                    if (TryParseClass(classToken, out var classId))
                        function.RegisterVirtualRegisterWithClass(vreg, classId);

                    // Consume optional (tied-def M) annotation; the value is
                    // derivable from the instruction descriptor so we discard it.
                    if (tokens.Current.Kind == MachineTokenKind.LParen)
                    {
                        tokens.Advance(); // consume '('
                        tokens.Expect(MachineTokenKind.Identifier); // "tied-def"
                        tokens.Expect(MachineTokenKind.Integer);    // def index
                        tokens.Expect(MachineTokenKind.RParen);     // ')'
                    }
                }

                return new VirtualRegisterOperand(vreg, IsDefinition: false);
            }

            case MachineTokenKind.PhysRegRef:
                return new PhysicalRegisterOperand(ResolvePhysReg(tokens.Advance()), IsDefinition: false);

            case MachineTokenKind.Integer:
                return new ImmediateOperand(tokens.Advance().IntValue!.Value);

            case MachineTokenKind.BlockLabel:
                return new BlockOperand(blockMap[tokens.Advance().IntValue!.Value]);

            case MachineTokenKind.At:
            {
                tokens.Advance(); // consume '@'
                var nameToken = tokens.Expect(MachineTokenKind.Identifier);
                return new ExternalSymbolOperand(nameToken.Text!);
            }

            case MachineTokenKind.Identifier when tokens.Current.Text is "implicit" or "implicit-def":
            {
                var isDef = tokens.Advance().Text == "implicit-def";
                var physToken = tokens.Expect(MachineTokenKind.PhysRegRef);
                return new PhysicalRegisterOperand(ResolvePhysReg(physToken), IsDefinition: isDef, IsImplicit: true);
            }

            default:
                throw Fail(tokens.Current, $"Expected operand, got '{tokens.Current.Kind}'");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private int ResolvePhysReg(MachineToken token)
    {
        // Integer-based: $0, $1 etc.
        if (token.IntValue.HasValue)
            return (int)token.IntValue.Value;

        // Name-based: $A, $X, $RC2 etc.
        var name = token.Text!;
        if (_registerInfo.ParseRegister(name) is { } id)
            return id;

        throw Fail(token, $"Unknown physical register '${name}'");
    }

    private bool TryParseClass(MachineToken token, out int classId)
    {
        classId = 0;
        var result = _registerInfo.ParseRegisterClass(token.Text!);
        if (result == null) return false;
        classId = result.Value;
        return true;
    }

    private static bool TryParseType(MachineToken token, out IRType type)
    {
        type = token.Text switch
        {
            "void" => IRType.Void,
            "i1"   => IRType.I1,
            "i8"   => IRType.I8,
            "i16"  => IRType.I16,
            "i32"  => IRType.I32,
            _ => null!,
        };
        return type != null;
    }

    // Collects all tokens between the opening '{' (already consumed) and the
    // matching '}', consuming the '}' itself.  Function bodies have no nested
    // braces, so we simply stop at the first '}'.
    private List<MachineToken> CollectBodyTokens()
    {
        var tokens = new List<MachineToken>();
        while (_current.Kind != MachineTokenKind.RBrace && _current.Kind != MachineTokenKind.Eof)
        {
            tokens.Add(_current);
            Advance();
        }
        Expect(MachineTokenKind.RBrace);
        return tokens;
    }

    private MachineToken Expect(MachineTokenKind kind)
    {
        if (_current.Kind != kind)
            throw Fail(_current, $"Expected {kind}, got '{_current.Kind}'");
        return Advance();
    }

    private MachineToken Advance()
    {
        var token = _current;
        _current = _lexer.NextToken();
        return token;
    }

    private static ParseException Fail(MachineToken token, string message) =>
        new(new Diagnostic(token.Line, token.Column, message));

    // -------------------------------------------------------------------------
    // TokenReader — wraps a pre-collected token list for two-pass block parsing
    // -------------------------------------------------------------------------

    private sealed class TokenReader(List<MachineToken> tokens)
    {
        private int _position;

        public MachineToken Current => At(_position);
        public MachineToken Peek    => At(_position + 1);
        public MachineToken Peek2   => At(_position + 2);
        public MachineToken Peek3   => At(_position + 3);
        public bool IsAtEnd         => _position >= tokens.Count;

        private MachineToken At(int pos) =>
            pos < tokens.Count
                ? tokens[pos]
                : new MachineToken(MachineTokenKind.Eof, 0, 0);

        public MachineToken Advance()
        {
            var token = Current;
            if (_position < tokens.Count) _position++;
            return token;
        }

        public MachineToken Expect(MachineTokenKind kind)
        {
            if (Current.Kind != kind)
                throw Fail(Current, $"Expected {kind}, got '{Current.Kind}'");
            return Advance();
        }

        private static ParseException Fail(MachineToken token, string message) =>
            new(new Diagnostic(token.Line, token.Column, message));
    }
}
