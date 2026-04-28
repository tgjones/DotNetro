using Irie.IR;
using Irie.IR.Parsing;

namespace Irie.CodeGen.Parsing;

internal sealed class MachineParser
{
    private readonly MachineLexer _lexer;
    private MachineToken _current;

    private MachineParser(TextReader reader)
    {
        _lexer = new MachineLexer(reader);
        _current = _lexer.NextToken();
    }

    public static MachineModule Parse(TextReader reader) => new MachineParser(reader).ParseModule();

    // -------------------------------------------------------------------------
    // Module / function header  (streaming — no forward refs at this level)
    // -------------------------------------------------------------------------

    private MachineModule ParseModule()
    {
        var module = new MachineModule();
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

    private static void ParseBlock(
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
                block.LiveIns.Add((int)tokens.Advance().IntValue!.Value);
                if (tokens.Current.Kind == MachineTokenKind.Comma)
                    tokens.Advance();
                else
                    break;
            }
        }

        while (!tokens.IsAtEnd && tokens.Current.Kind != MachineTokenKind.BlockLabel)
            ParseInstruction(tokens, function, block, blockMap);
    }

    private static void ParseBlockParameter(
        TokenReader tokens,
        MachineFunction function,
        MachineBasicBlock block)
    {
        var vregToken = tokens.Expect(MachineTokenKind.ValueRef);
        tokens.Expect(MachineTokenKind.Colon);
        var type = ParseType(tokens.Expect(MachineTokenKind.Identifier));

        function.RegisterVirtualRegister((int)vregToken.IntValue!.Value, type);
        block.Parameters.Add((int)vregToken.IntValue.Value);
    }

    private static void ParseInstruction(
        TokenReader tokens,
        MachineFunction function,
        MachineBasicBlock block,
        Dictionary<long, MachineBasicBlock> blockMap)
    {
        var defOperands = new List<MachineOperand>();

        // Parse zero or more virtual-register definitions: %N:type, ...
        // A def is identified by ValueRef immediately followed by Colon.
        // Multiple defs are separated by commas: %a:type, %b:type = opcode
        while (tokens.Current.Kind == MachineTokenKind.ValueRef
               && tokens.Peek.Kind == MachineTokenKind.Colon)
        {
            var vregToken = tokens.Advance(); // consume %N
            tokens.Advance();                 // consume :
            var type = ParseType(tokens.Expect(MachineTokenKind.Identifier));

            var vreg = (int)vregToken.IntValue!.Value;
            function.RegisterVirtualRegister(vreg, type);
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

        // Physical register definition: $N =
        if (defOperands.Count == 0 && tokens.Current.Kind == MachineTokenKind.PhysRegRef)
        {
            var physToken = tokens.Advance();
            defOperands.Add(new PhysicalRegisterOperand((int)physToken.IntValue!.Value, IsDefinition: true));
        }

        if (defOperands.Count > 0)
            tokens.Expect(MachineTokenKind.Equals);

        // Opcode
        var opcodeToken = tokens.Expect(MachineTokenKind.Identifier);
        if (!GenericOpcode.TryParse(opcodeToken.Text!, out var opcode))
            throw Fail(opcodeToken, $"Unknown opcode '{opcodeToken.Text}'");

        // Use operands (comma-separated; terminated by start of next def or block label)
        var useOperands = new List<MachineOperand>();
        while (IsUseOperandStart(tokens))
        {
            useOperands.Add(ParseUseOperand(tokens, blockMap));
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
        MachineTokenKind.BlockLabel => true,
        MachineTokenKind.At         => true,
        // %N is a use only when NOT immediately followed by ':', which would mean a new def.
        MachineTokenKind.ValueRef when tokens.Peek.Kind != MachineTokenKind.Colon => true,
        _ => false,
    };

    private static MachineOperand ParseUseOperand(
        TokenReader tokens,
        Dictionary<long, MachineBasicBlock> blockMap)
    {
        switch (tokens.Current.Kind)
        {
            case MachineTokenKind.ValueRef:
                return new VirtualRegisterOperand((int)tokens.Advance().IntValue!.Value, IsDefinition: false);

            case MachineTokenKind.PhysRegRef:
                return new PhysicalRegisterOperand((int)tokens.Advance().IntValue!.Value, IsDefinition: false);

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

            default:
                throw Fail(tokens.Current, $"Expected operand, got '{tokens.Current.Kind}'");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IRType ParseType(MachineToken token) => token.Text switch
    {
        "void" => IRType.Void,
        "i1"   => IRType.I1,
        "i8"   => IRType.I8,
        "i16"  => IRType.I16,
        "i32"  => IRType.I32,
        _ => throw Fail(token, $"Unknown type '{token.Text}'"),
    };

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
