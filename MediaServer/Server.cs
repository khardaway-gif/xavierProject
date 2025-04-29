using System.Net.Sockets;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Reflection.PortableExecutable;
using System.Collections.Concurrent;

class SocketAsyncEventArgsPool
{
    private readonly ConcurrentBag<SocketAsyncEventArgs> pool;

    public SocketAsyncEventArgsPool(int initialCapacity)
    {
        pool = new ConcurrentBag<SocketAsyncEventArgs>();

        // Prepopulate the pool with instances
        for (int i = 0; i < initialCapacity; i++)
        {
            var args = new SocketAsyncEventArgs();
            args.Completed += (sender, e) => { /* Handle completion */ };
            pool.Add(args);
        }
    }

    public SocketAsyncEventArgs Rent()
    {
        if (pool.TryTake(out var args))
        {
            return args;
        }

        // Create a new instance if the pool is empty
        var newArgs = new SocketAsyncEventArgs();
        newArgs.Completed += (sender, e) => { /* Handle completion */ };
        return newArgs;
    }

    public void Return(SocketAsyncEventArgs args)
    {
        // Reset the instance before returning it to the pool
        args.AcceptSocket = null;
        pool.Add(args);
    }
}

namespace MediaServer
{
    class Server
    {
        private const int MAXCONNECTIONS = 10;
        private int connections;
        private Socket socServer;
        private string ip;
        private int port;
        private bool running;
        private FileStream servedFile = null;
        ILogger logger;
        private AvailableMedia availableMedia;
        private Semaphore maxNumberAcceptedClients;
        private IPEndPoint IPE;
        private SocketAsyncEventArgsPool eventArgsPool;

        private Server()
        {
            socServer = null;
            connections = 0;
            maxNumberAcceptedClients = new Semaphore(MAXCONNECTIONS, MAXCONNECTIONS);
            eventArgsPool = new SocketAsyncEventArgsPool(MAXCONNECTIONS);
        }
        public Server(string ip, int port, string mediaDir) : this()
        {
            availableMedia = new AvailableMedia(mediaDir);

            using ILoggerFactory factory = LoggerFactory.Create(builder => builder
            .AddFilter("MediaServer.Server", LogLevel.Debug)
            .AddConsole());

            logger = factory.CreateLogger<Server>();
            socServer = null;
            this.ip = ip;
            this.port = port;
            socServer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPE = new IPEndPoint(IPAddress.Parse(this.ip), this.port);
        }

        public void Start()
        {
            socServer.Bind(IPE);
            socServer.Listen(0);
            running = true;
            Thread thread = new Thread(Listen);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            Thread.Sleep(100);
            if (this.servedFile != null)
            { try { servedFile.Close(); } catch {; } }
            if (socServer != null && socServer.Connected) socServer.Shutdown(SocketShutdown.Both);
        }

        //TODO: Finish Implementation
        /// <summary>
        /// This method listens for requests
        /// Use the pattern found at https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socketasynceventargs?view=net-9.0
        /// The Start method at the link is our Listen method   
        /// </summary>
        private void Listen()
        {
            while (this.running)
            {
                Console.WriteLine("Waiting for a connection...");
                maxNumberAcceptedClients.WaitOne();

                // Rent a SocketAsyncEventArgs instance from the pool
                var e = eventArgsPool.Rent();

                // Start an asynchronous accept operation
                if (!socServer.AcceptAsync(e))
                {
                    // If the operation completed synchronously, handle it immediately
                    AcceptCallback(socServer, e);
                }
            }
        }

        /// <summary>
        /// Accepts the request and starts to process it on a new thread
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AcceptCallback(object sender, SocketAsyncEventArgs e)
        {
            Thread thread = new Thread(ProcessAccept);
            thread.Start(e);
        }

        private void ProcessAccept(object eObj)
        {
            SocketAsyncEventArgs e = (SocketAsyncEventArgs)eObj;
            try
            {
                Interlocked.Increment(ref connections);
                Console.WriteLine("Client connection accepted. There are {0} clients connected to the server", connections);
                Socket sock = e.AcceptSocket;
                byte[] buffer = new byte[3000];
                int receivedSize = 0;
                var sb = new StringBuilder();
                MemoryStream ms;

                while (sock.Available > 0)
                {
                    receivedSize = sock.Receive(buffer, SocketFlags.None);

                    ms = new MemoryStream();
                    ms.Write(buffer, 0, receivedSize);
                    string toAdd = UTF8Encoding.UTF8.GetString(ms.ToArray());
                    sb.Append(toAdd);
                    logger.LogDebug("Received {0} bytes", receivedSize);
                }
                string requestData = sb.ToString();

                BusinessLogic(requestData, sock);
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Error processing request");
            }
            finally
            {
                Listen();
            }

        }

        private void CloseClientSocket(Socket socket)
        {
            // close the socket associated with the client
            try
            {
                socket.Shutdown(SocketShutdown.Send);
            }
            // throws if client process has already closed
            catch (Exception) { }
            socket.Close();

            // decrement the counter keeping track of the total number of clients connected to the server
            Interlocked.Decrement(ref connections);

            maxNumberAcceptedClients.Release();
            Console.WriteLine("A client has been disconnected from the server. There are {0} clients connected to the server", connections);
        }

        //TODO: Finish implementation. Analyze through the entire method.
        private void BusinessLogic(string request, Socket handler)
        {
            // Parse the request into lines
            string[] requestLines = GetRequestLines(request);
            if (requestLines.Length == 0)
            {
                logger.LogWarning("Empty request received.");
                CloseClientSocket(handler);
                return;
            }

            // Extract headers and method/path
            List<KeyValuePair<string, string>> headers = GetHeaders(requestLines);
            KeyValuePair<string, string> methodAndPath = GetMethodAndPath(requestLines);

            // Log the method and path
            string methPath = string.Format("Method: {0} | Path: {1}", methodAndPath.Key, methodAndPath.Value);
            logger.LogDebug(methPath);

            // Handle favicon requests (ignore them)
            if (methodAndPath.Value.Contains("favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                CloseClientSocket(handler);
                return;
            }

            // Log headers for debugging
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(methPath);
            foreach (KeyValuePair<string, string> pair in headers)
            {
                sb.AppendLine($"{pair.Key}: {pair.Value}");
            }
            logger.LogDebug(sb.ToString());

            // Route the request based on the HTTP method
            if (methodAndPath.Key.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                HandleHead(handler, headers, methodAndPath.Value);
            }
            else if (methodAndPath.Key.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                HandleGet(handler, headers, methodAndPath.Value);
            }
            else
            {
                // Unsupported HTTP method
                logger.LogWarning("Unsupported HTTP method: {0}", methodAndPath.Key);
                CloseClientSocket(handler);
            }
        }

        //TODO: Finish implementation
        /// <summary>
        /// This method takes the raw request splits it based on end of line, and returns each line in a string array
        /// </summary>
        /// <param name="request">The raw request</param>
        /// <returns>A string array with each cell being a line in the request</returns>
        private string[] GetRequestLines(string request)
        {
            if (string.IsNullOrWhiteSpace(request))
            {
                // Return an empty array if the request is null or empty
                return Array.Empty<string>();
            }

            // Split the request into lines using the standard HTTP line break
            return request.Split(new[] { "\r\n" }, StringSplitOptions.None);
        }

        /// <summary>
        /// This method returns the HTTP method used in the request and the requested path as a key-value pair.
        /// </summary>
        /// <param name="requestLines">The request lines</param>
        /// <returns>Key-Value Pair containing the method as the key and the path as the value</returns>
        private KeyValuePair<string, string> GetMethodAndPath(string[] requestLines)
        {
            if (requestLines == null || requestLines.Length == 0)
            {
                throw new ArgumentException("Request lines cannot be null or empty.", nameof(requestLines));
            }

            // The first line of the HTTP request contains the method, path, and HTTP version
            string[] parts = requestLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
            {
                throw new FormatException("Invalid HTTP request line format.");
            }

            string method = parts[0]; // HTTP method (e.g., GET, POST, HEAD)
            string path = parts[1];   // Requested path (e.g., /index.html)

            return new KeyValuePair<string, string>(method, path);
        }

        //TODO: Implement
        /// <summary>
        /// This method returns the headers submitted in the request as a list of key-value pairs.
        /// </summary>
        /// <param name="requestLines">The request as a string array</param>
        /// <returns>List of key-value pairs where each header name is the key and its value is the value</returns>
        private List<KeyValuePair<string, string>> GetHeaders(string[] requestLines)
        {
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();

            try
            {
                // Start parsing headers after the first line (request line)
                for (int i = 1; i < requestLines.Length; i++)
                {
                    string line = requestLines[i];

                    // Stop parsing when an empty line is encountered (end of headers)
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        break;
                    }

                    // Split the header line into key and value
                    int separatorIndex = line.IndexOf(':');
                    if (separatorIndex > 0)
                    {
                        string key = line.Substring(0, separatorIndex).Trim();
                        string value = line.Substring(separatorIndex + 1).Trim();
                        headers.Add(new KeyValuePair<string, string>(key, value));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Exception occurred while parsing headers.");
            }

            return headers;
        }

        private int GetIndexFromPath(string path)
        {
            int index = -1;
            string requestIndex = path.Remove(0, 1);
            logger.LogDebug("Attempting to find match for: {0}", requestIndex);
            if (!int.TryParse(requestIndex, out index))
            {
                logger.LogDebug("Index parsing from path failed.");
                index = -1;
            }
            return index;
        }

        //TODO: Finish implementation
        /// <summary>
        /// This method returns the header information of a particular file.
        /// </summary>
        /// <param name="handler">The socket to send the response to.</param>
        /// <param name="headers">The request headers.</param>
        /// <param name="path">The requested file path.</param>
        private void HandleHead(Socket handler, List<KeyValuePair<string, string>> headers, string path)
        {
            int index = GetIndexFromPath(path);
            string requestFile = availableMedia.getAbsolutePath(index);

            logger.LogDebug("HEAD requested for file: {0}", requestFile);

            FileInfo fileInfo = new FileInfo(requestFile);
            if (fileInfo.Exists)
            {
                // Construct the HTTP response headers
                string contentType = GetContentType(requestFile.ToLower());
                string reply = "HTTP/1.1 200 OK" + Environment.NewLine +
                               "Server: VLC" + Environment.NewLine +
                               "Content-Type: " + contentType + Environment.NewLine +
                               "Content-Length: " + fileInfo.Length + Environment.NewLine +
                               "Last-Modified: " + GMTTime(fileInfo.LastWriteTime) + Environment.NewLine +
                               "Connection: close" + Environment.NewLine + Environment.NewLine;

                // Send the headers to the client
                handler.Send(Encoding.UTF8.GetBytes(reply), SocketFlags.None);
            }
            else
            {
                // File not found, send a 404 response
                string reply = "HTTP/1.1 404 Not Found" + Environment.NewLine +
                               "Server: VLC" + Environment.NewLine +
                               "Connection: close" + Environment.NewLine + Environment.NewLine;

                handler.Send(Encoding.UTF8.GetBytes(reply), SocketFlags.None);
            }

            // Close the client socket
            CloseClientSocket(handler);
        }

        private void HandleGet(Socket handler, List<KeyValuePair<string, string>> headers, string path)
        {
            int index = GetIndexFromPath(path);
            if (index == -1)
            {
                ReturnList(handler); //List requested
            }
            else //File possibly requested
            {
                string requestFile = availableMedia.getAbsolutePath(index);
                logger.LogDebug("GET requested for file: {0}", requestFile);
                ServeFile(handler, headers, requestFile);
            }
        }

        //TODO: Finish implementation
        //TODO: Finish implementation
        /// <summary>
        /// This method returns a webpage containing a list of media found in the media directory.
        /// Each entry on the page is clickable via an anchor tag, showing the name of the media file
        /// with its href attribute set to the index number of the file. Clicking the link opens the media item.
        /// The names of the files do not show the root path to the media directory.
        /// </summary>
        /// <param name="handler">The socket to write the webpage to</param>
        private void ReturnList(Socket handler)
        {
            // Read the HTML template
            string template = File.ReadAllText("template.txt");
            string media = "";

            // Get the list of available media files
            string[] files = availableMedia.getAvailableFiles().ToArray();

            // Generate clickable links for each media file
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]); // Extract the file name without the path
                media += $"<a href=\"/{i}\">{fileName}</a><br/>\n";
            }

            // Replace the placeholder in the template with the generated media list
            template = template.Replace("{{mediaList}}", media);

            // Construct the HTTP response
            string contentType = "text/html";
            string reply = "HTTP/1.1 200 OK" + Environment.NewLine +
                           "Server: VLC" + Environment.NewLine +
                           "Content-Type: " + contentType + Environment.NewLine +
                           "Last-Modified: " + GMTTime(DateTime.Now) + Environment.NewLine +
                           "Date: " + GMTTime(DateTime.Now) + Environment.NewLine +
                           "Accept-Ranges: bytes" + Environment.NewLine;

            UTF8Encoding encoding = new UTF8Encoding();
            byte[] bytes = encoding.GetBytes(template);
            long length = bytes.Length;

            reply += "Content-Length: " + length + Environment.NewLine +
                     "Connection: close" + Environment.NewLine + Environment.NewLine;

            // Send the HTTP headers and the HTML content
            handler.Send(Encoding.UTF8.GetBytes(reply), SocketFlags.None);
            handler.Send(bytes);

            // Close the client socket
            CloseClientSocket(handler);
        }

        //TODO: Finish implementation
        /// <summary>
        /// This method determines whether to stream a file or send it in its entirety.
        /// </summary>
        /// <param name="handler">The socket to send the file to</param>
        /// <param name="headers">The request headers</param>
        /// <param name="requestFile">The requested file</param>
        private void ServeFile(Socket handler, List<KeyValuePair<string, string>> headers, string requestFile)
        {
            long tempRange = 0;
            bool hasRange = false;

            // Check for the Range header
            KeyValuePair<string, string> rangeHeader = headers.Find(e => e.Key.Equals("range", StringComparison.OrdinalIgnoreCase));
            if (!rangeHeader.Equals(default(KeyValuePair<string, string>)))
            {
                hasRange = true;
                string range = rangeHeader.Value.ToLower().Replace("bytes=", "").Split('-')[0];
                long.TryParse(range, out tempRange);
            }

            // Create a FileSenderHelper object
            FileSenderHelper fsHelper = new FileSenderHelper(requestFile, handler, tempRange);

            // Determine whether to stream or send the file entirely
            if (!hasRange || IsMusicOrImage(requestFile))
            {
                // Send the file without using ranges
                Thread t = new Thread(new ParameterizedThreadStart(NoRangeSend));
                t.Start(fsHelper);
            }
            else
            {
                // Stream the file using ranges
                Thread t = new Thread(new ParameterizedThreadStart(SendWithRange));
                t.Start(fsHelper);
            }
        }

        //TODO: Finish implementation
        /// <summary>
        /// Sends the entire file to the requestor since it is small.
        /// </summary>
        /// <param name="fsHelperObj">Helper object containing data relevant for thread execution</param>
        private void NoRangeSend(object fsHelperObj)
        {
            FileSenderHelper fsHelper = (FileSenderHelper)fsHelperObj;
            Socket handler = fsHelper.getSocket();
            string requestFile = fsHelper.getRequestFile();
            FileStream fsFile = null;
            long chunkSize = 50000; // Default chunk size
            long bytesSent = 0;

            string contentType = GetContentType(requestFile.ToLower());
            logger.LogDebug("Sending file: {0}", requestFile);

            if (!File.Exists(requestFile))
            {
                handler.Close();
                return;
            }

            FileInfo fInfo = new FileInfo(requestFile);
            if (fInfo.Length > 8000000)
                chunkSize = 500000; // Increase chunk size for large files

            // Construct the HTTP response headers
            string reply = "HTTP/1.1 200 OK" + Environment.NewLine +
                           "Server: VLC" + Environment.NewLine +
                           "Content-Type: " + contentType + Environment.NewLine +
                           "Connection: close" + Environment.NewLine +
                           "Content-Length: " + fInfo.Length + Environment.NewLine + Environment.NewLine;

            // Send the headers
            handler.Send(Encoding.UTF8.GetBytes(reply), SocketFlags.None);

            // Open the file and send it in chunks
            try
            {
                fsFile = new FileStream(requestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fsFile.Seek(0, SeekOrigin.Begin);

                byte[] buffer = new byte[chunkSize];
                int bytesRead;

                while (this.running && handler.Connected && (bytesRead = fsFile.Read(buffer, 0, buffer.Length)) > 0)
                {
                    handler.Send(buffer, bytesRead, SocketFlags.None);
                    bytesSent += bytesRead;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while sending file: {0}", requestFile);
            }
            finally
            {
                fsFile?.Close();
                CloseClientSocket(handler);
            }

            logger.LogDebug("Finished sending file: {0}. Total bytes sent: {1}", requestFile, bytesSent);
        }

        //TODO: Finish implementation
        /// <summary>
        /// Streams a movie to the requestor in chunks starting from the specified byte range.
        /// </summary>
        /// <param name="fsHelperObj">Helper object containing data relevant for thread execution</param>
        private void SendWithRange(object fsHelperObj)
        {
            FileSenderHelper fsHelper = (FileSenderHelper)fsHelperObj;
            Socket handler = fsHelper.getSocket();
            string requestFile = fsHelper.getRequestFile();

            logger.LogDebug("Streaming movie: {0}", requestFile);

            long chunkSize = 500000; // Default chunk size
            long range = fsHelper.getRange(); // Starting byte range from the request
            long bytesSent = 0;

            string contentType = GetContentType(requestFile.ToLower());
            FileInfo fInfo = new FileInfo(requestFile);
            long fileLength = fInfo.Length;

            // Construct the HTTP response headers for partial content
            string reply = ContentString(range, contentType, fileLength);
            handler.Send(Encoding.UTF8.GetBytes(reply), SocketFlags.None);

            // Open the file and start streaming from the specified range
            try
            {
                using FileStream fs = new FileStream(requestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.CanSeek)
                    fs.Seek(range, SeekOrigin.Begin);

                byte[] buffer = new byte[chunkSize];
                int bytesRead;

                while (this.running && handler.Connected && (bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    handler.Send(buffer, bytesRead, SocketFlags.None);
                    bytesSent += bytesRead;

                    // Stop if the entire file has been sent
                    if (bytesSent + range >= fileLength)
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while streaming file: {0}", requestFile);
            }
            finally
            {
                CloseClientSocket(handler);
            }

            logger.LogDebug("Finished streaming file: {0}. Total bytes sent: {1}", requestFile, bytesSent);
        }

        public class FileSenderHelper 
    {
        private readonly string requestFile;
        private readonly Socket handler;
        private readonly long range;

        public FileSenderHelper(string requestFile, Socket handler, long range)
        {
            this.requestFile = requestFile;
            this.handler = handler;
            this.range = range;
        }

        public string getRequestFile() => requestFile;
        public Socket getSocket() => handler;
        public long getRange() => range;
    }

        //INSPIRED BY ORIGINAL PROJECT
        private string EncodeUrlPaths(string Value)
        {//Encode requests sent to the DLNA device
            if (Value == null) return null;
            return Value.Replace("%", "&percnt;").Replace("&", "&amp;").Replace("\\", "/");
        }

        //FROM ORIGINAL PROJECT
        private bool IsMusicOrImage(string fileName)
        {//We don't want to use byte-ranges for music or image data so we test the filename here
            if (fileName.ToLower().EndsWith(".jpg") || fileName.ToLower().EndsWith(".png") || fileName.ToLower().EndsWith(".gif") || fileName.ToLower().EndsWith(".mp3"))
                return true;
            return false;
        }

        //FROM ORIGINAL PROJECT
        private string GetContentType(string FileName)
        {//Based on the file type we create our content type for the reply to the TV/DLNA device
            string ContentType = "audio/mpeg";
            if (FileName.ToLower().EndsWith(".jpg")) ContentType = "image/jpg";
            else if (FileName.ToLower().EndsWith(".png")) ContentType = "image/png";
            else if (FileName.ToLower().EndsWith(".gif")) ContentType = "image/gif";
            else if (FileName.ToLower().EndsWith(".avi")) ContentType = "video/avi";
            if (FileName.ToLower().EndsWith(".mp4")) ContentType = "video/mp4";
            return ContentType;
        }

        //INSPIRED FROM ORIGINAL PROJECT
        private string GMTTime(DateTime Time)
        {//Covert date to GMT time/date
            string GMT = Time.ToString("ddd, dd MMM yyyy HH':'mm':'ss 'GMT'");
            return GMT;//Example "Sat, 25 Jan 2014 12:03:19 GMT";
        }

        //FROM ORIGINAL PROJECT
        private string ContentString(long Range, string ContentType, long FileLength)
        {//Builds up our HTTP reply string for byte-range requests
            string Reply = "";
            Reply = "HTTP/1.1 206 Partial Content" + Environment.NewLine + "Server: VLC" + Environment.NewLine + "Content-Type: " + ContentType + Environment.NewLine;
            Reply += "Accept-Ranges: bytes" + Environment.NewLine;
            Reply += "Date: " + GMTTime(DateTime.Now) + Environment.NewLine;
            if (Range == 0)
            {
                Reply += "Content-Length: " + FileLength + Environment.NewLine;
                Reply += "Content-Range: bytes 0-" + (FileLength - 1) + "/" + FileLength + Environment.NewLine;
            }
            else
            {
                Reply += "Content-Length: " + (FileLength - Range) + Environment.NewLine;
                Reply += "Content-Range: bytes " + Range + "-" + (FileLength - 1) + "/" + FileLength + Environment.NewLine;
            }
            return Reply + Environment.NewLine;
        }
    }
}
