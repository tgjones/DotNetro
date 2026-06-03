// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var s = new MyClass
{
    A = 1,
    B = 2,
};
Console.WriteLine(s.A + s.B);

class MyClass
{
    public int A;
    public int B;
}
