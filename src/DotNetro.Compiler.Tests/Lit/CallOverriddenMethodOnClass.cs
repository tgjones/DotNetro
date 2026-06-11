// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var c = new MyInheritedClassWithVirtualMethod();
Console.WriteLine(c.MyMethod());

class MyClassWithVirtualMethod
{
    public virtual int MyMethod() => 42;
}

class MyInheritedClassWithVirtualMethod : MyClassWithVirtualMethod
{
    public override int MyMethod() => 43;
}
