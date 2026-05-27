using System.Text;

namespace Irie.Mir.Parsing;

internal sealed class MirLexer
{
    private static readonly Dictionary<string, MirTokenKind> Keywords = new()
    {
        ["func"] = MirTokenKind.Func,
    };

    private readonly TextReader _reader;
    private int _current;
    private int _line = 1;
    private int _column = 1;

    public MirLexer(TextReader reader)
    {
        _reader = reader;
        _current = reader.Read();
    }

    public MirToken NextToken()
    {
        SkipWhitespaceAndComments();

        var line = _line;
        var col = _column;

        if (_current == -1)
            return new MirToken(MirTokenKind.Eof, line, col);

        var ch = (char)_current;

        switch (ch)
        {
            case '@': Advance(); return new MirToken(MirTokenKind.At, line, col);
            case ':': Advance(); return new MirToken(MirTokenKind.Colon, line, col);
            case '=': Advance(); return new MirToken(MirTokenKind.Equals, line, col);
            case ',': Advance(); return new MirToken(MirTokenKind.Comma, line, col);
            case '.': Advance(); return new MirToken(MirTokenKind.Dot, line, col);
            case '(': Advance(); return new MirToken(MirTokenKind.LParen, line, col);
            case ')': Advance(); return new MirToken(MirTokenKind.RParen, line, col);
            case '{': Advance(); return new MirToken(MirTokenKind.LBrace, line, col);
            case '}': Advance(); return new MirToken(MirTokenKind.RBrace, line, col);
            case '[': Advance(); return new MirToken(MirTokenKind.LBracket, line, col);
            case ']': Advance(); return new MirToken(MirTokenKind.RBracket, line, col);

            case '-':
                if (_reader.Peek() == '>')
                {
                    Advance(); // consume '-'
                    Advance(); // consume '>'
                    return new MirToken(MirTokenKind.Arrow, line, col);
                }
                return new MirToken(MirTokenKind.Integer, line, col, IntValue: ReadInteger());

            case '%':
                Advance(); // consume '%'
                if (_current == -1 || !char.IsAsciiDigit((char)_current))
                    throw Fail(line, col, "Expected digit after '%'");
                return new MirToken(MirTokenKind.ValueRef, line, col, IntValue: ReadInteger());

            case '$':
                Advance(); // consume '$'
                if (_current == -1)
                    throw Fail(line, col, "Expected register name or number after '$'");
                if (char.IsAsciiDigit((char)_current))
                    return new MirToken(MirTokenKind.PhysRegRef, line, col, IntValue: ReadInteger());
                if (char.IsAsciiLetter((char)_current) || (char)_current == '_')
                    return new MirToken(MirTokenKind.PhysRegRef, line, col, Text: ReadWord());
                throw Fail(line, col, "Expected register name or number after '$'");

            default:
                if (char.IsAsciiDigit(ch))
                    return new MirToken(MirTokenKind.Integer, line, col, IntValue: ReadInteger());

                if (char.IsAsciiLetter(ch) || ch == '_')
                {
                    var word = ReadWord();

                    // Block label: 'bb' followed only by digits, e.g. bb0, bb42.
                    if (word.Length > 2 && word.StartsWith("bb") && word[2..].All(char.IsAsciiDigit))
                        return new MirToken(MirTokenKind.BlockLabel, line, col, IntValue: long.Parse(word[2..]));

                    if (Keywords.TryGetValue(word, out var kind))
                        return new MirToken(kind, line, col);

                    return new MirToken(MirTokenKind.Identifier, line, col, Text: word);
                }

                throw Fail(line, col, $"Unexpected character '{ch}'");
        }
    }

    private void SkipWhitespaceAndComments()
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

    // Identifier: ASCII letter/digit/underscore, optionally with embedded hyphens
    // followed by another letter/underscore (lets us tokenize `implicit-def`,
    // `tied-def`, etc. as a single identifier — same convention as the IR/Mir
    // grammar uses elsewhere).
    private string ReadWord()
    {
        var sb = new StringBuilder();
        while (_current != -1)
        {
            var ch = (char)_current;
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(ch);
                Advance();
            }
            else if (ch == '-' && _reader.Peek() is var peek && peek != -1 && (char.IsAsciiLetter((char)peek) || peek == '_'))
            {
                sb.Append(ch);
                Advance();
            }
            else
            {
                break;
            }
        }
        return sb.ToString();
    }

    private static ParseException Fail(int line, int col, string message) =>
        new(new Diagnostic(line, col, message));
}
