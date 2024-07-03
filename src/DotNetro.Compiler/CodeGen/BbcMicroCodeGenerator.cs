namespace DotNetro.Compiler.CodeGen;

internal sealed class BbcMicroCodeGenerator(TextWriter output)
    : M6502CodeGenerator(output)
{
    protected override void WriteSystemConstants()
    {
        Output.WriteLine("oswrch = $FFEE");
        Output.WriteLine("osasci = $FFE3");
        Output.WriteLine("osword = $FFF1");
        Output.WriteLine();
    }

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
        WritePushY();

        Output.WriteLine("    LDX #<sound");
        Output.WriteLine("    LDY #>sound");
        Output.WriteLine("    LDA #07");
        Output.WriteLine("    JSR osword");

        WritePopY();
        WritePopX();

        Output.WriteLine("    RTS");
        Output.WriteLine();

        WriteLabel("sound");
        Output.WriteLine("channel:   .word 1");
        Output.WriteLine("amplitude: .short -15");
        Output.WriteLine("pitch:     .word 0");
        Output.WriteLine("duration:  .word 4 ; duration in 1/20ths of a second");
    }

    public override void CompileSystemConsoleReadLine()
    {
        WritePushX();
        WritePushY();

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

        Output.WriteLine("    LDX #$37");
        Output.WriteLine("    LDA #00");
        Output.WriteLine("    JSR osword");

        WritePopY();
        WritePopX();

        WritePushConstant([0x00, 0x06]);

        Output.WriteLine("    RTS");
    }

    public override void CompileSystemConsoleWriteLineString()
    {
        for (var i = 0; i < PointerSize; i++)
        {
            Output.WriteLine($"    LDA {ArgsLabel}+{i},Y");
            Output.WriteLine($"    STA {ScratchLabel}+{i}");
        }

        WritePushY();

        Output.WriteLine("    LDY #0");
        Output.WriteLine("");

        WriteLabel("loop");
        Output.WriteLine($"    LDA({ScratchLabel}),Y");
        Output.WriteLine($"    BEQ finished");
        Output.WriteLine($"    JSR osasci");
        Output.WriteLine($"    INY");
        Output.WriteLine($"    BNE loop");
        Output.WriteLine("");

        WriteLabel("finished");
        WritePopY();
        Output.WriteLine("    RTS");
    }

    public override void CompileSystemConsoleWriteLineInt32()
    {
        for (var i = 0; i < 4; i++)
        {
            Output.WriteLine($"    LDA {ArgsLabel}+{i},Y");
            Output.WriteLine($"    STA {ScratchLabel}+{i}");
        }

        Output.WriteLine($"    LDA {ScratchLabel}+3 ; Is it positive?");
        Output.WriteLine($"    BPL positive");

        WriteLabel("negative");
        Output.WriteLine($"    LDA #'-' ; 2s-complement it");
        Output.WriteLine($"    JSR osasci");
        Output.WriteLine($"    CLC");
        Output.WriteLine($"    LDA {ScratchLabel}+0");
        Output.WriteLine($"    EOR #$FF");
        Output.WriteLine($"    ADC #1");
        Output.WriteLine($"    STA {ScratchLabel}+0");
        Output.WriteLine($"    LDA {ScratchLabel}+1");
        Output.WriteLine($"    EOR #$FF");
        Output.WriteLine($"    ADC #0");
        Output.WriteLine($"    STA {ScratchLabel}+1");
        Output.WriteLine($"    LDA {ScratchLabel}+2");
        Output.WriteLine($"    EOR #$FF");
        Output.WriteLine($"    ADC #0");
        Output.WriteLine($"    STA {ScratchLabel}+2");
        Output.WriteLine($"    LDA {ScratchLabel}+3");
        Output.WriteLine($"    EOR #$FF");
        Output.WriteLine($"    ADC #0");
        Output.WriteLine($"    STA {ScratchLabel}+3");

        WriteLabel("positive");
        Output.WriteLine("    JMP SystemConsoleWriteLineUInt32");

        WriteLabel("SystemConsoleWriteLineUInt32");
        WritePushX();
        WritePushY();
        Output.WriteLine($"    LDY #36");
        Output.WriteLine($"    LDA #0");
        Output.WriteLine($"    STA pad");
        Output.WriteLine($"    STA outputsomething");

        WriteLabel("PrDec32Lp1");
        Output.WriteLine($"    LDX #$FF:SEC");

        WriteLabel("PrDec32Lp2");
        Output.WriteLine($"    LDA {ScratchLabel}+0:SBC PrDec32Tens+0,Y:STA {ScratchLabel}+0");
        Output.WriteLine($"    LDA {ScratchLabel}+1:SBC PrDec32Tens+1,Y:STA {ScratchLabel}+1");
        Output.WriteLine($"    LDA {ScratchLabel}+2:SBC PrDec32Tens+2,Y:STA {ScratchLabel}+2");
        Output.WriteLine($"    LDA {ScratchLabel}+3:SBC PrDec32Tens+3,Y:STA {ScratchLabel}+3");
        Output.WriteLine($"    INX:BCS PrDec32Lp2");
        Output.WriteLine($"    LDA {ScratchLabel}+0:ADC PrDec32Tens+0,Y:STA {ScratchLabel}+0");
        Output.WriteLine($"    LDA {ScratchLabel}+1:ADC PrDec32Tens+1,Y:STA {ScratchLabel}+1");
        Output.WriteLine($"    LDA {ScratchLabel}+2:ADC PrDec32Tens+2,Y:STA {ScratchLabel}+2");
        Output.WriteLine($"    LDA {ScratchLabel}+3:ADC PrDec32Tens+3,Y:STA {ScratchLabel}+3");
        Output.WriteLine($"    TXA:BNE PrDec32Digit");
        Output.WriteLine($"    LDA pad:BNE PrDec32Print:BEQ PrDec32Next");

        WriteLabel("PrDec32Digit");
        Output.WriteLine($"    LDX #'0':STX pad ; No more zero padding");
        Output.WriteLine($"    ORA #'0'");

        WriteLabel("PrDec32Print");
        Output.WriteLine($"    STA outputsomething");
        Output.WriteLine($"    JSR osasci");

        WriteLabel("PrDec32Next");
        Output.WriteLine($"    DEY:DEY:DEY:DEY:BPL PrDec32Lp1");
        Output.WriteLine($"    LDA outputsomething");
        Output.WriteLine($"    BNE finish");
        Output.WriteLine($"    LDA #'0'");
        Output.WriteLine($"    JSR osasci");

        WriteLabel("finish");
        Output.WriteLine("    LDA #13");
        Output.WriteLine("    JSR osasci");
        WritePopY();
        WritePopX();
        Output.WriteLine("    RTS");

        WriteLabel("PrDec32Tens");
        Output.WriteLine($"    .dint 1");
        Output.WriteLine($"    .dint 10");
        Output.WriteLine($"    .dint 100");
        Output.WriteLine($"    .dint 1000");
        Output.WriteLine($"    .dint 10000");
        Output.WriteLine($"    .dint 100000");
        Output.WriteLine($"    .dint 1000000");
        Output.WriteLine($"    .dint 10000000");
        Output.WriteLine($"    .dint 100000000");
        Output.WriteLine($"    .dint 1000000000");

        WriteLabel("pad");
        Output.WriteLine($"    .byte 0");

        WriteLabel("outputsomething");
        Output.WriteLine($"    .byte 0");
    }
}
