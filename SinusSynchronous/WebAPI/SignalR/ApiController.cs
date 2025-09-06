﻿using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using SinusSynchronous.API.Data;
using SinusSynchronous.API.Data.Extensions;
using SinusSynchronous.API.Dto;
using SinusSynchronous.API.Dto.User;
using SinusSynchronous.API.SignalR;
using SinusSynchronous.PlayerData.Pairs;
using SinusSynchronous.Services;
using SinusSynchronous.Services.Mediator;
using SinusSynchronous.Services.ServerConfiguration;
using SinusSynchronous.SinusConfiguration;
using SinusSynchronous.SinusConfiguration.Models;
using SinusSynchronous.WebAPI.SignalR.Utils;
using System.Collections.Concurrent;
using System.Reflection;

namespace SinusSynchronous.WebAPI;
using ServerIndex = int;

#pragma warning disable MA0040
public sealed partial class ApiController : DisposableMediatorSubscriberBase
{
    public const string MainServer = "Sinus Synchronous";
    public const string MainServiceUri = "wss://sinus.syrilai.dev";

    private readonly DalamudUtilService _dalamudUtil;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILoggerProvider _loggerProvider;
    private readonly MultiConnectTokenService _multiConnectTokenService;
    private readonly SinusConfigService _sinusConfigService;

    /// <summary>
    /// In preparaption for multi connect, all of the SignalR connection functionality has been moved into an
    /// instantiated, not injected, client.
    /// That client, internally, uses the server index to access the server object, and everything is based on that index. No more
    /// usage of ServerConfigurationManager#Current server
    /// Down the line, we move this into a list or a Map of active servers.
    /// </summary>
    private readonly ConcurrentDictionary<ServerIndex, MultiConnectSinusClient> _sinusClients = new();

    public ApiController(ILogger<ApiController> logger, ILoggerFactory loggerFactory, DalamudUtilService dalamudUtil, ILoggerProvider loggerProvider,
        PairManager pairManager, ServerConfigurationManager serverManager, SinusMediator mediator, MultiConnectTokenService multiConnectTokenService, SinusConfigService sinusConfigService) : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _serverManager = serverManager;
        _multiConnectTokenService = multiConnectTokenService;
        _sinusConfigService = sinusConfigService;
        _loggerFactory = loggerFactory;
        _loggerProvider = loggerProvider;

        // TODO
        // Auto connect every server. Ideally in sequence, in an extra method
        GetOrCreateForServer(_serverManager.CurrentServerIndex).DalamudUtilOnLogIn();
    }

    // TODO all in this region still needs to be reworked to not use current server
    #region StillBasedOnCurrentServer

    public string DisplayName => CurrentConnectionDto?.User.AliasOrUID ?? string.Empty;

    public bool ServerAlive => ServerState is ServerState.Connected or ServerState.RateLimited
        or ServerState.Unauthorized or ServerState.Disconnected;
    
    public string AuthFailureMessage
    {
        get
        {
            return GetClientForServer(_serverManager.CurrentServerIndex)?.AuthFailureMessage ?? string.Empty;
        }
    }
    
    private ConnectionDto? CurrentConnectionDto
    {
        get
        {
            // For now, display for the one selected in drop-down. Later, we will have to do this per-server
            return GetClientForServer(_serverManager.CurrentServerIndex)?.ConnectionDto;
        }
    }
    
    public ServerInfo ServerInfo => CurrentConnectionDto?.ServerInfo ?? new ServerInfo();

    public ServerState ServerState
    {
        get
        {
            return GetClientForServer(_serverManager.CurrentServerIndex)?._serverState ?? ServerState.Offline;
        }
    }

    public ServerState GetServerState(ServerIndex index)
    {
        return GetClientForServer(index)?._serverState ?? ServerState.Offline;
    }

    public string UID => CurrentConnectionDto?.User.UID ?? string.Empty;

    #endregion

    public bool IsServerConnected(int index)
    {
        return GetClientForServer(index)?._serverState == ServerState.Connected;
    }
    
    public int OnlineUsers
    {
        get
        {
            return _sinusClients.Sum(entry => entry.Value.SystemInfoDto?.OnlineUsers ?? 0);
        }
    }
    
    public int GetOnlineUsersForServer(ServerIndex index) {
        return GetClientForServer(index)?.SystemInfoDto?.OnlineUsers ?? 0;
    }

    public ServerInfo? GetServerInfoForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ConnectionDto?.ServerInfo;
    }

    public DefaultPermissionsDto? GetDefaultPermissionsForServer(ServerIndex index)
    {
        return GetClientForServer(index)?.ConnectionDto?.DefaultPreferredPermissions;
    }

    public ServerState GetServerStateForServer(ServerIndex index)
    {
        // No client found means it's offline
        return GetClientForServer(index)?._serverState ?? ServerState.Offline;
    }

    public bool AnyServerConnected
    {
        get
        {
            return _sinusClients.Any(client => client.Value._serverState == ServerState.Connected);
        }
    }

    public bool AnyServerDisconnecting
    {
        get
        {
            return _sinusClients.Any(client => client.Value._serverState == ServerState.Disconnecting);
        }
    }

    public int[] ConnectedServerIndexes {
        get
        {
            return [.._sinusClients.Keys];
        }
    }

    public int GetMaxGroupsJoinedByUser(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.ConnectionDto?.ServerInfo.MaxGroupsJoinedByUser ?? 0;
    }

    public string GetUidByServer(ServerIndex serverIndex)
    {
        return GetClientForServer(serverIndex)?.UID ?? string.Empty;
    }

    public async Task PauseConnectionAsync(ServerIndex serverIndex)
    {
        _sinusClients.TryRemove(serverIndex, out MultiConnectSinusClient? removed);
        if (removed != null)
        {
            await removed.DisposeAsync().ConfigureAwait(false);
        }

    }
    
    public async Task CreateConnectionsAsync(ServerIndex serverIndex)
    {
        await ConnectMultiClient(serverIndex).ConfigureAwait(false);
    }

    public Task CyclePauseAsync(ServerIndex serverIndex, UserData userData)
    {
        CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        _ = Task.Run(async () =>
        {
            await GetOrCreateForServer(serverIndex).CyclePauseAsync(serverIndex, userData).ConfigureAwait(false);
        }, cts.Token);
        return Task.CompletedTask;
    }

    private MultiConnectSinusClient CreateNewClient(ServerIndex serverIndex)
    {
        return new MultiConnectSinusClient(serverIndex, _serverManager, _pairManager, _dalamudUtil,
            _loggerFactory, _loggerProvider, Mediator, _multiConnectTokenService, _sinusConfigService);
    }

    private MultiConnectSinusClient? GetClientForServer(ServerIndex serverIndex)
    {
        _sinusClients.TryGetValue(serverIndex, out var client);
        return client;
    }
    
    private MultiConnectSinusClient GetOrCreateForServer(ServerIndex serverIndex)
    {
        return _sinusClients.GetOrAdd(serverIndex, CreateNewClient);
    }

    private Task ConnectMultiClient(ServerIndex serverIndex)
    {
        return GetOrCreateForServer(serverIndex).CreateConnectionsAsync();
    }

    protected override void Dispose(bool disposing)
    {
        // We can always just dispose this - even if not used
        foreach (MultiConnectSinusClient multiConnectSinusClient in _sinusClients.Values)
        {
            multiConnectSinusClient.Dispose();
        }
        _sinusClients.Clear();
        base.Dispose(disposing);
    }
}
#pragma warning restore MA0040