// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.InteropServices;

namespace System.Buffers
{
    /// <summary>
    /// Slab tracking object used by the byte buffer memory pool. A slab is a large allocation which is divided into smaller blocks. The
    /// individual blocks are then treated as independent array segments.
    /// </summary>
    internal class MemoryPoolSlab : IDisposable
    {
        private bool _isDisposed;

        public MemoryPoolSlab(int length)
        {
            Array = GC.AllocateArray<byte>(length, pinned: true);
            NativePointer = Marshal.UnsafeAddrOfPinnedArrayElement(Array, 0);
        }

        /// <summary>
        /// True as long as the blocks from this slab are to be considered returnable to the pool. In order to shrink the
        /// memory pool size an entire slab must be removed. That is done by (1) setting IsActive to false and removing the
        /// slab from the pool's _slabs collection, (2) as each block currently in use is Return()ed to the pool it will
        /// be allowed to be garbage collected rather than re-pooled, and (3) when all block tracking objects are garbage
        /// collected and the slab is no longer references the slab will be garbage collected and the memory unpinned will
        /// be unpinned by the slab's Dispose.
        /// </summary>
        public bool IsActive => !_isDisposed;

        public IntPtr NativePointer { get; private set; }

        public byte[] Array { get; private set; }

        protected void Dispose(bool disposing)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Array = null;
            NativePointer = IntPtr.Zero;
        }

        ~MemoryPoolSlab()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
