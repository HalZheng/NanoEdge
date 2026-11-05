using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace NanoEdge
{
    // 单实例的第二层：第二个进程把「要打开的 URL」交给已经运行的实例。
    // 没它的话，把 NanoEdge 设成默认浏览器后点外部链接只会弹一句"已经在运行"。
    // 用命名管道而不是窗口消息：不需要找 HWND，也不用跨会话枚举窗口。
    public static class Ipc
    {
        private const string PipeName = "NanoEdge.Ipc.v1";
        private static volatile bool _running;

        // "" 表示只激活窗口；非空表示要打开的 URL。
        public static event Action<string> Received;

        public static void Start()
        {
            if (_running) return;
            _running = true;
            var unused = Task.Run(LoopAsync);
        }

        public static void Stop() { _running = false; }

        private static async Task LoopAsync()
        {
            while (_running)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        await server.WaitForConnectionAsync().ConfigureAwait(false);
                        string line;
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                            line = await reader.ReadLineAsync().ConfigureAwait(false);

                        var handler = Received;
                        if (handler != null) handler(line ?? "");
                    }
                }
                catch (Exception ex)
                {
                    if (!_running) return;
                    Diagnostics.Log("ipc server: " + ex.Message);
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        }

        // 返回 false 表示没人在听（首个实例还没起好，或管道被占用）。
        public static bool TrySend(string message, int timeoutMs = 1500)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(timeoutMs);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true })
                        writer.WriteLine(message ?? "");
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
