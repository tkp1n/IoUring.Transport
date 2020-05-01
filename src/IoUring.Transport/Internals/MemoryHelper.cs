using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IoUring.Transport.Internals
{
    internal static class MemoryHelper
    {
        public unsafe static void* UnsafeGetAddressOfPinnedArrayData(byte[] array)
            => Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
    }
}