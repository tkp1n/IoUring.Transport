using System;

namespace IoUring.Transport.Internals
{
    internal readonly struct AsyncOperationResult
    {
        public static readonly Exception CompleteWithoutErrorSentinel = new();
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
            return _error == CompleteWithoutErrorSentinel ? null : _error;
        }
    }
}