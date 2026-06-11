// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
var s = new MyClassOuter
{
    Inner = new MyClassInner
    {
        A = 1,
        B = 2,
    },
    C = 3,
};
Console.WriteLine(s.Inner.A + s.Inner.B + s.C);

class MyClassOuter
{
    public MyClassInner? Inner;
    public int C;
}

class MyClassInner
{
    public int A;
    public int B;
}
