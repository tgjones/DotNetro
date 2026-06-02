// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// CHECK: Hello, World!
Console.WriteLine("Hello, World!");
