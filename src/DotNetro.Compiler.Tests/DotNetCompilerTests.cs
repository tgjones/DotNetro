using System.Buffers.Binary;
using System.Reflection;
using System.Text;

using Aemula.Chips.Mos6502;

namespace DotNetro.Compiler.Tests;

public class DotNetCompilerTests
{
    [CompilerTest]
    private static void PrintHelloWorld()
    {
        Console.WriteLine("Hello, World!");
    }

    [CompilerTest]
    private static void Print42()
    {
        Console.WriteLine(42);
    }

    [CompilerTest]
    private static void PrintMinus1()
    {
        Console.WriteLine(-1);
    }

    [CompilerTest]
    private static void AddInt32()
    {
        var a = 1;
        var b = 1;
        Console.WriteLine(a + b);
    }

    [CompilerTest]
    private static void CallMethodWithParameter()
    {
        PrintParameter(16);
    }

    private static void PrintParameter(int value)
    {
        Console.WriteLine(value);
    }

    [CompilerTest]
    private static void CallMethodWithReturnValue()
    {
        Console.WriteLine(MethodWithReturnValue());
    }

    private static int MethodWithReturnValue()
    {
        return 43;
    }

    [CompilerTest]
    private static void CallMethodWithParameterAndReturnValue()
    {
        Console.WriteLine(MethodWithParameterAndReturnValue(44));
    }

    private static int MethodWithParameterAndReturnValue(int value)
    {
        return value + 1;
    }

    [CompilerTest]
    private static void CallMethodWithTwoParametersAndReturnValue()
    {
        Console.WriteLine(MethodWithParametersAndReturnValue(1, 2));
    }

    private static int MethodWithParametersAndReturnValue(int a, int b)
    {
        return a + b;
    }

    [CompilerTest(ConsoleInputs = ["Foo"])]
    private static void ReadAndWriteLine()
    {
        Console.WriteLine("Enter some text:");

        var text = Console.ReadLine();

        Console.WriteLine("You said:");
        Console.WriteLine(text);

        Console.Beep();
    }

    //private static void CodeAddTwoIntegers()
    //{
    //    Console.WriteLine("Enter an integer:");
    //    var intA = int.Parse(Console.ReadLine()!);

    //    Console.WriteLine("Enter another integer:");
    //    var intB = int.Parse(Console.ReadLine()!);

    //    var result = intA + intB;

    //    Console.Write("Result is ");
    //    Console.Write(result);
    //    Console.WriteLine();
    //}

    [CompilerTest]
    private static void UseStruct()
    {
        var s = new MyStruct
        {
            A = 1,
            B = 2,
        };

        Console.WriteLine(s.A + s.B);
    }

    private struct MyStruct
    {
        public int A;
        public int B;
    }

    [CompilerTest]
    private static void ForLoop()
    {
        Console.WriteLine("Begin");

        for (var i = 0; i < 5; i++)
        {
            Console.WriteLine(i);
        }

        Console.WriteLine("End");
    }

    [CompilerTest]
    private static void CallNestedMethods()
    {
        MethodA();
        MethodA();
    }

    private static void MethodA()
    {
        var x = 1;
        var y = 2;

        MethodB(x + y);

        Console.WriteLine(x + y);
    }

    private static void MethodB(int x)
    {
        MethodC(x + 42);

        Console.WriteLine(x);
    }

    private static void MethodC(int x)
    {
        Console.WriteLine(x);
    }

    [TestCaseSource(nameof(GetCompilerTests))]
    public void CompilerTests(CompilerTest test)
    {
        // Run reference .NET version.
        string expectedOutput = ExecuteDotNet(test);
        
        // Compile and run DotNetro version.
        var actualOutput = ExecuteDotNetro(test);

        // Compare results.
        Assert.That(actualOutput, Is.EqualTo(expectedOutput));
        Console.WriteLine(actualOutput);
    }

    private static string ExecuteDotNet(CompilerTest test)
    {
        using var reader = new StringReader(string.Join("\r", test.ConsoleInputs));

        using var writer = new StringWriter { NewLine = "\r" };

        var tmpOut = Console.Out;
        var tmpIn = Console.In;

        Console.SetOut(writer);
        Console.SetIn(reader);
        test.Method.Invoke(null, null);

        Console.SetIn(tmpIn);
        Console.SetOut(tmpOut);

        return writer.ToString();
    }

    private static string ExecuteDotNetro(CompilerTest test)
    {
        // Compile method.
        var compiledProgram = CompilerDriver.Compile(typeof(DotNetCompilerTests).Assembly.Location, test.Method.Name, null);

        // Copy compiled program into memory.
        var memory = new byte[ushort.MaxValue + 1];
        compiledProgram.CopyTo(memory, 0x2000);

        // Stub BBC Micro OS functions - OSWRCH and OSASCI.
        memory[0xFFEE] = 0x60; // oswrch, RTS
        memory[0xFFE3] = 0x60; // osasci, RTS
        memory[0xFFF1] = 0x60; // osword, RTS

        memory[0xFFFC] = 0x00; // Reset vector $FF00
        memory[0xFFFD] = 0xFF;

        memory[0xFF00] = 0x20; // JSR $2000
        memory[0xFF01] = 0x00;
        memory[0xFF02] = 0x20;
        memory[0xFF03] = 0x60; // RTS

        var cpu = new Mos6502(Mos6502Options.Default);

        ref var pins = ref cpu.Pins;

        using var reader = new StringReader(string.Join("\r", test.ConsoleInputs));

        var output = "";

        var shouldContinue = true;
        while (shouldContinue)
        {
            cpu.Tick();

            var address = pins.Address;

            if (pins.RW)
            {
                pins.Data = memory[address];
            }
            else
            {
                memory[address] = pins.Data;
            }

            switch (cpu.PC)
            {
                case 0xFF03:
                    shouldContinue = false;
                    break;

                case 0xFFE3:
                    output += (char)cpu.A;
                    break;

                case 0xFFF1:
                    if (cpu.A == 0) // OSWORD 0
                    {
                        var input = reader.ReadLine() ?? throw new InvalidOperationException();
                        var stringAddress = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(0x37));
                        Encoding.ASCII.GetBytes(input + '\r').CopyTo(memory.AsSpan(stringAddress));
                    }
                    break;
            }
        }

        return output;
    }

    private static IEnumerable<CompilerTest> GetCompilerTests()
    {
        foreach (var method in typeof(DotNetCompilerTests).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var compilerTestAttribute = method.GetCustomAttribute<CompilerTestAttribute>();
            if (compilerTestAttribute != null)
            {
                yield return new CompilerTest(method, compilerTestAttribute.ConsoleInputs);
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class CompilerTestAttribute : Attribute
{
    public string[] ConsoleInputs { get; set; } = Array.Empty<string>();
}

public sealed class CompilerTest(MethodInfo method, string[] consoleInputs)
{
    public MethodInfo Method => method;

    public string[] ConsoleInputs => consoleInputs;

    public override string ToString() => method.Name;
}