using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KeyIsle
{
    // agy's manual OAuth prompt reads terminal keystrokes. A redirected StandardInput is ignored.
    internal sealed class LoginTerminal : IDisposable
    {
        private IntPtr console;
        private SafeWaitHandle process;
        private FileStream input;
        private StreamReader output;
        private Task drain;

        internal static LoginTerminal Start(ProcessStartInfo start, Action<string> lineReceived)
        {
            var terminal = new LoginTerminal();
            try
            {
                terminal.Connect(start, lineReceived);
                return terminal;
            }
            catch
            {
                terminal.Dispose();
                throw;
            }
        }

        private void Connect(ProcessStartInfo start, Action<string> lineReceived)
        {
            SafeFileHandle consoleInput, inputWriter, outputReader, consoleOutput;
            if (!CreatePipe(out consoleInput, out inputWriter, IntPtr.Zero, 16384)) throw LastError();
            using (consoleInput)
            {
                input = new FileStream(inputWriter, FileAccess.Write);
                if (!CreatePipe(out outputReader, out consoleOutput, IntPtr.Zero, 16384)) throw LastError();
                using (consoleOutput)
                {
                    output = new StreamReader(new FileStream(outputReader, FileAccess.Read), Encoding.UTF8, false, 4096);
                    // A wide terminal preserves long authorization URLs without display wrapping.
                    int result = CreatePseudoConsole(new Coord { X = 8192, Y = 24 }, consoleInput, consoleOutput, 0, out console);
                    Marshal.ThrowExceptionForHR(result);
                }
            }
            drain = Task.Factory.StartNew(() => ReadLines(lineReceived), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Spawn(start);
        }

        private void Spawn(ProcessStartInfo start)
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) throw LastError();
            var startup = new StartupInfoEx();
            startup.Info.Size = Marshal.SizeOf(typeof(StartupInfoEx));
            // Null standard handles with STARTF_USESTDHANDLES let ConPTY supply all three.
            // Otherwise a console-subsystem child can still receive the launcher's redirected handles.
            startup.Info.Flags = 0x100;
            startup.Attributes = Marshal.AllocHGlobal(size);
            bool initialized = false;
            IntPtr environment = IntPtr.Zero;
            try
            {
                if (!InitializeProcThreadAttributeList(startup.Attributes, 1, 0, ref size)) throw LastError();
                initialized = true;
                if (!UpdateProcThreadAttribute(startup.Attributes, 0, new IntPtr(0x20016), console,
                    new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw LastError();

                var variables = new List<string>();
                foreach (string key in start.EnvironmentVariables.Keys)
                    variables.Add(key + "=" + start.EnvironmentVariables[key]);
                variables.Sort(StringComparer.OrdinalIgnoreCase);
                environment = Marshal.StringToHGlobalUni(String.Join("\0", variables) + "\0");
                var command = new StringBuilder("\"" + start.FileName + "\" " + start.Arguments);
                ProcessInfo info;
                if (!CreateProcessW(start.FileName, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000 | 0x00000400, environment, start.WorkingDirectory, ref startup, out info)) throw LastError();
                process = new SafeWaitHandle(info.Process, true);
                using (new SafeWaitHandle(info.Thread, true)) { }
            }
            finally
            {
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (initialized) DeleteProcThreadAttributeList(startup.Attributes);
                Marshal.FreeHGlobal(startup.Attributes);
            }
        }

        internal bool Wait(int milliseconds)
        {
            uint result = WaitForSingleObject(process, (uint)milliseconds);
            if (result == 0xffffffff) throw LastError();
            return result == 0;
        }

        internal uint Complete()
        {
            uint code;
            if (!GetExitCodeProcess(process, out code)) throw LastError();
            CloseConsole();
            drain.GetAwaiter().GetResult();
            return code;
        }

        internal void TypeCode(string code)
        {
            // ConPTY's VT input uses CR for an Enter key. Never put codes in process arguments or logs.
            byte[] bytes = Encoding.UTF8.GetBytes(code + "\r");
            try
            {
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        internal void Stop()
        {
            if (process != null && !process.IsClosed && !Wait(0)) TerminateProcess(process, 1);
        }

        private void CloseConsole()
        {
            var handle = Interlocked.Exchange(ref console, IntPtr.Zero);
            if (handle != IntPtr.Zero) ClosePseudoConsole(handle);
        }

        // Strip terminal presentation, delivering just bounded lines to the login parser.
        private enum EscapeState { Text, Escape, Csi, ControlString, StringEscape }
        private void ReadLines(Action<string> receive)
        {
            var line = new StringBuilder();
            var chunk = new char[4096];
            var state = EscapeState.Text;
            bool oversized = false;
            int count;
            while ((count = output.Read(chunk, 0, chunk.Length)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char c = chunk[i];
                    switch (state)
                    {
                        case EscapeState.Escape:
                            state = c == '[' ? EscapeState.Csi : (c == ']' || c == 'P') ? EscapeState.ControlString : EscapeState.Text;
                            continue;
                        case EscapeState.Csi:
                            if (c >= '@' && c <= '~') state = EscapeState.Text;
                            continue;
                        case EscapeState.ControlString:
                            if (c == '\a') state = EscapeState.Text;
                            else if (c == '\x1b') state = EscapeState.StringEscape;
                            continue;
                        case EscapeState.StringEscape:
                            state = c == '\\' ? EscapeState.Text : EscapeState.ControlString;
                            continue;
                    }
                    if (c == '\x1b') { state = EscapeState.Escape; continue; }
                    if (c == '\n')
                    {
                        if (!oversized && line.Length != 0) receive(line.ToString());
                        line.Clear();
                        oversized = false;
                    }
                    else if (c >= ' ' && !oversized)
                    {
                        if (line.Length < 1024 * 1024) line.Append(c);
                        else { line.Clear(); oversized = true; }
                    }
                }
            }
            if (!oversized && line.Length != 0) receive(line.ToString());
        }

        public void Dispose()
        {
            try { Stop(); }
            finally
            {
                CloseConsole(); // Keep the reader draining while Windows closes the pseudo console.
                if (drain != null)
                {
                    try { drain.GetAwaiter().GetResult(); } catch (IOException) { }
                }
                if (input != null) input.Dispose();
                if (output != null) output.Dispose();
                if (process != null) process.Dispose();
            }
        }

        private static Win32Exception LastError() { return new Win32Exception(Marshal.GetLastWin32Error()); }

        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, uint size);
        [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool CreateProcessW(string exe, StringBuilder args, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeWaitHandle process, uint code);
    }
}
