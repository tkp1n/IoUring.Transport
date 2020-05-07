using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IoUring.Transport.Internals
{
    internal static class MemoryHelper
    {
        public static unsafe void* UnsafeGetAddressOfPinnedArrayData(byte[] array)
            => Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
    }
}