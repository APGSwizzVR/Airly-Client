using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace AirlyClient.Network;
public sealed class NetworkSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket=new();
    private readonly Uri _endpoint;
    public NetworkSession(Uri endpoint,string accessToken){_endpoint=endpoint;_socket.Options.SetRequestHeader("Authorization",$"Bearer {accessToken}");}
    public Task ConnectAsync(CancellationToken ct=default)=>_socket.ConnectAsync(_endpoint,ct);
    public Task SendAsync<T>(string type,T payload,CancellationToken ct=default){var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{type,payload}));return _socket.SendAsync(bytes,WebSocketMessageType.Text,true,ct);}
    public async ValueTask DisposeAsync(){try{if(_socket.State==WebSocketState.Open)await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"client closing",CancellationToken.None);}catch{} _socket.Dispose();}
}