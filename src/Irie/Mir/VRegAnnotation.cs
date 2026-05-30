
namespace Irie.Mir;

// A virtual register starts life as TypedVReg (pre-isel) and is overwritten
// with ClassedVReg by instruction selection. There is never both at once.
public abstract record VRegAnnotation;

public sealed record TypedVReg(IRType Type) : VRegAnnotation;

public sealed record ClassedVReg(int ClassId, string Name) : VRegAnnotation;
