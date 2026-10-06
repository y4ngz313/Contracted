namespace Y4NGZCompany.Contracts._Shared
{
    // Local presentation noise, independent of Unity's gameplay random stream.
    internal static class ContractHudScramble
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$%&*+=?!";
        private static int _lastTick = int.MinValue;
        private static string _text;
        internal static string GetText(int tick)
        {
            if (_text != null && tick == _lastTick) return _text;
            uint state = unchecked((uint)tick * 747796405u + 2891336453u);
            var chars = new char[36];
            for (int i = 0; i < chars.Length; i++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                chars[i] = Alphabet[(int)(state % (uint)Alphabet.Length)];
            }
            _lastTick = tick;
            return _text = new string(chars);
        }
        private static int _sliceTick = int.MinValue;
        private static int _sliceLength;
        private static string _slice;
        // A leading window of the same stream (the Seraphim scan header, #1336), cached per tick so a
        // caller polling every frame allocates only when the tick changes.
        internal static string GetText(int tick, int length)
        {
            string text = GetText(tick);
            if (length >= text.Length) return text;
            if (_slice == null || tick != _sliceTick || length != _sliceLength)
            {
                _sliceTick = tick;
                _sliceLength = length;
                _slice = text.Substring(0, length);
            }
            return _slice;
        }
    }
}
