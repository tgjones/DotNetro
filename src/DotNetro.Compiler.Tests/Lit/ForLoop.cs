// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: Begin
// CHECK: 0
// CHECK: 1
// CHECK: 2
// CHECK: 3
// CHECK: 4
// CHECK: End
Console.WriteLine("Begin");
for (var i = 0; i < 5; i++)
{
    Console.WriteLine(i);
}
Console.WriteLine("End");
