using System.Collections.Generic;

namespace Mirror
{
    public static class NetworkWriterPool
    {
        private static readonly Stack<NetworkWriter> pool = new Stack<NetworkWriter>();

        public static NetworkWriter GetWriter()
        {
            if (pool.Count != 0)
            {
                var writer = pool.Pop();
                // reset cached writer length and position
                writer.SetLength(0);
                return writer;
            }

            return new NetworkWriter();
        }

        public static void Recycle(NetworkWriter writer)
        {
            pool.Push(writer);
        }
    }
}