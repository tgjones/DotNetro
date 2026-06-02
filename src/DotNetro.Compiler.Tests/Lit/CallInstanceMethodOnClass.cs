// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 42
var c = new MyClassWithInstanceMethod();
Console.WriteLine(c.MyMethod());

class MyClassWithInstanceMethod
{
    public int MyMethod() => 42;
}
