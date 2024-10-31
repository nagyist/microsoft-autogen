// Copyright (c) Microsoft Corporation. All rights reserved.
// GrpcGateway.cs

using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AutoGen.Agents;

internal sealed class GrpcGateway : Gateway, IGateway
{
    private static readonly TimeSpan s_agentResponseTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<Gateway> _logger;
    private readonly IClusterClient _clusterClient;
    private readonly IAgentRegistry _gatewayRegistry;
    private readonly IGateway _reference;

    // The local mapping of agents to worker processes.
    private readonly ConcurrentDictionary<GrpcWorkerConnection, GrpcWorkerConnection> _workers = new();
    // The agents supported by each worker process.
    private readonly ConcurrentDictionary<string, List<GrpcWorkerConnection>> _supportedAgentTypes = [];
    // The mapping from agent id to worker process.
    private readonly ConcurrentDictionary<(string Type, string Key), GrpcWorkerConnection> _agentDirectory = new();
    // RPC
    private readonly ConcurrentDictionary<(GrpcWorkerConnection, string), TaskCompletionSource<RpcResponse>> _pendingRequests = new();
    // InMemory Message Queue

    public WorkerGateway(IClusterClient clusterClient, ILogger<Gateway> logger)
    {
        _logger = logger;
        _clusterClient = clusterClient;
        _reference = clusterClient.CreateObjectReference<IGateway>(this);
        _gatewayRegistry = clusterClient.GetGrain<IAgentRegistry>(0);
    }
    private async ValueTask SendMessage(GrpcWorkerConnection connection, CloudEvent cloudEvent, CancellationToken cancellationToken = default)
    {
        await connection.ResponseStream.WriteAsync(new Message { CloudEvent = cloudEvent }, cancellationToken);
    }

    private void DispatchResponse(GrpcWorkerConnection connection, RpcResponse response)
    {
        if (!_pendingRequests.TryRemove((connection, response.RequestId), out var completion))
        {
            _logger.LogWarning("Received response for unknown request.");
            return;
        }

        // Complete the request.
        completion.SetResult(response);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _gatewayRegistry.AddWorker(_reference);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Error adding worker to registry.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }

        try
        {
            await _gatewayRegistry.RemoveWorker(_reference);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error removing worker from registry.");
        }
    }
    //new is intentional...
    internal new async Task OnReceivedMessageAsync(GrpcWorkerConnection connection, Message message)
    {
        _logger.LogInformation("Received message {Message} from connection {Connection}.", message, connection);
        switch (message.MessageCase)
        {
            case Message.MessageOneofCase.Request:
                await DispatchRequestAsync(connection, message.Request);
                break;
            case Message.MessageOneofCase.Response:
                DispatchResponse(connection, message.Response);
                break;
            case Message.MessageOneofCase.CloudEvent:
                await DispatchEventAsync(message.CloudEvent);
                break;
            case Message.MessageOneofCase.RegisterAgentTypeRequest:
                await RegisterAgentTypeAsync(connection, message.RegisterAgentTypeRequest);
                break;
            default:
                throw new InvalidOperationException($"Unknown message type for message '{message}'.");
        };
    }

    private async ValueTask RegisterAgentTypeAsync(GrpcWorkerConnection connection, RegisterAgentTypeRequest msg)
    {
        connection.AddSupportedType(msg.Type);
        _supportedAgentTypes.GetOrAdd(msg.Type, _ => []).Add(connection);

        await _gatewayRegistry.RegisterAgentType(msg.Type, _reference);
    }

    private async ValueTask DispatchEventAsync(CloudEvent evt)
    {
        await BroadcastEvent(evt);
        /*
        var topic = _clusterClient.GetStreamProvider("agents").GetStream<Event>(StreamId.Create(evt.Namespace, evt.Type));
        await topic.OnNextAsync(evt.ToEvent());
        */
    }

    private async ValueTask DispatchRequestAsync(GrpcWorkerConnection connection, RpcRequest request)
    {
        var requestId = request.RequestId;
        if (request.Target is null)
        {
            throw new InvalidOperationException($"Request message is missing a target. Message: '{request}'.");
        }

        /*
        if (string.Equals("runtime", request.Target.Type, StringComparison.Ordinal))
        {
            if (string.Equals("subscribe", request.Method))
            {
                await InvokeRequestDelegate(connection, request, async (_) =>
                {
                    await SubscribeToTopic(connection, request);
                    return new RpcResponse { Result = "Ok" };
                });

                return;
            }
        }
        else
        {
        */
        await InvokeRequestDelegate(connection, request, async request =>
        {
            var (gateway, isPlacement) = await _gatewayRegistry.GetOrPlaceAgent(request.Target);
            if (gateway is null)
            {
                return new RpcResponse { Error = "Agent not found and no compatible gateways were found." };
            }

            if (isPlacement)
            {
                // Activate the worker: load state
                // TODO
            }

            // Forward the message to the gateway and return the result.
            return await gateway.InvokeRequest(request);
        });
        //}
    }

    private static async Task InvokeRequestDelegate(GrpcWorkerConnection connection, RpcRequest request, Func<RpcRequest, Task<RpcResponse>> func)
    {
        try
        {
            var response = await func(request);
            response.RequestId = request.RequestId;
            await connection.ResponseStream.WriteAsync(new Message { Response = response });
        }
        catch (Exception ex)
        {
            await connection.ResponseStream.WriteAsync(new Message { Response = new RpcResponse { RequestId = request.RequestId, Error = ex.Message } });
        }
    }
    public async ValueTask Store(AgentState value)
    {
        var agentId = value.AgentId ?? throw new ArgumentNullException(nameof(value.AgentId));
        var agentState = _clusterClient.GetGrain<IWorkerAgentGrain>($"{agentId.Type}:{agentId.Key}");
        await agentState.WriteStateAsync(value, value.ETag);
    }

    public async ValueTask<AgentState> Read(AgentId agentId)
    {
        var agentState = _clusterClient.GetGrain<IWorkerAgentGrain>($"{agentId.Type}:{agentId.Key}");
        return await agentState.ReadStateAsync();
    }
    /*
    private async ValueTask SubscribeToTopic(WorkerProcessConnection connection, RpcRequest request)
    {
        // Subscribe to a topic
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(request.Data)
            ?? throw new ArgumentException($"Request data does not contain required payload format: {{\"namespace\": \"string\", \"type\": \"string\"}}.");
        var ns = parameters["namespace"];
        var type = parameters["type"];
        var topic = _clusterClient.GetStreamProvider("agents").GetStream<Event>(StreamId.Create(ns: type, key: ns));
        await topic.SubscribeAsync(OnNextAsync);
        return;

        async Task OnNextAsync(IList<SequentialItem<Event>> items)
        {
            foreach (var item in items)
            {
                var evt = item.Item.ToRpcEvent();
                evt.Namespace = ns;
                evt.Type = evt.Type;
                var payload = new Dictionary<string, string>
                {
                    ["sequenceId"] = item.Token.SequenceNumber.ToString(),
                    ["eventIdx"] = item.Token.EventIndex.ToString()
                };
                evt.Data = JsonSerializer.Serialize(payload);
                await connection.ResponseStream.WriteAsync(new Message { Event = evt });
            }
        }
    }
    */

    internal Task ConnectToWorkerProcess(IAsyncStreamReader<Message> requestStream, IServerStreamWriter<Message> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Received new connection from {Peer}.", context.Peer);
        var workerProcess = new GrpcWorkerConnection(this, requestStream, responseStream, context);
        _workers[workerProcess] = workerProcess;
        return workerProcess.Completion;
    }

    internal void OnRemoveWorkerProcess(GrpcWorkerConnection workerProcess)
    {
        _workers.TryRemove(workerProcess, out _);
        var types = workerProcess.GetSupportedTypes();
        foreach (var type in types)
        {
            if (_supportedAgentTypes.TryGetValue(type, out var supported))
            {
                supported.Remove(workerProcess);
            }
        }

        // Any agents activated on that worker are also gone.
        foreach (var pair in _agentDirectory)
        {
            if (pair.Value == workerProcess)
            {
                ((IDictionary<(string Type, string Key), GrpcWorkerConnection>)_agentDirectory).Remove(pair);
            }
        }
    }
}