using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetDaemon.Client.Internal.HomeAssistant.Commands;

namespace NetDaemon.HassClient.Tests.Integration;

/// <summary>
///     The Home Assistant Mock class implements a fake Home Assistant server by
///     exposing the websocket api and fakes responses to requests.
/// </summary>
public class HomeAssistantMock : IAsyncDisposable
{
    public const int RecieiveBufferSize = 1024 * 4;
    public readonly IHost HomeAssistantHost;

    public HomeAssistantMock()
    {
        HomeAssistantHost = CreateHostBuilder().Build() ?? throw new ApplicationException("Failed to create host");
        HomeAssistantHost.Start();
        var server = HomeAssistantHost.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>() ?? throw new NullReferenceException();
        foreach (var address in addressFeature.Addresses)
        {
            ServerPort = int.Parse(address.Split(':').Last());
            break;
        }
    }

    public int ServerPort { get; }

    public async ValueTask DisposeAsync()
    {
        await Stop().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Starts a websocket server in a generic host
    /// </summary>
    /// <returns>Returns a IHostBuilder instance</returns>
    private static IHostBuilder CreateHostBuilder()
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(s =>
            {
                s.Configure<HostOptions>(
                    opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(30)
                );
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls("http://127.0.0.1:63372"); //"http://172.17.0.2:5001"
                webBuilder.UseStartup<HassMockStartup>();
            });
    }


    /// <summary>
    ///     Stops the fake Home Assistant server
    /// </summary>
    private async Task Stop()
    {
        await HomeAssistantHost.StopAsync().ConfigureAwait(false);
        await HomeAssistantHost.WaitForShutdownAsync().ConfigureAwait(false);
    }
}


/// <summary>
///     The class implementing the mock hass server
/// </summary>
public class HassMockStartup : IHostedService
{
    private readonly byte[] _authOkMessage =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Testdata", "auth_ok.json"));

    private readonly string _eventMessage =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "event.json"));

    private readonly string _togglePerfTestEventMessage =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "toggle_perf_test_event.json"));

    private readonly CancellationTokenSource _cancelSource = new();

    // Get the path to mock testdata
    private readonly string _mockTestdataPath = Path.Combine(AppContext.BaseDirectory, "Testdata");

    public HassMockStartup(IConfiguration configuration)
    {
    }

    private static int DefaultTimeOut => 5000;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancelSource.Cancel();
        return Task.CompletedTask;
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment e)
    {
        var webSocketOptions = new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(120)
        };
        app.UseWebSockets(webSocketOptions);
        app.Map("/api/websocket", builder =>
        {
            builder.Use(async (context, next) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await ProcessWebsocket(webSocket);
                    return;
                }

                await next();
            });
        });
        app.UseRouting();

        // app.UseEndpoints(
        //     builder =>
        //     {
        //     });
    }

    // For testing the API we just return a entity
    // private static async Task ProcessRequest(HttpContext context)
    // {
    //     var entityName = "test.entity";
    //     if (context.Request.Method == "POST")
    //         entityName = "test.post";
    //
    //     await context.Response.WriteAsJsonAsync(
    //         new HassEntity
    //         {
    //             EntityId = entityName,
    //             DeviceId = "ksakksk22kssk2",
    //             AreaId = "ssksks2ksk3k333kk",
    //             Name = "name"
    //         }
    //     ).ConfigureAwait(false);
    // }

    /// <summary>
    ///     Replaces the id of the result being sent by the id of the command received
    /// </summary>
    /// <param name="responseMessageFileName">Filename of the result</param>
    /// <param name="id">Id of the command</param>
    /// <param name="websocket">The websocket to send to</param>
    private async Task ReplaceIdInResponseAndSendMsg(string msg, int id, WebSocket websocket)
    {
        // All testdata has id=3 so easy to replace it
        msg = msg.Replace("\"id\": 3", $"\"id\": {id}");
        var bytes = Encoding.UTF8.GetBytes(msg);

        await websocket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length),
            WebSocketMessageType.Text, true, _cancelSource.Token).ConfigureAwait(false);
    }


    private readonly ConcurrentBag<int> _eventSubscriptions = new();

    /// <summary>
    ///     Process incoming websocket requests to simulate Home Assistant websocket API
    /// </summary>
    private async Task ProcessWebsocket(WebSocket webSocket)
    {
        // Buffer is set.
        var buffer = new byte[HomeAssistantMock.RecieiveBufferSize];

        try
        {
            // First send auth required to the client
            var authRequiredMessage =
                await File.ReadAllBytesAsync(Path.Combine(_mockTestdataPath, "auth_required.json"))
                    .ConfigureAwait(false);

            await webSocket.SendAsync(new ArraySegment<byte>(authRequiredMessage, 0, authRequiredMessage.Length),
                WebSocketMessageType.Text, true, _cancelSource.Token).ConfigureAwait(false);


            while (true)
            {
                // Wait for incoming messages
                var result =
                    await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancelSource.Token)
                        .ConfigureAwait(false);

                _cancelSource.Token.ThrowIfCancellationRequested();

                if (result.CloseStatus.HasValue && webSocket.State == WebSocketState.CloseReceived)
                    break;

                var hassMessage =
                    JsonSerializer.Deserialize<HassMessage>(new ReadOnlySpan<byte>(buffer, 0, result.Count))
                    ?? throw new ApplicationException("Unexpected not able to deserialize");
                
                Console.WriteLine($"Got MessageType {hassMessage.Type}");
                switch (hassMessage.Type)
                {
                    // We have an auth message
                    case "auth":
                        var authMessage =
                            JsonSerializer.Deserialize<AuthMessage>(
                                new ReadOnlySpan<byte>(buffer, 0, result.Count));
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(_authOkMessage, 0, _authOkMessage.Length),
                            WebSocketMessageType.Text, true, _cancelSource.Token).ConfigureAwait(false);
              
                        break;
                    case "get_config":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_config.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        break;
                    
                    case "config/area_registry/list":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_get_areas.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);

                        break;
                    case "config/device_registry/list":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_get_devices.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        break;
                    case "config/entity_registry/list":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_get_entities.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        break;
                    case "get_states":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_states.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        break;
                    case "input_boolean/create":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_msg.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        break;
                    case "call_service":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_msg.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        break;
                    case "subscribe_events":
                        await ReplaceIdInResponseAndSendMsg(
                            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Testdata", "result_msg.json")),
                            hassMessage.Id,
                            webSocket).ConfigureAwait(false);
                        
                        _eventSubscriptions.Add(hassMessage.Id);
                        _= DoPerformanceTesting(hassMessage.Id, webSocket);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Normal", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new ApplicationException("The thing is closed unexpectedly", e);
        }
        finally
        {
            try
            {
                await SendCorrectCloseFrameToRemoteWebSocket(webSocket).ConfigureAwait(false);
            }
            catch
            {
                // Just fail silently                
            }
        }
    }

    private async Task DoPerformanceTesting(int messageId, WebSocket webSocket)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        Console.WriteLine("Starting test...!");
        await ReplaceIdInResponseAndSendMsg(
            _togglePerfTestEventMessage,
            messageId,
            webSocket).ConfigureAwait(false);

        for (int i = 0; i <100000; i++)
        {
            _cancelSource.Token.ThrowIfCancellationRequested();
             await ReplaceIdInResponseAndSendMsg(
                _eventMessage,
                messageId,
                webSocket).ConfigureAwait(false);

             // await Task.Delay(1);
        }
        Console.WriteLine("Ending test...!");
        await ReplaceIdInResponseAndSendMsg(
            _togglePerfTestEventMessage,
            messageId,
            webSocket).ConfigureAwait(false);
    }
    /// <summary>
    ///     Closes correctly the websocket depending on websocket state
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Closing a websocket has special handling. When the client
    ///         wants to close it calls CloseAsync and the websocket takes
    ///         care of the proper close handling.
    ///     </para>
    ///     <para>
    ///         If the remote websocket wants to close the connection dotnet
    ///         implementation requires you to use CloseOutputAsync instead.
    ///     </para>
    ///     <para>
    ///         We do not want to cancel operations until we get closed state
    ///         this is why own timer cancellation token is used and we wait
    ///         for correct state before returning and disposing any connections
    ///     </para>
    /// </remarks>
    private static async Task SendCorrectCloseFrameToRemoteWebSocket(WebSocket ws)
    {
        using var timeout = new CancellationTokenSource(DefaultTimeOut);

        try
        {
            switch (ws.State)
            {
                case WebSocketState.CloseReceived:
                {
                    // after this, the socket state which change to CloseSent
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token)
                        .ConfigureAwait(false);
                    // now we wait for the server response, which will close the socket
                    while (ws.State != WebSocketState.Closed && !timeout.Token.IsCancellationRequested)
                        await Task.Delay(100).ConfigureAwait(false);
                    break;
                }
                case WebSocketState.Open:
                {
                    // Do full close 
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token)
                        .ConfigureAwait(false);
                    if (ws.State != WebSocketState.Closed)
                        throw new ApplicationException("Expected the websocket to be closed!");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal upon task/token cancellation, disregard
        }
    }

    private class AuthMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
    }

    private class SendCommandMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("id")] public int Id { get; set; } = 0;
    }
}