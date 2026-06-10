// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --mir --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
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
