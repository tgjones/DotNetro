namespace Irie.CodeGen.Parsing;

internal enum MachineTokenKind
{
    // Keywords
    Func,

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
    Identifier,    // word not otherwise classified (type names, opcode names)
    Integer,       // decimal integer literal, possibly negative
    ValueRef,      // %N — virtual register reference
    PhysRegRef,    // $N — physical register reference
    BlockLabel,    // bbN — block label (definition or reference)

    Eof,
}
