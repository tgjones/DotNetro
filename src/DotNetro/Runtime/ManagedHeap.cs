namespace DotNetro.Runtime;

internal static class ManagedHeap
{
    private static IntPtr s_heapPtr = 0x1000;

    public static IntPtr Alloc(nint size)
    {
        var result = s_heapPtr;

        s_heapPtr += size;

        return result;
    }
}
