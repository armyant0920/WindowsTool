namespace WindowsTool.App.Security;

/// <summary>
/// 本次啟動的連線資訊：session token 與實際監聽的埠號。
/// </summary>
public sealed class LocalServerState(string token, int port)
{
    private int _port = port;

    public string Token { get; } = token;

    /// <summary>實際監聽的埠號；0 代表尚未確定，此時所有請求都會被拒絕。</summary>
    public int Port => Volatile.Read(ref _port);

    internal void SetPort(int port) => Volatile.Write(ref _port, port);
}
