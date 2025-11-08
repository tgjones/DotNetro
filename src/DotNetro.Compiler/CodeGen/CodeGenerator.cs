using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler.CodeGen;

internal abstract class CodeGenerator(TextWriter output)
{
    public abstract int PointerSize { get; }

    protected TextWriter Output { get; } = output;

    public abstract void WriteHeader();
    public abstract void WriteEntryPoint(string entryPointMethodName);
    public abstract void WriteFooter(ReadOnlySpan<EcmaMethod> staticConstructors);

    public abstract void WriteVtables(ReadOnlySpan<Vtable> vtables);

    public abstract void WriteStringConstant(string name, string value);

    public abstract void WriteStaticField(EcmaField field);

    public abstract void WriteMethodStart(string name);
    public abstract void WriteMethodEnd();

    public abstract void CompileSystemConsoleBeep();
    public abstract void CompileSystemConsoleReadLine();
    public abstract void CompileSystemConsoleWriteLineInt32();
    public abstract void CompileSystemConsoleWriteLineString();

    public abstract void WriteLabel(string label);
    public abstract void WriteComment(string text);

    public abstract void WriteAddInt32();
    public abstract void WriteAddIntPtr();
    public abstract void WriteBr(string label);
    public abstract void WriteBrfalse(TypeDescription stackObjectType, string label);
    public abstract void WriteBrtrue(TypeDescription stackObjectType, string label);
    public abstract void WriteCall(EcmaMethod caller, EcmaMethod callee);
    public abstract void WriteCallvirt(EcmaMethod caller, EcmaMethod callee, string vtableSlotLabel);
    public abstract void WriteCgeInt32();
    public abstract void WriteCltInt32();
    public abstract void WriteConviInt32();
    public abstract void WriteDup(TypeDescription type);
    public abstract void WriteInitobj(TypeDescription type);
    public abstract void WriteLdarg(Parameter parameter);
    public abstract void WriteLdcI4(int value);
    public abstract void WriteLdfld(TypeDescription objectType, EcmaField field);
    public abstract void WriteLdloc(LocalVariable local);
    public abstract void WriteLdloca(LocalVariable local);
    public abstract void WriteLdsfld(EcmaField field);
    public abstract void WriteLdstr(string name);
    public abstract void WriteNewobj(EcmaMethod caller, EcmaMethod constructor, EcmaMethod allocMethod);
    public abstract void WriteRet();
    public abstract void WriteStfld(EcmaField field);
    public abstract void WriteStind(TypeDescription type);
    public abstract void WriteStloc(LocalVariable local);
    public abstract void WriteStsfld(EcmaField field);
}