namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Tells when the bytes read so far hold one complete top-level JSON object or array.
    /// The TCP service has no length prefix or delimiter: a request is one JSON value, and a
    /// large one reaches the socket in several segments with pauses between them (always
    /// through a forwarding proxy such as Docker Desktop's, and on loopback for large
    /// requests). Reading only what is buffered at that moment cut such a request and it was
    /// answered "Invalid JSON" without an id. Only structural bytes are looked at; they are
    /// ASCII, so multi-byte UTF-8 text needs no decoding here.
    /// </summary>
    internal sealed class JsonFrameScanner
    {
        private int _depth;
        private bool _inString;
        private bool _escaped;
        private bool _started;
        private bool _notAContainer;

        /// <summary>
        /// True when a top-level object or array has been closed, or when the text does not
        /// start with one (then it is handed on as it is, to be refused by the parser).
        /// </summary>
        public bool Complete
        {
            get { return _notAContainer || (_started && _depth == 0); }
        }

        public void Feed(byte[] bytes, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                byte b = bytes[i];
                if (!_started)
                {
                    if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n')
                        continue;
                    // A UTF-8 byte order mark before the value.
                    if (b == 0xEF || b == 0xBB || b == 0xBF)
                        continue;
                    if (b != (byte)'{' && b != (byte)'[')
                    {
                        _notAContainer = true;
                        return;
                    }
                    _started = true;
                    _depth = 1;
                    continue;
                }
                if (_depth == 0)
                    return; // complete; anything after it is left to the parser as before
                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (b == (byte)'\\') _escaped = true;
                    else if (b == (byte)'"') _inString = false;
                    continue;
                }
                if (b == (byte)'"') _inString = true;
                else if (b == (byte)'{' || b == (byte)'[') _depth++;
                else if (b == (byte)'}' || b == (byte)']') _depth--;
            }
        }
    }
}
