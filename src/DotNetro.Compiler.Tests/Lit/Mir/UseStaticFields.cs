// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --mir --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
MyStructWithStaticFields.A = 1;
MyStructWithStaticFields.B = 2;
Console.WriteLine(MyStructWithStaticFields.A + MyStructWithStaticFields.B);

struct MyStructWithStaticFields
{
    public static int A;
    public static int B;
}
