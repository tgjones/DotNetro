// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 45
// CHECK: 3
// CHECK: 3
// CHECK: 45
// CHECK: 3
// CHECK: 3
MethodA();
MethodA();

static void MethodA()
{
    var x = 1;
    var y = 2;
    MethodB(x + y);
    Console.WriteLine(x + y);
}

static void MethodB(int x)
{
    MethodC(x + 42);
    Console.WriteLine(x);
}

static void MethodC(int x)
{
    Console.WriteLine(x);
}
