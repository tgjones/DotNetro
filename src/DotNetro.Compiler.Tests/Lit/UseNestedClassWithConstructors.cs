// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var s = new MyClassOuterWithConstructor(
    new MyClassInnerWithConstructor(1, 2),
    3);
Console.WriteLine(s.Inner.A + s.Inner.B + s.C);

class MyClassOuterWithConstructor(MyClassInnerWithConstructor inner, int c)
{
    public MyClassInnerWithConstructor Inner = inner;
    public int C = c;
}

sealed class MyClassInnerWithConstructor(int a, int b)
{
    public int A = a;
    public int B = b;
}
