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
    private static void UseStaticFields()
    {
        MyStructWithStaticFields.A = 1;
        MyStructWithStaticFields.B = 2;

        Console.WriteLine(MyStructWithStaticFields.A + MyStructWithStaticFields.B);
    }

    private struct MyStructWithStaticFields
    {
        public static int A;
        public static int B;
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

    [CompilerTest]
    private static void UseClass()
    {
        var s = new MyClass
        {
            A = 1,
            B = 2,
        };

        Console.WriteLine(s.A + s.B);
    }

    private class MyClass
    {
        public int A;
        public int B;
    }

    [CompilerTest]
    private static void UseNestedClass()
    {
        var s = new MyClassOuter
        {
            Inner = new MyClassInner
            {
                A = 1,
                B = 2,
            },
            C = 3,
        };

        Console.WriteLine(s.Inner.A + s.Inner.B + s.C);
    }

    private class MyClassOuter
    {
        public MyClassInner? Inner;
        public int C;
    }

    private class MyClassInner
    {
        public int A;
        public int B;
    }

    [CompilerTest]
    private static void UseNestedClassWithConstructors()
    {
        var s = new MyClassOuterWithConstructor(
            new MyClassInnerWithConstructor(1, 2),
            3);

        Console.WriteLine(s.Inner.A + s.Inner.B + s.C);
    }

    private class MyClassOuterWithConstructor(MyClassInnerWithConstructor inner, int c)
    {
        public MyClassInnerWithConstructor Inner = inner;
        public int C = c;
    }

    private sealed class MyClassInnerWithConstructor(int a, int b)
    {
        public int A = a;
        public int B = b;
    }

    [CompilerTest]
    private static void UseStructWithConstructor()
    {
        var s = new MyStructWithConstructor(1, 2);

        Console.WriteLine(s.A + s.B);
    }

    private struct MyStructWithConstructor(int a, int b)
    {
        public int A = a;
        public int B = b;
    }

    [CompilerTest]
    private static void CallInstanceMethodOnStruct()
    {
        var s = new MyStructWithInstanceMethod(42);

        Console.WriteLine(s.MyMethod());
    }

    private struct MyStructWithInstanceMethod(int a)
    {
        public int MyMethod() => a;
    }

    [CompilerTest]
    private static void CallInstanceMethodOnClass()
    {
        var c = new MyClassWithInstanceMethod();

        Console.WriteLine(c.MyMethod());
    }

    private class MyClassWithInstanceMethod
    {
        public int MyMethod() => 42;
    }

    [CompilerTest]
    private static void CallOverriddenMethodOnClass()
    {
        var c = new MyInheritedClassWithVirtualMethod();

        Console.WriteLine(c.MyMethod());
    }

    private class MyClassWithVirtualMethod
    {
        public virtual int MyMethod() => 42;
    }

    private class MyInheritedClassWithVirtualMethod : MyClassWithVirtualMethod
    {
        public override int MyMethod() => 43;
    }

    [CompilerTest]
    private static void StringConcat()
    {
        var who = "world";
        Console.WriteLine($"Hello {who}");
    }

    [TestCaseSource(nameof(GetCompilerTests))]
    public void CompilerTests(CompilerTest test)
    {
        // Run reference .NET version.
        string expectedOutput = ExecuteDotNet(test);
        
        // Compile and run DotNetro version.
        var actualOutput = ExecuteDotNetro(test);

        // Compare results.
        Console.WriteLine($".NET     Output: {expectedOutput}");
        Console.WriteLine($"DotNetro Output: {actualOutput}");
        Assert.That(actualOutput, Is.EqualTo(expectedOutput));
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
        var compilationResult = CompilerDriver.Compile(typeof(DotNetCompilerTests).Assembly.Location, test.Method.Name);

        File.WriteAllText($"CompilerTest{test.Method.Name}.asm", compilationResult.AssemblyCode);
        File.WriteAllText($"CompilerTest{test.Method.Name}.lst", compilationResult.Listing);

        // Copy compiled program into memory.
        var memory = new byte[ushort.MaxValue + 1];
        compilationResult.CompiledProgram.CopyTo(memory, 0x2000);

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

        var debugFilePath = Path.GetFullPath($"CompilerTest{test.Method.Name}.out");
        using var debugWriter = new StreamWriter(File.OpenWrite(debugFilePath));

        var output = "";

        var shouldContinue = true;
        var loopCount = 0;
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

            if (cpu.Pins.Sync)
            {
                debugWriter.WriteLine($"{cpu.PC:X4}  A:{cpu.A:X2} X:{cpu.X:X2} Y:{cpu.Y:X2} P:{cpu.P.AsByte(false):X2} SP:{cpu.SP:X2}");
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

            loopCount++;
            if (loopCount > 30000)
            {
                debugWriter.Flush();
                Assert.Fail($"Didn't complete in a sensible time. Debug output: {debugFilePath}");
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