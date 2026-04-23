using System.Text;

namespace Irie.IR.Parsing;

internal sealed class IRLexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["func"] = TokenKind.Func,
        ["return"] = TokenKind.Return,
        ["integer_literal"] = TokenKind.IntegerLiteralOpcode,
        ["integer_add"] = TokenKind.IntegerAdd,
    };

    private readonly TextReader _reader;
    private int _current;
    private int _line = 1;
    private int _column = 1;

    public IRLexer(TextReader reader)
    {
        _reader = reader;
        _current = reader.Read();
    }

    public Token NextToken()
    {
        SkipWhitespace();

        var line = _line;
        var col = _column;

        if (_current == -1)
        {
            return new Token(TokenKind.Eof, line, col);
        }

        var ch = (char)_current;

        switch (ch)
        {
            case '@': Advance(); return new Token(TokenKind.At, line, col);
            case ':': Advance(); return new Token(TokenKind.Colon, line, col);
            case '=': Advance(); return new Token(TokenKind.Equals, line, col);
            case ',': Advance(); return new Token(TokenKind.Comma, line, col);
            case '(': Advance(); return new Token(TokenKind.LParen, line, col);
            case ')': Advance(); return new Token(TokenKind.RParen, line, col);
            case '{': Advance(); return new Token(TokenKind.LBrace, line, col);
            case '}': Advance(); return new Token(TokenKind.RBrace, line, col);

            case '-':
                // Disambiguate: '->' arrow vs negative integer literal
                if (_reader.Peek() == '>')
                {
                    Advance(); // consume '-'
                    Advance(); // consume '>'
                    return new Token(TokenKind.Arrow, line, col);
                }
                return new Token(TokenKind.Integer, line, col, IntValue: ReadInteger());

            case '%':
                Advance(); // consume '%'
                if (_current == -1 || !char.IsAsciiDigit((char)_current))
                {
                    throw Fail(line, col, "Expected digit after '%'");
                }
                return new Token(TokenKind.ValueRef, line, col, IntValue: ReadInteger());

            default:
                if (char.IsAsciiDigit(ch))
                {
                    return new Token(TokenKind.Integer, line, col, IntValue: ReadInteger());
                }

                if (char.IsAsciiLetter(ch) || ch == '_')
                {
                    var word = ReadWord();

                    // Block label: 'bb' followed by digits, e.g. bb0, bb42
                    // ReadWord consumes the digits too, so check the whole word.
                    if (word.Length > 2 && word.StartsWith("bb") && word[2..].All(char.IsAsciiDigit))
                    {
                        return new Token(TokenKind.BlockLabel, line, col, IntValue: long.Parse(word[2..]));
                    }

                    if (Keywords.TryGetValue(word, out var kind))
                    {
                        return new Token(kind, line, col);
                    }

                    return new Token(TokenKind.Identifier, line, col, Text: word);
                }

                throw Fail(line, col, $"Unexpected character '{ch}'");
        }
    }

    private void SkipWhitespace()
    {
        while (_current != -1)
        {
            if (_current == '\n')
            {
                _line++;
                _column = 1;
                _current = _reader.Read();
            }
            else if (_current == '\r')
            {
                _current = _reader.Read();
            }
            else if (_current == ';')
            {
                while (_current != -1 && _current != '\n')
                    _current = _reader.Read();
            }
            else if (char.IsWhiteSpace((char)_current))
            {
                Advance();
            }
            else
            {
                break;
            }
        }
    }

    private void Advance()
    {
        _current = _reader.Read();
        _column++;
    }

    private long ReadInteger()
    {
        var sb = new StringBuilder();
        if (_current == '-')
        {
            sb.Append('-');
            Advance();
        }
        while (_current != -1 && char.IsAsciiDigit((char)_current))
        {
            sb.Append((char)_current);
            Advance();
        }
        return long.Parse(sb.ToString());
    }

    private string ReadWord()
    {
        var sb = new StringBuilder();
        while (_current != -1 && (char.IsAsciiLetterOrDigit((char)_current) || _current == '_'))
        {
            sb.Append((char)_current);
            Advance();
        }
        return sb.ToString();
    }

    private static ParseException Fail(int line, int col, string message) =>
        new(new Diagnostic(line, col, message));
}
