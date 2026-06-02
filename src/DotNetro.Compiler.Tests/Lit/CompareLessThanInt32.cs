// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: True
var a = 1;
var b = 2;
Console.WriteLine(a < b);
