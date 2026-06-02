// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 3
var s = new MyStructWithConstructor(1, 2);
Console.WriteLine(s.A + s.B);

struct MyStructWithConstructor(int a, int b)
{
    public int A = a;
    public int B = b;
}
