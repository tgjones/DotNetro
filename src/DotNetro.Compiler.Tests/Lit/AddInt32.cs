// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: 2
var a = 1;
var b = 1;
Console.WriteLine(a + b);
