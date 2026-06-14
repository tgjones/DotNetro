using System.Runtime.CompilerServices;

namespace DotNetro.Runtime;

internal static class ConsoleHelper
{
    // osasci: write one character to output, expanding CR to CR/LF. The body is
    // the BBC Micro ROM call (JSR $FFE3), which can't be expressed in portable
    // C#/IL — so it is hand-written MOS6502 MIR in runtime.irie as @osasci.
    // [MethodImpl(InternalCall)] marks this method as runtime-provided (no IL
    // body); the IL→MIR translator routes calls to it straight to @osasci. The
    // i8 argument arrives in $a per CC_MOS, which is exactly what @osasci reads.
    [MethodImpl(MethodImplOptions.InternalCall)]
    internal static extern void osasci(byte c);

    // Print a NUL-terminated string: a 16-bit pointer to ASCII bytes followed by
    // CR (0x0D) then NUL (0x00) — the format RegisterStringGlobal emits. Walk it
    // byte-by-byte calling osasci (so a CR expands to CR/LF) until the terminator.
    // Replaces the hand-written @WriteLineString MIR that used to live in
    // runtime.irie; the translator now compiles this loop and emits it under the
    // stable MIR name @WriteLineString.
    //
    // The argument is a raw byte buffer, not a managed string, so it is byte*:
    // indexing a .NET string would emit string::get_Chars, which the translator
    // can't compile. A local pointer `q` (not the parameter) is incremented so
    // the loop avoids `starg`.
    internal static unsafe void WriteLineString(byte* p)
    {
        byte* q = p;
        byte c;
        while ((c = *q) != 0)
        {
            osasci(c);
            q++;
        }
    }
}
