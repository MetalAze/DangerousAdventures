// ---------------------------------------------------------------------
// Copyright 2018 David Haig
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// ---------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ninja.WebSockets.Exceptions;
using System.Linq;

namespace Ninja.WebSockets
{
    public class HttpHelper
    {
        private const string HTTP_GET_HEADER_REGEX = @"^GET(.*)HTTP\/1\.1";

        /// <summary>
        /// Calculates a random WebSocket key that can be used to initiate a WebSocket handshake
        /// </summary>
        /// <returns>A random websocket key</returns>
        public static string CalculateWebSocketKey()
        {
            // this is not used for cryptography so doing something simple like he code below is op
            var rand = new Random((int) DateTime.Now.Ticks);
            var keyAsBytes = new byte[16];
            rand.NextBytes(keyAsBytes);
            return Convert.ToBase64String(keyAsBytes);
        }

        /// <summary>
        /// Computes a WebSocket accept string from a given key
        /// </summary>
        /// <param name="secWebSocketKey">The web socket key to base the accept string on</param>
        /// <returns>A web socket accept string</returns>
        public static string ComputeSocketAcceptString(string secWebSocketKey)
        {
            // this is a guid as per the web socket spec
            const string webSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            var concatenated = secWebSocketKey + webSocketGuid;
            var concatenatedAsBytes = Encoding.UTF8.GetBytes(concatenated);

            // note an instance of SHA1 is not threadsafe so we have to create a new one every time here
            var sha1Hash = SHA1.Create().ComputeHash(concatenatedAsBytes);
            var secWebSocketAccept = Convert.ToBase64String(sha1Hash);
            return secWebSocketAccept;
        }

        /// <summary>
        /// Reads an http header as per the HTTP spec
        /// </summary>
        /// <param name="stream">The stream to read UTF8 text from</param>
        /// <param name="token">The cancellation token</param>
        /// <returns>The HTTP header</returns>
        public static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken token)
        {
            var length = 1024 * 16; // 16KB buffer more than enough for http header
            var buffer = new byte[length];
            var offset = 0;
            var bytesRead = 0;

            do
            {
                if (offset >= length)
                    throw new EntityTooLargeException("Http header message too large to fit in buffer (16KB)");

                bytesRead = await stream.ReadAsync(buffer, offset, length - offset, token);
                offset += bytesRead;
                var header = Encoding.UTF8.GetString(buffer, 0, offset);

                // as per http specification, all headers should end this this
                if (header.Contains("\r\n\r\n")) return header;
            } while (bytesRead > 0);

            return string.Empty;
        }

        /// <summary>
        /// Decodes the header to detect is this is a web socket upgrade response
        /// </summary>
        /// <param name="header">The HTTP header</param>
        /// <returns>True if this is an http WebSocket upgrade response</returns>
        public static bool IsWebSocketUpgradeRequest(string header)
        {
            var getRegex = new Regex(HTTP_GET_HEADER_REGEX, RegexOptions.IgnoreCase);
            var getRegexMatch = getRegex.Match(header);

            if (getRegexMatch.Success)
            {
                // check if this is a web socket upgrade request
                var webSocketUpgradeRegex = new Regex("Upgrade: websocket", RegexOptions.IgnoreCase);
                var webSocketUpgradeRegexMatch = webSocketUpgradeRegex.Match(header);
                return webSocketUpgradeRegexMatch.Success;
            }

            return false;
        }

        /// <summary>
        /// Gets the path from the HTTP header
        /// </summary>
        /// <param name="httpHeader">The HTTP header to read</param>
        /// <returns>The path</returns>
        public static string GetPathFromHeader(string httpHeader)
        {
            var getRegex = new Regex(HTTP_GET_HEADER_REGEX, RegexOptions.IgnoreCase);
            var getRegexMatch = getRegex.Match(httpHeader);

            if (getRegexMatch.Success)
                // extract the path attribute from the first line of the header
                return getRegexMatch.Groups[1].Value.Trim();

            return null;
        }

        public static IList<string> GetSubProtocols(string httpHeader)
        {
            var regex = new Regex(@"Sec-WebSocket-Protocol:(?<protocols>.+)", RegexOptions.IgnoreCase);
            var match = regex.Match(httpHeader);

            if (match.Success)
            {
                const int MAX_LEN = 2048;
                if (match.Length > MAX_LEN)
                    throw new EntityTooLargeException(
                        $"Sec-WebSocket-Protocol exceeded the maximum of length of {MAX_LEN}");

                // extract a csv list of sub protocols (in order of highest preference first)
                var csv = match.Groups["protocols"].Value.Trim();
                return csv.Split(new char[] {','}, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .ToList();
            }

            return new List<string>();
        }

        /// <summary>
        /// Reads the HTTP response code from the http response string
        /// </summary>
        /// <param name="response">The response string</param>
        /// <returns>the response code</returns>
        public static string ReadHttpResponseCode(string response)
        {
            var getRegex = new Regex(@"HTTP\/1\.1 (.*)", RegexOptions.IgnoreCase);
            var getRegexMatch = getRegex.Match(response);

            if (getRegexMatch.Success)
                // extract the path attribute from the first line of the header
                return getRegexMatch.Groups[1].Value.Trim();

            return null;
        }

        /// <summary>
        /// Writes an HTTP response string to the stream
        /// </summary>
        /// <param name="response">The response (without the new line characters)</param>
        /// <param name="stream">The stream to write to</param>
        /// <param name="token">The cancellation token</param>
        public static async Task WriteHttpHeaderAsync(string response, Stream stream, CancellationToken token)
        {
            response = response.Trim() + "\r\n\r\n";
            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length, token);
        }
    }
}