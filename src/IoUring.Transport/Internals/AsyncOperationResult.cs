using System;
using System.Diagnostics;

namespace IoUring.Transport.Internals
{
    internal readonly struct AsyncOperationResult
    {
        public static readonly Exception CompleteWithoutErrorSentinel = new Exception();
        private readonly bool _completedSynchronously;
        private readonly Exception _error;

        public AsyncOperationResult(bool completedSynchronously, Exception error)
        {
            _completedSynchronously = completedSynchronously;
            _error = error;
        }

        public bool CompletedSuccessfully => _completedSynchronously && _error == null;
        public bool CompletedExceptionally => _completedSynchronously && _error != null;

        public Exception GetError()
        {
            Debug.Assert(_error != null);

            return _error == CompleteWithoutErrorSentinel ? null : _error;
        }
    }
}