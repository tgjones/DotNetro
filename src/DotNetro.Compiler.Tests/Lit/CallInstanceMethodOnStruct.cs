// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 42
var s = new MyStructWithInstanceMethod(42);
Console.WriteLine(s.MyMethod());

struct MyStructWithInstanceMethod(int a)
{
    public int MyMethod() => a;
}
