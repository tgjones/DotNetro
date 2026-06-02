// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 45
Console.WriteLine(MethodWithParameterAndReturnValue(44));

static int MethodWithParameterAndReturnValue(int value)
{
    return value + 1;
}
