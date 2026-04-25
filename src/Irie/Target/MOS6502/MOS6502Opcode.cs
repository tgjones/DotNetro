namespace Irie.Target.MOS6502;

public static class MOS6502Opcode
{
    // LDA
    public const int LDA_Immediate = 0xA9;
    public const int LDA_ZeroPage  = 0xA5;
    public const int LDA_ZeroPageX = 0xB5;
    public const int LDA_Absolute  = 0xAD;
    public const int LDA_AbsoluteX = 0xBD;
    public const int LDA_AbsoluteY = 0xB9;
    public const int LDA_IndirectX = 0xA1;
    public const int LDA_IndirectY = 0xB1;

    // STA
    public const int STA_ZeroPage  = 0x85;
    public const int STA_ZeroPageX = 0x95;
    public const int STA_Absolute  = 0x8D;
    public const int STA_AbsoluteX = 0x9D;
    public const int STA_AbsoluteY = 0x99;
    public const int STA_IndirectX = 0x81;
    public const int STA_IndirectY = 0x91;

    // LDX
    public const int LDX_Immediate = 0xA2;
    public const int LDX_ZeroPage  = 0xA6;
    public const int LDX_ZeroPageY = 0xB6;
    public const int LDX_Absolute  = 0xAE;
    public const int LDX_AbsoluteY = 0xBE;

    // STX
    public const int STX_ZeroPage  = 0x86;
    public const int STX_ZeroPageY = 0x96;
    public const int STX_Absolute  = 0x8E;

    // LDY
    public const int LDY_Immediate = 0xA0;
    public const int LDY_ZeroPage  = 0xA4;
    public const int LDY_ZeroPageX = 0xB4;
    public const int LDY_Absolute  = 0xAC;
    public const int LDY_AbsoluteX = 0xBC;

    // STY
    public const int STY_ZeroPage  = 0x84;
    public const int STY_ZeroPageX = 0x94;
    public const int STY_Absolute  = 0x8C;

    // ADC
    public const int ADC_Immediate = 0x69;
    public const int ADC_ZeroPage  = 0x65;
    public const int ADC_ZeroPageX = 0x75;
    public const int ADC_Absolute  = 0x6D;
    public const int ADC_AbsoluteX = 0x7D;
    public const int ADC_AbsoluteY = 0x79;
    public const int ADC_IndirectX = 0x61;
    public const int ADC_IndirectY = 0x71;

    // SBC
    public const int SBC_Immediate = 0xE9;
    public const int SBC_ZeroPage  = 0xE5;
    public const int SBC_ZeroPageX = 0xF5;
    public const int SBC_Absolute  = 0xED;
    public const int SBC_AbsoluteX = 0xFD;
    public const int SBC_AbsoluteY = 0xF9;
    public const int SBC_IndirectX = 0xE1;
    public const int SBC_IndirectY = 0xF1;

    // AND
    public const int AND_Immediate = 0x29;
    public const int AND_ZeroPage  = 0x25;
    public const int AND_ZeroPageX = 0x35;
    public const int AND_Absolute  = 0x2D;
    public const int AND_AbsoluteX = 0x3D;
    public const int AND_AbsoluteY = 0x39;
    public const int AND_IndirectX = 0x21;
    public const int AND_IndirectY = 0x31;

    // ORA
    public const int ORA_Immediate = 0x09;
    public const int ORA_ZeroPage  = 0x05;
    public const int ORA_ZeroPageX = 0x15;
    public const int ORA_Absolute  = 0x0D;
    public const int ORA_AbsoluteX = 0x1D;
    public const int ORA_AbsoluteY = 0x19;
    public const int ORA_IndirectX = 0x01;
    public const int ORA_IndirectY = 0x11;

    // EOR
    public const int EOR_Immediate = 0x49;
    public const int EOR_ZeroPage  = 0x45;
    public const int EOR_ZeroPageX = 0x55;
    public const int EOR_Absolute  = 0x4D;
    public const int EOR_AbsoluteX = 0x5D;
    public const int EOR_AbsoluteY = 0x59;
    public const int EOR_IndirectX = 0x41;
    public const int EOR_IndirectY = 0x51;

    // CMP
    public const int CMP_Immediate = 0xC9;
    public const int CMP_ZeroPage  = 0xC5;
    public const int CMP_ZeroPageX = 0xD5;
    public const int CMP_Absolute  = 0xCD;
    public const int CMP_AbsoluteX = 0xDD;
    public const int CMP_AbsoluteY = 0xD9;
    public const int CMP_IndirectX = 0xC1;
    public const int CMP_IndirectY = 0xD1;

    // CPX
    public const int CPX_Immediate = 0xE0;
    public const int CPX_ZeroPage  = 0xE4;
    public const int CPX_Absolute  = 0xEC;

    // CPY
    public const int CPY_Immediate = 0xC0;
    public const int CPY_ZeroPage  = 0xC4;
    public const int CPY_Absolute  = 0xCC;

    // INC / DEC
    public const int INC_ZeroPage  = 0xE6;
    public const int INC_ZeroPageX = 0xF6;
    public const int INC_Absolute  = 0xEE;
    public const int INC_AbsoluteX = 0xFE;
    public const int DEC_ZeroPage  = 0xC6;
    public const int DEC_ZeroPageX = 0xD6;
    public const int DEC_Absolute  = 0xCE;
    public const int DEC_AbsoluteX = 0xDE;

    // Register increments / decrements
    public const int INX = 0xE8;
    public const int DEX = 0xCA;
    public const int INY = 0xC8;
    public const int DEY = 0x88;

    // ASL
    public const int ASL_Accumulator = 0x0A;
    public const int ASL_ZeroPage    = 0x06;
    public const int ASL_ZeroPageX   = 0x16;
    public const int ASL_Absolute    = 0x0E;
    public const int ASL_AbsoluteX   = 0x1E;

    // LSR
    public const int LSR_Accumulator = 0x4A;
    public const int LSR_ZeroPage    = 0x46;
    public const int LSR_ZeroPageX   = 0x56;
    public const int LSR_Absolute    = 0x4E;
    public const int LSR_AbsoluteX   = 0x5E;

    // ROL
    public const int ROL_Accumulator = 0x2A;
    public const int ROL_ZeroPage    = 0x26;
    public const int ROL_ZeroPageX   = 0x36;
    public const int ROL_Absolute    = 0x2E;
    public const int ROL_AbsoluteX   = 0x3E;

    // ROR
    public const int ROR_Accumulator = 0x6A;
    public const int ROR_ZeroPage    = 0x66;
    public const int ROR_ZeroPageX   = 0x76;
    public const int ROR_Absolute    = 0x6E;
    public const int ROR_AbsoluteX   = 0x7E;

    // Branches (all Relative)
    public const int BEQ = 0xF0;
    public const int BNE = 0xD0;
    public const int BCC = 0x90;
    public const int BCS = 0xB0;
    public const int BMI = 0x30;
    public const int BPL = 0x10;
    public const int BVC = 0x50;
    public const int BVS = 0x70;

    // Jumps
    public const int JMP_Absolute = 0x4C;
    public const int JMP_Indirect = 0x6C;
    public const int JSR_Absolute = 0x20;
    public const int RTS          = 0x60;
    public const int RTI          = 0x40;

    // Stack
    public const int PHA = 0x48;
    public const int PLA = 0x68;
    public const int PHP = 0x08;
    public const int PLP = 0x28;

    // Register transfers
    public const int TAX = 0xAA;
    public const int TXA = 0x8A;
    public const int TAY = 0xA8;
    public const int TYA = 0x98;
    public const int TSX = 0xBA;
    public const int TXS = 0x9A;

    // Flag operations
    public const int CLC = 0x18;
    public const int SEC = 0x38;
    public const int CLI = 0x58;
    public const int SEI = 0x78;
    public const int CLV = 0xB8;
    public const int CLD = 0xD8;
    public const int SED = 0xF8;

    // Misc
    public const int NOP = 0xEA;
    public const int BRK = 0x00;
}
