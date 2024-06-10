﻿namespace DotNetro.Compiler.CodeGen;

internal sealed class BbcMicroCodeGenerator(StreamWriter output)
    : M6502CodeGenerator(output)
{
    protected override void WriteStartupCode()
    {
        Output.WriteLine("    ; Enable MODE 7");
        Output.WriteLine("    LDA #22");
        Output.WriteLine("    JSR oswrch");
        Output.WriteLine("    LDA #7");
        Output.WriteLine("    JSR oswrch");
        Output.WriteLine();
    }

    public override void CompileSystemConsoleBeep()
    {
        WritePushX();

        Output.WriteLine("    LDX #LO(sound)");
        Output.WriteLine("    LDY #HI(sound)");
        Output.WriteLine("    LDA #07");
        Output.WriteLine("    JSR osword");

        WritePopX();

        Output.WriteLine("    RTS");
        Output.WriteLine();
        Output.WriteLine(".sound");
        Output.WriteLine(".channel   EQUW 1");
        Output.WriteLine(".amplitude EQUW -15");
        Output.WriteLine(".pitch     EQUW 0");
        Output.WriteLine(".duration  EQUW 4 ; duration in 1/20ths of a second");
    }

    public override void CompileSystemConsoleReadLine()
    {
        Output.WriteLine("    ; Control block layout:");
        Output.WriteLine("    ; $37-$38 : $0600 = address to place string");
        Output.WriteLine("    ; $39     : $EE   = maximum input line length");
        Output.WriteLine("    ; $3A     : $20   = minimum ASCII code");
        Output.WriteLine("    ; $3B     : $FF   = maximum ASCII code");
        Output.WriteLine("    LDA #$00");
        Output.WriteLine("    STA $37");
        Output.WriteLine("    LDA #$06");
        Output.WriteLine("    STA $38");
        Output.WriteLine("    LDA #$EE");
        Output.WriteLine("    STA $39");
        Output.WriteLine("    LDA #$20");
        Output.WriteLine("    STA $3A");
        Output.WriteLine("    LDA #$FF");
        Output.WriteLine("    STA $3B");
        Output.WriteLine("    LDY #0");

        WritePushX();

        Output.WriteLine("    LDX #$37");
        Output.WriteLine("    LDA #00");
        Output.WriteLine("    JSR osword");

        WritePopX();

        WritePushConstant([0x00, 0x06]);

        Output.WriteLine("    RTS");
    }

    public override void CompileSystemConsoleWriteLineString()
    {
        Output.WriteLine("    LDY #0");
        Output.WriteLine("");
        Output.WriteLine(".loop");
        Output.WriteLine("    LDA(args),Y");
        Output.WriteLine("    BEQ finished");
        Output.WriteLine("    JSR osasci");
        Output.WriteLine("    INY");
        Output.WriteLine("    BNE loop");
        Output.WriteLine("");
        Output.WriteLine(".finished");
        Output.WriteLine("    RTS");
    }

    public override void CompileSystemConsoleWriteLineInt32()
    {
        Output.WriteLine("    LDA args+3 ; Is it positive?");
        Output.WriteLine("    BPL positive");
        Output.WriteLine(".negative ; 2s-complement it");
        Output.WriteLine("    LDA #'-'");
        Output.WriteLine("    JSR osasci");
        Output.WriteLine("    CLC");
        Output.WriteLine("    LDA args+0");
        Output.WriteLine("    EOR #$FF");
        Output.WriteLine("    ADC #1");
        Output.WriteLine("    STA args+0");
        Output.WriteLine("    LDA args+1");
        Output.WriteLine("    EOR #$FF");
        Output.WriteLine("    ADC #0");
        Output.WriteLine("    STA args+1");
        Output.WriteLine("    LDA args+2");
        Output.WriteLine("    EOR #$FF");
        Output.WriteLine("    ADC #0");
        Output.WriteLine("    STA args+2");
        Output.WriteLine("    LDA args+3");
        Output.WriteLine("    EOR #$FF");
        Output.WriteLine("    ADC #0");
        Output.WriteLine("    STA args+3");
        Output.WriteLine(".positive");
        Output.WriteLine("    JMP SystemConsoleWriteLineUInt32");

        Output.WriteLine(".SystemConsoleWriteLineUInt32");
        Output.WriteLine("    LDY #36");
        Output.WriteLine(".PrDec32Lp1");
        Output.WriteLine("    LDX #&FF:SEC");
        Output.WriteLine(".PrDec32Lp2");
        Output.WriteLine("    LDA args+0:SBC PrDec32Tens+0,Y:STA args+0");
        Output.WriteLine("    LDA args+1:SBC PrDec32Tens+1,Y:STA args+1");
        Output.WriteLine("    LDA args+2:SBC PrDec32Tens+2,Y:STA args+2");
        Output.WriteLine("    LDA args+3:SBC PrDec32Tens+3,Y:STA args+3");
        Output.WriteLine("    INX:BCS PrDec32Lp2");
        Output.WriteLine("    LDA args+0:ADC PrDec32Tens+0,Y:STA args+0");
        Output.WriteLine("    LDA args+1:ADC PrDec32Tens+1,Y:STA args+1");
        Output.WriteLine("    LDA args+2:ADC PrDec32Tens+2,Y:STA args+2");
        Output.WriteLine("    LDA args+3:ADC PrDec32Tens+3,Y:STA args+3");
        Output.WriteLine("    TXA:BNE PrDec32Digit");
        Output.WriteLine("    JMP PrDec32Next");
        Output.WriteLine(".PrDec32Digit");
        Output.WriteLine("    ORA #'0'");
        Output.WriteLine(".PrDec32Print");
        Output.WriteLine("    JSR osasci");
        Output.WriteLine(".PrDec32Next");
        Output.WriteLine("    DEY:DEY:DEY:DEY:BPL PrDec32Lp1");
        Output.WriteLine("    LDA #13");
        Output.WriteLine("    JSR osasci");
        Output.WriteLine("    RTS");
        Output.WriteLine(".PrDec32Tens");
        Output.WriteLine("    EQUD 1");
        Output.WriteLine("    EQUD 10");
        Output.WriteLine("    EQUD 100");
        Output.WriteLine("    EQUD 1000");
        Output.WriteLine("    EQUD 10000");
        Output.WriteLine("    EQUD 100000");
        Output.WriteLine("    EQUD 1000000");
        Output.WriteLine("    EQUD 10000000");
        Output.WriteLine("    EQUD 100000000");
        Output.WriteLine("    EQUD 1000000000");
    }
}
