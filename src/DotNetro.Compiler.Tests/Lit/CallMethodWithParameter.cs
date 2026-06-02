// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 16
PrintParameter(16);

static void PrintParameter(int value)
{
    Console.WriteLine(value);
}
