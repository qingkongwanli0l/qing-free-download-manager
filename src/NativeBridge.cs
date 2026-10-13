using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingDownloadManager
{
    public sealed class NativeBridge : IDisposable
    {
        public static readonly string PipeName = "QingFDM-" + WindowsIdentity.GetCurrent().User.Value;
        const int MaximumMessage = 1024 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly Func<Dictionary<string, object>, Task<object>> receive;
        readonly Thread worker;
        readonly object gate = new object();
        readonly HashSet<NamedPipeServerStream> connections = new HashSet<NamedPipeServerStream>();
        volatile bool stopped;

        public NativeBridge(Func<Dictionary<string, object>, Task<object>> receiver)
        {
            receive = receiver;
            worker = new Thread(Listen) { IsBackground = true, Name = "Browser integration" };
            worker.Start();
        }

        static int Read(Stream input, byte[] buffer, int offset, int count, Stopwatch elapsed, int timeout)
        {
            if (timeout < 0) return input.Read(buffer, offset, count);
            int remaining = timeout - (int)elapsed.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException("The message was not received in time.");
            Task<int> pending = input.ReadAsync(buffer, offset, count);
            if (Task.WaitAny(new Task[] { pending }, remaining) < 0)
            {
                input.Dispose();
                throw new TimeoutException("The message was not received in time.");
            }
            return pending.GetAwaiter().GetResult();
        }

        public static byte[] ReadFrame(Stream input, int timeoutMilliseconds = -1)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            byte[] length = new byte[4];
            int read = Read(input, length, 0, 4, elapsed, timeoutMilliseconds);
            if (read == 0) return null;
            while (read < 4)
            {
                int n = Read(input, length, read, 4 - read, elapsed, timeoutMilliseconds);
                if (n == 0) throw new EndOfStreamException("The message length was truncated.");
                read += n;
            }
            int count = BitConverter.ToInt32(length, 0);
            if (count < 1 || count > MaximumMessage) throw new InvalidDataException("The message must contain 1 to 1048576 bytes.");
            byte[] data = new byte[count];
            read = 0;
            while (read < count)
            {
                int n = Read(input, data, read, count - read, elapsed, timeoutMilliseconds);
                if (n == 0) throw new EndOfStreamException("The message body was truncated.");
                read += n;
            }
            return data;
        }

        public static void WriteFrame(Stream output, byte[] data)
        {
            if (data == null) throw new EndOfStreamException("The desktop application closed without a response.");
            if (data.Length < 1 || data.Length > MaximumMessage) throw new InvalidDataException("The message must contain 1 to 1048576 bytes.");
            output.Write(BitConverter.GetBytes(data.Length), 0, 4);
            output.Write(data, 0, data.Length);
            output.Flush();
        }

        static Dictionary<string, object> Decode(byte[] data)
        {
            Dictionary<string, object> request;
            try
            {
                request = new JavaScriptSerializer { MaxJsonLength = MaximumMessage }
                    .Deserialize<Dictionary<string, object>>(Utf8.GetString(data));
            }
            catch (ArgumentException) { throw new InvalidDataException("The message must be a UTF-8 JSON object."); }
            catch (InvalidOperationException) { throw new InvalidDataException("The message must be a JSON object."); }
            if (request == null) throw new InvalidDataException("The message must be a JSON object.");
            return request;
        }

        static byte[] Encode(object response)
        {
            return Utf8.GetBytes(new JavaScriptSerializer { MaxJsonLength = MaximumMessage }.Serialize(response));
        }

        void Listen()
        {
            PipeSecurity security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!stopped)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                    lock (gate)
                    {
                        if (stopped) { pipe.Dispose(); return; }
                        connections.Add(pipe);
                    }
                    pipe.WaitForConnection();
                    NamedPipeServerStream connected = pipe;
                    Task.Run(() => HandleAsync(connected));
                    pipe = null;
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { if (stopped) return; }
                finally { if (pipe != null) { lock (gate) connections.Remove(pipe); pipe.Dispose(); } }
            }
        }

        async Task HandleAsync(NamedPipeServerStream pipe)
        {
            try
            {
                Dictionary<string, object> request = null;
                byte[] response;
                try
                {
                    byte[] message = ReadFrame(pipe, 15000);
                    if (message == null) return;
                    request = Decode(message);
                    response = Encode(await receive(request).ConfigureAwait(false));
                    if (response.Length > MaximumMessage) throw new InvalidDataException("The response exceeds 1 MiB.");
                }
                catch (Exception error) { response = Encode(new { id = Get(request, "id"), ok = false, error = error.Message, uncertain = Get(request, "action") == "add" }); }
                WriteFrame(pipe, response);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { lock (gate) connections.Remove(pipe); pipe.Dispose(); }
        }

        public static string Get(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInfo
        {
            public IntPtr Process, Thread;
            public int ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfo startup, out ProcessInfo process);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static void StartDesktop()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string executable = Path.Combine(directory, "QingFDM.exe");
            StartupInfo startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Flags = 1 };
            ProcessInfo process;
            // Firefox owns the native host's job; the desktop must outlive that connection.
            if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" --background"),
                IntPtr.Zero, IntPtr.Zero, false, 0x01000000 | 0x08000000, IntPtr.Zero, directory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
        }

        public static byte[] Send(byte[] data, bool launch)
        {
            Dictionary<string, object> request = Decode(data);
            bool mayPrompt = Get(request, "action") == "add";
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try { pipe.Connect(300); }
                catch (TimeoutException)
                {
                    if (!launch) throw;
                    Mutex instance;
                    bool running = Mutex.TryOpenExisting("Local\\" + PipeName, out instance);
                    if (instance != null) instance.Dispose();
                    if (!running) StartDesktop();
                    pipe.Connect(15000);
                }
                try
                {
                    WriteFrame(pipe, data);
                    byte[] response = ReadFrame(pipe, mayPrompt ? 600000 : Get(request, "action") == "mediaFormats" ? 125000 : 15000);
                    if (response == null) throw new EndOfStreamException("The desktop application closed without a response.");
                    return response;
                }
                catch (Exception error)
                {
                    if (mayPrompt) error.Data["QingAcceptanceUnknown"] = true;
                    throw;
                }
            }
        }

        public static int RunHost()
        {
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            while (true)
            {
                byte[] message;
                try { message = ReadFrame(input); }
                catch (IOException error) { Console.Error.WriteLine(error.Message); return 1; }
                if (message == null) return 0;
                Dictionary<string, object> request = null;
                byte[] response;
                try
                {
                    request = Decode(message);
                    response = Send(message, true);
                }
                catch (Exception error) { response = Encode(new { id = Get(request, "id"), ok = false, error = error.Message, uncertain = error.Data.Contains("QingAcceptanceUnknown") }); }
                try { WriteFrame(output, response); }
                catch (IOException) { return 1; }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                stopped = true;
                foreach (NamedPipeServerStream pipe in connections) pipe.Dispose();
            }
        }
    }
}
