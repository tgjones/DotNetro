using System.Diagnostics;

namespace DotNetro.Compiler.Tests;

public class DotNetCompilerTests
{
    [Test]
    public void TestPrintHelloWorld()
    {
        var outputPath = "Test.asm";
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        DotNetCompiler.Compile(GetType().Assembly.Location, nameof(CodePrintHelloWorld), outputPath, null);

        foreach (var line in File.ReadAllLines(outputPath))
        {
            Console.WriteLine(line);
        }

        // TODO: Assert something.
    }

    [Test]
    public void TestCurrentThing()
    {
        var outputPath = Path.GetFullPath("Test.asm");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        DotNetCompiler.Compile(GetType().Assembly.Location, nameof(ForLoop), outputPath, null);

        foreach (var line in File.ReadAllLines(outputPath))
        {
            Console.WriteLine(line);
        }

        using (var p = Process.Start(
            "C:\\Users\\Tim\\Downloads\\beebasm-win32\\beebasm.exe",
            $"-i \"{outputPath}\" -do \"{Path.ChangeExtension(outputPath, ".ssd")}\" -boot MyCode -v"))
        {
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException("Failed to compile.");
            }
        }

        using (var p = Process.Start(
            "C:\\Code\\Emulators\\BBC Micro\\b-em-a790022-w64\\b-em.exe",
            Path.ChangeExtension(outputPath, ".ssd")))
        {

        }

        // TODO: Assert something.
    }

    [Test]
    public void TestCallFunctions()
    {
        var outputPath = "Test.asm";
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        DotNetCompiler.Compile(GetType().Assembly.Location, nameof(CodeMain), outputPath, null);

        foreach (var line in File.ReadAllLines(outputPath))
        {
            Console.WriteLine(line);
        }

        // TODO: Assert something.
    }

    private static void CodeMain()
    {
        CodePrintHelloWorld();
        CodePrint42();
        CodePrintMinus1();
        CodePrint1Plus1();
        CodePrintParameter(16);

        Console.WriteLine(FunctionWithReturnValue());
        Console.WriteLine(FunctionWithParameterAndReturnValue(44));
        Console.WriteLine(FunctionWithParametersAndReturnValue(1, 1));
    }

    private static void CodePrintHelloWorld()
    {
        Console.WriteLine("Hello, World!");
    }

    private static void CodePrint42()
    {
        Console.WriteLine(42);
    }

    private static void CodePrintMinus1()
    {
        Console.WriteLine(-1);
    }

    private static void CodePrint1Plus1()
    {
        var a = 1;
        var b = 1;
        Console.WriteLine(a + b);
    }

    private static void CodePrintParameter(int value)
    {
        Console.WriteLine(value);
    }

    private static int FunctionWithReturnValue()
    {
        return 43;
    }

    private static int FunctionWithParameterAndReturnValue(int value)
    {
        return value + 1;
    }

    private static int FunctionWithParametersAndReturnValue(int a, int b)
    {
        return a + b;
    }

    private static void ReadAndWriteLine()
    {
        Console.WriteLine("Enter some text:");

        var text = Console.ReadLine();

        Console.WriteLine("You said:");
        Console.WriteLine(text);

        Console.Beep();
    }

    private static void CodeAddTwoIntegers()
    {
        Console.WriteLine("Enter an integer:");
        var intA = int.Parse(Console.ReadLine()!);

        Console.WriteLine("Enter another integer:");
        var intB = int.Parse(Console.ReadLine()!);

        var result = intA + intB;

        Console.Write("Result is ");
        Console.Write(result);
        Console.WriteLine();
    }

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

    private static void ForLoop()
    {
        Console.WriteLine("Begin");

        for (var i = 0; i < 5; i++)
        {
            Console.WriteLine(i);
        }

        Console.WriteLine("End");
    }
}