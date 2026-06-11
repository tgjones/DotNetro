// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --mir --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var c = new MyClassWithInstanceMethod();
Console.WriteLine(c.MyMethod());

class MyClassWithInstanceMethod
{
    public int MyMethod() => 42;
}
