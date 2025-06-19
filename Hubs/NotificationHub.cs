using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace ADManagerAPI.Hubs;

public class NotificationHub(ILogger<NotificationHub> logger) : Hub
{
    private static readonly ConcurrentDictionary<string, string> _userConnections = new();

    public override async Task OnConnectedAsync()
    {
        logger.LogInformation($"Client connecté au NotificationHub: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation($"Client déconnecté du NotificationHub: {Context.ConnectionId}");

        foreach (var kvp in _userConnections.Where(x => x.Value == Context.ConnectionId).ToList())
        {
            _userConnections.TryRemove(kvp.Key, out _);
            logger.LogInformation($"Association utilisateur-connexion supprimée pour {kvp.Key}");
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task RegisterUser(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            logger.LogWarning("Tentative d'enregistrement avec un userId vide");
            return;
        }

        _userConnections[userId] = Context.ConnectionId;
        logger.LogInformation($"Utilisateur {userId} enregistré avec la connexion {Context.ConnectionId}");

        await Clients.Caller.SendAsync("RegistrationConfirmed", userId);
    }


    public async Task SendNotificationToUser(string userId, NotificationMessage notification)
    {
        if (string.IsNullOrEmpty(userId) || notification == null)
        {
            logger.LogWarning("UserId vide ou notification nulle");
            return;
        }

        if (_userConnections.TryGetValue(userId, out var connectionId) && connectionId != null)
        {
            await Clients.Client(connectionId).SendAsync("ReceiveNotification", notification);
            logger.LogInformation($"Notification envoyée à l'utilisateur {userId} (connexion {connectionId})");
        }
        else
        {
            logger.LogWarning($"Aucune connexion trouvée pour l'utilisateur {userId}");
        }
    }

    public async Task MarkNotificationAsRead(string notificationId)
    {
        logger.LogInformation($"Notification {notificationId} marquée comme lue par {Context.ConnectionId}");

        await Clients.Caller.SendAsync("NotificationMarkedAsRead", notificationId);
    }

    public async Task MarkAllNotificationsAsRead()
    {
        logger.LogInformation($"Toutes les notifications marquées comme lues par {Context.ConnectionId}");

        await Clients.Caller.SendAsync("AllNotificationsMarkedAsRead");
    }
}

public abstract class NotificationMessage
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public required string Type { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsRead { get; set; }
}