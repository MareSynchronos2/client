﻿using SinusSynchronous.SinusConfiguration.Models;
using SinusSynchronous.Services.Mediator;
using Microsoft.AspNetCore.SignalR.Client;

namespace SinusSynchronous.WebAPI.SignalR.Utils;

public class ForeverRetryPolicy : IRetryPolicy
{
    private readonly SinusMediator _mediator;
    private readonly int _serverIndex;
    private bool _sentDisconnected = false;

    public ForeverRetryPolicy(SinusMediator mediator, int serverIndex)
    {
        _mediator = mediator;
        _serverIndex = serverIndex;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        TimeSpan timeToWait = TimeSpan.FromSeconds(new Random().Next(10, 20));
        if (retryContext.PreviousRetryCount == 0)
        {
            _sentDisconnected = false;
            timeToWait = TimeSpan.FromSeconds(3);
        }
        else if (retryContext.PreviousRetryCount == 1) timeToWait = TimeSpan.FromSeconds(5);
        else if (retryContext.PreviousRetryCount == 2) timeToWait = TimeSpan.FromSeconds(10);
        else
        {
            if (!_sentDisconnected)
            {
                _mediator.Publish(new NotificationMessage("Connection lost", "Connection lost to server", NotificationType.Warning, TimeSpan.FromSeconds(10)));
                _mediator.Publish(new DisconnectedMessage(_serverIndex));
            }
            _sentDisconnected = true;
        }

        return timeToWait;
    }
}