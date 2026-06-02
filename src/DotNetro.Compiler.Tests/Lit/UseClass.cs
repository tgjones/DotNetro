// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 3
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
