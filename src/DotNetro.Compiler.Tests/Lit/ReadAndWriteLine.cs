// RUN-dotnet: @cs_compiler @file | @dotnet_runner --input "Foo"
// RUN-emulated: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --input "Foo"
// DIFF: dotnet emulated
Console.WriteLine("Enter some text:");
var text = Console.ReadLine();
Console.WriteLine("You said:");
Console.WriteLine(text);
Console.Beep();
