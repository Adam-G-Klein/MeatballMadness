using System;
using System.Collections.Generic;
using Unity.Services.Multiplayer;

namespace Blocks.Sessions
{
    public class SessionInfoViewModel : ISessionInfo, IDisposable
    {
        private const string k_Unavailable = "N/A";

        private ISession _session;
        private ISessionInfo _sessionInfo;

        public SessionInfoViewModel(ISessionInfo sessionInfo)
        {
            _sessionInfo = sessionInfo;
        }

        public SessionInfoViewModel(ISession session)
        {
            _session = session;
            _session.Changed += OnSessionChanged;
            _session.SessionHostChanged += OnSessionHostChanged;
            _session.SessionPropertiesChanged += OnSessionPropertiesChanged;
        }

        public string Name => _sessionInfo?.Name ?? _session?.Name;
        public string Id => _sessionInfo?.Id ?? _session?.Id;
        public string Upid => _sessionInfo?.Upid ?? k_Unavailable;
        public string HostId => _sessionInfo?.HostId ?? _session?.Host;
        public int AvailableSlots => _sessionInfo?.AvailableSlots ?? _session?.AvailableSlots ?? 0;
        public int MaxPlayers => _sessionInfo?.MaxPlayers ?? _session?.MaxPlayers ?? 0;
        public bool IsLocked => _sessionInfo?.IsLocked ?? _session?.IsLocked ?? true;
        public bool HasPassword => _sessionInfo?.HasPassword ?? _session?.HasPassword ?? true;
        public DateTime LastUpdated => _sessionInfo?.LastUpdated ?? DateTime.UnixEpoch;
        public DateTime Created => _sessionInfo?.Created ?? DateTime.UnixEpoch;
        public IReadOnlyDictionary<string, SessionProperty> Properties
            => _sessionInfo?.Properties ?? _session?.Properties;

        private void OnSessionChanged() { }
        private void OnSessionHostChanged(string obj) { }
        private void OnSessionPropertiesChanged() { }

        public void Dispose()
        {
            if (_session != null)
            {
                _session.Changed -= OnSessionChanged;
                _session.SessionHostChanged -= OnSessionHostChanged;
                _session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
            }

            _session = null;
            _sessionInfo = null;
        }
    }
}
