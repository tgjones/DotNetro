using Irie.CodeGen.Parsing;

namespace Irie.CodeGen;

public sealed class MachineModule
{
    public List<MachineFunction> Functions { get; } = [];

    // Writers: int → name (for output)
    public Func<int, string?>? OpcodeNamer { get; set; }
    public Func<int, string>? RegisterNamer { get; set; }
    public Func<int, string?>? RegisterClassNamer { get; set; }
    public Func<int, int[]?>? TiedOperandsProvider { get; set; }

    // Parsers: name → int (for input); used by MachineParser to resolve target-specific names
    public Func<string, int?>? OpcodeParser { get; set; }
    public Func<string, int?>? RegisterParser { get; set; }
    public Func<string, int?>? RegisterClassParser { get; set; }

    public MachineFunction CreateFunction(string name, Action<MachineFunction> configure)
    {
        var function = new MachineFunction(name);
        configure(function);
        Functions.Add(function);
        return function;
    }

    // Parse with no target-specific knowledge (generic MIR, integer-based physreg refs).
    public static MachineModule Parse(TextReader reader) =>
        MachineParser.Parse(reader, opcodeParser: null, registerParser: null, registerClassParser: null);

    // Parse using this module's parser delegates (for class-tagged / target-named MIR).
    public void ParseInto(TextReader reader)
    {
        var parsed = MachineParser.Parse(reader, OpcodeParser, RegisterParser, RegisterClassParser);
        Functions.AddRange(parsed.Functions);
    }

    public void Write(TextWriter writer) => MachineWriter.Write(this, writer);
}
