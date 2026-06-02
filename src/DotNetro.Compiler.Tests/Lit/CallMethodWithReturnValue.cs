// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 43
Console.WriteLine(MethodWithReturnValue());

static int MethodWithReturnValue()
{
    return 43;
}
