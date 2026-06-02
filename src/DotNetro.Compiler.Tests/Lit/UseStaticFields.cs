// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 3
MyStructWithStaticFields.A = 1;
MyStructWithStaticFields.B = 2;
Console.WriteLine(MyStructWithStaticFields.A + MyStructWithStaticFields.B);

struct MyStructWithStaticFields
{
    public static int A;
    public static int B;
}
