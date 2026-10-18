namespace NzbDrone.Core.Notifications
{
    public interface IRecentlyAddedNotification
    {
        void MarkAsRecentlyAdded(DownloadMessage message);
    }
}
