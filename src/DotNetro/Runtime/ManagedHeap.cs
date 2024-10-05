using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNetro.Runtime;

internal static class ManagedHeap
{
    private static IntPtr s_heapPtr = 0x1000;

    public static unsafe IntPtr Alloc(nint size, IntPtr vtablePtr)
    {
        var result = s_heapPtr;

        // Increment by size of object header + size of object.
        s_heapPtr += sizeof(IntPtr) + size;

        // Fill object header with vtable pointer.
        *(IntPtr*)result = vtablePtr;

        // Return pointer to object contents.
        return result + sizeof(IntPtr);
    }
}
