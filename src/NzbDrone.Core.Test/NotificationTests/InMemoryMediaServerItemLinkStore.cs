using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Notifications;

namespace NzbDrone.Core.Test.NotificationTests
{
    public class InMemoryMediaServerItemLinkStore : IMediaServerItemLinkStore
    {
        private readonly Dictionary<string, List<MediaServerItemLink>> _links = new();
        private readonly HashSet<string> _negative = new();

        public bool TryGet(string key, out List<MediaServerItemLink> links)
        {
            return _links.TryGetValue(key, out links);
        }

        public void Set(string key, IEnumerable<MediaServerItemLink> links)
        {
            _links[key] = links.ToList();
            _negative.Remove(key);
        }

        public void Remove(string key)
        {
            _links.Remove(key);
            _negative.Remove(key);
        }

        public bool IsNegative(string key)
        {
            return _negative.Contains(key);
        }

        public void SetNegative(string key)
        {
            _links.Remove(key);
            _negative.Add(key);
        }
    }
}
