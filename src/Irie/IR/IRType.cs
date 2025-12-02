namespace Irie.IR;

public abstract record IRType(int SizeInBits)
{
    public static readonly IRType Void = new VoidType();

    public static readonly IntegerType I8 = new(8);
    public static readonly IntegerType I16 = new(16);
    public static readonly IntegerType I32 = new(32);

    public static readonly IRType Pointer = I16;

    public static readonly IRType Label = new LabelType();

    public abstract string DisplayName { get; }
}

public sealed record IntegerType(int SizeInBits)
    : IRType(SizeInBits)
{
    public override string DisplayName => $"i{SizeInBits}";
}

public sealed record FloatType(int SizeInBits)
    : IRType(SizeInBits)
{
    public override string DisplayName => $"f{SizeInBits}";
}

public sealed record LabelType()
    : IRType(0)
{
    public override string DisplayName => "label";
}

public sealed record VoidType()
    : IRType(0)
{
    public override string DisplayName => "void";
}