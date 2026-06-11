// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Console.WriteLine(MethodWithParametersAndReturnValue(1, 2));

static int MethodWithParametersAndReturnValue(int a, int b)
{
    return a + b;
}
