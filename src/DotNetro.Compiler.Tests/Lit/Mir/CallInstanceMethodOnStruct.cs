// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --mir --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var s = new MyStructWithInstanceMethod(42);
Console.WriteLine(s.MyMethod());

struct MyStructWithInstanceMethod(int a)
{
    public int MyMethod() => a;
}
