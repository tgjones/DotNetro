using DotNetro.Compiler.TypeSystem;

namespace DotNetro.Compiler.CodeGen;

internal abstract class CodeGenerator(TextWriter output)
{
    public abstract int PointerSize { get; }

    protected TextWriter Output { get; } = output;

    public abstract void WriteHeader();
    public abstract void WriteEntryPoint(string entryPointMethodName);
    public abstract void WriteFooter();

    public abstract void WriteStringConstant(string name, string value);

    public abstract void WriteMethodStart(string name);
    public abstract void WriteMethodEnd();

    public abstract void CompileSystemConsoleBeep();
    public abstract void CompileSystemConsoleReadLine();
    public abstract void CompileSystemConsoleWriteLineInt32();
    public abstract void CompileSystemConsoleWriteLineString();

    public abstract void WriteLabel(string label);
    public abstract void WriteComment(string text);

    public abstract void WriteAddInt32();
    public abstract void WriteBr(string label);
    public abstract void WriteBrtrue(TypeDescription stackObjectType, string label);
    public abstract void WriteCall(EcmaMethod caller, EcmaMethod callee);
    public abstract void WriteCltInt32();
    public abstract void WriteInitobj(TypeDescription type);
    public abstract void WriteLdarg(Parameter parameter);
    public abstract void WriteLdcI4(int value);
    public abstract void WriteLdfld(TypeDescription objectType, EcmaField field);
    public abstract void WriteLdloc(LocalVariable local);
    public abstract void WriteLdloca(LocalVariable local);
    public abstract void WriteLdstr(string name);
    public abstract void WriteRet();
    public abstract void WriteStfld( EcmaField field);
    public abstract void WriteStloc(TypeDescription stackEntryType, LocalVariable local);
}