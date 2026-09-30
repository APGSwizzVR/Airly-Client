using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AirlyClient.Network;

public sealed class NetworkSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Uri _endpoint;

    public NetworkSession(Uri endpoint, string accessToken)
    {
        _endpoint = endpoint;
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        await _socket.ConnectAsync(_endpoint, cancellationToken);

    public async Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken = default)
    {
        var envelope = new { type, payload };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", CancellationToken.None); }
            catch { }
        }
        _socket.Dispose();
    }
}
