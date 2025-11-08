namespace DotNetro.Runtime;

internal static class ConsoleHelper
{
    public static void WriteLineBoolean(bool value)
    {
        // TODO: I'd like to write
        // Console.WriteLine(value ? "True" : "False");
        // but it produces IL that I can't compile yet.

        if (value)
        {
            Console.WriteLine("True");
        }
        else
        {
            Console.WriteLine("False");
        }
    }
}
