// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 3
Console.WriteLine(MethodWithParametersAndReturnValue(1, 2));

static int MethodWithParametersAndReturnValue(int a, int b)
{
    return a + b;
}
