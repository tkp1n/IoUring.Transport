using System;

namespace IoUring.Transport.Internals
{
    internal sealed class AddressNotAvailableException : Exception
    {
        public AddressNotAvailableException(string message) : base(message) { }
    }
}