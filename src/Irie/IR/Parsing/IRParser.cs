namespace Irie.IR.Parsing;

internal sealed class IRParser
{
    private readonly IRLexer _lexer;
    private Token _current;
    private readonly Dictionary<int, IRValue> _valueMap = [];

    private IRParser(TextReader reader)
    {
        _lexer = new IRLexer(reader);
        _current = _lexer.NextToken();
    }

    public static IRModule Parse(TextReader reader) => new IRParser(reader).ParseModule();

    private IRModule ParseModule()
    {
        var module = new IRModule();
        while (_current.Kind != TokenKind.Eof)
        {
            module.Functions.Add(ParseFunction());
        }
        return module;
    }

    private IRFunction ParseFunction()
    {
        Expect(TokenKind.Func);
        Expect(TokenKind.At);
        var name = Expect(TokenKind.Identifier).Text!;
        Expect(TokenKind.Colon);
        Expect(TokenKind.LParen);

        var paramTypes = new List<IRType>();
        if (_current.Kind != TokenKind.RParen)
        {
            paramTypes.Add(ParseType());
            while (_current.Kind == TokenKind.Comma)
            {
                Advance();
                paramTypes.Add(ParseType());
            }
        }

        Expect(TokenKind.RParen);
        Expect(TokenKind.Arrow);
        var returnType = ParseType();
        Expect(TokenKind.LBrace);

        _valueMap.Clear();
        var function = new IRFunction(name, [.. paramTypes], returnType);

        while (_current.Kind == TokenKind.BlockLabel)
        {
            ParseBlock(function);
        }

        Expect(TokenKind.RBrace);
        return function;
    }

    private void ParseBlock(IRFunction function)
    {
        Expect(TokenKind.BlockLabel);
        Expect(TokenKind.LParen);

        var block = new IRBasicBlock();

        if (_current.Kind == TokenKind.ValueRef)
        {
            ParseBlockArg(block);
            while (_current.Kind == TokenKind.Comma)
            {
                Advance();
                ParseBlockArg(block);
            }
        }

        Expect(TokenKind.RParen);
        Expect(TokenKind.Colon);

        while (_current.Kind != TokenKind.BlockLabel &&
               _current.Kind != TokenKind.RBrace &&
               _current.Kind != TokenKind.Eof)
        {
            ParseInstruction(block);
        }

        function.Blocks.Add(block);
    }

    private void ParseBlockArg(IRBasicBlock block)
    {
        var refToken = Expect(TokenKind.ValueRef);
        Expect(TokenKind.Colon);
        var type = ParseType();
        var arg = block.CreateArgument(type);
        RegisterValue(refToken, (int)refToken.IntValue!.Value, arg);
    }

    private void ParseInstruction(IRBasicBlock block)
    {
        Token? resultToken = null;

        if (_current.Kind == TokenKind.ValueRef)
        {
            resultToken = _current;
            Advance();
            Expect(TokenKind.Equals);
        }

        var instruction = _current.Kind switch
        {
            TokenKind.IntegerLiteralOpcode => ParseIntegerLiteral(block),
            TokenKind.IntegerAdd => ParseIntegerAdd(block),
            TokenKind.Return => ParseReturn(block),
            _ => throw Fail(_current, $"Expected instruction opcode, got '{_current.Kind}'"),
        };

        if (resultToken != null)
        {
            RegisterValue(resultToken, (int)resultToken.IntValue!.Value, instruction);
        }
    }

    private IRInstruction ParseIntegerLiteral(IRBasicBlock block)
    {
        Advance(); // consume 'integer_literal'
        var type = ParseType();
        var value = Expect(TokenKind.Integer).IntValue!.Value;
        return block.CreateIntegerLiteral(type, value);
    }

    private IRInstruction ParseIntegerAdd(IRBasicBlock block)
    {
        Advance(); // consume 'integer_add'
        var lhsToken = Expect(TokenKind.ValueRef);
        var lhs = LookupValue(lhsToken);
        Expect(TokenKind.Comma);
        var rhsToken = Expect(TokenKind.ValueRef);
        var rhs = LookupValue(rhsToken);
        return block.CreateIntegerAdd(lhs, rhs);
    }

    private IRInstruction ParseReturn(IRBasicBlock block)
    {
        Advance(); // consume 'return'
        return _current.Kind == TokenKind.ValueRef
            ? block.CreateReturn(LookupValue(Advance()))
            : block.CreateReturn();
    }

    private IRType ParseType()
    {
        var token = Expect(TokenKind.Identifier);
        return token.Text switch
        {
            "void" => IRType.Void,
            "i8" => IRType.I8,
            "i16" => IRType.I16,
            "i32" => IRType.I32,
            _ => throw Fail(token, $"Unknown type '{token.Text}'"),
        };
    }

    private IRValue LookupValue(Token token)
    {
        var index = (int)token.IntValue!.Value;
        if (!_valueMap.TryGetValue(index, out var value))
        {
            throw Fail(token, $"Undefined value '%{index}'");
        }
        return value;
    }

    private void RegisterValue(Token token, int index, IRValue value)
    {
        if (!_valueMap.TryAdd(index, value))
        {
            throw Fail(token, $"Duplicate value definition '%{index}'");
        }
    }

    private Token Expect(TokenKind kind)
    {
        if (_current.Kind != kind)
        {
            throw Fail(_current, $"Expected {kind}, got '{_current.Kind}'");
        }
        return Advance();
    }

    private Token Advance()
    {
        var token = _current;
        _current = _lexer.NextToken();
        return token;
    }

    private static ParseException Fail(Token token, string message) =>
        new(new Diagnostic(token.Line, token.Column, message));
}
