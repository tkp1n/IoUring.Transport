using System;

namespace IoUring.Transport.Internals
{
    internal class AddressNotAvailableException : Exception
    {
        public AddressNotAvailableException(string message) : base(message) { }
    }
}