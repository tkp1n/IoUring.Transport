using System;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Connections;
using static Tmds.Linux.LibC;

namespace IoUring.Transport.Internals
{
    internal static class ThrowHelper
    {
        public static void ThrowNewInvalidOperationException()
            => throw NewInvalidOperationException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewInvalidOperationException()
            => new InvalidOperationException();

        public static void ThrowNewObjectDisposedException(ExceptionArgument argument)
            => throw NewObjectDisposedException(argument);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewObjectDisposedException(ExceptionArgument argument)
            => new ObjectDisposedException(argument.ToString());

        public static void ThrowNewErrnoException()
            => throw NewErrnoException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewErrnoException() => new ErrnoException(errno);

        public static void ThrowNewErrnoException(int error)
            => throw NewErrnoException(error);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewErrnoException(int error) => new ErrnoException(error);

        public static void ThrowNewNotSupportedException_EndPointNotSupported()
            => throw NewNotSupportedException_EndPointNotSupported();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewNotSupportedException_EndPointNotSupported()
            => new NotSupportedException("EndPoint type not supported");

        public static void ThrowNewNotSupportedException_AddressFamilyNotSupported()
            => throw NewNotSupportedException_AddressFamilyNotSupported();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewNotSupportedException_AddressFamilyNotSupported()
            => new NotSupportedException("AddressFamily type not supported");

        public static void ThrowNewAddressInUseException()
            => throw NewAddressInUseException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewAddressInUseException() => new AddressInUseException("Address in use.");

        public static void ThrowNewAddressNotAvailableException()
            => throw NewAddressNotAvailableException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewAddressNotAvailableException()
            => new AddressNotAvailableException("Address not available.");

        public static void ThrowNewSubmissionQueueFullException()
            => throw NewSubmissionQueueFullException();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception NewSubmissionQueueFullException()
            => new SubmissionQueueFullException();

        public enum ExceptionArgument
        {
            ConnectionFactory,
            ConnectionListener,
            IoUringTransport
        }
    }
}