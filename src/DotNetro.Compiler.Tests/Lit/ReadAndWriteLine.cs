// RUN: @cs_compiler @file | @dnrc --emit program | @emulator --target-system bbcmicro --input "Foo"
// CHECK: Enter some text:
// CHECK: You said:
// CHECK: Foo
Console.WriteLine("Enter some text:");
var text = Console.ReadLine();
Console.WriteLine("You said:");
Console.WriteLine(text);
Console.Beep();
