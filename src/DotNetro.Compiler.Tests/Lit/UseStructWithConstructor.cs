// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var s = new MyStructWithConstructor(1, 2);
Console.WriteLine(s.A + s.B);

struct MyStructWithConstructor(int a, int b)
{
    public int A = a;
    public int B = b;
}
