// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro
// RUN-asm: @cs_compiler @file | @dnrc --emit assembly

// DIFF: dotnet emulated
// CHECK-asm: \.cstring "Hello, World!"
Console.WriteLine("Hello, World!");
