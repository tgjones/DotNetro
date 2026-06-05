// RUN-dotnet: @cs_compiler @file | @dotnet_runner
// RUN-emulated: @cs_compiler @file | @dnrc --mir --emit program | @emulator --target-system bbcmicro
// DIFF: dotnet emulated
Console.WriteLine("Hello, World!");
