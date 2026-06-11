namespace Irie.Mir.Parsing;

internal enum MirTokenKind
{
    // Punctuation
    At,
    Arrow,
    Colon,
    Equals,
    Comma,
    Dot,
    LParen,
    RParen,
    LBrace,
    RBrace,
    LBracket,
    RBracket,

    // Value-carrying
    Identifier,    // word not otherwise classified (type names, opcode parts, class names)
    Integer,       // decimal integer literal, possibly negative
    ValueRef,      // %N — virtual register reference
    PhysRegRef,    // $N (numeric) or $name (symbolic) — physical register reference
    BlockLabel,    // bbN — block label (header definition or operand reference)
    String,        // "..." — quoted string literal (used for global initializers)

    Eof,
}
