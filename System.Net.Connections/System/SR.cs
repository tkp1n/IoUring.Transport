namespace System
{
    internal class SR
    {
        public const string net_connections_createpipe_null = "The CreatePipe implementation returned null; a valid reference was expected.";
        public const string net_connections_createstream_null = "The CreateStream implementation returned null; a valid reference was expected.";
        public const string net_connections_no_create_overrides = "One of CreatePipe or CreateStream must be implemented";
        public const string net_connections_pipe_use_after_stream = "The Connection's Pipe may not be accessed after Stream has been accessed.";
        public const string net_connections_stream_use_after_pipe = "The Connection's Stream may not be accessed after Pipe has been accessed.";
        public const string net_connections_zero_byte_pipe_read = "The PipeReader returned a zero-length read.";
    }
}