namespace Irie.IR;

internal enum TokenKind
{
    // Keywords
    Func,
    Return,

    // Opcode keywords
    IntegerLiteralOpcode,
    IntegerAdd,

    // Punctuation
    At,
    Arrow,
    Colon,
    Equals,
    Comma,
    LParen,
    RParen,
    LBrace,
    RBrace,

    // Value-carrying
    Identifier,
    Integer,
    ValueRef,
    BlockLabel,

    Eof,
}
