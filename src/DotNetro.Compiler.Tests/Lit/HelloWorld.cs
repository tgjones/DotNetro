// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-asm: @cs_compiler @file | @dnrc --emit assembly

// CHECK: Hello, World!
// CHECK-asm: \.cstring "Hello, World!"
Console.WriteLine("Hello, World!");
